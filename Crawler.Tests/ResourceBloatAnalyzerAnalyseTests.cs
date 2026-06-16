using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// End-to-end tests for ResourceBloatAnalyzer.Analyse and the private helpers
	/// it drives (BuildJsCssIndex, LoadBase64Log, AnalysePage). Each test lays out
	/// a temp download directory with HTML pages and JS/CSS files, optionally a
	/// log-19 base64 file, populates UrlCache so filenames resolve to URLs, runs
	/// Analyse, then asserts on the written report (log 20).
	///
	/// Thresholds are passed in small so tiny fixtures trip the OVERSIZED /
	/// BASE64_LARGE gates deterministically. All fixtures are SYNTHETIC.
	///
	/// UrlCache is process-wide static with no reset, so every test uses
	/// GUID-unique filenames; lookups are keyed by filename and stay isolated.
	/// In the Logger collection: Analyse logs progress via the static Logger.
	/// </summary>
	[Collection("Logger")]
	public class ResourceBloatAnalyzerAnalyseTests : IDisposable
	{
		private const string Site = "https://site.test";

		private readonly string _dir;
		private readonly string _log19;
		private readonly string _report;
		private readonly List<string> _lookups = new();

		public ResourceBloatAnalyzerAnalyseTests()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"ResBloat_{Guid.NewGuid():N}");
			Directory.CreateDirectory(_dir);
			Logger.Initialize(Path.Combine(_dir, "test.log"), silent: true);
			_log19 = Path.Combine(_dir, "19_base64.log");
			_report = Path.Combine(_dir, "20_bloat.log");
		}

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── fixture helpers ─────────────────────────────────────────────────

		private static string NewPage() => $"page_{Guid.NewGuid():N}.html";
		private static string NewJs() => $"app_{Guid.NewGuid():N}.js";
		private static string NewCss() => $"style_{Guid.NewGuid():N}.css";

		private static string UrlFor(string filename) => Site + "/" + filename;

		// Analyse reads HTML and JS/CSS as Latin1; write the same so bytes match.
		private void WriteFile(string filename, string content) =>
			File.WriteAllText(Path.Combine(_dir, filename), content, Encoding.Latin1);

		// Register filename → its canonical site URL (committed via CommitLookups).
		private string Register(string filename)
		{
			_lookups.Add($"{filename}|{UrlFor(filename)}");
			return filename;
		}

		private void CommitLookups()
		{
			var path = Path.Combine(_dir, $"lookup_{Guid.NewGuid():N}.lku");
			File.WriteAllLines(path, _lookups, Encoding.UTF8);
			UrlCache.LoadCache(path);
		}

		// Log 19: header line (skipped by the loader) plus the given data rows.
		private void WriteLog19(params string[] dataLines)
		{
			var lines = new List<string>
			{
				"SourceUrl@@@SavedFile@@@MediaType@@@EncodedBytes@@@DecodedBytes",
			};
			lines.AddRange(dataLines);
			File.WriteAllLines(_log19, lines, Encoding.UTF8);
		}

		private void Run(int base64Threshold = 10, int jsThreshold = 10, int cssThreshold = 10,
			string pageExt = "") =>
			ResourceBloatAnalyzer.Analyse(_dir, _log19, _report, Site, pageExt,
				base64Threshold, jsThreshold, cssThreshold);

		private string[] ReportLines() =>
			File.Exists(_report) ? File.ReadAllLines(_report) : Array.Empty<string>();

		private string[] DataRows() => ReportLines().Skip(1).ToArray();

		private static string[] Cols(string line) => line.Split("@@@");

		private static string PageScript(string jsFilename) =>
			$"<html><body><script src=\"/{jsFilename}\"></script></body></html>";

		// ── early exit ──────────────────────────────────────────────────────

		[Fact]
		public void Analyse_DownloadDirectoryMissing_NoReportWritten()
		{
			ResourceBloatAnalyzer.Analyse(Path.Combine(_dir, "nope"), _log19, _report,
				Site, "", 10, 10, 10);

			Assert.False(File.Exists(_report));
		}

		// ── issue types ─────────────────────────────────────────────────────

		[Fact]
		public void Analyse_OversizedJs_Flagged()
		{
			var js = Register(NewJs());
			WriteFile(js, new string('a', 50)); // 50 bytes ≥ threshold 10
			var page = Register(NewPage());
			WriteFile(page, PageScript(js));
			CommitLookups();

			Run();

			var row = Cols(Assert.Single(DataRows()));
			Assert.Equal(UrlFor(page), row[0]);
			Assert.Equal("1", row[1]);            // JS/CSS file count
			Assert.Contains("OVERSIZED_JS", row[11]);
		}

		[Fact]
		public void Analyse_OversizedCss_Flagged()
		{
			var css = Register(NewCss());
			WriteFile(css, new string('a', 50));
			var page = Register(NewPage());
			WriteFile(page,
				$"<html><body><link href=\"/{css}\" rel=\"stylesheet\"></body></html>");
			CommitLookups();

			Run();

			var row = Cols(Assert.Single(DataRows()));
			Assert.Contains("OVERSIZED_CSS", row[11]);
		}

		[Fact]
		public void Analyse_InlinedCss_Flagged()
		{
			var js = Register(NewJs());
			// .textContent = `…` body ≥100 chars containing { : ;
			var cssBody = string.Concat(Enumerable.Repeat(".a{color:red;}", 10)); // 140 chars
			WriteFile(js, $"el.textContent = `{cssBody}`;");
			var page = Register(NewPage());
			WriteFile(page, PageScript(js));
			CommitLookups();

			// Large JS threshold so the only issue is the inlined CSS.
			Run(jsThreshold: 1_000_000);

			var row = Cols(Assert.Single(DataRows()));
			Assert.Equal("INLINED_CSS", row[11]);
			Assert.NotEqual("0", row[8]); // InlinedCSSBytes > 0
		}

		[Fact]
		public void Analyse_JsonBlob_Flagged()
		{
			var page = Register(NewPage());
			var blob = "{" + new string('a', 80) + "}"; // ≥64 chars inside braces
			WriteFile(page, $"<html><body><div data='{blob}'></div></body></html>");
			CommitLookups();

			Run();

			var row = Cols(Assert.Single(DataRows()));
			Assert.Equal("1", row[9]); // JSONBlobCount
			Assert.Contains("JSON_BLOBS", row[11]);
		}

		[Fact]
		public void Analyse_Base64LargeAsset_Flagged()
		{
			var js = Register(NewJs());
			WriteFile(js, "a"); // small — not oversized under a large JS threshold
			var page = Register(NewPage());
			WriteFile(page, PageScript(js));
			// Base64 asset attributed to the JS culprit URL, decoded ≥ threshold.
			WriteLog19($"{UrlFor(js)}@@@asset1.png@@@image/png@@@500@@@200000");
			CommitLookups();

			Run(base64Threshold: 10, jsThreshold: 1_000_000);

			var row = Cols(Assert.Single(DataRows()));
			Assert.Equal("1", row[5]);        // Base64AssetCount
			Assert.Equal("200000", row[6]);   // Base64TotalDecodedBytes
			Assert.NotEqual("none", row[7]);  // Base64LargeAssets column
			Assert.Contains("asset1.png", row[7]);
			Assert.Contains("BASE64_LARGE", row[11]);
		}

		// ── exclusion / skip paths ──────────────────────────────────────────

		[Fact]
		public void Analyse_PageWithoutIssues_NotInReport()
		{
			var js = Register(NewJs());
			WriteFile(js, "a"); // 1 byte
			var page = Register(NewPage());
			WriteFile(page, PageScript(js));
			CommitLookups();

			Run(jsThreshold: 1_000_000); // not oversized; no inline/base64/json

			Assert.Single(ReportLines()); // header only
		}

		[Fact]
		public void Analyse_PageUrlUnresolved_Skipped()
		{
			var js = Register(NewJs());
			WriteFile(js, new string('a', 50));
			var page = NewPage();           // deliberately NOT registered → "error"
			WriteFile(page, PageScript(js));
			CommitLookups();

			Run();

			Assert.Single(ReportLines()); // page skipped → header only
		}

		[Fact]
		public void Analyse_JsFileUrlUnresolved_NotIndexed()
		{
			var js = NewJs();               // NOT registered → not indexed
			WriteFile(js, new string('a', 50));
			var page = Register(NewPage());
			WriteFile(page, PageScript(js));
			CommitLookups();

			Run();

			// The reference resolves to no indexed file → no match → no issue.
			Assert.Single(ReportLines());
		}

		// ── ordering & log-19 robustness ────────────────────────────────────

		[Fact]
		public void Analyse_SortsPagesByJsCssBytesDescending()
		{
			var jsBig = Register(NewJs());
			WriteFile(jsBig, new string('a', 100));
			var jsSmall = Register(NewJs());
			WriteFile(jsSmall, new string('a', 50));

			var pageBig = Register(NewPage());
			WriteFile(pageBig, PageScript(jsBig));
			var pageSmall = Register(NewPage());
			WriteFile(pageSmall, PageScript(jsSmall));
			CommitLookups();

			Run();

			var rows = DataRows();
			Assert.Equal(2, rows.Length);
			Assert.Equal(UrlFor(pageBig), Cols(rows[0])[0]);   // larger total first
			Assert.Equal(UrlFor(pageSmall), Cols(rows[1])[0]);
		}

		[Fact]
		public void Analyse_Log19MalformedLines_Skipped()
		{
			var js = Register(NewJs());
			WriteFile(js, "a");
			var page = Register(NewPage());
			WriteFile(page, PageScript(js));
			WriteLog19(
				$"{UrlFor(js)}@@@bad.png@@@image/png@@@10@@@notanumber", // non-int decoded
				$"{UrlFor(js)}@@@short@@@only3parts",                    // < 5 columns
				$"{UrlFor(js)}@@@good.png@@@image/png@@@500@@@300");     // valid
			CommitLookups();

			Run(base64Threshold: 10, jsThreshold: 1_000_000);

			var row = Cols(Assert.Single(DataRows()));
			Assert.Equal("1", row[5]); // only the valid asset counted
			Assert.Contains("BASE64_LARGE", row[11]);
		}

		[Fact]
		public void Analyse_ManyLargeAssets_OverflowSummaryInColumn()
		{
			var js = Register(NewJs());
			WriteFile(js, "a");
			var page = Register(NewPage());
			WriteFile(page, PageScript(js));

			// Seven large assets → the column shows the top 5 plus a "+2 more" tail.
			var rows = Enumerable.Range(0, 7)
				.Select(i => $"{UrlFor(js)}@@@asset{i}.png@@@image/png@@@500@@@{110000 + i * 10000}")
				.ToArray();
			WriteLog19(rows);
			CommitLookups();

			Run(base64Threshold: 10, jsThreshold: 1_000_000);

			var row = Cols(Assert.Single(DataRows()));
			Assert.Equal("7", row[5]);            // all seven counted
			Assert.Contains("+2 more", row[7]);   // overflow summary
		}

		[Fact]
		public void Analyse_CustomConfiguredPageExtension_Processed()
		{
			// A file with the configured (non-.html) page extension is treated as a
			// page — exercises the configuredPageExt clause of the html-file filter.
			var page = Register($"custom_{Guid.NewGuid():N}.page");
			var blob = "{" + new string('a', 80) + "}";
			WriteFile(page, $"<html><body><div data='{blob}'></div></body></html>");
			CommitLookups();

			Run(pageExt: ".page");

			var row = Cols(Assert.Single(DataRows()));
			Assert.Equal(UrlFor(page), row[0]);
			Assert.Contains("JSON_BLOBS", row[11]);
		}
	}
}
