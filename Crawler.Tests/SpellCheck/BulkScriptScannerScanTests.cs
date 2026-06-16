using System.Text;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for the BulkScriptScanner scan core: ScanText (gate off / gate on
	/// survive / gate on demote / undecodable skip / no literals), the Run
	/// harvest-and-scan pipeline over inline &lt;script&gt; bodies, and the
	/// TrimLiteral audit-note formatter.
	///
	/// ScanText flags via the same RunChecker script path proven elsewhere: a
	/// literal whose word tokens are mostly accepted with a single miss surfaces
	/// that miss; an all-miss literal is demoted by the prose-ratio gate. Bundles
	/// are in-memory (SharedUser accepts, System null = miss). HTML fixtures are
	/// written UTF-8-with-BOM so DomTraverser's encoding detection is deterministic.
	/// SYNTHETIC fixtures throughout.
	///
	/// HeaderOffsets / SourceForOffset / ContextWindow are covered by the existing
	/// SpellCheckBulkScriptScannerTests. In the Logger collection: the spell path
	/// logs via the static Logger.
	/// </summary>
	[Collection("Logger")]
	public class SpellCheckBulkScriptScannerScanTests : IDisposable
	{
		private static readonly IReadOnlyList<string> En = new List<string> { "en" };

		private readonly string _dir;
		private readonly string _blob;
		private readonly string _findings;

		public SpellCheckBulkScriptScannerScanTests()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"BulkScript_{Guid.NewGuid():N}");
			Directory.CreateDirectory(_dir);
			Logger.Initialize(Path.Combine(_dir, "test.log"), silent: true);
			_blob = Path.Combine(_dir, "28_blob.js");
			_findings = Path.Combine(_dir, "29_findings.log");
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

		private static ToolsSpellChecker Checker(DictionaryBundle bundle)
			=> new(bundle, new Dictionary<string, DictionaryBundle> { ["en"] = bundle },
				Array.Empty<string>(), Array.Empty<string>());

		private static HtmlNode ScriptNode() => HtmlNode.CreateNode("<script></script>");

		private void WriteHtml(string filename, string html) =>
			File.WriteAllText(Path.Combine(_dir, filename), html,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

		private BulkScriptScanner.Result RunScan(
			IReadOnlyList<string> dictionaries,
			IReadOnlyDictionary<string, DictionaryBundle> bundles)
			=> BulkScriptScanner.Run(
				_dir, "*.html", _blob, _findings,
				dictionaries, bundles,
				Array.Empty<string>(), Array.Empty<string>(),
				f => "https://site.test/" + f);

		// ── ScanText ──────────────────────────────────────────────────────────

		[Fact]
		public void ScanText_GateOff_FlagsMisspelledLiteral()
		{
			var outw = new StringWriter();
			var outcome = BulkScriptScanner.ScanText(
				"var t = \"hallo wolrd\";", _ => "src", En, Checker(Bundle("hallo")), ScriptNode(), outw);

			Assert.Equal(1, outcome.Findings);
			Assert.Contains("wolrd", outw.ToString());
		}

		[Fact]
		public void ScanText_LiteralWithReplacementChar_SkippedWhole()
		{
			var outcome = BulkScriptScanner.ScanText(
				"var t = \"ab\uFFFDcd\";", _ => "src", En, Checker(Bundle()), ScriptNode(), new StringWriter());

			Assert.Equal(1, outcome.Skipped);
			Assert.Equal(0, outcome.Findings);
		}

		[Fact]
		public void ScanText_NoStringLiterals_ReturnsZeroOutcome()
		{
			var outcome = BulkScriptScanner.ScanText(
				"var x = 1 + 2;", _ => "src", En, Checker(Bundle()), ScriptNode(), new StringWriter());

			Assert.Equal(0, outcome.Findings);
			Assert.Equal(0, outcome.Skipped);
			Assert.Equal(0, outcome.GatedLiterals);
		}

		[Fact]
		public void ScanText_GateOn_LowMissRatio_SurvivesAndEmits()
		{
			var outw = new StringWriter();
			var trimw = new StringWriter();
			var unique = new HashSet<string>(StringComparer.Ordinal);
			var sink = new List<ScriptWordHit>();

			// 4 accepted words + 1 miss → ratio 0.2 < τ 0.4 → passes the gate.
			var outcome = BulkScriptScanner.ScanText(
				"var t = \"hallo welt guten morgen wolrd\";",
				_ => "src", En, Checker(Bundle("hallo", "welt", "guten", "morgen")), ScriptNode(), outw,
				trimmedWriter: trimw, proseRatioGate: true, proseRatioTau: 0.4,
				uniqueWords: unique, findingSink: sink);

			Assert.Equal(1, outcome.Findings);
			Assert.Equal(0, outcome.GatedLiterals);
			Assert.Contains("wolrd", outw.ToString());
			Assert.Contains("wolrd", trimw.ToString());
			Assert.Contains("wolrd", unique);
			Assert.Equal("wolrd", Assert.Single(sink).Word);
		}

		[Fact]
		public void ScanText_GateOn_HighMissRatio_DemotedWithAuditNote()
		{
			var outw = new StringWriter();
			var trimw = new StringWriter();

			// All tokens miss → ratio 1.0 ≥ τ 0.4 → literal demoted, no findings.
			var outcome = BulkScriptScanner.ScanText(
				"var t = \"wolrd zzqx qwxz frpl glmp\";",
				_ => "src", En, Checker(Bundle()), ScriptNode(), outw,
				trimmedWriter: trimw, proseRatioGate: true, proseRatioTau: 0.4);

			Assert.Equal(1, outcome.GatedLiterals);
			Assert.Equal(0, outcome.Findings);
			Assert.True(outcome.SuppressedFindings > 0);
			Assert.Contains("# gated", outw.ToString());
			Assert.DoesNotContain("wolrd", trimw.ToString()); // never reaches the trimmed log
		}

		// ── Run (harvest + scan) ────────────────────────────────────────────

		[Fact]
		public void Run_InlineScriptMisspelling_HarvestsAndFinds()
		{
			WriteHtml("p.html", "<html><body><script>var t = \"hallo wolrd\";</script></body></html>");

			var result = RunScan(En, new Dictionary<string, DictionaryBundle> { ["en"] = Bundle("hallo") });

			Assert.True(result.Blocks >= 1);
			Assert.True(result.Findings >= 1);
		}

		[Fact]
		public void Run_NoDictionaries_HarvestsButFindsNothing()
		{
			WriteHtml("p.html", "<html><body><script>var t = \"hallo wolrd\";</script></body></html>");

			var result = RunScan(new List<string>(), new Dictionary<string, DictionaryBundle>());

			Assert.True(result.Blocks >= 1); // inline body still harvested
			Assert.Equal(0, result.Findings); // scan early-returns
		}

		[Fact]
		public void Run_ExternalAndEmptyScripts_HarvestNothing()
		{
			WriteHtml("p.html",
				"<html><body><script src=\"x.js\"></script><script>   </script></body></html>");

			var result = RunScan(En, new Dictionary<string, DictionaryBundle> { ["en"] = Bundle() });

			Assert.Equal(0, result.Blocks);
			Assert.Equal(0, result.Findings);
		}

		[Fact]
		public void Run_PageWithoutScripts_HarvestsNothing()
		{
			WriteHtml("p.html", "<html><body><p>no scripts here</p></body></html>");

			var result = RunScan(En, new Dictionary<string, DictionaryBundle> { ["en"] = Bundle() });

			Assert.Equal(0, result.Blocks);
		}

		// ── TrimLiteral ─────────────────────────────────────────────────────

		[Fact]
		public void TrimLiteral_CollapsesWhitespaceAndTrims()
		{
			Assert.Equal("a b c", BulkScriptScanner.TrimLiteral("  a   b\n c  "));
		}

		[Fact]
		public void TrimLiteral_OverCap_TruncatedWithEllipsis()
		{
			var trimmed = BulkScriptScanner.TrimLiteral(new string('x', 150));
			Assert.EndsWith("…", trimmed);
			Assert.Equal(121, trimmed.Length); // 120 chars + ellipsis

			Assert.Equal("short", BulkScriptScanner.TrimLiteral("short")); // under cap unchanged
		}
	}
}
