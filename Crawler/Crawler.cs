namespace Crawler
{
	using HtmlAgilityPack;
	using Crawler.Urls;
	using Crawler.Logging;
	using Crawler.Security;
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Net.Http;
	using System.Net.Sockets;
	using System.Security.Authentication;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	public class Crawl
	{
		// Shared handler/client — initialised once via Initialize() before first use
		private static SocketsHttpHandler? socketsHandler;
		private static HttpClient? client;

		// Use Random.Shared (.NET 6+) instead of new Random() per call,
		// which avoids the risk of multiple instances seeded with the same timestamp
		// producing identical sequences when called in rapid succession.
		private static readonly Random _random = Random.Shared;

		/// <summary>
		/// Call once before DownloadWebsiteAsync to configure the shared HttpClient.
		/// Configures a proxy when <paramref name="proxyUrl"/> is non-empty.
		/// </summary>
		public static void Initialize(string? proxyUrl, string? proxyUser, string? proxyPassword,
			int maxConcurrentPageDownloads = 100, int maxConcurrentAssetDownloads = 200)
		{
			lock (containmentRefusalLock)
			{
				containmentRefusals.Clear();
			}

			pageSemaphore = new SemaphoreSlim(maxConcurrentPageDownloads);
			assetSemaphore = new SemaphoreSlim(maxConcurrentAssetDownloads);
			socketsHandler = new SocketsHttpHandler
			{
				AllowAutoRedirect = false,
				PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
				PooledConnectionLifetime = TimeSpan.FromMinutes(10),
				MaxConnectionsPerServer = 20
			};

			var proxy = ProxyConfig.Build(proxyUrl, proxyUser, proxyPassword, "Crawler");
			if (proxy != null)
			{
				socketsHandler.Proxy = proxy;
				socketsHandler.UseProxy = true;
			}
			else
			{
				socketsHandler.UseProxy = false;
			}

			client = new HttpClient(socketsHandler, disposeHandler: false);
		}

		// Global concurrency limiters — initialised in Initialize() from config values.
		private static SemaphoreSlim pageSemaphore = new(100);
		private static SemaphoreSlim assetSemaphore = new(200);

		// Track visited URLs thread-safely
		private static readonly ConcurrentDictionary<string, byte> visited = new();

		// Records each refused (out-of-root) write for the end-of-crawl summary.
		// Containment is enforced unconditionally at the write itself
		// (PathContainmentCheck); this is only the inspection trail, so adding to it
		// never blocks a download. Reset per crawl in Initialize().
		private static readonly object containmentRefusalLock = new();
		private static readonly List<(string Url, string Reason)> containmentRefusals = [];

		// [KEEP] Security boundary — the per-crawl host allowlist, set once at crawl
		// launch (Crawl is the single fetch authority). The gate resolves each
		// discovered reference and admits only exact allowed hosts. If unset (e.g.
		// a direct test call), the download methods fall back to a policy built from
		// the threaded websiteUrl + allowedSubdomains.
		private static CrawlPolicy? crawlPolicy;

		internal static void SetCrawlPolicy(CrawlPolicy policy) => crawlPolicy = policy;

		// Retry policy parameters
		private const int MaxRetries = 5;
		private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(200);

		/// <summary>
		/// Canonicalizes a URL prior to fetch. Two-branch decision driven by the
		/// configured modal-query-parameter list:
		///
		///   - If any configured modal parameter substring appears in <paramref name="url"/>,
		///     the URL is reinterpreted as a modal carrier — <c>Query.ExtractModalUrl</c>
		///     unwraps the inner URL (the actual page the modal would display) using
		///     <paramref name="websiteUrl"/> as the resolution base.
		///   - Otherwise the URL is stripped of its query string entirely
		///     (<c>url.Split('?')[0]</c>): fetch the page itself, drop any selector or
		///     tracking parameters.
		///
		/// The first match wins (<c>FirstOrDefault</c>) when multiple modal parameters
		/// are configured. Comparison is case-insensitive.
		///
		/// This canonicalization runs BEFORE the <c>visited</c> de-dup gate, so the
		/// same downloadable URL reached via different modal-wrapper URLs collapses to
		/// one fetch — preventing duplicate downloads when a page is linked both
		/// directly and through a modal wrapper.
		///
		/// Behavioral note: the query-stripping side of the branch is
		/// intentionally a blanket <c>Split('?')[0]</c>, NOT driven by
		/// <c>config.QueryStringParametersToIgnoreForCrawl</c> (that config drives
		/// link validation, not query stripping — the two are separate mechanisms).
		/// Changes to this canonicalization should be deliberate: the blanket split
		/// removes every query parameter, including any a caller might want kept.
		/// </summary>
		internal static string CanonicalizeUrlForFetch(
			string url,
			string websiteUrl,
			List<string> modalQueryParameters)
		{
			var matchedParam = modalQueryParameters
				.FirstOrDefault(p => url.Contains(p, StringComparison.OrdinalIgnoreCase));

			if (matchedParam != null)
			{
				return Query.ExtractModalUrl(url, websiteUrl, matchedParam);
			}

			return url.Split('?')[0];
		}

		public static async Task DownloadWebsiteAsync
		(
			string url,
			string saveDirectory,
			string log,
			string websiteUrl,
			IReadOnlyList<CrawlLinkExclusion> downloadExclusions,
			List<string> modalQueryParameters,
			string source = "discovery",  // [KEEP] "discovery" = normal link crawl, "list" = post-crawl pass from 05-not-directly-crawlable.log
			List<string>? jsonPathPrefixes = null,           // [KEEP] site-specific prefixes for JSON/script URL extraction — configured in config.private.json
		IReadOnlyList<string>? allowedSubdomains = null // [KEEP] Security boundary — only explicitly listed subdomain base URLs are followed. See Config.UrlSubdomainsAllowed.
		)
		{
			url = CanonicalizeUrlForFetch(url, websiteUrl, modalQueryParameters);

			if (!visited.TryAdd(url, 0))
			{
				return;
			}

			CrawlLogWriter.Write(url, "crawled", "info", log);

			List<string> urlsToDownload = [];
			List<string> assetsToDownload = [];

			// Security gate inputs for this page: the base to resolve relative refs
			// against (the page url), the host allowlist, and the host-scoped base
			// the operational exclusion filter runs against (scheme://host of the
			// configured site — path is a seed, not a fence).
			var pageBase = new Uri(url);
			var policy = crawlPolicy ?? CrawlPolicy.FromConfig(websiteUrl, allowedSubdomains);
			var scopeBase = new Uri(websiteUrl).GetLeftPart(UriPartial.Authority);

			await pageSemaphore.WaitAsync();
			try
			{
				// Build a fresh request upfront and clone before each retry attempt
				// rather than reusing the same HttpRequestMessage (which can only be sent once).
				using var initialRequest = new HttpRequestMessage(HttpMethod.Get, url);
				HttpResponseMessage response = await SendWithRetriesAsync(initialRequest);

				if (response != null)
				{
					try
					{
						var responseCode = response.StatusCode;
						var location = response.Headers.Location;

						if (location != null)
						{
							var locationUrl = location.ToString();
							CrawlLogWriter.Write(url, responseCode.ToString(), locationUrl, log);
							// [KEEP] Security boundary — only follow redirects to the primary domain
							// or an explicitly configured subdomain. Redirects to arbitrary external
							// domains are silently dropped to prevent open-redirect crawl escapes.
							var redirectAdmission = CrawlGate.TryAdmit(locationUrl, pageBase, policy);
							if (redirectAdmission.Admitted)
							{
								urlsToDownload.Add(redirectAdmission.AbsoluteUrl!);
							}
						}

						response.EnsureSuccessStatusCode();

						// Capture the response Content-Type up front — it is
						// persisted into the raw crawl log (00) so the settle phase can
						// classify the file later without the live response, and it still
						// gates HTML link-extraction below.
						var contentType = response.Content.Headers.ContentType?.MediaType;

						// Pages are saved under a provisional ".unverified" name.
						// The settle phase (Step_SettleExtensions) decides the final
						// extension (.html when verified HTML, else kept .unverified) per
						// UnverifiedHtmlPolicy. GenerateFileName supplies the hash+suffix
						// identity; its URL-derived extension is intentionally replaced —
						// .htm/.htmlx no longer appear for pages. Assets (CrawlAsset) keep
						// their real extensions and are not settled.
						var baseName = Naming.GenerateFileName(url);
						var fileName = Path.ChangeExtension(baseName, FileTypeClassifier.UnverifiedExtension.TrimStart('.'));
						var downloadDir = Path.Combine(saveDirectory, "download");
						Directory.CreateDirectory(downloadDir);
						// [KEEP] Security boundary — never write a body to a path that
						// escapes the capture root. The crawled origin may be compromised
						// and the file name is attacker-influenced, so containment is
						// enforced here, at the write, independent of how the name was
						// produced (see PathContainmentCheck / ReportContainmentRefusal).
						var containment = PathContainmentCheck.Resolve(downloadDir, fileName);
						if (!containment.Safe)
						{
							ReportContainmentRefusal(containment, url, downloadDir, log);
							return;
						}

						var filePath = containment.FullPath;

						using (var responseStream = await response.Content.ReadAsStreamAsync())
						using (var fs = File.Create(filePath))
						{
							await responseStream.CopyToAsync(fs);
						}

						// Capture full request+response headers next to the body.
						WriteHeaderSidecar(response, url, downloadDir, fileName);

						// Raw saved row carries the Content-Type column (00-log format).
						CrawlLogWriter.WriteSaved(url, fileName, log, source, contentType);

						// Only attempt HTML link extraction when the server
						// declared HTML content. Anchor hrefs reach PDFs, archives,
						// images, and other non-HTML assets via <a href="…">; those
						// are saved for the asset/PDF pipelines but contribute no
						// anchor links to follow. Previously every download was
						// decoded and HTML-parsed regardless, producing wasted CPU
						// and spurious encoding errors in 01-crawler.log for PDFs.
						if (!FileTypeClassifier.IsHtmlContentType(contentType))
						{
							// Skip parsing for non-HTML downloads. response.Dispose()
							// runs via the inner finally; semaphore release runs via
							// the outer finally; the recursive downloadTasks/assetTasks
							// blocks after the outer finally are equivalent to no-ops
							// since urlsToDownload and assetsToDownload are still empty.
							return;
						}

						// Detect encoding from the saved bytes rather than assuming UTF-8,
						// consistent with how MarkupFile.RemoveByXPath reads files.
						var rawBytes = await File.ReadAllBytesAsync(filePath);
						var encoding = DetectEncoding.FromBytes(rawBytes);
						var content = encoding.GetString(rawBytes);

						var document = new HtmlDocument();
						document.LoadHtml(content);

						var links = document.DocumentNode.SelectNodes("//a[@href]");
						if (links != null)
						{
							foreach (var link in links)
							{
								// [REVIEW — candidate for removal once HAP behavior is
								// confirmed]: read the href via DeEntitizeValue rather than
								// GetAttributeValue. Hrefs whose query separators are HTML-
								// entity-encoded (e.g. "?x&#61;value" = "?x=value") were observed
								// arriving truncated to "?x&" — the entity dropped and a bare
								// "&" left — producing a dead URL that 404s, so the target (e.g.
								// linked PDFs) never reaches disk. DeEntitizeValue resolves the
								// numeric/named reference explicitly. If a probe shows
								// GetAttributeValue already returns the correct decoded value on
								// the target HAP version, this line can revert to the simpler
								// GetAttributeValue form. Defensive double-measure paired with
								// the removal of the .pdf asset short-circuit below.
								var hrefAttr = link.Attributes["href"];
								var href = hrefAttr?.DeEntitizeValue ?? string.Empty;
								// [KEEP] Security boundary — IsInDownloadScope enforces primary domain + allowed subdomains only.
								// Resolve-then-admit on the resolved URL (CrawlGate): document-relative
								// links are followed; off-host / off-scheme / IP-literal refs are denied.
								// IsInDownloadScope then applies the operator exclusions, host-scoped via
								// scopeBase (a configured seed path is a start point, not a scope fence).
								var linkAdmission = CrawlGate.TryAdmit(href, pageBase, policy);
								if (linkAdmission.Admitted
									&& Validity.IsInDownloadScope(linkAdmission.AbsoluteUrl!, scopeBase, downloadExclusions, allowedSubdomains))
								{
									var absoluteUrl = linkAdmission.AbsoluteUrl!;
									// Strip fragment — never sent to server, prevents duplicate crawl entries.
									var hashIdx = absoluteUrl.IndexOf('#');
									if (hashIdx >= 0)
									{
										absoluteUrl = absoluteUrl[..hashIdx];
									}

									// All <a href> targets go to the page path. PDFs are no
									// longer special-cased to the asset queue here (the prior
									// short-circuit is removed). The page path saves provisionally
									// as .unverified, then the settle phase classifies
									// by byte-sniff: a "%PDF-" body is renamed to .pdf and picked up
									// by PdfQualityAnalyzer, regardless of the URL's name or query
									// string. This makes PDF identity URL-shape-independent and
									// removes the leak (which bypassed settle and missed PDFs
									// reached via the post-crawl list or via mangled/odd URLs).
									urlsToDownload.Add(absoluteUrl);
								}
							}
						}

						var assets = document.DocumentNode.SelectNodes("//img[@src]");
						if (assets != null)
						{
							foreach (var asset in assets)
							{
								var src = asset.GetAttributeValue("src", asset.GetAttributeValue("href", string.Empty));
								// [KEEP] Security boundary — assets were previously queued with only a
								// well-formedness check (no scope gate), so an <img src> could pull
								// off-host or IP-literal targets. Resolve-then-admit closes that, and
								// the exclusion filter still applies (host-scoped via scopeBase).
								var assetAdmission = CrawlGate.TryAdmit(src, pageBase, policy);
								if (assetAdmission.Admitted
									&& Validity.IsInDownloadScope(assetAdmission.AbsoluteUrl!, scopeBase, downloadExclusions, allowedSubdomains))
								{
									assetsToDownload.Add(assetAdmission.AbsoluteUrl!);
								}
							}
						}

						// Seed canonical URLs into the crawl queue so canonical targets
						// are always downloaded and can be validated during analysis.
						// Pipe delimiter used elsewhere since RFC 3986 defines | as not valid in URLs.
						var canonicalNodes = document.DocumentNode
							.SelectNodes("//link[@rel='canonical'][@href]");
						if (canonicalNodes != null)
						{
							foreach (var node in canonicalNodes)
							{
								var canonicalHref = node.GetAttributeValue("href", string.Empty);
								// [KEEP] Security boundary — canonical URLs are scope-checked before being
								// seeded into the crawl queue to prevent following off-domain canonicals.
								var canonicalAdmission = CrawlGate.TryAdmit(canonicalHref, pageBase, policy);
								if (canonicalAdmission.Admitted
									&& Validity.IsInDownloadScope(canonicalAdmission.AbsoluteUrl!, scopeBase, downloadExclusions, allowedSubdomains))
								{
									urlsToDownload.Add(canonicalAdmission.AbsoluteUrl!);
								}
							}
						}

						// [KEEP] Extended URL extraction — discovers URLs in non-standard
						// locations missed by <a href> crawling. See Urls/Extractor.cs for
						// full rationale. Results feed into assetsToDownload for full download
						// and 404 checking. CSS files are scanned one level for url() refs.
						// JS files are downloaded but NOT scanned (minified, no signal).
						var extended = Extractor.FromHtml(content, url, jsonPathPrefixes);
						foreach (var extracted in extended)
						{
							// [KEEP] Security boundary — extended URLs extracted from JSON/scripts are
							// scope-checked to prevent following arbitrary URLs embedded in page content.
							var extAdmission = CrawlGate.TryAdmit(extracted.Url, pageBase, policy);
							if (!extAdmission.Admitted
								|| !Validity.IsInDownloadScope(extAdmission.AbsoluteUrl!, scopeBase, downloadExclusions, allowedSubdomains))
							{
								continue;
							}
							// Strip fragment.
							var extUrl = extAdmission.AbsoluteUrl!;
							var hashIdx = extUrl.IndexOf('#');
							if (hashIdx >= 0)
							{
								extUrl = extUrl[..hashIdx];
							}

							assetsToDownload.Add(extUrl);
						}
					}
					finally
					{
						response.Dispose();
					}
				}
			}
			catch (HttpRequestException httpEx)
			{
				CrawlLogWriter.Write(url, httpEx.StatusCode?.ToString() ?? "HttpRequestException", httpEx.Message, log);
			}
			catch (Exception ex)
			{
				CrawlLogWriter.Write(url, "N/A", ex.Message, log);
			}
			finally
			{
				pageSemaphore.Release();
			}

			// Note: crawl recursion has no central queue, so peak memory grows with
			// the number of in-flight URLs. Only relevant at very large scale
			// (>100k pages); below that it is a non-issue. At that scale other limits
			// bind first (triage partitioning across operators; splitting the crawl by
			// exclusion or per language tree), so a bounded queue/channel refactor here
			// is a theoretical improvement, not a pressing one.
			var downloadTasks = urlsToDownload.Select(u => DownloadWebsiteAsync(u, saveDirectory, log, websiteUrl, downloadExclusions, modalQueryParameters, source, jsonPathPrefixes, allowedSubdomains)).ToList();
			await Task.WhenAll(downloadTasks);

			var assetTasks = assetsToDownload.Select(async a =>
			{
				await assetSemaphore.WaitAsync();
				try
				{
					await CrawlAsset.DownloadAssetAsync(a, saveDirectory, log, source);
				}
				finally
				{
					assetSemaphore.Release();
				}
			}).ToList();

			await Task.WhenAll(assetTasks);
		}

		// [KEEP] Security boundary — reporting for a refused (out-of-root) write.
		// The PathContainmentCheck primitive stays pure; the side effects live here
		// at the call layer. Containment is enforced unconditionally at the write
		// itself, so the offending bytes are never written regardless of what this
		// does — every escape attempt is refused independently, no matter how many
		// times a hostile origin tries. This therefore does not halt the crawl:
		// each refusal is logged to file and collected, the crawl continues at full
		// parallelism, and all refusals are presented in one end-of-crawl summary
		// for inspection (or, in a silent run, left in the log).
		internal static void ReportContainmentRefusal(
			PathContainmentCheck.Verdict verdict, string sourceUrl, string captureRoot, string log)
		{
			// File-only forensic record (timestamped, never red-walls the console);
			// the end-of-crawl summary renders the inspectable block.
			Logger.LogDetailToFile(
				$"Path containment: blocked an out-of-root write for '{sourceUrl}' — the download " +
				$"name resolved outside the capture root '{captureRoot}' (reason: {verdict.Reason}). " +
				"No bytes were written.");

			lock (containmentRefusalLock)
			{
				containmentRefusals.Add((sourceUrl, verdict.Reason));
			}
		}

		// Presents the collected out-of-root refusals once, after all download
		// phases complete. Interactive runs get a calm console block for inspection;
		// silent runs already carry the per-refusal lines in the log.
		internal static void WriteContainmentRefusalSummary()
		{
			List<(string Url, string Reason)> refusals;
			lock (containmentRefusalLock)
			{
				if (containmentRefusals.Count == 0)
				{
					return;
				}

				refusals = [.. containmentRefusals];
			}

			if (CrawlerContext.Silent)
			{
				return;
			}

			ConsoleUi.WriteBlank();
			ConsoleUi.WriteActionRequired(
				$"Path containment: {refusals.Count} download(s) were blocked from writing outside the capture folder.");
			ConsoleUi.WriteActionRequired(
				"These files were NOT downloaded. Review for a benign config slip vs. the site serving hostile content:");
			foreach (var (url, reason) in refusals)
			{
				ConsoleUi.WriteActionRequired($"  {url}  (reason: {reason})");
			}

			ConsoleUi.WriteBlank();
		}

		// Write the per-download header sidecar ("<hash>.header") next to the
		// body. Captures the full request and response headers verbatim as offline
		// ground truth so a later design change can read a previously-untreated
		// header from disk instead of re-crawling. Shared by the page download
		// (above) and the asset download (CrawlAsset). A sidecar failure must never
		// abort or fail a download — it is best-effort diagnostics — so all I/O is
		// guarded and logged as a warning only.
		internal static void WriteHeaderSidecar(
			HttpResponseMessage response, string url, string downloadDir, string bodyFileName)
		{
			try
			{
				var headerName = Path.ChangeExtension(bodyFileName,
					HeaderSidecar.HeaderSidecarExtension.TrimStart('.'));
				var headerPath = Path.Combine(downloadDir, headerName);

				var request = response.RequestMessage;
				var method = request?.Method?.Method ?? "GET";
				var requestLine = $"{method} {url}";

				// Request headers: the actual outgoing headers .NET recorded on the
				// request message. Content headers included when a request body exists
				// (rare for crawl GETs, but captured for completeness).
				var requestHeaders = new List<(string, string)>();
				if (request is not null)
				{
					foreach (var h in request.Headers)
					{
						foreach (var v in h.Value)
						{
							requestHeaders.Add((h.Key, v));
						}
					}

					if (request.Content is not null)
					{
						foreach (var h in request.Content.Headers)
						{
							foreach (var v in h.Value)
							{
								requestHeaders.Add((h.Key, v));
							}
						}
					}
				}

				var statusLine =
					$"HTTP/{response.Version} {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();

				// Response headers: general/response headers plus entity (content)
				// headers — the latter carry Content-Type, Content-Length,
				// Content-Disposition, Last-Modified, etc.
				var responseHeaders = new List<(string, string)>();
				foreach (var h in response.Headers)
				{
					foreach (var v in h.Value)
					{
						responseHeaders.Add((h.Key, v));
					}
				}

				foreach (var h in response.Content.Headers)
				{
					foreach (var v in h.Value)
					{
						responseHeaders.Add((h.Key, v));
					}
				}

				var text = HeaderSidecar.FormatHeaderSidecar(requestLine, requestHeaders, statusLine, responseHeaders);
				File.WriteAllText(headerPath, text, Encoding.UTF8);
			}
			catch (Exception ex)
			{
				// Best-effort: a sidecar failure must never abort or corrupt a
				// download. Any failure (I/O, disposed response, encoding) is logged
				// and swallowed.
				Logger.LogWarning($"Header sidecar: could not write for " +
					$"{Path.GetFileName(bodyFileName)} — {ex.Message}");
			}
		}

		private static bool IsTransient(Exception ex)
		{
			return ex is HttpRequestException
				|| ex is IOException
				|| ex is SocketException
				|| ex is AuthenticationException;
		}

		// Clone the request at the start of every retry attempt rather than
		// reusing the original (HttpRequestMessage can only be sent once).
		private static async Task<HttpResponseMessage> SendWithRetriesAsync(HttpRequestMessage originalRequest)
		{
			if (client is null)
			{
				throw new InvalidOperationException("Crawl.Initialize() must be called before DownloadWebsiteAsync.");
			}

			HttpRequestMessage current = CloneHttpRequestMessage(originalRequest);

			for (int attempt = 0; attempt < MaxRetries; attempt++)
			{
				try
				{
					var response = await client.SendAsync(current, HttpCompletionOption.ResponseHeadersRead);

					// Treat server 5xx as transient
					if ((int)response.StatusCode >= 500 && attempt < MaxRetries - 1)
					{
						response.Dispose();
						await Task.Delay(BackoffDelay(attempt));
						current = CloneHttpRequestMessage(originalRequest);
						continue;
					}

					return response;
				}
				catch (Exception ex) when (IsTransient(ex) && attempt < MaxRetries - 1)
				{
					await Task.Delay(BackoffDelay(attempt));
					current = CloneHttpRequestMessage(originalRequest);
					continue;
				}
			}

			// Final attempt — let any exception propagate naturally
			return await client!.SendAsync(CloneHttpRequestMessage(originalRequest), HttpCompletionOption.ResponseHeadersRead);
		}

		// Use shared Random instance via _random field.
		private static TimeSpan BackoffDelay(int attempt)
		{
			var maxMillis = (int)(BaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
			var jitter = _random.Next(0, Math.Min(1000, maxMillis));
			return TimeSpan.FromMilliseconds(maxMillis + jitter);
		}

		private static HttpRequestMessage CloneHttpRequestMessage(HttpRequestMessage req)
		{
			var clone = new HttpRequestMessage(req.Method, req.RequestUri)
			{
				Version = req.Version
			};

			foreach (var header in req.Headers)
			{
				clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}

			// Note: only GET requests are used here; if a body were needed it would have to be copied too.
			return clone;
		}
	}
}
