using System.Text;
using System.Text.RegularExpressions;

namespace Crawler
{
	// ── ResourceBloatAnalyzer ─────────────────────────────────────────────────
	//
	// Analyses downloaded pages for resource bloat — large JS/CSS files,
	// Base64-inlined assets, inlined CSS injected via textContent, and
	// JSON config blobs embedded in HTML data attributes.
	//
	// Output: 20-resource-bloat.log — one row per page with at least one issue,
	// @@@ delimited, sorted by JSCSSTotalBytes descending:
	//
	//   PageUrl @@@
	//   JSCSSFileCount @@@
	//   JSCSSTotalBytes @@@
	//   Base64AssetCount @@@
	//   Base64TotalDecodedBytes @@@
	//   Base64LargeAssets @@@
	//   InlinedCSSBytes @@@
	//   JSONBlobCount @@@
	//   JSONBlobTotalBytes @@@
	//   Issues
	//
	// Design notes:
	// - Runs outside the crawl pipeline — safe to re-run
	// - Reads log 19 for Base64 data already extracted by Base64AssetExtractor
	// - Scans raw HTML files in download/ for script/link refs and JSON blobs
	// - Scans JS/CSS file content for inlined CSS (textContent= pattern)
	// - Only pages with at least one issue appear in the log
	// ─────────────────────────────────────────────────────────────────────────

	public static class ResourceBloatAnalyzer
	{
		private const string Header =
			"PageUrl@@@JSCSSFileCount@@@JSCSSTotalBytes@@@JSFileSizeBytes@@@CSSFileSizeBytes@@@" +
			"Base64AssetCount@@@Base64TotalDecodedBytes@@@Base64LargeAssets@@@InlinedCSSBytes@@@" +
			"JSONBlobCount@@@JSONBlobTotalBytes@@@Issues";

		private static readonly Encoding Latin1 = Encoding.Latin1;

		// Oversized threshold — JS/CSS files above this are flagged OVERSIZED.
		private const int OversizedThresholdBytes = 102_400; // 100KB

		// Base64 large asset threshold — must match Base64AssetExtractor.
		private const int Base64LargeThresholdBytes = 102_400; // 100KB

		// Minimum JSON blob size to count — avoids tiny {key:val} noise.
		private const int MinJsonBlobBytes = 64;

		// ── Regex patterns ────────────────────────────────────────────────────

#pragma warning disable SYSLIB1045
		// <script src="..."> and <link href="..."> — extracts JS/CSS references
		private static readonly Regex ScriptSrcPattern = new(
			@"<script[^>]+\bsrc=[""']([^""']+\.js[^""']*)[""']",
			RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static readonly Regex LinkHrefPattern = new(
			@"<link[^>]+\bhref=[""']([^""']+\.css[^""']*)[""']",
			RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// data="{ or data='{ — JSON blob in HTML data attribute
		private static readonly Regex JsonBlobPattern = new(
			@"\bdata=[""']\s*(\{[^""']{" + MinJsonBlobBytes + @",})[""']",
			RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// textContent=`...` containing CSS markers ({ and : and ;)
		private static readonly Regex InlinedCssPattern = new(
			@"\.textContent\s*=\s*`([^`]{100,})`",
			RegexOptions.Compiled);
#pragma warning restore SYSLIB1045

		// ── Public entry point ────────────────────────────────────────────────

		public static void Analyse(
			string downloadDirectory,
			string base64AssetsLogPath,
			string logPath,
			string siteUrl,
			string configuredPageExt,
			int base64ThresholdBytes = 102_400,
			int jsThresholdBytes = 512_000,
			int cssThresholdBytes = 512_000)
		{
			if (!Directory.Exists(downloadDirectory))
			{
				Logger.LogInfo("ResourceBloatAnalyzer: download directory not found, skipping.");
				ConsoleUi.WriteStepRow("Resource bloat", "skipped", dimmed: true);
				return;
			}

			// ── Step 1: Load log 19 → Base64 data keyed by source URL ─────────
			var base64ByUrl = LoadBase64Log(base64AssetsLogPath);

			// ── Step 2: Build JS/CSS file info → url, size, inlined CSS bytes ─
			var jscssByUrl = BuildJsCssIndex(downloadDirectory);

			// ── Step 3: Process each HTML page ───────────────────────────────
			var htmlFiles = Directory.GetFiles(downloadDirectory, "*.*",
				SearchOption.TopDirectoryOnly)
				.Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
						 || f.EndsWith(".htmlx", StringComparison.OrdinalIgnoreCase)
						 || (!string.IsNullOrEmpty(configuredPageExt)
							 && f.EndsWith(configuredPageExt, StringComparison.OrdinalIgnoreCase)))
				.OrderBy(f => f)
				.ToList();

			Logger.LogInfo($"ResourceBloatAnalyzer: analysing {htmlFiles.Count} HTML page(s).");

			var rows = new List<(long jscssBytes, string row)>();

			foreach (var htmlFile in htmlFiles)
			{
				var filename = Path.GetFileName(htmlFile);
				var pageUrl = CrawlIndex.LookUpUrlForFile(filename);
				if (string.IsNullOrEmpty(pageUrl) || pageUrl == "error")
				{
					continue;
				}

				string html;
				try { html = File.ReadAllText(htmlFile, Latin1); }
				catch { continue; }

				var row = AnalysePage(pageUrl, html, jscssByUrl, base64ByUrl, siteUrl,
					base64ThresholdBytes, jsThresholdBytes, cssThresholdBytes);
				if (row.HasValue)
				{
					rows.Add(row.Value);
				}
			}

			// Sort by JSCSSTotalBytes descending — worst offenders first.
			var lines = new List<string> { Header };
			lines.AddRange(rows
				.OrderByDescending(r => r.jscssBytes)
				.Select(r => r.row));

			FileIo.WriteAllLinesWithRetry(logPath, lines, Path.GetFileName(logPath));
			Logger.LogInfo($"ResourceBloatAnalyzer: {rows.Count} page(s) with issues. " +
				$"See {Path.GetFileName(logPath)}.");
			ConsoleUi.WriteStepRow("Resource bloat", $"{rows.Count} page(s)");
		}

		// ── Page analysis ─────────────────────────────────────────────────────

		private static (long jscssBytes, string row)? AnalysePage(
			string pageUrl,
			string html,
			Dictionary<string, JsCssInfo> jscssByUrl,
			Dictionary<string, List<Base64Asset>> base64ByUrl,
			string siteUrl,
			int base64ThresholdBytes,
			int jsThresholdBytes,
			int cssThresholdBytes)
		{
			// Find all JS/CSS URLs referenced by this page.
			// Resolve relative paths to absolute using siteUrl as base.
			var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (Match m in ScriptSrcPattern.Matches(html))
			{
				refs.Add(NormaliseUrl(ResolveRef(m.Groups[1].Value, siteUrl)));
			}

			foreach (Match m in LinkHrefPattern.Matches(html))
			{
				refs.Add(NormaliseUrl(ResolveRef(m.Groups[1].Value, siteUrl)));
			}

			// Aggregate JS/CSS metrics across all referenced files.
			int jscssCount = 0;
			long jscssTotalBytes = 0;
			long jsTotalBytes = 0;
			long cssTotalBytes = 0;
			int base64Count = 0;
			long base64TotalBytes = 0;
			long inlinedCssBytes = 0;
			var largeAssets = new List<(string name, int kb)>();
			var issues = new HashSet<string>();

			foreach (var refUrl in refs)
			{
				if (!jscssByUrl.TryGetValue(refUrl, out var info))
				{
					continue;
				}

				jscssCount++;
				jscssTotalBytes += info.FileSizeBytes;
				if (info.Ext == ".js")
				{
					jsTotalBytes += info.FileSizeBytes;
				}
				else
				{
					cssTotalBytes += info.FileSizeBytes;
				}

				var threshold = info.Ext == ".js" ? jsThresholdBytes : cssThresholdBytes;
				if (info.FileSizeBytes >= threshold)
				{
					issues.Add(info.Ext == ".js" ? "OVERSIZED_JS" : "OVERSIZED_CSS");
				}

				inlinedCssBytes += info.InlinedCssBytes;
				if (info.InlinedCssBytes > 0)
				{
					issues.Add("INLINED_CSS");
				}

				// Base64 assets for this culprit from log 19.
				if (base64ByUrl.TryGetValue(refUrl, out var assets))
				{
					base64Count += assets.Count;
					base64TotalBytes += assets.Sum(a => a.DecodedBytes);

					foreach (var a in assets.Where(a => a.DecodedBytes >= base64ThresholdBytes))
					{
						largeAssets.Add((a.SavedFile, a.DecodedBytes / 1024));
						issues.Add("BASE64_LARGE");
					}
				}
			}

			// JSON blobs in the HTML itself.
			var jsonMatches = JsonBlobPattern.Matches(html);
			int jsonCount = jsonMatches.Count;
			long jsonBytes = jsonMatches.Cast<Match>()
				.Sum(m => (long)m.Groups[1].Length);
			if (jsonCount > 0)
			{
				issues.Add("JSON_BLOBS");
			}

			// Skip pages with no issues.
			if (issues.Count == 0)
			{
				return null;
			}

			// Format Base64LargeAssets column — top assets descending, then overflow.
			largeAssets.Sort((a, b) => b.kb.CompareTo(a.kb));
			string largeAssetsCol;
			if (largeAssets.Count == 0)
			{
				largeAssetsCol = "none";
			}
			else
			{
				const int MaxShown = 5;
				var shown = largeAssets.Take(MaxShown)
					.Select(a => $"{ShortenAssetName(a.name)}({a.kb}KB)");
				var rest = largeAssets.Skip(MaxShown).ToList();
				var suffix = rest.Count > 0
					? $"|+{rest.Count} more({rest.Sum(a => a.kb)}KB)"
					: string.Empty;
				largeAssetsCol = string.Join("|", shown) + suffix;
			}

			var issueCol = string.Join("|", issues.OrderBy(i => i));

			var row = $"{pageUrl}@@@{jscssCount}@@@{jscssTotalBytes}@@@" +
					  $"{jsTotalBytes}@@@{cssTotalBytes}@@@" +
					  $"{base64Count}@@@{base64TotalBytes}@@@{largeAssetsCol}@@@" +
					  $"{inlinedCssBytes}@@@{jsonCount}@@@{jsonBytes}@@@{issueCol}";

			return (jscssTotalBytes, row);
		}

		// ── Build JS/CSS index ────────────────────────────────────────────────

		private static Dictionary<string, JsCssInfo> BuildJsCssIndex(
			string downloadDirectory)
		{
			var result = new Dictionary<string, JsCssInfo>(
				StringComparer.OrdinalIgnoreCase);

			var files = Directory.GetFiles(downloadDirectory, "*.*",
				SearchOption.TopDirectoryOnly)
				.Where(f =>
				{
					var name = Path.GetFileName(f);
					return name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
						|| name.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
				})
				.ToList();

			foreach (var file in files)
			{
				var filename = Path.GetFileName(file);
				var sourceUrl = CrawlIndex.LookUpUrlForFile(filename);
				if (string.IsNullOrEmpty(sourceUrl) || sourceUrl == "error")
				{
					continue;
				}

				var fileSize = new FileInfo(file).Length;
				var ext = filename.EndsWith(".css",
					StringComparison.OrdinalIgnoreCase) ? ".css" : ".js";

				// Detect inlined CSS — textContent=`...` containing CSS markers.
				long inlinedCss = 0;
				try
				{
					var content = File.ReadAllText(file, Latin1);
					foreach (Match m in InlinedCssPattern.Matches(content))
					{
						var body = m.Groups[1].Value;
						// Verify it looks like CSS — must contain { : and ;
						if (body.Contains('{') && body.Contains(':') && body.Contains(';'))
						{
							inlinedCss += body.Length;
						}
					}
				}
				catch { /* skip unreadable files */ }

				var normalised = NormaliseUrl(sourceUrl);
				result[normalised] = new JsCssInfo
				{
					Url = sourceUrl,
					FileSizeBytes = fileSize,
					Ext = ext,
					InlinedCssBytes = inlinedCss,
				};
			}

			return result;
		}

		// ── Load log 19 ───────────────────────────────────────────────────────

		private static Dictionary<string, List<Base64Asset>> LoadBase64Log(
			string logPath)
		{
			var result = new Dictionary<string, List<Base64Asset>>(
				StringComparer.OrdinalIgnoreCase);

			if (!File.Exists(logPath))
			{
				return result;
			}

			foreach (var line in File.ReadLines(logPath, Encoding.UTF8).Skip(1))
			{
				var parts = line.Split("@@@");
				if (parts.Length < 5)
				{
					continue;
				}

				var sourceUrl = parts[0].Trim();
				var savedFile = parts[1].Trim();
				var mediaType = parts[2].Trim();
				if (!int.TryParse(parts[3].Trim(), out var encodedBytes))
				{
					continue;
				}

				if (!int.TryParse(parts[4].Trim(), out var decodedBytes))
				{
					continue;
				}

				var key = NormaliseUrl(sourceUrl);
				if (!result.TryGetValue(key, out var list))
				{
					list = [];
					result[key] = list;
				}
				list.Add(new Base64Asset
				{
					SavedFile = savedFile,
					MediaType = mediaType,
					EncodedBytes = encodedBytes,
					DecodedBytes = decodedBytes,
				});
			}

			return result;
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		// Shorten a Base64 asset filename for display in log 20.
		// Input:  245e4941...egs.min.efdeff026fbddf7623760c6560514031__2.png
		// Output: egs.min__2.png
		internal static string ShortenAssetName(string filename)
		{
			// Strip leading 64-char hex hash
			var name = filename.Length > 64
				&& filename[..64].All(c => "0123456789abcdefABCDEF".Contains(c))
				? filename[64..]
				: filename;
			// Strip intermediate content hash: .{32hex}__N.ext → __N.ext
			var m = System.Text.RegularExpressions.Regex.Match(
				name, @"^(.+?)\.([0-9a-f]{32})(__\d+\..+)$",
				RegexOptions.IgnoreCase);
			return m.Success ? m.Groups[1].Value + m.Groups[3].Value : name;
		}

		// Resolve a potentially relative URL against the site base.
		internal static string ResolveRef(string href, string siteUrl)
		{
			if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
				href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				return href;
			}

			var root = siteUrl.TrimEnd('/');
			return href.StartsWith('/') ? root + href : root + '/' + href;
		}

		// Strip query string and fragment for consistent URL matching.
		internal static string NormaliseUrl(string url)
		{
			var q = url.IndexOf('?');
			if (q >= 0)
			{
				url = url[..q];
			}

			var h = url.IndexOf('#');
			if (h >= 0)
			{
				url = url[..h];
			}

			return url.TrimEnd('/');
		}

		// ── Data structures ───────────────────────────────────────────────────

		private record JsCssInfo
		{
			public required string Url { get; init; }
			public required long FileSizeBytes { get; init; }
			public required string Ext { get; init; }
			public required long InlinedCssBytes { get; init; }
		}

		private record Base64Asset
		{
			public required string SavedFile { get; init; }
			public required string MediaType { get; init; }
			public required int EncodedBytes { get; init; }
			public required int DecodedBytes { get; init; }
		}
	}
}
