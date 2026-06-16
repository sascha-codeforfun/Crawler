using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the 644 Script TOKEN VETO on <see cref="RunChecker"/>: on the gate (file-scan) path, a
	/// literal that passes the prose-ratio gate (low miss-ratio) can still carry a lone non-word token
	/// — a code identifier, ALLCAPS acronym, single char, universal code term, or lowercase slug — that
	/// leaked only because it rode inside a multi-word literal. The veto drops such a token while
	/// leaving a genuine misspelling untouched. Language-agnostic: it can never suppress a real
	/// misspelling in any configured dictionary. The veto is OFF unless the gate flag is on, so the
	/// live per-page path is unaffected.
	///
	/// Uses a fake <see cref="RunCheck"/> — "miss" = a token the fake is told to flag. Fixtures embed
	/// the target token among unflagged ("known") words so the literal stays under the ratio threshold
	/// and the test isolates the per-token veto, not the literal gate. Fixtures are invented.
	/// </summary>
	public class RunCheckerScriptTokenVetoTests
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

		private static List<SpellFinding> Gated(string text, params string[] flag) =>
			RunChecker.Check(ScriptRun(text), "de", FlagWords(flag),
				scriptProseRatioGate: true, scriptProseRatioTau: 0.4).ToList();

		[Fact]
		public void CamelCaseIdentifier_Vetoed()
		{
			// Low ratio (1/6) so the literal is kept; the camelCase token is dropped by the veto.
			var findings = Gated("this is fine useEffect here ok", "useEffect");
			Assert.Empty(findings);
		}

		[Fact]
		public void AllCapsAcronym_Vetoed()
		{
			var findings = Gated("open the MUI panel now please", "MUI");
			Assert.Empty(findings);
		}

		[Fact]
		public void SingleChar_Vetoed()
		{
			var findings = Gated("the value of x is set here now", "x");
			Assert.Empty(findings);
		}

		[Fact]
		public void CodeVocabToken_Vetoed()
		{
			var findings = Gated("we parse the json then move on", "json");
			Assert.Empty(findings);
		}

		[Fact]
		public void LowercaseKebabSlug_Vetoed()
		{
			var findings = Gated("set the aria-labelledby attribute here now", "aria-labelledby");
			Assert.Empty(findings);
		}

		[Fact]
		public void PlainMisspelling_Survives()
		{
			// A word-shaped miss is NOT a slug/acronym/code term → the veto leaves it, and it surfaces.
			var findings = Gated("this is fine wolrd here ok", "wolrd");
			var f = Assert.Single(findings);
			Assert.Equal("wolrd", f.Word);
		}

		[Fact]
		public void Veto_Off_WhenGateFlagOff()
		{
			// Default (no gate params) = the live path: the veto does not run, so the camelCase token
			// surfaces exactly as before 644.
			var findings = RunChecker.Check(ScriptRun("this is fine useEffect here ok"), "de", FlagWords("useEffect")).ToList();
			var f = Assert.Single(findings);
			Assert.Equal("useEffect", f.Word);
		}

		// ── Regression anchors: a real German prose typo in a minified bundle must survive every gate.
		// Fixtures are GENERIC (invented German prose, no real content): the property under test is the
		// SHAPE — a capitalized German-noun misspelling embedded among valid words reads as prose to the
		// ratio gate (low miss-ratio → kept) and is not a slug/acronym/code token (→ not vetoed), so it
		// surfaces; clean German prose surfaces nothing.

		[Fact]
		public void GermanNounTypo_InProseLiteral_SurvivesGateAndVeto()
		{
			// One capitalized-noun typo among six valid German words → ratio 1/7 ≈ 0.14 < τ (kept), and
			// "Beratunng" is a plain capitalized word (no interior cap/digit/slug shape) → not vetoed.
			var gate = new RunChecker.ScriptGateInfo();
			var findings = RunChecker.Check(
				ScriptRun("Wir bieten Ihnen eine umfassende Beratunng an"), "de", FlagWords("Beratunng"),
				scriptProseRatioGate: true, scriptProseRatioTau: 0.4, scriptGateInfo: gate).ToList();

			Assert.False(gate.Gated);
			var f = Assert.Single(findings);
			Assert.Equal("Beratunng", f.Word);
		}

		[Fact]
		public void CleanGermanProse_SurfacesNothing()
		{
			// The same sentence correctly spelled (nothing flagged) → no findings: the gate/veto do not
			// false-positive on valid German prose sitting in a bundle.
			var findings = RunChecker.Check(
				ScriptRun("Wir bieten Ihnen eine umfassende Beratung an"), "de", FlagWords(),
				scriptProseRatioGate: true, scriptProseRatioTau: 0.4).ToList();

			Assert.Empty(findings);
		}
	}
}
