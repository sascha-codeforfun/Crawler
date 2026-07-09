using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the site-specific script-literal filter (config: SpellCheckJavaScript.TokensToFilter),
	/// surfaced through <see cref="ValueClassifier.ClassifyScriptLiteral"/> and threaded by
	/// <see cref="RunChecker"/>. The match is WHOLE-LITERAL and case-insensitive, with a distinct
	/// <see cref="ValueVerdict.SkipSiteToken"/> verdict so a configured drop is attributable.
	///
	/// Safety: it can only ever drop a literal that EXACTLY equals a configured token — it never
	/// reaches into a longer literal, so it cannot suppress a word inside a real sentence. A null /
	/// absent set must leave behaviour exactly as before (no filtering).
	///
	/// All fixtures are invented neutral tokens — no real site content.
	/// </summary>
	public class ValueClassifierSiteTokenTests
	{
		private static IReadOnlySet<string> Filter(params string[] tokens) =>
			new HashSet<string>(tokens, System.StringComparer.OrdinalIgnoreCase);

		[Fact]
		public void WholeLiteralMatch_IsSkippedAsSiteToken()
		{
			var v = ValueClassifier.ClassifyScriptLiteral("widgetfoo", Filter("widgetfoo"));
			Assert.Equal(ValueVerdict.SkipSiteToken, v.Verdict);
			Assert.False(v.ShouldCheck);
		}

		[Fact]
		public void Match_IsCaseInsensitive()
		{
			Assert.Equal(ValueVerdict.SkipSiteToken,
				ValueClassifier.ClassifyScriptLiteral("WidgetFoo", Filter("widgetfoo")).Verdict);
		}

		[Fact]
		public void NonMatchingLiteral_IsUnaffected_AndStillChecked()
		{
			// "componentbar" is not configured, and is ordinary-word-shaped, so it stays checkable.
			var v = ValueClassifier.ClassifyScriptLiteral("componentbar", Filter("widgetfoo"));
			Assert.NotEqual(ValueVerdict.SkipSiteToken, v.Verdict);
			Assert.True(v.ShouldCheck);
		}

		[Fact]
		public void NullFilter_BehavesAsBefore()
		{
			// Same call without a filter set: the literal is checked exactly as it was pre-619.
			Assert.True(ValueClassifier.ClassifyScriptLiteral("componentbar").ShouldCheck);
			Assert.True(ValueClassifier.ClassifyScriptLiteral("componentbar", null).ShouldCheck);
		}

		[Fact]
		public void TokenInsideLongerLiteral_DoesNotSkipTheLongerLiteral()
		{
			// Whole-literal only: configuring "widgetfoo" must NOT suppress a multiword literal that
			// merely contains it — otherwise it could hide real prose around the token.
			var v = ValueClassifier.ClassifyScriptLiteral("widgetfoo ist kaputt", Filter("widgetfoo"));
			Assert.NotEqual(ValueVerdict.SkipSiteToken, v.Verdict);
			Assert.True(v.ShouldCheck);
		}

		// --- RunChecker threading: a configured token drops the Script finding; others survive. ---

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
		public void RunChecker_ConfiguredToken_ProducesNoFinding()
		{
			// Even though the fake checker would flag it, the site filter drops the literal first.
			var findings = RunChecker.Check(
				ScriptRun("widgetfoo"), "de", FlagWords("widgetfoo"),
				scriptTokensToFilter: Filter("widgetfoo")).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void RunChecker_UnconfiguredToken_StillSurfaces()
		{
			var f = Assert.Single(RunChecker.Check(
				ScriptRun("componentbar"), "de", FlagWords("componentbar"),
				scriptTokensToFilter: Filter("widgetfoo")).ToList());
			Assert.Equal("componentbar", f.Word);
		}
	}
}
