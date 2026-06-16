using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// 656 — pins TokensToFilter on the file-scan path. A configured token drops a literal that EXACTLY
	/// equals it (whole-literal, case-insensitive), but the filter is single-token-safe: it can never
	/// reach inside a multi-word literal, so a real misspelling riding alongside a configured token still
	/// surfaces. That safety property is what makes enabling suppression on the file scan sound.
	///
	/// The whole-literal cases run with the prose-ratio gate OFF to isolate the FILTER (a lone all-miss
	/// literal would otherwise be gated too, masking which mechanism acted). The safety case runs with
	/// the gate ON — the real file-scan config. Uses a fake <see cref="RunCheck"/>; fixtures invented.
	/// </summary>
	public class RunCheckerScriptFileScanFilterTests
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

		private static IReadOnlySet<string> Filter(params string[] tokens) =>
			new HashSet<string>(tokens, System.StringComparer.OrdinalIgnoreCase);

		private static List<SpellFinding> Check(string text, IReadOnlySet<string>? filter, bool gate, params string[] flag) =>
			RunChecker.Check(ScriptRun(text), "de", FlagWords(flag),
				scriptTokensToFilter: filter,
				scriptProseRatioGate: gate, scriptProseRatioTau: 0.4).ToList();

		[Fact]
		public void WholeLiteralEqualsConfiguredToken_Suppressed()
		{
			// Gate off, so the empty result is the FILTER's doing — not the prose-ratio gate.
			Assert.Empty(Check("Displaybox", Filter("Displaybox"), gate: false, "Displaybox"));
		}

		[Fact]
		public void Match_IsCaseInsensitive()
		{
			Assert.Empty(Check("DISPLAYBOX", Filter("displaybox"), gate: false, "DISPLAYBOX"));
		}

		[Fact]
		public void NullFilter_TokenSurfaces_IsolatesTheFilter()
		{
			// Same literal, same gate-off, but no filter — the token surfaces. This is the contrast that
			// proves the suppression above is the filter, not some other gate.
			var f = Assert.Single(Check("Displaybox", null, gate: false, "Displaybox"));
			Assert.Equal("Displaybox", f.Word);
		}

		[Fact]
		public void ConfiguredToken_CannotHideTypo_InMultiWordLiteral()
		{
			// THE safety property, with the real file-scan gate ON: "Displaybox" is configured, but the
			// literal is multi-word so the whole-literal filter does not match — the literal is still
			// checked and the real typo (Maximaleichweite, 1/3 tokens, under τ) surfaces. Suppression can
			// never mask a misspelling.
			var f = Assert.Single(Check("Displaybox Maximaleichweite ok", Filter("Displaybox"), gate: true, "Maximaleichweite"));
			Assert.Equal("Maximaleichweite", f.Word);
		}
	}
}
