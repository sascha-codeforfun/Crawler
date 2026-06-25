using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for CanonicalAnalyzer.Analyse (the public orchestrator, exercised
	/// end-to-end with a crawler-index file + HTML fixtures), plus the DetectIssues
	/// and BuildCanonicalMap arms the existing CanonicalAnalyzerTests don't reach:
	/// the allowed-subdomain in-scope path, the trailing-slash 404 avoidance, and
	/// relative / empty canonical href handling.
	///
	/// SYNTHETIC fixtures. In the Logger collection: Analyse / WriteLog write via
	/// the static Logger and IssueLogWriter.
	/// </summary>
	[Collection("Logger")]
	public class CanonicalAnalyzerAnalyseTests : IDisposable
	{
		private const string SiteBase = "https://example.com";

		private readonly string _temp;
		private readonly string _downloadDir;

		public CanonicalAnalyzerAnalyseTests()
		{
			_temp = Path.Combine(Path.GetTempPath(), $"canon-{Guid.NewGuid():N}");
			_downloadDir = Path.Combine(_temp, "download");
			Directory.CreateDirectory(_downloadDir);
			Logger.Initialize(Path.Combine(_temp, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_temp, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── helpers ─────────────────────────────────────────────────────────

		private void HtmlFile(string filename, string canonicalHref) =>
			File.WriteAllText(Path.Combine(_downloadDir, filename),
				$"<html><head><link rel=\"canonical\" href=\"{canonicalHref}\"/></head><body></body></html>",
				Encoding.UTF8);

		private string WriteIndex(params (string filename, string url)[] entries)
		{
			var path = Path.Combine(_temp, $"crawler-index-{Guid.NewGuid():N}.txt");
			File.WriteAllLines(path, entries.Select(e => $"{e.filename}|{e.url}"), Encoding.UTF8);
			return path;
		}

		private List<IssueTracking.IssueRecord> Analyse(string indexPath,
			IReadOnlyList<string>? allowedSubdomains = null)
		{
			var csvBase = Path.Combine(_temp, $"canon-log-{Guid.NewGuid():N}");
			return CanonicalAnalyzer.Analyse(_downloadDir, indexPath, csvBase, SiteBase,
				allowedSubdomains ?? Array.Empty<string>(), "*.html");
		}

		private static Dictionary<string, List<string>> Map(string pageUrl, params string[] canonicals) =>
			new(StringComparer.OrdinalIgnoreCase) { [pageUrl] = canonicals.ToList() };

		private static Dictionary<string, string> UrlIndex(params (string url, string filename)[] e) =>
			e.ToDictionary(x => x.url, x => x.filename, StringComparer.OrdinalIgnoreCase);

		// ── Analyse end-to-end ──────────────────────────────────────────────

		[Fact]
		public void Analyse_Canonical404_ReturnsQualityRecord()
		{
			HtmlFile("page.html", "https://example.com/missing");
			var index = WriteIndex(("page.html", "https://example.com/page"));

			var records = Analyse(index);

			var r = Assert.Single(records);
			Assert.Equal("QUALITY", r.Type);
			Assert.Equal("CANONICAL_404", r.Word);
			Assert.Equal("https://example.com/page", r.Url);
		}

		[Fact]
		public void Analyse_WritesDualLocaleCsvPair_WithHeader()
		{
			HtmlFile("page.html", "https://example.com/missing");
			var index = WriteIndex(("page.html", "https://example.com/page"));
			var csvBase = Path.Combine(_temp, $"canon-csv-{Guid.NewGuid():N}");

			CanonicalAnalyzer.Analyse(_downloadDir, index, csvBase, SiteBase,
				Array.Empty<string>(), "*.html");

			var semicolon = csvBase + IssueLogWriter.CsvSemicolonSuffix;
			var comma = csvBase + IssueLogWriter.CsvCommaSuffix;
			Assert.True(File.Exists(semicolon), "semicolon CSV not written");
			Assert.True(File.Exists(comma), "comma CSV not written");

			var header = IssueLogWriter.ParseCsvLine(File.ReadAllLines(semicolon)[0], ';');
			Assert.Equal(new[] { "PageUrl", "IssueType", "CanonicalUrl", "Detail" }, header);
		}

		[Fact]
		public void Analyse_ExternalCanonical_ExcludedFromReturnedRecords()
		{
			HtmlFile("page.html", "https://other-site.com/elsewhere");
			var index = WriteIndex(("page.html", "https://example.com/page"));

			var records = Analyse(index);

			// CANONICAL_EXTERNAL is logged but not promoted.
			Assert.DoesNotContain(records, r => r.Word == "CANONICAL_EXTERNAL");
			Assert.Empty(records);
		}

		[Fact]
		public void Analyse_MissingCrawlerIndex_ReturnsNoRecords()
		{
			HtmlFile("page.html", "https://example.com/whatever");
			var missingIndex = Path.Combine(_temp, "does-not-exist.txt");

			// No filename→url mappings → no indexed pages → empty map → no issues.
			Assert.Empty(Analyse(missingIndex));
		}

		// ── DetectIssues remaining arms ─────────────────────────────────────

		[Fact]
		public void DetectIssues_AllowedSubdomainCanonical_NotExternal()
		{
			var map = Map("https://example.com/page", "https://module.example.com/page");
			var urlIdx = UrlIndex(("https://module.example.com/page", "module.html"));

			var issues = CanonicalAnalyzer.DetectIssues(map, urlIdx, SiteBase,
				new[] { "https://module.example.com" });

			Assert.DoesNotContain(issues, i => i.IssueType == "CANONICAL_EXTERNAL");
		}

		[Fact]
		public void DetectIssues_CanonicalTrailingSlash_MatchesStrippedIndex_No404()
		{
			var map = Map("https://example.com/page", "https://example.com/target/");
			var urlIdx = UrlIndex(("https://example.com/target", "target.html")); // no slash

			var issues = CanonicalAnalyzer.DetectIssues(map, urlIdx, SiteBase);

			Assert.DoesNotContain(issues, i => i.IssueType == "CANONICAL_404");
		}

		// ── BuildCanonicalMap remaining arms ────────────────────────────────

		[Fact]
		public void BuildCanonicalMap_RelativeCanonical_ResolvedAgainstPageUrl()
		{
			HtmlFile("page.html", "/canonical-target");
			var filenameToUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["page.html"] = "https://example.com/page"
			};

			var map = CanonicalAnalyzer.BuildCanonicalMap(_downloadDir, filenameToUrl, "*.html");

			Assert.Equal("https://example.com/canonical-target",
				Assert.Single(map["https://example.com/page"]));
		}

		[Fact]
		public void BuildCanonicalMap_EmptyHref_PageNotIncluded()
		{
			HtmlFile("page.html", string.Empty); // href="" → skipped
			var filenameToUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["page.html"] = "https://example.com/page"
			};

			var map = CanonicalAnalyzer.BuildCanonicalMap(_downloadDir, filenameToUrl, "*.html");

			Assert.Empty(map);
		}
	}
}
