using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the 643 Script prose-ratio gate on <see cref="RunChecker"/>: when the gate flag is on
	/// (the external-.js file-scan path), a literal whose WORD tokens are mostly union-misses is
	/// demoted WHOLE and emits nothing; a sentence carrying a single typo is KEPT so the typo still
	/// surfaces. The gate is OFF by default, so the live per-page path is provably unaffected. Also
	/// pins the class-2 placeholder strip (##tag## → space) on the same gated path.
	///
	/// Uses a fake <see cref="RunCheck"/> — no Hunspell/dictionaries. "Miss" = a token the fake is
	/// told to flag; everything else is treated as a known word. Fixtures are invented.
	/// </summary>
	public class RunCheckerScriptRatioGateTests
	{
		private static TextRun ScriptRun(string text) =>
			new(HtmlNode.CreateNode("<script>x</script>"), RunSource.Script, "script[L1:1]", text);

		private static RunCheck FlagWords(params string[] words)
		{
			var set = new HashSet<string>(words);
			return (canonicalText, lang) =>
				SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", canonicalText))
					.Select(t => t.Text)
					.Where(set.Contains)
					.Distinct()
					.Select(w => new CheckMiss(w, "suggestion"));
		}

		[Fact]
		public void GatedMachineList_Demoted_NoFindings()
		{
			// Three word tokens, all non-words → ratio 1.0 ≥ τ → whole literal demoted.
			var gate = new RunChecker.ScriptGateInfo();
			var findings = RunChecker.Check(
				ScriptRun("foo bar baz"), "de", FlagWords("foo", "bar", "baz"),
				scriptProseRatioGate: true, scriptProseRatioTau: 0.4, scriptGateInfo: gate).ToList();

			Assert.Empty(findings);
			Assert.True(gate.Evaluated);
			Assert.True(gate.Gated);
			Assert.Equal(3, gate.TotalTokens);
			Assert.Equal(3, gate.MissTokens);
		}

		[Fact]
		public void SingleUnknownToken_Demoted()
		{
			// The 73% bucket: a bare unknown token (a DOM id / event / glyph name) → 1/1 = 1.0 → gated.
			var findings = RunChecker.Check(
				ScriptRun("foobar"), "de", FlagWords("foobar"),
				scriptProseRatioGate: true, scriptProseRatioTau: 0.4).ToList();

			Assert.Empty(findings);
		}

		[Fact]
		public void SentenceWithOneTypo_Kept_TypoSurfaces()
		{
			// One miss among three words → ratio 0.33 < τ → kept; the typo still surfaces.
			var gate = new RunChecker.ScriptGateInfo();
			var findings = RunChecker.Check(
				ScriptRun("hallo wolrd freund"), "de", FlagWords("wolrd"),
				scriptProseRatioGate: true, scriptProseRatioTau: 0.4, scriptGateInfo: gate).ToList();

			var f = Assert.Single(findings);
			Assert.Equal("wolrd", f.Word);
			Assert.False(gate.Gated);
			Assert.Equal(3, gate.TotalTokens);
			Assert.Equal(1, gate.MissTokens);
		}

		[Fact]
		public void GateOff_MachineList_NotDemoted_LivePathUnaffected()
		{
			// Default (no gate params) = the live/inline contract: the same all-miss list is NOT
			// demoted, so every token surfaces. This is exactly what the file-scan gate changes,
			// and proves leaving the flag off keeps the proven path byte-for-byte.
			var findings = RunChecker.Check(ScriptRun("foo bar baz"), "de", FlagWords("foo", "bar", "baz")).ToList();
			Assert.Equal(3, findings.Count);
		}

		[Theory]
		[InlineData(0.5, true)]   // 1/2 = 0.50, τ=0.50 → gated (>= semantics)
		[InlineData(0.6, false)]  // 1/2 = 0.50 < τ=0.60 → kept
		public void RatioAtThreshold_UsesGreaterOrEqual(double tau, bool expectGated)
		{
			var gate = new RunChecker.ScriptGateInfo();
			var findings = RunChecker.Check(
				ScriptRun("foo bar"), "de", FlagWords("foo"),
				scriptProseRatioGate: true, scriptProseRatioTau: tau, scriptGateInfo: gate).ToList();

			Assert.Equal(expectGated, gate.Gated);
			Assert.Equal(expectGated ? 0 : 1, findings.Count);
		}

		[Fact]
		public void PlaceholderStrip_RemovesMergeTag_KeepsProse()
		{
			// ##WZ## is stripped before tokenizing: it never flags (even though the fake is told to),
			// the surrounding prose is recovered, and a real typo within it still surfaces.
			var findings = RunChecker.Check(
				ScriptRun("Hallo ##WZ## schoenes Wetter heute wolrd"), "de", FlagWords("WZ", "wolrd"),
				scriptProseRatioGate: true, scriptProseRatioTau: 0.4).ToList();

			var f = Assert.Single(findings);
			Assert.Equal("wolrd", f.Word);
			Assert.DoesNotContain(findings, x => x.Word == "WZ");
		}

		[Fact]
		public void Gate_DoesNotRun_WhenFlagOff_EvenForScript()
		{
			// The ScriptGateInfo is left untouched (Evaluated false) when the flag is off — the caller
			// then writes no audit note, and behaviour is identical to 642.
			var gate = new RunChecker.ScriptGateInfo();
			RunChecker.Check(ScriptRun("foo bar baz"), "de", FlagWords("foo", "bar", "baz"), scriptGateInfo: gate).ToList();
			Assert.False(gate.Evaluated);
		}
	}
}
