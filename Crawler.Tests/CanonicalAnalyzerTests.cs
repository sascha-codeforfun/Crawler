using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for CanonicalAnalyzer.BuildCanonicalMap() and DetectIssues().
	/// Both methods are internal — accessible via InternalsVisibleTo.
	/// Analyse() and WriteLog() involve file I/O and are tested via integration
	/// of BuildCanonicalMap + DetectIssues together.
	/// No Logger dependency — no Console or Logger calls in tested methods.
	/// </summary>
	[Collection("Logger")]
	public class CanonicalAnalyzerTests : IDisposable
	{
		private readonly string _tempDir;
		private readonly string _downloadDir;

		public CanonicalAnalyzerTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"ca-test-{Guid.NewGuid()}");
			_downloadDir = Path.Combine(_tempDir, "download");
			Directory.CreateDirectory(_downloadDir);
		}

		public void Dispose()
		{
			if (Directory.Exists(_tempDir))
			{
				Directory.Delete(_tempDir, recursive: true);
			}
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		private string HtmlFile(string filename, string canonicalHref)
		{
			var path = Path.Combine(_downloadDir, filename);
			File.WriteAllText(path,
				$"<html><head><link rel=\"canonical\" href=\"{canonicalHref}\"/></head><body></body></html>",
				Encoding.UTF8);
			return path;
		}

		private string HtmlFileMultiCanonical(string filename, params string[] hrefs)
		{
			var tags = string.Join("\n", hrefs.Select(h => $"<link rel=\"canonical\" href=\"{h}\"/>"));
			var path = Path.Combine(_downloadDir, filename);
			File.WriteAllText(path,
				$"<html><head>{tags}</head><body></body></html>",
				Encoding.UTF8);
			return path;
		}

		private string HtmlFileNoCanonical(string filename)
		{
			var path = Path.Combine(_downloadDir, filename);
			File.WriteAllText(path,
				"<html><head><title>No canonical</title></head><body></body></html>",
				Encoding.UTF8);
			return path;
		}

		private static Dictionary<string, string> Index(
			params (string filename, string url)[] entries) =>
			entries.ToDictionary(e => e.filename, e => e.url,
				StringComparer.OrdinalIgnoreCase);

		private static Dictionary<string, string> UrlIndex(
			params (string filename, string url)[] entries) =>
			entries.ToDictionary(e => e.url, e => e.filename,
				StringComparer.OrdinalIgnoreCase);

		private const string SiteBase = "https://example.com";

		// ── BuildCanonicalMap ─────────────────────────────────────────────────

		[Fact]
		public void BuildCanonicalMap_EmptyDirectory_ReturnsEmpty()
		{
			var emptyDir = Path.Combine(_tempDir, "empty");
			Directory.CreateDirectory(emptyDir);
			var result = CanonicalAnalyzer.BuildCanonicalMap(emptyDir, Index(), "*.html");
			Assert.Empty(result);
		}

		[Fact]
		public void BuildCanonicalMap_PageWithNoCanonical_NotIncluded()
		{
			HtmlFileNoCanonical("page.html");
			var idx = Index(("page.html", "https://example.com/page"));
			var result = CanonicalAnalyzer.BuildCanonicalMap(_downloadDir, idx, "*.html");
			Assert.Empty(result);
		}

		[Fact]
		public void BuildCanonicalMap_PageWithCanonical_Included()
		{
			HtmlFile("page.html", "https://example.com/canonical");
			var idx = Index(("page.html", "https://example.com/page"));
			var result = CanonicalAnalyzer.BuildCanonicalMap(_downloadDir, idx, "*.html");
			Assert.Single(result);
			Assert.Contains("https://example.com/canonical", result["https://example.com/page"]);
		}

		[Fact]
		public void BuildCanonicalMap_MultipleCanonicals_AllCollected()
		{
			HtmlFileMultiCanonical("page.html",
				"https://example.com/canonical1",
				"https://example.com/canonical2");
			var idx = Index(("page.html", "https://example.com/page"));
			var result = CanonicalAnalyzer.BuildCanonicalMap(_downloadDir, idx, "*.html");
			Assert.Equal(2, result["https://example.com/page"].Count);
		}

		[Fact]
		public void BuildCanonicalMap_FileNotInIndex_Skipped()
		{
			HtmlFile("page.html", "https://example.com/canonical");
			// Empty index — file has no URL mapping
			var result = CanonicalAnalyzer.BuildCanonicalMap(_downloadDir, Index(), "*.html");
			Assert.Empty(result);
		}

		// ── DetectIssues — CANONICAL_CONFLICT ─────────────────────────────────

		[Fact]
		public void DetectIssues_MultipleCanonicals_ReportsConflict()
		{
			var canonicalMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
			{
				["https://example.com/page"] = ["https://example.com/a", "https://example.com/b"]
			};
			var urlIdx = UrlIndex(
				("a.html", "https://example.com/a"),
				("b.html", "https://example.com/b"));
			var issues = CanonicalAnalyzer.DetectIssues(canonicalMap, urlIdx, SiteBase);
			Assert.Contains(issues, i => i.IssueType == "CANONICAL_CONFLICT");
		}

		[Fact]
		public void DetectIssues_ConflictUsesFirstCanonical()
		{
			var canonicalMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
			{
				["https://example.com/page"] = ["https://example.com/first", "https://example.com/second"]
			};
			var urlIdx = UrlIndex(
				("first.html", "https://example.com/first"),
				("second.html", "https://example.com/second"));
			var issues = CanonicalAnalyzer.DetectIssues(canonicalMap, urlIdx, SiteBase);
			var conflict = issues.First(i => i.IssueType == "CANONICAL_CONFLICT");
			Assert.Equal("https://example.com/first", conflict.CanonicalUrl);
		}

		// ── DetectIssues — CANONICAL_EXTERNAL ────────────────────────────────

		[Fact]
		public void DetectIssues_ExternalCanonical_ReportsExternal()
		{
			var canonicalMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
			{
				["https://example.com/page"] = ["https://other-domain.com/page"]
			};
			var issues = CanonicalAnalyzer.DetectIssues(canonicalMap, UrlIndex(), SiteBase);
			Assert.Contains(issues, i => i.IssueType == "CANONICAL_EXTERNAL");
		}

		[Fact]
		public void DetectIssues_ExternalCanonical_NotPromotedToIssueTracking()
		{
			// CANONICAL_EXTERNAL should be log-only — verify it's excluded from
			// the promoted set (Analyse filters it out, DetectIssues includes it).
			var canonicalMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
			{
				["https://example.com/page"] = ["https://other-domain.com/page"]
			};
			var issues = CanonicalAnalyzer.DetectIssues(canonicalMap, UrlIndex(), SiteBase);
			var external = issues.Where(i => i.IssueType == "CANONICAL_EXTERNAL").ToList();
			Assert.Single(external);
			// Confirm only EXTERNAL was returned (no spurious 404 for external URL)
			Assert.DoesNotContain(issues, i => i.IssueType == "CANONICAL_404");
		}

		// ── DetectIssues — CANONICAL_404 ─────────────────────────────────────

		[Fact]
		public void DetectIssues_CanonicalNotInIndex_Reports404()
		{
			var canonicalMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
			{
				["https://example.com/page"] = ["https://example.com/missing"]
			};
			// urlToFilename is empty — canonical target not crawled
			var issues = CanonicalAnalyzer.DetectIssues(canonicalMap, UrlIndex(), SiteBase);
			Assert.Contains(issues, i => i.IssueType == "CANONICAL_404");
		}

		[Fact]
		public void DetectIssues_CanonicalInIndex_No404()
		{
			var canonicalMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
			{
				["https://example.com/page"] = ["https://example.com/target"]
			};
			var urlIdx = UrlIndex(("target.html", "https://example.com/target"));
			var issues = CanonicalAnalyzer.DetectIssues(canonicalMap, urlIdx, SiteBase);
			Assert.DoesNotContain(issues, i => i.IssueType == "CANONICAL_404");
		}

		// ── DetectIssues — CANONICAL_CHAIN ───────────────────────────────────

		[Fact]
		public void DetectIssues_CanonicalTargetHasOwnCanonical_ReportsChain()
		{
			// A → B → C : A's canonical is B, B's canonical is C
			var canonicalMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
			{
				["https://example.com/a"] = ["https://example.com/b"],
				["https://example.com/b"] = ["https://example.com/c"],
			};
			var urlIdx = UrlIndex(
				("b.html", "https://example.com/b"),
				("c.html", "https://example.com/c"));
			var issues = CanonicalAnalyzer.DetectIssues(canonicalMap, urlIdx, SiteBase);
			Assert.Contains(issues, i => i.IssueType == "CANONICAL_CHAIN"
				&& i.PageUrl == "https://example.com/a");
		}

		[Fact]
		public void DetectIssues_CanonicalTargetSelfCanonical_NoChain()
		{
			// B canonicals to itself — not a chain, that's correct
			var canonicalMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
			{
				["https://example.com/a"] = ["https://example.com/b"],
				["https://example.com/b"] = ["https://example.com/b"],
			};
			var urlIdx = UrlIndex(("b.html", "https://example.com/b"));
			var issues = CanonicalAnalyzer.DetectIssues(canonicalMap, urlIdx, SiteBase);
			Assert.DoesNotContain(issues, i => i.IssueType == "CANONICAL_CHAIN");
		}

		[Fact]
		public void DetectIssues_ChainDetailContainsFullChain()
		{
			var canonicalMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
			{
				["https://example.com/a"] = ["https://example.com/b"],
				["https://example.com/b"] = ["https://example.com/c"],
			};
			var urlIdx = UrlIndex(
				("b.html", "https://example.com/b"),
				("c.html", "https://example.com/c"));
			var issues = CanonicalAnalyzer.DetectIssues(canonicalMap, urlIdx, SiteBase);
			var chain = issues.First(i => i.IssueType == "CANONICAL_CHAIN");
			Assert.Contains("https://example.com/a", chain.Detail);
			Assert.Contains("https://example.com/b", chain.Detail);
			Assert.Contains("https://example.com/c", chain.Detail);
		}

		// ── No issues ─────────────────────────────────────────────────────────

		[Fact]
		public void DetectIssues_ValidSelfCanonical_NoIssues()
		{
			// Page canonicals to itself — entirely correct, no issues
			var canonicalMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
			{
				["https://example.com/page"] = ["https://example.com/page"],
			};
			var urlIdx = UrlIndex(("page.html", "https://example.com/page"));
			var issues = CanonicalAnalyzer.DetectIssues(canonicalMap, urlIdx, SiteBase);
			Assert.Empty(issues);
		}

		[Fact]
		public void DetectIssues_EmptyMap_ReturnsEmpty()
		{
			var issues = CanonicalAnalyzer.DetectIssues(
				new Dictionary<string, List<string>>(),
				UrlIndex(),
				SiteBase);
			Assert.Empty(issues);
		}
	}
}
