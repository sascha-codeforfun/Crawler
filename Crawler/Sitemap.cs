using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Crawler
{
	// ── Sitemap ──────────────────────────────────────────────────────────────
	//
	// Generates an XML sitemap from the downloaded HTML corpus. Each page is
	// included unless one of the following applies:
	//   1. Its <meta name="robots"> declares "noindex".
	//   2. Its crawler-log source is "list" (post-crawl pass — not a normal
	//      discovery, never appears in sitemaps regardless of robots).
	//   3. Its URL matches a configured exclusion AND no forced-inclusion override.
	//
	// Performance notes:
	//   - The previous implementation read every byte of every HTML file and built
	//     a full HtmlAgilityPack DOM just to read one tag. With ~1900 pages totalling
	//     ~580 MB on a typical site, this took 86 seconds — 31% of total run time.
	//   - This implementation reads only the head (≤16KB per file, stops at </head>)
	//     and uses a compiled regex to extract the robots meta content. DOM allocation
	//     is eliminated. Files are processed in parallel.
	// ─────────────────────────────────────────────────────────────────────────

	public partial class Sitemap
	{
		// Maximum bytes per file to read in search of <meta name="robots">.
		// A typical CMS head is well under 4KB; 16KB is ~8× safety margin for
		// pages with rich JSON-LD, social meta tags, or instrumentation scripts.
		private const int HeadReadCapBytes = 16 * 1024;

		// Matches <meta name="robots" content="..."> with attributes in either order,
		// single or double quotes, and arbitrary whitespace. Captures the content value.
		// Case-insensitive — HTML attribute names are case-insensitive.
		[GeneratedRegex(
			"<meta\\b[^>]*?\\bname\\s*=\\s*[\"']robots[\"'][^>]*?\\bcontent\\s*=\\s*[\"'](?<content>[^\"']*)[\"']",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
		private static partial Regex RobotsMetaNameFirst();

		// Same but with attribute order reversed: content="..." appearing before name="robots".
		[GeneratedRegex(
			"<meta\\b[^>]*?\\bcontent\\s*=\\s*[\"'](?<content>[^\"']*)[\"'][^>]*?\\bname\\s*=\\s*[\"']robots[\"']",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
		private static partial Regex RobotsMetaContentFirst();

		public static void Generate(
			string baseDirectory,
			string sitemapFilePath,
			List<string> exclusions,
			List<string> forcedInclusions,
			string filePattern,
			int degreeOfParallelism = 0)
		{
			string sitemap = GenerateSitemap(baseDirectory, exclusions, forcedInclusions, filePattern, degreeOfParallelism);
			FileIo.WriteAllTextWithRetry(sitemapFilePath, sitemap, Path.GetFileName(sitemapFilePath));
		}

		public static string GenerateSitemap(
			string directoryPath,
			List<string> exclusions,
			List<string> forcedInclusions,
			string filePattern,
			int degreeOfParallelism = 0)
		{
			var htmlFiles = Directory.GetFiles(directoryPath, filePattern);
			var validUrls = new ConcurrentBag<string>();
			int truncatedHeads = 0;

			var parallelOptions = new ParallelOptions
			{
				MaxDegreeOfParallelism = degreeOfParallelism <= 0
					? Environment.ProcessorCount
					: degreeOfParallelism
			};

			Parallel.ForEach(htmlFiles, parallelOptions, file =>
			{
				// 1. Read only the head (or up to the cap if </head> is absent/late).
				var headBytes = Tools.ReadHeadBytes(file, HeadReadCapBytes, out bool reachedCap);
				if (reachedCap)
				{
					Interlocked.Increment(ref truncatedHeads);
				}

				// 2. Detect encoding from the head bytes (BOM + meta-charset).
				var encoding = DetectEncoding.FromBytes(headBytes);
				var headText = encoding.GetString(headBytes);

				// 3. Check <meta name="robots"> content for "noindex".
				// Tolerate both attribute orders; first match wins.
				var match = RobotsMetaNameFirst().Match(headText);
				if (!match.Success)
				{
					match = RobotsMetaContentFirst().Match(headText);
				}

				if (match.Success)
				{
					var content = match.Groups["content"].Value.ToLowerInvariant();
					if (ContainsToken(content, "noindex"))
					{
						return;
					}
				}

				var fileName = Path.GetFileName(file);

				// 4. Safeguard — pages from the CMS list pass (not reachable via normal
				// crawl) must never enter the sitemap regardless of their robots directive.
				var source = CrawlIndex.LookUpSourceForFile(fileName);
				if (source.Equals("list", StringComparison.OrdinalIgnoreCase))
				{
					return;
				}

				// 5. Apply URL exclusion + forced-inclusion rules.
				var relativeUrl = CrawlIndex.LookUpUrlForFile(fileName);
				if (exclusions.Any(page => relativeUrl.Contains(page)))
				{
					if (!forcedInclusions.Any(page => relativeUrl.Contains(page)))
					{
						return;
					}
				}

				validUrls.Add($"<url><loc>{relativeUrl}</loc></url>");
			});

			if (truncatedHeads > 0)
			{
				Logger.LogWarning(
					$"Sitemap: {truncatedHeads} file(s) had a </head> not found within {HeadReadCapBytes / 1024}KB. " +
					$"Their robots meta may have been missed if it appears beyond that point.");
			}

			// Sort for stable output — sitemap.xml diffs cleanly across runs regardless
			// of file-system enumeration order or parallel completion order.
			var sortedUrls = validUrls.OrderBy(s => s, StringComparer.Ordinal).ToList();

			return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+ "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\" "
				+ "xmlns:image=\"http://www.google.com/schemas/sitemap-image/1.1\" "
				+ "xmlns:video=\"http://www.google.com/schemas/sitemap-video/1.1\">"
				+ string.Join(Environment.NewLine, sortedUrls)
				+ "</urlset>";
		}

		// Robots meta content is a comma-separated directive list (e.g. "noindex,follow").
		// Match whole-token only so "noindex" is not falsely detected as a substring
		// of an unrelated directive (no current example, but defensive).
		private static bool ContainsToken(string content, string token)
		{
			foreach (var part in content.Split(','))
			{
				if (part.Trim().Equals(token, StringComparison.Ordinal))
				{
					return true;
				}
			}
			return false;
		}
	}
}

