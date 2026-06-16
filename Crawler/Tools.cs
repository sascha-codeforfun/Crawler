namespace Crawler
{
	using HtmlAgilityPack;
	using System.Collections.Concurrent;
	using System.Diagnostics.CodeAnalysis;
	using System.Net;
	using System.Security.Cryptography;
	using System.Text;
	using System.Text.RegularExpressions;
	using System.Threading;
	using System.Web;
	using WeCantSpell.Hunspell;

	public static partial class Tools
	{
		public static string NormalizeRemoveIso6391Suffix(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return string.Empty;
			}

			s = s.Trim();
			if (s.Length == 0)
			{
				return string.Empty;
			}

			// If string ends with ")", check for " (xx)" or "(xx)" where xx are letters
			if (s.EndsWith(')'))
			{
				int open = s.LastIndexOf('(');
				if (open >= 0 && s.Length - open - 1 == 2) // exactly two chars inside parentheses
				{
					char c1 = s[open + 1], c2 = s[open + 2];
					if (char.IsLetter(c1) && char.IsLetter(c2))
					{
						// remove preceding space if present
						int cut = (open > 0 && s[open - 1] == ' ') ? open - 1 : open;
						return s[..cut].TrimEnd();
					}
				}
			}

			return s;
		}

		// Matches domain names like "example.com" or "sub.example.co.uk".
		// Requires letter-only TLD (min 2 chars) and a letter immediately before the dot
		// to avoid matching version numbers (1.0), decimals, or file paths.
		[GeneratedRegex(@"\b([a-zA-Z][a-zA-Z0-9-]*\.[a-zA-Z]{2,}(?:\.[a-zA-Z]{2,})?)\b", RegexOptions.Compiled)]
		private static partial Regex DomainNamePattern();

		public static string ReplaceDomainNames(string input)
		{
			return DomainNamePattern().Replace(input, string.Empty);
		}

		/// <summary>
		/// Returns the timestamp to use for the current run.
		/// When DebugDisableCrawl is false, generates a fresh timestamp.
		/// When DebugDisableCrawl is true:
		///   - "latest" (case-insensitive) → finds the most recently created subfolder
		///     under <paramref name="sessionParentDirectory"/> and uses its name.
		///   - Any other value → used as-is.
		/// </summary>
		public static string GetTimeStamp(
			bool debugDisableCrawl,
			string debugTimeStamp,
			string? sessionParentDirectory = null)
		{
			if (!debugDisableCrawl)
			{
				return $"{DateTime.Now:yyyy-MM-dd-HH-mm-ss}";
			}

			if (debugTimeStamp.Equals("latest", StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrEmpty(sessionParentDirectory)
				&& Directory.Exists(sessionParentDirectory))
			{
				// Only consider folders whose names look like a crawler timestamp
				// (yyyy-MM-dd-HH-mm-ss — 19 chars, digits and hyphens only).
				var latest = Directory
					.EnumerateDirectories(sessionParentDirectory)
					.Select(d => new DirectoryInfo(d))
					.Where(d => IsTimestampFolderName(d.Name))
					.OrderByDescending(d => d.CreationTimeUtc)
					.FirstOrDefault();

				if (latest != null)
				{
					Logger.LogInfo($"DebugTimeStamp \"latest\" resolved to: {latest.Name}");
					return latest.Name;
				}

				Logger.LogWarning(
					$"DebugTimeStamp is \"latest\" but no valid timestamp subfolders found " +
					$"in {sessionParentDirectory}. A fresh timestamp will be used — " +
					$"run a full crawl first to create a snapshot.");
				return $"{DateTime.Now:yyyy-MM-dd-HH-mm-ss}";
			}

			return debugTimeStamp;
		}

		/// <summary>
		/// Returns true when the folder name matches the crawler timestamp format
		/// yyyy-MM-dd-HH-mm-ss (e.g. "2026-04-25-14-30-00").
		/// </summary>
		public static bool IsTimestampFolderName(string name) =>
			name.Length == 19
			&& name[4] == '-' && name[7] == '-' && name[10] == '-'
			&& name[13] == '-' && name[16] == '-'
			&& name.Replace("-", "").All(char.IsDigit);

		// Use SelectNodes + loop so ALL matching nodes are removed, not just the first.
		[ExcludeFromCodeCoverage(Justification =
			"Filesystem read + HtmlAgilityPack parse + filesystem write. " +
			"XPath removal correctness is exercised end-to-end by the " +
			"normalization pipeline; unit-testing this method would require " +
			"temp file fixtures with little added confidence over what " +
			"HtmlAgilityPack already guarantees.")]
		public static void RemoveHtmlByXPath(string sourceFilePath, List<string> elementsToRemove, string destinationDirectory)
		{
			if (string.IsNullOrEmpty(sourceFilePath) || elementsToRemove == null)
			{
				return;
			}

			byte[] bytes = File.ReadAllBytes(sourceFilePath);

			// Detect encoding from BOM or meta charset (fallback to Windows-1252)
			Encoding detected = DetectEncoding.FromBytes(bytes) ?? Encoding.GetEncoding(1252);
			string html = detected.GetString(bytes);

			var doc = new HtmlDocument();
			doc.LoadHtml(html);

			foreach (var xpath in elementsToRemove)
			{
				var nodes = doc.DocumentNode.SelectNodes(xpath);
				if (nodes != null)
				{
					// Snapshot the list before iterating — the collection changes as nodes are removed.
					foreach (var node in nodes.ToList())
					{
						node.Remove();
					}
				}
			}

			string destFilePath = Path.Combine(destinationDirectory, Path.GetFileName(sourceFilePath));

			Directory.CreateDirectory(destinationDirectory);

			// Save as UTF-8 without BOM
			var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
			using var fs = File.Create(destFilePath);
			using var sw = new StreamWriter(fs, utf8NoBom);
			doc.Save(sw);
		}

		// ── Head-only HTML reading ───────────────────────────────────────────
		//
		// For analyses that only need <head> content (e.g. checking <meta name="robots">
		// for sitemap inclusion), reading the entire file is wasteful — typical HTML
		// pages are 100KB-1MB but the head is under 4KB. This helper streams just
		// enough bytes to find the closing </head> tag, capped at maxBytes for safety.
		//
		// The cap (default 16KB) is ~8× a typical CMS head and handles outliers with
		// rich JSON-LD, social meta tags, or instrumentation scripts. If the cap is
		// reached without finding </head>, the returned bytes still cover the typical
		// position of robots/canonical/title metas — the caller decides whether to
		// log or treat as truncated.

		/// <summary>
		/// Reads the bytes from the start of an HTML file up to (and including) the
		/// closing &lt;/head&gt; tag, or up to <paramref name="maxBytes"/>, whichever
		/// comes first. Match for &lt;/head&gt; is case-insensitive on the ASCII bytes.
		/// </summary>
		/// <param name="path">Path to the HTML file.</param>
		/// <param name="maxBytes">Hard cap on bytes read (default 16384).</param>
		/// <param name="reachedCap">
		/// True when <paramref name="maxBytes"/> was reached before &lt;/head&gt; was
		/// seen — the caller may want to warn since the head is unusually large or
		/// malformed.
		/// </param>
		/// <returns>Bytes from offset 0 to either &lt;/head&gt; end or the cap.</returns>
		public static byte[] ReadHeadBytes(string path, int maxBytes, out bool reachedCap)
		{
			reachedCap = false;
			if (maxBytes <= 0)
			{
				return [];
			}

			using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
				bufferSize: 4096, useAsync: false);

			// Read up to maxBytes; allocate the buffer at the size we actually need.
			var fileLen = fs.Length;
			int toRead = (int)Math.Min(fileLen, maxBytes);
			var buf = new byte[toRead];
			int read = 0;
			while (read < toRead)
			{
				int n = fs.Read(buf, read, toRead - read);
				if (n <= 0)
				{
					break;
				}

				read += n;
			}

			// Search for "</head" (case-insensitive, ASCII). The closing '>' may have
			// whitespace before it; we accept any byte that follows "</head" and look
			// for the '>' within a small lookahead. In practice </head> appears
			// directly without whitespace, but tolerating it costs nothing.
			//
			// Pattern bytes (lowercase) for case-insensitive match:
			ReadOnlySpan<byte> pat = [(byte)'<', (byte)'/', (byte)'h', (byte)'e', (byte)'a', (byte)'d'];

			int endIdx = -1;
			for (int i = 0; i + pat.Length <= read; i++)
			{
				bool ok = true;
				for (int k = 0; k < pat.Length; k++)
				{
					byte b = buf[i + k];
					// Lowercase ASCII letters: A..Z (0x41..0x5A) ORed with 0x20.
					byte lower = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b | 0x20) : b;
					if (lower != pat[k]) { ok = false; break; }
				}
				if (!ok)
				{
					continue;
				}

				// Found "</head"; advance to the '>' (allow up to 16 trailing chars
				// of whitespace/attributes, though </head> takes none in practice).
				int j = i + pat.Length;
				int limit = Math.Min(read, j + 16);
				while (j < limit && buf[j] != (byte)'>')
				{
					j++;
				}

				if (j < limit && buf[j] == (byte)'>')
				{
					endIdx = j + 1;
					break;
				}
			}

			if (endIdx > 0 && endIdx <= read)
			{
				var result = new byte[endIdx];
				Buffer.BlockCopy(buf, 0, result, 0, endIdx);
				return result;
			}

			// </head> not found within the buffered window. If the file is larger
			// than maxBytes we hit the cap; otherwise the file just has no </head>
			// (truncated HTML, fragment, etc.) and we return what we have.
			reachedCap = fileLen > maxBytes;
			if (read == buf.Length)
			{
				return buf;
			}

			var trimmed = new byte[read];
			Buffer.BlockCopy(buf, 0, trimmed, 0, read);
			return trimmed;
		}

		[ExcludeFromCodeCoverage(Justification =
			"Filesystem write of grouped log lines. Output formatting is " +
			"operator-visible in the resulting log file; logic is a " +
			"GroupBy + foreach with File.AppendAllLines.")]
		public static void Log404Sources(IEnumerable<KeyValuePair<string, string>> pagesContaining404Link, string errorSourcesLogPath)
		{
			// [KEEP] Routed through IssueLogWriter for consistency. The fields
			// are URLs — low risk of containing delimiters in well-formed input,
			// but URL parameters can technically contain unusual characters
			// and the cost of central sanitization is negligible.
			foreach (var item in pagesContaining404Link)
			{
				string errorUrl = CrawlIndex.LookUpUrlForFile(item.Value);
				// Format: {404Url}|{sourcePageUrl}
				IssueLogWriter.Append(errorSourcesLogPath, IssueLogWriter.PipeDelimiter,
					item.Key, errorUrl);
			}
		}

		// ── Crawler-index integrity & previous-snapshot analysis ─────────────
		//
		// Pure helpers extracted from Program.cs as part of the InteractiveTriage
		// refactor. The interactive prompt loops live in
		// InteractiveTriage; the orchestration dispatch lives in Program.cs.
		// What lives here are the pure file-system and log-parsing functions
		// that both sides need.

		/// <summary>
		/// Returns the list of HTML files in <paramref name="downloadDirectory"/>
		/// whose filename is not present in the crawler index. Comparison is
		/// case-insensitive on the filename only (no path component).
		/// Returns an empty list if the directory or index is missing.
		/// </summary>
		public static List<string> DetectOrphans(
			string downloadDirectory, string indexPath, string filePattern)
		{
			if (!Directory.Exists(downloadDirectory) || !File.Exists(indexPath))
			{
				return [];
			}

			// Build a set of all filenames known to the index.
			var indexedFiles = File.ReadAllLines(indexPath, Encoding.UTF8)
				.Select(l => l.Split('|')[0].Trim())
				.Where(f => f.Length > 0)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			return
			[
				.. Directory
					.EnumerateFiles(downloadDirectory, filePattern)
					.Select(Path.GetFileName)
					.Where(f => !string.IsNullOrEmpty(f) && !indexedFiles.Contains(f!))
					.Select(f => f!)
					.OrderBy(f => f)
			];
		}

		/// <summary>
		/// Renames an orphan file in place by appending <paramref name="bakExt"/>
		/// (e.g. ".html.bak"). Overwrites any existing .bak with the same name.
		/// Logs an error if the rename fails — does not throw.
		/// </summary>
		[ExcludeFromCodeCoverage(Justification =
			"Filesystem rename with collision handling. Behaviour is a File.Move " +
			"wrapper; testing would re-test File.Move.")]
		public static void RenameOrphan(string directory, string filename, string bakExt)
		{
			var src = Path.Combine(directory, filename);
			var dest = Path.Combine(directory, filename + bakExt);
			try { File.Move(src, dest, overwrite: true); }
			catch (Exception ex) { Logger.LogError($"Could not rename {filename}: {ex.Message}"); }
		}

		/// <summary>
		/// Analyses a previous snapshot's 01-crawler.log to determine if the
		/// crawl completed normally and how long it took.
		/// Returns (isComplete, duration, lastEntry) where duration is null if
		/// incomplete or if timestamps could not be parsed.
		/// Snapshot health is determined by checking only the LAST non-empty
		/// line for the word "completed". When CmsContentList.PostCrawlPass is enabled
		/// there are two "completed" markers — the post-crawl one is final and
		/// is the one this method validates against.
		/// </summary>
		public static (bool IsComplete, TimeSpan? Duration, string LastEntry) AnalysePreviousCrawl(
			string crawlerLogPath, string baseUrl)
		{
			if (!File.Exists(crawlerLogPath))
			{
				return (false, null, "");
			}

			var lines = File.ReadAllLines(crawlerLogPath, Encoding.UTF8)
				.Where(l => l.Trim().Length > 0).ToList();

			if (lines.Count == 0)
			{
				return (false, null, "");
			}

			var lastLine = lines[^1];
			bool isComplete = lastLine.Contains("completed", StringComparison.OrdinalIgnoreCase);

			if (!isComplete)
			{
				return (false, null, lastLine);
			}

			// Parse duration: first line matching base URL + "crawled" → last line "completed"
			TimeSpan? duration = null;
			var startLine = lines.FirstOrDefault(l =>
				l.Contains(baseUrl, StringComparison.OrdinalIgnoreCase) &&
				l.Contains("crawled", StringComparison.OrdinalIgnoreCase));
			if (startLine != null)
			{
				var startTs = ParseLogTimestamp(startLine);
				var endTs = ParseLogTimestamp(lastLine);
				if (startTs.HasValue && endTs.HasValue)
				{
					duration = endTs.Value - startTs.Value;
				}
			}

			return (true, duration, lastLine);
		}

		/// <summary>
		/// Parses a crawler log line timestamp ("yyyy-MM-dd HH:mm:ss | ...").
		/// Returns null if the leading token doesn't parse.
		/// </summary>
		public static DateTime? ParseLogTimestamp(string logLine)
		{
			// Crawler log format: "2026-04-25 12:07:22 | url | status | message"
			var pipeIdx = logLine.IndexOf('|');
			var raw = pipeIdx > 0 ? logLine[..pipeIdx].Trim() : logLine.Trim();
			if (DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss",
				System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.None, out var dt))
			{
				return dt;
			}

			if (DateTime.TryParse(raw, out var dt2))
			{
				return dt2;
			}

			return null;
		}

		/// <summary>
		/// Pure half of the snapshot integrity check. Returns whether the
		/// snapshot's crawler log shows a completed marker, plus the snapshot's
		/// file count and total size in MB (used by the interactive dialog to
		/// inform the delete/keep/abort decision).
		/// The interactive dialog lives in InteractiveTriage.CheckSnapshotIntegrity;
		/// silent-mode callers can use this method and warn-and-proceed.
		/// </summary>
		[ExcludeFromCodeCoverage(Justification =
			"Filesystem enumeration + completion-marker detection. Marker logic is " +
			"exercised by AnalysePreviousCrawl tests (which run at 100% coverage).")]
		public static (bool IsComplete, int FileCount, double TotalMb) CheckSnapshotComplete(
			DirectoryInfo snapshot, string baseUrl)
		{
			var crawlerLog = Path.Combine(snapshot.FullName, "01-crawler.log");
			var (isComplete, _, _) = AnalysePreviousCrawl(crawlerLog, baseUrl);
			if (isComplete)
			{
				return (true, 0, 0);
			}

			var allFiles = snapshot.Exists
				? snapshot.GetFiles("*", SearchOption.AllDirectories) : [];
			var fileCount = allFiles.Length;
			var totalMb = allFiles.Sum(f => f.Length) / 1_048_576.0;
			return (false, fileCount, totalMb);
		}

		// Guard against relative URLs which cause UriFormatException.
		public static string RemoveQueryStringElements(string url, string keys)
		{
			if (string.IsNullOrEmpty(url))
			{
				throw new ArgumentException("URL cannot be null or empty.", nameof(url));
			}

			if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			{
				// Relative URL — no query string manipulation possible, return as-is.
				Logger.LogError($"RemoveQueryStringElements: '{url}' is not an absolute URL; returning unchanged.");
				return url;
			}

			UriBuilder uriBuilder = new(uri);
			var query = HttpUtility.ParseQueryString(uriBuilder.Query);
			var keysToRemove = keys.Split('|');
			foreach (var key in keysToRemove)
			{
				query.Remove(key);
			}
			uriBuilder.Query = query.ToString();
			return uriBuilder.ToString();
		}

		[ExcludeFromCodeCoverage(Justification =
			"Filesystem enumeration + per-file regex scan. Logic is a foreach " +
			"over directory contents; no decidable behaviour beyond the I/O.")]
		public static Dictionary<string, string> SearchStringsInHtml(List<string> strings, string directory, string filePattern)
		{
			if (strings == null || strings.Count == 0)
			{
				throw new ArgumentException("The list of strings cannot be null or empty.", nameof(strings));
			}

			if (!Directory.Exists(directory))
			{
				throw new DirectoryNotFoundException($"The provided directory '{directory}' does not exist.");
			}

			// Track unfound strings so we can stop scanning once all are matched
			HashSet<string> remaining = new(strings, StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

			foreach (var filePath in Directory.EnumerateFiles(directory, filePattern))
			{
				if (remaining.Count == 0)
				{
					break;
				}

				var content = File.ReadAllText(filePath, Encoding.UTF8);
				var fileName = Path.GetFileName(filePath);

				foreach (var str in remaining.ToList())
				{
					if (content.Contains(str, StringComparison.OrdinalIgnoreCase))
					{
						result[str] = fileName;
						remaining.Remove(str);
					}
				}
			}

			return result;
		}

		/// <summary>
		/// Extracts a URL-encoded target URL from a modal/overlay query parameter.
		/// Some sites load modal content by encoding the target URL as a query parameter
		/// on a carrier page (e.g. page.html?lightbox=%2Fmodal%2Fbox1.htm).
		/// Returns the decoded target URL when the parameter is present, or the carrier
		/// URL with the query string stripped when it is not.
		/// </summary>
		public static string ExtractModalUrl(string carrierUrl, string baseUrl, string parameterName)
		{
			var uriBuilder = new UriBuilder(new Uri(carrierUrl));
			var query = HttpUtility.ParseQueryString(uriBuilder.Query);
			var encoded = query.Get(parameterName);

			if (!string.IsNullOrEmpty(encoded))
			{
				var decoded = HttpUtility.UrlDecode(encoded);
				if (!decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase))
				{
					decoded = baseUrl.TrimEnd('/') + "/" + decoded.TrimStart('/');
				}

				return RemoveQueryString(decoded);
			}

			// Parameter not found — return the carrier URL without its query string.
			return carrierUrl.Split('?')[0];
		}

		public static string GenerateFileName(string url)
		{
			var uri = new Uri(url);
			var fileName = uri.AbsolutePath;
			if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
			{
				fileName += ".htmlx";
			}

			if (uri.Query.Length == 0)
			{
				var queryHash = GetHash(uri.AbsoluteUri);
				var extension = ".htm";

				foreach (var item in uri.Segments)
				{
					if (item.Contains('.'))
					{
						extension = item;
					}
				}

				fileName = queryHash + extension;
			}

			return fileName;
		}

		public static string GetHash(string filename)
		{
			if (string.IsNullOrEmpty(filename))
			{
				throw new ArgumentException("Filename cannot be null or empty.", nameof(filename));
			}

			byte[] bytes = Encoding.UTF8.GetBytes(filename);
			byte[] hashBytes = SHA256.HashData(bytes);
			StringBuilder hashString = new();
			foreach (byte b in hashBytes)
			{
				hashString.Append(b.ToString("x2"));
			}
			return hashString.ToString();
		}

		private static readonly object logLock = new();

		[ExcludeFromCodeCoverage(Justification =
			"Locking + filesystem append. Logging output is operator-visible at " +
			"runtime; format regressions surface immediately.")]
		public static void Log(string url, string? httpStatusCode, string message, string logFile,
			string source = "")
		{
			if (string.IsNullOrEmpty(httpStatusCode))
			{
				httpStatusCode = "n/a";
			}

			// [KEEP] The source column records how the URL was discovered.
			// "discovery" = found via normal link crawling (<a href>, <img src>, etc.)
			// "list"      = found via 05-not-directly-crawlable.log post-crawl pass
			// Empty       = status/info entries (crawled, completed, errors) — not data rows
			// CreateLookupFile reads this column to populate 02-crawler-index.log column 3.
			var sourceSuffix = string.IsNullOrEmpty(source) ? string.Empty : $" | {source}";
			var logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {url} | {httpStatusCode} | {message}{sourceSuffix}";

			lock (logLock)
			{
				File.AppendAllText(logFile, logEntry + Environment.NewLine, Encoding.UTF8);
			}
		}

		/// <summary>
		/// Writes a "saved" row to the raw crawl log (00-crawler.log), carrying the
		/// response Content-Type as a trailing column so the settle phase can read it
		/// back without the live HTTP response. Format:
		///   timestamp | url | status | filename | source | contentType
		/// The Content-Type column exists ONLY in 00; settle drops it when projecting
		/// 00 → 01, so 01 keeps its historical 5-field shape and downstream readers are
		/// untouched. <paramref name="contentType"/> may be null/empty (server omitted
		/// it) — recorded as "n/a" so the column position is always present.
		/// </summary>
		public static void LogSavedRaw(
			string url, string fileName, string logFile, string source, string? contentType)
		{
			var ct = string.IsNullOrEmpty(contentType) ? "n/a" : contentType;
			var src = string.IsNullOrEmpty(source) ? "discovery" : source;
			var logEntry =
				$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {url} | saved | {fileName} | {src} | {ct}";

			lock (logLock)
			{
				File.AppendAllText(logFile, logEntry + Environment.NewLine, Encoding.UTF8);
			}
		}
		// Rewritten without double-negation for clarity.
		// A link is valid if it starts with the website URL or is a root-relative path,
		// AND is not excluded by prefix, tel:, mailto:, or the hard-coded break.html guard.
		/// <summary>
		/// Returns true when <paramref name="link"/> is safe to follow and download.
		/// A link is valid when it either:
		///   - starts with <paramref name="websiteUrl"/> (primary domain), or
		///   - starts with one of the explicitly allowed subdomain base URLs, or
		///   - is a relative path (starts with '/') — resolved against the current page.
		///
		/// [KEEP] Security boundary: absolute URLs that do not match the primary domain
		/// or an explicitly allowed subdomain are rejected. This prevents the crawler
		/// from following links to arbitrary external domains discovered in page content.
		/// Never relax this check without explicit subdomain configuration — blind
		/// subdomain following is a security risk.
		/// </summary>
		public static bool IsValidLink(
			string link,
			string websiteUrl,
			IReadOnlyList<CrawlLinkExclusion> downloadExclusions,
			IReadOnlyList<string>? allowedSubdomains = null)
		{
			// [KEEP] Reject absolute URLs that don't match the primary domain or an
			// explicitly configured subdomain. Relative paths ('/') are always allowed
			// since they are resolved against the current page's host.
			//
			// THIS IS THE SECURITY BOUNDARY. Unknown schemes (tel:, mailto:, smarty:,
			// any future scheme), foreign domains, and any other non-matching prefix
			// fail-close here. Downstream filters (downloadExclusions below) are
			// operational, not security: an empty or misconfigured downloadExclusions
			// cannot relax this gate.
			if (!link.StartsWith(websiteUrl) && !link.StartsWith('/'))
			{
				var matchesSubdomain = allowedSubdomains is { Count: > 0 }
					&& allowedSubdomains.Any(s =>
						!string.IsNullOrWhiteSpace(s)
						&& link.StartsWith(s, StringComparison.OrdinalIgnoreCase));
				if (!matchesSubdomain)
				{
					return false;
				}
			}

			// Operational exclusions — operator-curated substring filter for URLs
			// they don't want crawled (large/uninteresting sections, CMS stubs,
			// forum paths, etc.). Each enabled entry's Value is matched anywhere
			// in the link string (case-insensitive Contains). Disabled entries
			// are skipped. Empty Value on an enabled entry would reject every
			// link (Contains("") is always true) — config validator catches that
			// at startup, but the function still guards defensively at runtime.
			if (downloadExclusions != null)
			{
				foreach (var entry in downloadExclusions)
				{
					if (entry == null || !entry.Enabled || string.IsNullOrEmpty(entry.Value))
					{
						continue;
					}

					if (link.Contains(entry.Value, StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}
				}
			}

			return true;
		}

		public static string RemoveQueryString(string url)
		{
			Uri uri = new(url);
			return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}{uri.AbsolutePath}";
		}

		/// <summary>
		/// Public entry point for StripWordPrefix — used by DictionaryCheck to identify
		/// redundant user dictionary entries covered by prefix stripping.
		/// </summary>
		/// <summary>
		/// When a token ends with a hyphen (German compound word list prefix,
		/// checks the stem against the dictionary by:
		///   1. Stripping the hyphen and checking as-is (covers Flug-, Auto- etc.)
		///   2. Stripping each Fugenelement (longest first) and checking the stem
		///      (covers Kalibrierungs- → Kalibrierung with element "s")
		/// Returns true if any stem form is accepted by the dictionary.
		/// </summary>
		internal static bool CheckTrailingHyphenStem(
			string word,
			DictionaryBundle dictionary,
			IReadOnlyList<string> fugenelemente)
		{
			// Strip the trailing hyphen.
			var stem = word[..^1];

			// Check stem as-is first.
			if (dictionary.Check(stem))
			{
				return true;
			}

			// Try stripping each Fugenelement (longest first).
			foreach (var fuge in fugenelemente.OrderByDescending(f => f.Length))
			{
				if (!string.IsNullOrEmpty(fuge) && stem.EndsWith(fuge, StringComparison.OrdinalIgnoreCase))
				{
					var stripped = stem[..^fuge.Length];
					if (!string.IsNullOrEmpty(stripped) && dictionary.Check(stripped))
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>
		/// Same as CheckTrailingHyphenStem but checks against all loaded bundles.
		/// </summary>
		internal static bool CheckTrailingHyphenStemAny(
			string word,
			IEnumerable<DictionaryBundle> bundles,
			IReadOnlyList<string> fugenelemente)
		{
			var stem = word[..^1];
			if (DictionaryBundle.CheckAny(stem, bundles))
			{
				return true;
			}

			foreach (var fuge in fugenelemente.OrderByDescending(f => f.Length))
			{
				if (!string.IsNullOrEmpty(fuge) && stem.EndsWith(fuge, StringComparison.OrdinalIgnoreCase))
				{
					var stripped = stem[..^fuge.Length];
					if (!string.IsNullOrEmpty(stripped) && DictionaryBundle.CheckAny(stripped, bundles))
					{
						return true;
					}
				}
			}

			return false;
		}

		public static string? StripWordPrefixPublic(string word, IReadOnlyList<string> prefixes)
			=> StripWordPrefix(word, prefixes);

		/// <summary>
		/// Strips configured prefixes from hyphenated compound words, recursively, so
		/// multi-part brand prefixes are fully removed before dictionary lookup.
		/// Returns null when the token exactly matches a prefix (no remainder to check)
		/// which signals the caller to accept the word without a dictionary lookup.
		/// Longest prefix is always tried first regardless of config order.
		/// Examples with prefixes ["BRAND-Suite", "BRAND", "Suite"]:
		///   "BRAND-Suite-Pro"    → strips "BRAND-Suite-" → "Pro"
		///   "Suite-Installation" → strips "Suite-"        → "Installation"
		///   "BRAND-Suite"        → exact prefix match     → null (accept outright)
		///   "normalword"         → no match               → "normalword"
		/// </summary>
		internal static string? StripWordPrefix(string word, IReadOnlyList<string> prefixes)
		{
			if (prefixes.Count == 0 || !word.Contains('-'))
			{
				return word;
			}

			// Sort longest prefix first so compound entries like "BRAND-Suite" are
			// tried before their shorter components "BRAND" and "Suite".
			var sorted = prefixes
				.Where(p => !string.IsNullOrEmpty(p))
				.OrderByDescending(p => p.Length);

			foreach (var prefix in sorted)
			{
				// Exact match — the token IS the prefix, nothing left to spell-check.
				if (word.Equals(prefix, StringComparison.OrdinalIgnoreCase))
				{
					return null;
				}

				var candidate = prefix + "-";
				if (word.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
				{
					var remainder = word[candidate.Length..];
					if (string.IsNullOrEmpty(remainder))
					{
						return null; // prefix with trailing hyphen but nothing after
					}

					// Recurse — the remainder may itself start with another prefix.
					return StripWordPrefix(remainder, prefixes);
				}
			}

			return word;
		}

		[ExcludeFromCodeCoverage(Justification =
			"Same shape as CheckSpelling — Hunspell-coupled multi-language check.")]
		public static IEnumerable<string> CheckSpellingAllDictionaries(
			string text,
			Dictionary<string, DictionaryBundle> allBundles,
			IReadOnlyList<string>? prefixesToStrip = null,
			IReadOnlyList<string>? fugenelemente = null)
		{
			var prefixes = prefixesToStrip ?? [];
			var fugen = fugenelemente ?? [];
			List<string> errors = [];

			foreach (var word in RegExPatterns.TokenizeText(text))
			{
				var trimmedWord = word?.Trim();
				if (string.IsNullOrEmpty(trimmedWord) || !RegExPatterns.IdentifyWord(trimmedWord))
				{
					continue;
				}

				var wordToCheck = StripWordPrefix(trimmedWord, prefixes);
				if (wordToCheck == null)
				{
					continue;
				}

				wordToCheck = wordToCheck.Replace("\u00AD", ""); // strip soft hyphen from stale normalized text
				if (string.IsNullOrEmpty(wordToCheck))
				{
					continue;
				}

				// Trailing-hyphen compound prefix — check both explicit trailing hyphen
				// and cases where the tokenizer dropped it at a word boundary.
				bool isTrailingHyphenStem = wordToCheck.EndsWith('-')
					|| text.Contains(wordToCheck + "-", StringComparison.Ordinal);

				if (isTrailingHyphenStem)
				{
					var stem = wordToCheck.EndsWith('-') ? wordToCheck : wordToCheck + "-";
					if (CheckTrailingHyphenStemAny(stem, allBundles.Values, fugen))
					{
						continue;
					}
				}

				if (!DictionaryBundle.CheckAny(wordToCheck, allBundles.Values))
				{
					errors.Add(wordToCheck);
				}
			}

			return errors.Distinct(StringComparer.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Spell-check <paramref name="text"/> against <paramref name="dictionary"/> for
		/// <paramref name="language"/>.
		/// Words starting with a configured prefix followed by a hyphen have the prefix
		/// stripped before lookup — the remainder is checked and reported instead of the
		/// full token, so errors are immediately actionable without manual prefix removal.
		/// </summary>
		[ExcludeFromCodeCoverage(Justification =
			"Coupled to Hunspell WordList.Check across multi-tier dictionary bundles. " +
			"Testing requires real .aff/.dic fixtures; exercises Hunspell library " +
			"behaviour rather than Tools logic.")]
		public static IEnumerable<KeyValuePair<string, List<string>>> CheckSpelling(
			string text,
			DictionaryBundle dictionary,
			string language,
			IReadOnlyList<string>? prefixesToStrip = null,
			IReadOnlyList<string>? fugenelemente = null)
		{
			var prefixes = prefixesToStrip ?? [];
			var fugen = fugenelemente ?? [];
			Dictionary<string, List<string>> errors = [];

			foreach (var word in RegExPatterns.TokenizeText(text))
			{
				var trimmedWord = word?.Trim();
				if (string.IsNullOrEmpty(trimmedWord) || !RegExPatterns.IdentifyWord(trimmedWord))
				{
					continue;
				}

				var wordToCheck = StripWordPrefix(trimmedWord, prefixes);
				if (wordToCheck == null)
				{
					continue;
				}

				wordToCheck = wordToCheck.Replace("\u00AD", ""); // strip soft hyphen from stale normalized text
				if (string.IsNullOrEmpty(wordToCheck))
				{
					continue;
				}
				// Trailing-hyphen compound prefix — two cases:
				// 1. Token ends with "-" (captured by tokenizer in some patterns)
				// 2. Token appears as "Word-," or "Word- " in source text — the tokenizer
				//    drops the trailing hyphen at a word boundary, so we check the source.
				bool isTrailingHyphenStem = wordToCheck.EndsWith('-')
					|| text.Contains(wordToCheck + "-", StringComparison.Ordinal);

				if (isTrailingHyphenStem)
				{
					if (CheckTrailingHyphenStem(
						wordToCheck.EndsWith('-') ? wordToCheck : wordToCheck + "-",
						dictionary, fugen))
					{
						continue;
					}
				}
				else
				{
					if (dictionary.Check(wordToCheck))
					{
						continue;
					}
				}

				var reportWord = wordToCheck;
				if (!errors.TryGetValue(reportWord, out var languages))
				{
					languages = [];
					errors[reportWord] = languages;
				}

				if (!languages.Contains(language))
				{
					languages.Add(language);
				}
			}

			return errors;
		}

		/// <summary>
		/// Applies normalization replacements to raw HTML.
		/// Global replacements (empty Pages list) apply to all pages.
		/// Page-scoped replacements (non-empty Pages) only apply when the current
		/// page URL matches one of the listed patterns (case-insensitive substring).
		/// </summary>
		public static string ReplaceHtmlEntities(
			string html,
			List<ReplacementItem> replacements,
			string pageUrl = "")
		{
			if (string.IsNullOrEmpty(html))
			{
				return html;
			}

			ArgumentNullException.ThrowIfNull(replacements);
			var result = new StringBuilder(html.Length);
			result.Append(html);

			foreach (var replacement in replacements)
			{
				// Page-scoped: skip if URL doesn't match any listed pattern.
				if (replacement.Pages.Count > 0)
				{
					if (string.IsNullOrEmpty(pageUrl))
					{
						continue;
					}

					if (!replacement.Pages.Any(p =>
						pageUrl.Contains(p, StringComparison.OrdinalIgnoreCase)))
					{
						continue;
					}
				}

				string normalizedValue = replacement.Value.Normalize(NormalizationForm.FormD);
				string normalizedReplacement = replacement.Replacement;
				result.Replace(normalizedValue, normalizedReplacement);
			}

			return result.ToString();
		}

		[GeneratedRegex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled)]
		private static partial Regex EmailPattern();

		/// <summary>
		/// Strips inline formatting tags from raw HTML before DOM parsing.
		/// Inline tags (b, strong, i, em, u, s, mark, small, sub, sup, span used purely
		/// for formatting) split text nodes at their boundaries, causing "I" and "dentifier"
		/// to tokenize separately from "&lt;b&gt;I&lt;/b&gt;dentifier".
		/// Replacing them with empty string merges the adjacent text so the DOM sees
		/// "Identifier" as a single token.
		/// Attributes are stripped with the tag since they carry no spell-check relevance.
		/// </summary>
		public static string StripInlineFormattingTags(string html)
		{
			// Match opening and closing tags for known inline formatting elements.
			// Uses a simple regex rather than DOM parsing since we're pre-parsing.
			return InlineTagPattern().Replace(html, "");
		}

		[System.Text.RegularExpressions.GeneratedRegex(
			@"</?(?:b|strong|i|em|u|s|mark|small|sub|sup)(?:\s[^>]*)?>",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase |
			System.Text.RegularExpressions.RegexOptions.Compiled)]
		private static partial System.Text.RegularExpressions.Regex InlineTagPattern();

		public static string RemoveEmailAddresses(string text)
		{
			return EmailPattern().Replace(text, string.Empty);
		}

		// LoadDictionary now returns a fully populated DictionaryBundle instead of
		// a raw WordList. SharedSite and SharedUser are populated from the plain word files so
		// DictionaryBundle.Check() works correctly for all three tiers (system, site, user).
		[ExcludeFromCodeCoverage(Justification =
			"Filesystem read + Hunspell WordList construction. Testing requires real " +
			"Hunspell dictionary fixtures and primarily exercises Hunspell, not Tools.")]
		public static DictionaryBundle LoadDictionary(
			string dictionaryFileName,
			string affFileName,
			string customDictionaryFile,
			string customSiteDictionaryFile,
			bool silent = false)
		{
			var systemWordList = WordList.CreateFromFiles(dictionaryFileName, affFileName);
			var bundle = new DictionaryBundle { System = systemWordList };

			if (!string.IsNullOrEmpty(customDictionaryFile) && File.Exists(customDictionaryFile))
			{
				CharacterValidator.ValidateDictionaryFileHalt(customDictionaryFile, silent);

				foreach (var raw in File.ReadLines(customDictionaryFile, Encoding.UTF8))
				{
					if (string.IsNullOrWhiteSpace(raw))
					{
						continue;
					}

					var line = raw.Trim().TrimStart('!'); // strip pin marker before spell-check
					if (line.Contains('/'))
					{
						continue;
					}

					bundle.SharedUser.Add(line);
				}
			}

			if (!string.IsNullOrEmpty(customSiteDictionaryFile) && File.Exists(customSiteDictionaryFile))
			{
				CharacterValidator.ValidateDictionaryFileHalt(customSiteDictionaryFile, silent);

				foreach (var raw in File.ReadLines(customSiteDictionaryFile, Encoding.UTF8))
				{
					if (string.IsNullOrWhiteSpace(raw))
					{
						continue;
					}

					var line = raw.Trim().TrimStart('!'); // strip pin marker before spell-check
					if (line.Contains('/'))
					{
						continue;
					}

					bundle.SharedSite.Add(line);
				}
			}

			return bundle;
		}

		// Load plain-word sets (shared site/user). Skips flagged lines that contain '/'
		[ExcludeFromCodeCoverage(Justification =
			"Filesystem read of word-per-line files into a HashSet. Logic is " +
			"File.ReadAllLines + comment-stripping; no decidable edge cases.")]
		public static HashSet<string> LoadPlainWordSet(params string[] paths)
		{
			HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);

			foreach (var path in paths.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)))
			{
				foreach (var raw in File.ReadLines(path, Encoding.UTF8))
				{
					if (string.IsNullOrWhiteSpace(raw))
					{
						continue;
					}

					var line = raw.Trim();

					// Skip hunspell flagged entries like "GESCHAEFT/AB" for now
					if (line.Contains('/'))
					{
						continue;
					}

					set.Add(line);
				}
			}

			return set;
		}

		// ── Header sidecar ────────────────────────────────────────────
		//
		// Every download writes a "<hash>.header" sibling next to its body in
		// download/, capturing the full request+response headers verbatim. This is
		// ground truth for offline replay: when a later design needs a header we did
		// not previously treat as a signal (Content-Disposition, Content-Length, …),
		// it is read from disk rather than re-crawled. The stem matches the body's
		// (both via GenerateFileName) so the pairing survives whatever extension the
		// settle phase lands the body on.


		/// <summary>Number of leading bytes read from a saved file for HTML sniffing.</summary>
		public const int HtmlSniffByteCount = 1024;

		/// <summary>The provisional extension a download is saved under before settle.</summary>
		public const string UnverifiedExtension = ".unverified";

	}
}
