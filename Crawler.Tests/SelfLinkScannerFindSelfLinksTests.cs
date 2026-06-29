using System.Text;
using Xunit;
using Crawler.Urls;

namespace Crawler.Tests
{
	/// <summary>
	/// End-to-end tests for SelfLinkScanner.FindSelfLinks. Each test lays out a
	/// temp directory with HTML pages, populates Cache so filenames resolve to
	/// page URLs, runs the scan, then asserts on the written results log
	/// (File|FileUrl|LinkFound|ContextSnippet, pipe-delimited, UTF-8 BOM).
	///
	/// A link counts as a self-link when it resolves to the same origin — rooted
	/// ("/…"), absolute same-origin, or document-relative ("x.html", "./x.html") —
	/// AND its resolved path equals the page's own path AND it carries no
	/// fragment; ignored query keys suppress it. The ExtractHtmlContextSnippet
	/// helper is covered by SelfLinkScannerSnippetTests; this file covers the scan
	/// driver and the self-link decision. All fixtures are SYNTHETIC.
	///
	/// Cache is process-wide static with no reset, so every test uses
	/// GUID-unique filenames; lookups are keyed by filename and stay isolated.
	/// In the Logger collection: CrawlIndex/Cache log via the static Logger.
	/// </summary>
	[Collection("Logger")]
	public class SelfLinkScannerFindSelfLinksTests : IDisposable
	{
		private const string PageUrl = "https://site.test/page";

		private readonly string _dir;
		private readonly string _results;
		private readonly List<string> _lookups = new();

		public SelfLinkScannerFindSelfLinksTests()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"SelfLink_{Guid.NewGuid():N}");
			Directory.CreateDirectory(_dir);
			Logger.Initialize(Path.Combine(_dir, "test.log"), silent: true);
			_results = Path.Combine(_dir, "selflinks");
		}

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── fixture helpers ─────────────────────────────────────────────────

		private static string NewPage() => $"page_{Guid.NewGuid():N}.html";

		private void WriteHtml(string filename, string bodyInner) =>
			File.WriteAllText(Path.Combine(_dir, filename),
				$"<html><body>{bodyInner}</body></html>", Encoding.UTF8);

		private string RegisterAs(string filename, string url)
		{
			_lookups.Add($"{filename}|{url}");
			return filename;
		}

		private void Commit()
		{
			var path = Path.Combine(_dir, $"lookup_{Guid.NewGuid():N}.lku");
			File.WriteAllLines(path, _lookups.Count > 0 ? _lookups : new[] { "_none_|_none_" },
				Encoding.UTF8);
			Cache.Load(path);
		}

		private void Scan(List<string>? ignoreQueryKeys = null, int maxDop = 0) =>
			SelfLinkScanner.FindSelfLinks(_dir, _results,
				ignoreQueryKeys ?? new List<string>(), "*.html", maxDop, 120, 240);

		private string[] ReportLines()
		{
			var semicolon = _results + IssueLogWriter.CsvSemicolonSuffix;
			return File.Exists(semicolon) ? File.ReadAllLines(semicolon) : Array.Empty<string>();
		}

		private string[] DataRows() => ReportLines().Skip(1).ToArray();

		private static string[] Cols(string line) => IssueLogWriter.ParseCsvLine(line, ';');

		// One page at PageUrl with the given anchor markup; returns the data rows.
		private string[] ScanOnePage(string anchor, string url = PageUrl,
			List<string>? ignoreQueryKeys = null, int maxDop = 0)
		{
			var name = NewPage();
			WriteHtml(name, anchor);
			RegisterAs(name, url);
			Commit();
			Scan(ignoreQueryKeys, maxDop);
			return DataRows();
		}

		// ── self-link detection: positive ──────────────────────────────────

		[Fact]
		public void RootedSelfLink_Reported()
		{
			var rows = ScanOnePage("<a href=\"/page\">self</a>");
			var c = Cols(Assert.Single(rows));
			Assert.Equal(PageUrl, c[1]);   // FileUrl
			Assert.Equal("/page", c[2]);   // LinkFound
		}

		[Fact]
		public void AbsoluteSameOriginSelfLink_Reported()
		{
			var rows = ScanOnePage("<a href=\"https://site.test/page\">self</a>");
			var c = Cols(Assert.Single(rows));
			Assert.Equal("https://site.test/page", c[2]);
		}

		[Fact]
		public void NonIgnoredQueryKey_StillReported()
		{
			var rows = ScanOnePage("<a href=\"/page?foo=1\">self</a>",
				ignoreQueryKeys: new List<string> { "utm_source" });
			Assert.Single(rows);
		}

		[Fact]
		public void NoIgnoreListConfigured_QuerySelfLink_Reported()
		{
			// Empty ignore list → the query-key check is skipped entirely.
			var rows = ScanOnePage("<a href=\"/page?x=1\">self</a>",
				ignoreQueryKeys: new List<string>());
			Assert.Single(rows);
		}

		// ── self-link detection: negative ──────────────────────────────────

		[Fact]
		public void CrossOriginAbsolute_NotReported()
		{
			Assert.Empty(ScanOnePage("<a href=\"https://other.test/page\">x</a>"));
		}

		// D109: document-relative self-links now resolve against the page URL and
		// fire — previously dropped by a same-origin pre-gate that only admitted
		// rooted/absolute hrefs (this test asserted the old NotReported behavior).
		[Fact]
		public void RelativeNonRooted_Reported()
		{
			var rows = ScanOnePage("<a href=\"page\">x</a>");
			var c = Cols(Assert.Single(rows));
			Assert.Equal("page", c[2]); // LinkFound (raw href)
		}

		[Fact]
		public void DotSlashRelativeSelfLink_Reported()
		{
			var rows = ScanOnePage("<a href=\"./page\">x</a>");
			var c = Cols(Assert.Single(rows));
			Assert.Equal("./page", c[2]);
		}

		[Fact]
		public void RelativeSelfLinkWithQuery_Reported()
		{
			// Bare-filename self-link carrying a non-ignored query still fires.
			var rows = ScanOnePage("<a href=\"page?x=1\">x</a>",
				ignoreQueryKeys: new List<string>());
			Assert.Single(rows);
		}

		[Fact]
		public void DifferentRootedPath_NotReported()
		{
			Assert.Empty(ScanOnePage("<a href=\"/other\">x</a>"));
		}

		[Fact]
		public void FragmentSelfLink_NotReported()
		{
			Assert.Empty(ScanOnePage("<a href=\"/page#section\">x</a>"));
		}

		[Fact]
		public void IgnoredQueryKey_NotReported()
		{
			Assert.Empty(ScanOnePage("<a href=\"/page?utm_source=x\">x</a>",
				ignoreQueryKeys: new List<string> { "utm_source" }));
		}

		// ── file-url resolution skips ───────────────────────────────────────

		[Fact]
		public void FileUrlEmpty_FileSkipped()
		{
			// Registered with an empty URL → IsNullOrWhiteSpace skip.
			Assert.Empty(ScanOnePage("<a href=\"/page\">self</a>", url: string.Empty));
		}

		[Fact]
		public void FileUrlUnresolved_FileSkipped()
		{
			// Filename never registered → lookup yields "error" → Uri ctor throws → skip.
			var name = NewPage();
			WriteHtml(name, "<a href=\"/page\">self</a>");
			Commit(); // no entry for this filename
			Scan();
			Assert.Empty(DataRows());
		}

		// ── output shape / robustness ───────────────────────────────────────

		[Fact]
		public void MultipleFiles_HeaderWrittenAndRowsSorted()
		{
			var p1 = RegisterAs(NewPage(), "https://site.test/p1");
			WriteHtml(p1, "<a href=\"/p1\">self</a>");
			var p2 = RegisterAs(NewPage(), "https://site.test/p2");
			WriteHtml(p2, "<a href=\"/p2\">self</a>");
			Commit();

			Scan(maxDop: 1); // explicit degree-of-parallelism branch

			var lines = ReportLines();
			Assert.StartsWith("File", lines[0]); // header row first
			var rows = DataRows();
			Assert.Equal(2, rows.Length);
			Assert.True(string.CompareOrdinal(rows[0], rows[1]) <= 0); // deterministic sort
		}

		[Fact]
		public void EmptyHrefAndPageWithoutAnchors_NothingReported()
		{
			var p1 = RegisterAs(NewPage(), "https://site.test/p1");
			WriteHtml(p1, "<a href=\"\">empty</a>");          // blank href → skipped
			var p2 = RegisterAs(NewPage(), "https://site.test/p2");
			WriteHtml(p2, "no anchors here");                 // SelectNodes returns null
			Commit();

			Scan();

			Assert.Empty(DataRows());
			Assert.Single(ReportLines()); // header only
		}
	}
}
