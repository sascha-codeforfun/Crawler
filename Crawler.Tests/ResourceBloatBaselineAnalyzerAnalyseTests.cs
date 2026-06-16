using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// End-to-end tests for ResourceBloatBaselineAnalyzer.Analyse and the private
	/// helpers it drives (LoadLog20, BuildUrlFrequency). The analyzer consumes
	/// log 20 (the resource-bloat report) and emits log 21: a BASELINE row plus
	/// per-page delta rows for pages above the detected baseline.
	///
	/// Most tests write crafted log-20 rows directly (the 12-column format is
	/// fixed) — no UrlCache needed. The download directory only feeds
	/// BuildUrlFrequency, which determines the baseline file list, so HTML
	/// fixtures appear only in the frequency tests. All fixtures are SYNTHETIC.
	///
	/// In the Logger collection: Analyse logs progress via the static Logger.
	///
	/// Note: the issue-column "ABOVE_BASELINE" fallback is effectively
	/// unreachable — any page passing the second filter does so via a condition
	/// that also keeps or adds an issue — so it is intentionally not tested.
	/// </summary>
	[Collection("Logger")]
	public class ResourceBloatBaselineAnalyzerAnalyseTests : IDisposable
	{
		private const string Site = "https://site.test";

		// 12-column header; content is irrelevant (LoadLog20 skips the first line).
		private const string Log20Header =
			"PageUrl@@@JSCSSFileCount@@@JSCSSTotalBytes@@@JSFileSizeBytes@@@CSSFileSizeBytes@@@" +
			"Base64AssetCount@@@Base64TotalDecodedBytes@@@Base64LargeAssets@@@InlinedCSSBytes@@@" +
			"JSONBlobCount@@@JSONBlobTotalBytes@@@Issues";

		private const int High = 100_000_000; // threshold large enough to exclude

		private readonly string _dir;
		private readonly string _log20;
		private readonly string _log21;

		public ResourceBloatBaselineAnalyzerAnalyseTests()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"ResBloatBase_{Guid.NewGuid():N}");
			Directory.CreateDirectory(_dir);
			Logger.Initialize(Path.Combine(_dir, "test.log"), silent: true);
			_log20 = Path.Combine(_dir, "20_bloat.log");
			_log21 = Path.Combine(_dir, "21_baseline.log");
		}

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── fixture helpers ─────────────────────────────────────────────────

		// Build one 12-column log-20 row. Big pages should carry a non-empty
		// Issues field (as real log 20 always does — rows are only emitted with
		// issues), so default it to OVERSIZED_JS.
		private static string Row(
			string pageUrl,
			long jsCssTotal,
			int fileCount = 1,
			long jsBytes = 0,
			long cssBytes = 0,
			int b64Count = 0,
			long b64Bytes = 0,
			string b64Large = "none",
			long inlined = 0,
			int jsonCount = 0,
			long jsonBytes = 0,
			string issues = "OVERSIZED_JS")
			=> $"{pageUrl}@@@{fileCount}@@@{jsCssTotal}@@@{jsBytes}@@@{cssBytes}@@@" +
			   $"{b64Count}@@@{b64Bytes}@@@{b64Large}@@@{inlined}@@@{jsonCount}@@@{jsonBytes}@@@{issues}";

		private void WriteLog20(params string[] rows)
		{
			var lines = new List<string> { Log20Header };
			lines.AddRange(rows);
			File.WriteAllLines(_log20, lines, Encoding.UTF8);
		}

		// Analyse reads HTML as Latin1; write the same so bytes match.
		private void WriteHtml(string filename, string html) =>
			File.WriteAllText(Path.Combine(_dir, filename), html, Encoding.Latin1);

		private void Run(int jsThreshold = 10, int cssThreshold = 10,
			int aboveBaseline = 10, string pageExt = "") =>
			ResourceBloatBaselineAnalyzer.Analyse(_dir, _log20, _log21, Site, pageExt,
				102_400, jsThreshold, cssThreshold, aboveBaseline);

		private string[] ReportLines() =>
			File.Exists(_log21) ? File.ReadAllLines(_log21) : Array.Empty<string>();

		private string[] DataRows() => ReportLines().Skip(1).ToArray();

		private static string[] Cols(string line) => line.Split("@@@");

		private string[] BaselineCols() =>
			Cols(DataRows().First(r => r.StartsWith("BASELINE", StringComparison.Ordinal)));

		// ── early exits ─────────────────────────────────────────────────────

		[Fact]
		public void Analyse_Log20Missing_NoReportWritten()
		{
			Run(); // _log20 never created
			Assert.False(File.Exists(_log21));
		}

		[Fact]
		public void Analyse_Log20HeaderOnly_NoReportWritten()
		{
			WriteLog20(); // header only → zero data rows
			Run();
			Assert.False(File.Exists(_log21));
		}

		// ── baseline row ────────────────────────────────────────────────────

		[Fact]
		public void Analyse_EmitsBaselineRow_WithMinTotalAndSitewide()
		{
			WriteLog20(
				Row("https://site.test/a", jsCssTotal: 100, fileCount: 2, jsBytes: 40, cssBytes: 20),
				Row("https://site.test/b", jsCssTotal: 1000, jsBytes: 1000));

			// High thresholds → the 1000 page is excluded, leaving only BASELINE.
			Run(jsThreshold: High, cssThreshold: High, aboveBaseline: High);

			var rows = DataRows();
			Assert.Single(rows);
			var c = Cols(rows[0]);
			Assert.Equal("BASELINE", c[0]);
			Assert.Equal("100", c[2]);       // min JSCSSTotalBytes
			Assert.Equal("SITEWIDE", c[11]);
		}

		// ── delta rows / issue flags ────────────────────────────────────────

		[Fact]
		public void Analyse_AbovePageBigJsDelta_FlagsExtraJs()
		{
			WriteLog20(
				Row("https://site.test/base", jsCssTotal: 100, jsBytes: 100),
				Row("https://site.test/big", jsCssTotal: 1000, jsBytes: 1000, issues: "OVERSIZED_JS"));

			Run(jsThreshold: 10, cssThreshold: 10, aboveBaseline: 10);

			var c = Cols(DataRows().First(r => r.StartsWith("https://site.test/big", StringComparison.Ordinal)));
			Assert.Equal("900", c[2]); // delta total
			Assert.Equal("900", c[3]); // delta JS
			Assert.Equal("EXTRA_JS", c[11]);
		}

		[Fact]
		public void Analyse_AbovePageBigCssDelta_FlagsExtraCss()
		{
			WriteLog20(
				Row("https://site.test/base", jsCssTotal: 100, jsBytes: 50, cssBytes: 50),
				Row("https://site.test/big", jsCssTotal: 1000, jsBytes: 50, cssBytes: 950, issues: "OVERSIZED_CSS"));

			Run(jsThreshold: 10, cssThreshold: 10, aboveBaseline: 10);

			var c = Cols(DataRows().First(r => r.StartsWith("https://site.test/big", StringComparison.Ordinal)));
			Assert.Equal("900", c[4]); // delta CSS
			Assert.Equal("EXTRA_CSS", c[11]);
		}

		[Fact]
		public void Analyse_AbovePageBase64Large_IssuePreservedAndPassthrough()
		{
			WriteLog20(
				Row("https://site.test/base", jsCssTotal: 100),
				Row("https://site.test/big", jsCssTotal: 150,
					b64Count: 1, b64Bytes: 200000, b64Large: "img.png(195KB)",
					issues: "BASE64_LARGE"));

			// High numeric thresholds: the page qualifies only via its BASE64_LARGE issue.
			Run(jsThreshold: High, cssThreshold: High, aboveBaseline: High);

			var c = Cols(DataRows().First(r => r.StartsWith("https://site.test/big", StringComparison.Ordinal)));
			Assert.Equal("img.png(195KB)", c[7]); // Base64LargeAssets passthrough
			Assert.Contains("BASE64_LARGE", c[11]);
		}

		[Fact]
		public void Analyse_OversizedFlags_StrippedFromDeltaIssues()
		{
			WriteLog20(
				Row("https://site.test/base", jsCssTotal: 100, jsBytes: 100),
				Row("https://site.test/big", jsCssTotal: 1000, jsBytes: 1000,
					issues: "OVERSIZED_JS|OVERSIZED_CSS|BASE64_LARGE"));

			Run(jsThreshold: 10, cssThreshold: 10, aboveBaseline: 10);

			var c = Cols(DataRows().First(r => r.StartsWith("https://site.test/big", StringComparison.Ordinal)));
			Assert.DoesNotContain("OVERSIZED", c[11]);
			Assert.Contains("BASE64_LARGE", c[11]);
			Assert.Contains("EXTRA_JS", c[11]);
		}

		// ── exclusion paths ─────────────────────────────────────────────────

		[Fact]
		public void Analyse_AllPagesAtBaseline_OnlyBaselineRow()
		{
			WriteLog20(
				Row("https://site.test/a", jsCssTotal: 100),
				Row("https://site.test/b", jsCssTotal: 100));

			Run();

			var rows = DataRows();
			Assert.Single(rows); // BASELINE only; nothing above baseline
			Assert.StartsWith("BASELINE", rows[0]);
		}

		[Fact]
		public void Analyse_AboveBaselineButBelowThresholds_Excluded()
		{
			WriteLog20(
				Row("https://site.test/base", jsCssTotal: 100, jsBytes: 0),
				Row("https://site.test/mid", jsCssTotal: 200, jsBytes: 150, issues: "OVERSIZED_JS"));

			// No special issue and delta below the (high) above-baseline threshold.
			Run(jsThreshold: High, cssThreshold: High, aboveBaseline: High);

			var rows = DataRows();
			Assert.Single(rows);
			Assert.StartsWith("BASELINE", rows[0]);
		}

		// ── ordering & robustness ───────────────────────────────────────────

		[Fact]
		public void Analyse_AboveBaselineRows_SortedByDeltaDescending()
		{
			WriteLog20(
				Row("https://site.test/base", jsCssTotal: 100, jsBytes: 0),
				Row("https://site.test/a", jsCssTotal: 300, jsBytes: 300, issues: "OVERSIZED_JS"),
				Row("https://site.test/b", jsCssTotal: 1100, jsBytes: 1100, issues: "OVERSIZED_JS"));

			Run(jsThreshold: 10, cssThreshold: 10, aboveBaseline: 10);

			var rows = DataRows();
			Assert.StartsWith("BASELINE", rows[0]);
			Assert.StartsWith("https://site.test/b", rows[1]); // larger delta first
			Assert.StartsWith("https://site.test/a", rows[2]);
		}

		[Fact]
		public void Analyse_Log20MalformedLines_Skipped()
		{
			WriteLog20(
				"https://site.test/short@@@1@@@2",                       // < 12 columns
				"https://site.test/bad@@@1@@@notanumber@@@0@@@0@@@0@@@0@@@none@@@0@@@0@@@0@@@X", // unparsable col 2
				Row("https://site.test/base", jsCssTotal: 100),
				Row("https://site.test/big", jsCssTotal: 1000, jsBytes: 1000, issues: "OVERSIZED_JS"));

			Run(jsThreshold: 10, cssThreshold: 10, aboveBaseline: 10);

			// Baseline derives from the two valid rows only.
			Assert.Equal("100", BaselineCols()[2]);
			Assert.Contains(DataRows(), r => r.StartsWith("https://site.test/big", StringComparison.Ordinal));
		}

		// ── baseline-file detection via URL frequency ───────────────────────

		[Fact]
		public void Analyse_BaselineFiles_DetectedFromUrlFrequency()
		{
			WriteLog20(
				Row("https://site.test/p1", jsCssTotal: 100),
				Row("https://site.test/p2", jsCssTotal: 200, issues: "OVERSIZED_JS"));

			// Two HTML pages, both loading common.js → 100% frequency ≥ 0.90.
			WriteHtml($"p1_{Guid.NewGuid():N}.html",
				"<html><body><script src=\"/common.js\"></script></body></html>");
			WriteHtml($"p2_{Guid.NewGuid():N}.html",
				"<html><body><script src=\"/common.js\"></script></body></html>");

			Run(jsThreshold: High, cssThreshold: High, aboveBaseline: High);

			Assert.Equal("common.js", BaselineCols()[7]); // shortened baseline file list
		}

		[Fact]
		public void Analyse_NoBaselineFiles_ColumnIsUnknown()
		{
			WriteLog20(Row("https://site.test/p1", jsCssTotal: 100));

			Run(); // no HTML files → frequency map empty

			Assert.Equal("unknown", BaselineCols()[7]);
		}

		[Fact]
		public void Analyse_CustomConfiguredPageExtension_CountedInFrequency()
		{
			WriteLog20(
				Row("https://site.test/p1", jsCssTotal: 100),
				Row("https://site.test/p2", jsCssTotal: 200, issues: "OVERSIZED_JS"));

			// Pages use a non-.html extension; only counted when pageExt matches.
			WriteHtml($"p1_{Guid.NewGuid():N}.page",
				"<html><body><script src=\"/common.js\"></script></body></html>");
			WriteHtml($"p2_{Guid.NewGuid():N}.page",
				"<html><body><script src=\"/common.js\"></script></body></html>");

			Run(jsThreshold: High, cssThreshold: High, aboveBaseline: High, pageExt: ".page");

			Assert.Equal("common.js", BaselineCols()[7]);
		}

		[Fact]
		public void Analyse_UrlFrequency_NormalisesAbsoluteRelativeQueryFragmentAndTrailingSlash()
		{
			// One page (so every distinct ref is loaded on 100% of pages) referencing
			// five differently-shaped URLs, to exercise ResolveRef (absolute
			// passthrough, rooted, and relative-without-slash) and NormaliseUrl
			// (query strip, fragment strip, trailing-slash trim).
			WriteLog20(Row("https://site.test/only", jsCssTotal: 100));

			WriteHtml($"page_{Guid.NewGuid():N}.html",
				"<html><body>" +
				"<script src=\"https://cdn.test/lib.js\"></script>" +  // absolute → passthrough
				"<script src=\"sub/app.js\"></script>" +               // relative → root + '/' + ref
				"<script src=\"/q.js?v=2\"></script>" +                // query stripped
				"<script src=\"/t.js/\"></script>" +                   // trailing slash trimmed
				"<link href=\"/f.css#frag\" rel=\"stylesheet\">" +     // fragment stripped
				"</body></html>");

			Run(jsThreshold: High, cssThreshold: High, aboveBaseline: High);

			// All five resolve/normalise to a clean filename and (at 100% frequency)
			// land in the baseline file list.
			var files = BaselineCols()[7];
			Assert.Contains("lib.js", files);
			Assert.Contains("app.js", files);
			Assert.Contains("q.js", files);
			Assert.Contains("t.js", files);
			Assert.Contains("f.css", files);
		}
	}
}
