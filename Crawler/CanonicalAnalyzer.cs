using HtmlAgilityPack;
using System.Text;

namespace Crawler
{
	// ── CanonicalAnalyzer ─────────────────────────────────────────────────────
	//
	// Reads all downloaded HTML files, extracts <link rel="canonical"> tags,
	// and detects the following issues:
	//
	//   CANONICAL_404      — canonical URL points to a page not in the crawler
	//                        index (not downloaded = not found on this site).
	//                        Auto-promoted to IssueTracking as "new".
	//
	//   CANONICAL_CHAIN    — canonical chain A→B→C (B also has a canonical).
	//                        Search engines only follow one hop — the chain is
	//                        ineffective. Auto-promoted to IssueTracking as "new".
	//
	//   CANONICAL_CONFLICT — page has multiple <link rel="canonical"> tags.
	//                        Only the first is honoured by search engines — the
	//                        rest are ignored. Auto-promoted to IssueTracking.
	//
	//   CANONICAL_EXTERNAL — canonical URL host differs from the crawled site's
	//                        host (may be intentional syndication). Logged only,
	//                        NOT auto-promoted to IssueTracking.
	//
	// Output: 16-canonical-issues_semicolon.csv and _comma.csv (timestamped folder,
	// dual-locale CSV pair, like the other human-facing analysis logs). IssueTracking
	// entries returned for the caller to merge.
	//
	// Fields are RFC 4180-quoted (IssueLogWriter.WriteCsvPair): a delimiter or quote
	// inside a URL or Detail value is preserved verbatim rather than stripped.
	// ─────────────────────────────────────────────────────────────────────────

	public static class CanonicalAnalyzer
	{
		// ── Public entry point ────────────────────────────────────────────────

		/// <summary>
		/// Analyses canonical links across all downloaded HTML files.
		/// Writes the 16-canonical-issues dual-locale CSV pair and returns IssueRecords for
		/// CANONICAL_404, CANONICAL_CHAIN, and CANONICAL_CONFLICT to be
		/// merged into IssueTracking.log by the caller.
		/// CANONICAL_EXTERNAL issues are logged but not returned.
		/// </summary>
		public static List<IssueTracking.IssueRecord> Analyse(
			string downloadDirectory,
			string crawlerIndexPath,
			string canonicalCsvBasePath,
			string siteBaseUrl,
			IReadOnlyList<string> allowedSubdomainUrls,
			string filePattern)
		{
			// Build filename→url and url→filename maps from the crawler index.
			// Pipe delimiter — RFC 3986 defines '|' as not valid in URLs.
			var filenameToUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var urlToFilename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			if (File.Exists(crawlerIndexPath))
			{
				foreach (var line in File.ReadAllLines(crawlerIndexPath, Encoding.UTF8))
				{
					if (string.IsNullOrWhiteSpace(line))
					{
						continue;
					}

					var parts = line.Split('|', StringSplitOptions.TrimEntries);
					if (parts.Length < 2)
					{
						continue;
					}
					var url = parts[1];

					filenameToUrl[parts[0]] = url;
					urlToFilename[url] = parts[0];
				}
			}

			// Build the canonical map: pageUrl → list of declared canonical URLs.
			var canonicalMap = BuildCanonicalMap(
				downloadDirectory, filenameToUrl, filePattern);

			// Detect issues.
			var issues = DetectIssues(canonicalMap, urlToFilename, siteBaseUrl, allowedSubdomainUrls);

			// Write log.
			WriteLog(canonicalCsvBasePath, issues);
			Logger.LogInfo($"Canonical analysis: {issues.Count} issue(s) found. " +
				$"See {Path.GetFileName(canonicalCsvBasePath)}{IssueLogWriter.CsvSemicolonSuffix} / " +
				$"{Path.GetFileName(canonicalCsvBasePath)}{IssueLogWriter.CsvCommaSuffix}.");

			// Return only auto-promotable issues (not CANONICAL_EXTERNAL).
			return issues
				.Where(i => i.IssueType != "CANONICAL_EXTERNAL")
				.Select(i => new IssueTracking.IssueRecord
				{
					Type = "QUALITY",
					Url = i.PageUrl,
					Word = i.IssueType,
					SourceLabel = i.CanonicalUrl,
					Excerpt = i.Detail,
				})
				.ToList();
		}

		// ── Issue model ───────────────────────────────────────────────────────

		public record CanonicalIssue(
			string PageUrl,
			string IssueType,
			string CanonicalUrl,
			string Detail);

		// ── Canonical map builder ─────────────────────────────────────────────

		/// <summary>
		/// Reads all HTML files in the download directory and extracts
		/// canonical URLs. Returns pageUrl → list of canonical URLs declared.
		/// </summary>
		internal static Dictionary<string, List<string>> BuildCanonicalMap(
			string downloadDirectory,
			IReadOnlyDictionary<string, string> filenameToUrl,
			string filePattern)
		{
			var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

			if (!Directory.Exists(downloadDirectory))
			{
				return result;
			}

			var files = Directory.GetFiles(downloadDirectory, filePattern,
				SearchOption.TopDirectoryOnly);

			foreach (var file in files)
			{
				var filename = Path.GetFileName(file);
				if (!filenameToUrl.TryGetValue(filename, out var pageUrl))
				{
					continue;
				}

				var doc = new HtmlDocument();
				try { doc.Load(file, Encoding.UTF8); }
				catch { continue; }

				var canonicalNodes = doc.DocumentNode
					.SelectNodes("//link[@rel='canonical'][@href]");
				if (canonicalNodes == null)
				{
					continue;
				}

				List<string> canonicals = [];
				foreach (var node in canonicalNodes)
				{
					var href = node.GetAttributeValue("href", string.Empty).Trim();
					if (string.IsNullOrEmpty(href))
					{
						continue;
					}

					// Resolve relative URLs against the page URL.
					if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
					{
						try
						{
							href = new Uri(new Uri(pageUrl), href).ToString();
						}
						catch { continue; }
					}

					canonicals.Add(href);
				}

				if (canonicals.Count > 0)
				{
					result[pageUrl] = canonicals;
				}
			}

			return result;
		}

		// ── Issue detection ───────────────────────────────────────────────────

		/// <summary>
		/// Detects canonical issues from the canonical map.
		/// Internal so it can be unit-tested without file I/O.
		/// </summary>
		internal static List<CanonicalIssue> DetectIssues(
			IReadOnlyDictionary<string, List<string>> canonicalMap,
			IReadOnlyDictionary<string, string> urlToFilename,
			string siteBaseUrl,
			IReadOnlyList<string>? allowedSubdomainUrls = null)
		{
			List<CanonicalIssue> issues = [];
			var siteHost = GetHost(siteBaseUrl);

			// In-scope hosts: the primary site plus any operator-declared allowed subdomains
			// (SiteConfig.UrlSubdomainsAllowed). A canonical pointing at any of these is in
			// scope; only a host outside the whole set is CANONICAL_EXTERNAL. (Before this,
			// the check compared against siteHost alone and wrongly flagged allowed subdomains
			// — e.g. a module.* page self-canonicalising — as external.)
			var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (!string.IsNullOrEmpty(siteHost))
			{
				allowedHosts.Add(siteHost);
			}
			if (allowedSubdomainUrls != null)
			{
				foreach (var allowed in allowedSubdomainUrls)
				{
					var allowedHost = GetHost(allowed);
					if (!string.IsNullOrEmpty(allowedHost))
					{
						allowedHosts.Add(allowedHost);
					}
				}
			}

			foreach (var (pageUrl, canonicals) in canonicalMap)
			{
				// CANONICAL_CONFLICT — multiple canonical tags on one page.
				if (canonicals.Count > 1)
				{
					issues.Add(new CanonicalIssue(
						pageUrl,
						"CANONICAL_CONFLICT",
						canonicals[0],
						$"{canonicals.Count} canonical tags found — only first honoured: {string.Join(", ", canonicals)}"));
				}

				// Analyse the first (effective) canonical.
				var canonical = canonicals[0];
				var canonicalHost = GetHost(canonical);

				// CANONICAL_EXTERNAL — host outside the in-scope set (primary + allowed subdomains).
				if (allowedHosts.Count > 0
					&& !allowedHosts.Contains(canonicalHost))
				{
					issues.Add(new CanonicalIssue(
						pageUrl,
						"CANONICAL_EXTERNAL",
						canonical,
						$"Canonical points outside allowed crawl scope ({string.Join(", ", allowedHosts)}) to: {canonicalHost}"));
					continue;
				}

				// CANONICAL_404 — canonical URL not in crawler index.
				if (!urlToFilename.ContainsKey(canonical)
					&& !urlToFilename.ContainsKey(StripTrailingSlash(canonical)))
				{
					issues.Add(new CanonicalIssue(
						pageUrl,
						"CANONICAL_404",
						canonical,
						$"Canonical target not found in crawler index: {canonical}"));
					continue;
				}

				// CANONICAL_CHAIN — canonical target itself has a canonical.
				if (canonicalMap.TryGetValue(canonical, out var targetCanonicals)
					&& targetCanonicals.Count > 0
					&& !targetCanonicals[0].Equals(canonical, StringComparison.OrdinalIgnoreCase))
				{
					issues.Add(new CanonicalIssue(
						pageUrl,
						"CANONICAL_CHAIN",
						canonical,
						$"Chain: {pageUrl} → {canonical} → {targetCanonicals[0]}"));
				}
			}

			return issues;
		}

		// ── Log writer ────────────────────────────────────────────────────────

		private static void WriteLog(string csvBasePath, List<CanonicalIssue> issues)
		{
			// [KEEP] Routed through IssueLogWriter — Detail can include canonical
			// URL fragments and crawled page metadata; sanitization protects
			// against pipe / CR / LF / control chars in those values.
			var records = new List<string?[]>
			{
				new string?[] { "PageUrl", "IssueType", "CanonicalUrl", "Detail" }
			};
			records.AddRange(issues
				.OrderBy(i => i.IssueType)
				.ThenBy(i => i.PageUrl)
				.Select(i => new string?[] { i.PageUrl, i.IssueType, i.CanonicalUrl, i.Detail }));
			IssueLogWriter.WriteCsvPair(csvBasePath, records);
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		private static string GetHost(string url)
		{
			try { return new Uri(url).Host.ToLowerInvariant(); }
			catch { return string.Empty; }
		}

		private static string StripTrailingSlash(string url) =>
			url.TrimEnd('/');
	}
}
