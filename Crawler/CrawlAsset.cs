using System.Collections.Concurrent;
using Crawler.Urls;
using Crawler.Logging;
using Crawler.Security;

namespace Crawler
{
	public class CrawlAsset
	{
		private static HttpClientHandler? handler;
		private static HttpClient? client;
		private static readonly ConcurrentDictionary<string, byte> visited = new();

		// Mirror of the crawl's host allowlist. Set at launch alongside
		// Crawl's policy so DownloadAssetAsync can self-gate (see the gate at
		// the top of the method). Null only if launch was skipped — fail-closed.
		private static CrawlPolicy? crawlPolicy;

		internal static void SetCrawlPolicy(CrawlPolicy policy) => crawlPolicy = policy;

		public static void Initialize(string? proxyUrl, string? proxyUser, string? proxyPassword)
		{
			handler = new HttpClientHandler { AllowAutoRedirect = false };

			var proxy = ProxyConfig.Build(proxyUrl, proxyUser, proxyPassword, "CrawlAsset");
			if (proxy != null)
			{
				handler.Proxy = proxy;
				handler.UseProxy = true;
			}
			else
			{
				handler.UseProxy = false;
			}

			client = new HttpClient(handler);
		}

		// The "download" subfolder is appended here, so the
		// caller must pass the session root (saveDirectory), not the download subfolder.
		public static async Task DownloadAssetAsync(string assetUrl, string saveDirectory, string logFilePath, string source = "discovery")
		{
			if (client is null)
			{
				throw new InvalidOperationException("CrawlAsset.Initialize() must be called before DownloadAssetAsync.");
			}

			// [KEEP] Security boundary — self-defending fetch gate. DownloadAssetAsync
			// is a network sink with two callers: the page path (which pre-gates its
			// discoveries) and the CSS url() recursion below (which did not — a hostile
			// in-scope stylesheet can point url() at a metadata IP or an off-host
			// target). Re-admitting here makes the network call itself the enforcement
			// point: no caller reaches an off-scheme, IP-literal or off-host target, so
			// the page path's upstream gate becomes redundant defence-in-depth.
			// Fail-closed — if the policy is unset (launch skipped), nothing is fetched.
			var policy = crawlPolicy;
			if (policy is null || !Uri.TryCreate(assetUrl, UriKind.Absolute, out var assetUri))
			{
				Logger.LogDetailToFile(
					$"Asset gate: refused '{assetUrl}' (policy unset or unparseable). Not fetched.");
				return;
			}

			var admission = CrawlGate.TryAdmit(assetUrl, assetUri, policy);
			if (!admission.Admitted)
			{
				Logger.LogDetailToFile(
					$"Asset gate: refused '{assetUrl}' (reason: {admission.Reason}). Not fetched.");
				return;
			}

			assetUrl = admission.AbsoluteUrl!;

			if (!visited.TryAdd(assetUrl, 0))
			{
				return;
			}

			CrawlLogWriter.Write(assetUrl, "crawled", "info", logFilePath);

			try
			{
				var response = await client.GetAsync(assetUrl).ConfigureAwait(false);
				response.EnsureSuccessStatusCode();

				var assetContent = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
				var assetFileName = Naming.GenerateFileName(assetUrl);
				var downloadDir = Path.Combine(saveDirectory, "download");
				Directory.CreateDirectory(downloadDir);

				// [KEEP] Security boundary — asset names are derived from
				// attacker-influenceable URLs (query-bearing assets take the
				// URL-path-based naming branch), so the write is pinned under the
				// capture root here. See Crawl.ReportContainmentRefusal.
				var containment = PathContainmentCheck.Resolve(downloadDir, assetFileName);
				if (!containment.Safe)
				{
					Crawl.ReportContainmentRefusal(containment, assetUrl, downloadDir, logFilePath);
					return;
				}

				await File.WriteAllBytesAsync(containment.FullPath, assetContent).ConfigureAwait(false);

				// Capture full request+response headers next to the asset body.
				Crawl.WriteHeaderSidecar(response, assetUrl, downloadDir, assetFileName);

				CrawlLogWriter.Write(assetUrl, "savedAsset", assetFileName, logFilePath, source);

				// [KEEP] CSS files are scanned one level deep for url() references.
				// This catches fonts, background images, and PDFs referenced in stylesheets.
				// JS files are NOT scanned — minified JS yields no useful URL signal.
				// One level only: CSS referenced from CSS is not followed to prevent
				// circular dependency chains and runaway crawl depth.
				if (assetUrl.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
				{
					var cssText = System.Text.Encoding.UTF8.GetString(assetContent);
					var cssUrls = Extractor.FromCss(cssText, assetUrl);
					var cssTasks = cssUrls
						.Select(u => DownloadAssetAsync(u.Url, saveDirectory, logFilePath, source))
						.ToList();
					await Task.WhenAll(cssTasks).ConfigureAwait(false);
				}
			}
			catch (HttpRequestException httpEx)
			{
				var status = httpEx.StatusCode?.ToString() ?? "N/A";
				CrawlLogWriter.Write(assetUrl, status, httpEx.Message, logFilePath);
			}
			catch (Exception ex)
			{
				CrawlLogWriter.Write(assetUrl, "N/A", ex.Message, logFilePath);
			}
		}
	}
}
