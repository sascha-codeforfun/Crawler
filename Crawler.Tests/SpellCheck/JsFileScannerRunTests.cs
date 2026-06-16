using System.Text;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Batch B for JsFileScanner — the Run scan loop driven end-to-end with real
	/// findings (via the shared BulkScriptScanner.ScanText core / RunChecker script
	/// path), covering: a surviving finding (routing, BuildBundleFindings, unique
	/// + routing logs, UNRESOLVED branch), a gated literal, an undecodable literal,
	/// and reach-based CLEAR routing when a page loads the bundle.
	///
	/// Bundles are in-memory (SharedUser accepts, System null = miss). The page
	/// fixture is written UTF-8-with-BOM so DomTraverser's encoding detection (used
	/// by ScriptPageIndex.BuildFromDownload) is deterministic. No UrlCache: Run
	/// resolves URLs solely through the injected fileToUrl. SYNTHETIC fixtures.
	///
	/// The pure helpers, BuildBundleFindings and Run's early return are covered by
	/// JsFileScannerTests. In the Logger collection: the spell path logs via Logger.
	/// </summary>
	[Collection("Logger")]
	public class SpellCheckJsFileScannerRunTests : IDisposable
	{
		private static readonly IReadOnlyList<string> En = new List<string> { "en" };

		private readonly string _dir;
		private readonly string _full;
		private readonly string _trim;
		private readonly string _unique;
		private readonly string _routing;

		public SpellCheckJsFileScannerRunTests()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"JsFileScannerRun_{Guid.NewGuid():N}");
			Directory.CreateDirectory(_dir);
			Logger.Initialize(Path.Combine(_dir, "test.log"), silent: true);
			_full = Path.Combine(_dir, "30_full.log");
			_trim = Path.Combine(_dir, "31_trim.log");
			_unique = Path.Combine(_dir, "32_unique.log");
			_routing = Path.Combine(_dir, "33_routing.log");
		}

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── helpers ───────────────────────────────────────────────────────────

		private static DictionaryBundle Bundle(params string[] acceptedWords)
		{
			var bundle = new DictionaryBundle();
			foreach (var w in acceptedWords)
			{
				bundle.SharedUser.Add(w);
			}
			return bundle;
		}

		private void WriteJs(string filename, string content) =>
			File.WriteAllText(Path.Combine(_dir, filename), content, new UTF8Encoding(false));

		private void WriteJsBytes(string filename, byte[] bytes) =>
			File.WriteAllBytes(Path.Combine(_dir, filename), bytes);

		private void WritePage(string filename, string html) =>
			File.WriteAllText(Path.Combine(_dir, filename), html,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

		private JsFileScanner.Result RunScan(
			Func<string, string> fileToUrl,
			IReadOnlyList<string> dictionaries,
			IReadOnlyDictionary<string, DictionaryBundle> bundles,
			int reachThreshold = 3)
			=> JsFileScanner.Run(
				_dir, _full, _trim, _unique, _routing,
				"*.html", reachThreshold, fileToUrl,
				dictionaries, bundles,
				Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

		// ── tests ─────────────────────────────────────────────────────────────

		[Fact]
		public void Run_SurvivingFinding_UnresolvedBundle_RecordsAndWritesLogs()
		{
			WriteJs("app.js", "var t = \"hallo welt guten morgen wolrd\";"); // 4 accepted + 1 miss
			var bundles = new Dictionary<string, DictionaryBundle>
			{
				["en"] = Bundle("hallo", "welt", "guten", "morgen"),
			};

			// fileToUrl returns "" → bundle URL unresolved → reach 0 → UNRESOLVED routing.
			var r = RunScan(_ => string.Empty, En, bundles);

			Assert.True(r.Findings >= 1);
			var bf = Assert.Single(r.BundleFindings);
			Assert.Equal(0, bf.Reach);
			Assert.Contains(bf.Words, w => w.Word == "wolrd");
			Assert.Contains("wolrd", File.ReadAllText(_unique));     // log 32
			Assert.Contains("UNRESOLVED", File.ReadAllText(_routing)); // log 33
		}

		[Fact]
		public void Run_GatedLiteral_DemotedNoFinding()
		{
			WriteJs("app.js", "var t = \"wolrd zzqx qwxz frpl glmp\";"); // all-miss → demoted
			var bundles = new Dictionary<string, DictionaryBundle> { ["en"] = Bundle() };

			var r = RunScan(_ => "https://site.test/app.js", En, bundles);

			Assert.True(r.GatedLiterals >= 1);
			Assert.Equal(0, r.Findings);
			Assert.True(r.SuppressedFindings > 0);
			Assert.Empty(r.BundleFindings); // no kept findings → no routing entry
		}

		[Fact]
		public void Run_UndecodableLiteral_Skipped()
		{
			// A literal carrying an invalid UTF-8 byte decodes to U+FFFD and is skipped whole.
			var bytes = Encoding.UTF8.GetBytes("var t = \"ab")
				.Concat(new byte[] { 0xFF })
				.Concat(Encoding.UTF8.GetBytes("cd\";"))
				.ToArray();
			WriteJsBytes("app.js", bytes);
			var bundles = new Dictionary<string, DictionaryBundle> { ["en"] = Bundle() };

			var r = RunScan(_ => "https://site.test/app.js", En, bundles);

			Assert.True(r.SkippedUndecodable >= 1);
			Assert.Equal(0, r.Findings);
		}

		[Fact]
		public void Run_BundleLoadedByPage_ReachClearRouting()
		{
			WriteJs("app.js", "var t = \"hallo welt guten morgen wolrd\";");
			WritePage("page.html", "<html><body><script src=\"/app.js\"></script></body></html>");
			var bundles = new Dictionary<string, DictionaryBundle>
			{
				["en"] = Bundle("hallo", "welt", "guten", "morgen"),
			};

			// .js → its own URL; page → a base URL. The page's "/app.js" resolves to the
			// same URL as the bundle, so their stable keys match and reach is 1.
			Func<string, string> map = f =>
				f.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
					? "https://site.test/" + f
					: "https://site.test/page";

			var r = RunScan(map, En, bundles, reachThreshold: 3);

			Assert.True(r.Findings >= 1);
			var bf = Assert.Single(r.BundleFindings);
			Assert.Equal(1, bf.Reach);
			Assert.False(bf.IsBulk); // reach 1 ≤ threshold 3
			Assert.Contains(bf.Pages, p => p.Contains("site.test/page"));
			Assert.Contains("CLEAR", File.ReadAllText(_routing));
		}
	}
}
