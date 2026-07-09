using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the hardcoded universal code-vocabulary skip in <see cref="ValueClassifier.ClassifyScriptLiteral"/>:
	/// a SCRIPT string literal whose ENTIRE value is one of the baked-in JS/DOM/web code tokens is skipped
	/// with <see cref="ValueVerdict.SkipCodeVocabulary"/>.
	///
	/// Two invariants matter most:
	///  • Whole-literal match only — a code word sitting INSIDE a longer literal (a sentence, an options
	///    string, an error message) is content and stays checked. The set never reaches inside prose.
	///  • Safety property — every member is correctly spelled, so a match can only ever decline to flag a
	///    non-typo; it can never suppress a misspelling (a real typo is not in the set and stays checked).
	///
	/// Also documents ordering: the universal gate runs first, so values that are also ConfigLiterals
	/// (true/false/yes/no) are reported as <see cref="ValueVerdict.SkipConfigLiteral"/>, not CodeVocabulary.
	///
	/// All fixtures are universal code keywords or invented neutral tokens — no content from any real page.
	/// </summary>
	public class ValueClassifierCodeVocabularyTests
	{
		private static ValueVerdict Script(string v) => ValueClassifier.ClassifyScriptLiteral(v).Verdict;
		private static bool ScriptChecks(string v) => ValueClassifier.ClassifyScriptLiteral(v).ShouldCheck;

		[Theory]
		[InlineData("keydown")]
		[InlineData("onkeydown")]
		[InlineData("keyup")]
		[InlineData("onkeyup")]
		[InlineData("click")]
		[InlineData("onclick")]
		[InlineData("blur")]
		[InlineData("none")]
		[InlineData("hidden")]
		[InlineData("visible")]
		[InlineData("invisible")]
		[InlineData("undefined")]
		[InlineData("value")]
		[InlineData("type")]
		[InlineData("target")]
		[InlineData("text")]
		[InlineData("password")]
		[InlineData("submit")]
		[InlineData("select-one")]
		[InlineData("disabled")]
		[InlineData("function")]
		[InlineData("string")]
		[InlineData("var")]
		[InlineData("return")]
		[InlineData("script")]
		[InlineData("stylesheet")]
		[InlineData("css")]
		[InlineData("link")]
		[InlineData("head")]
		[InlineData("fixed")]
		[InlineData("mode")]
		[InlineData("prod")]
		[InlineData("localhost")]
		[InlineData("same-origin")]
		[InlineData("this")]
		[InlineData("action")]
		[InlineData("src")]
		[InlineData("href")]
		[InlineData("img")]
		[InlineData("MIME")]
		[InlineData("text/css")]
		[InlineData("preview")]
		[InlineData("test")]
		[InlineData("term")]
		[InlineData("body")]
		[InlineData("button")]
		public void WholeLiteralCodeWord_IsSkipped(string token)
		{
			Assert.Equal(ValueVerdict.SkipCodeVocabulary, Script(token));
			Assert.False(ScriptChecks(token));
		}

		[Theory]
		[InlineData("Value")]
		[InlineData("KEYDOWN")]
		[InlineData("Select-One")]
		[InlineData("FUNCTION")]
		public void CodeWord_MatchIsCaseInsensitive(string token)
		{
			Assert.Equal(ValueVerdict.SkipCodeVocabulary, Script(token));
		}

		// Ordering: the universal gate runs BEFORE the vocabulary set, so a value that is also a
		// ConfigLiteral is reported as SkipConfigLiteral. It is still skipped — just by the earlier rule.
		[Theory]
		[InlineData("true")]
		[InlineData("false")]
		[InlineData("yes")]
		[InlineData("no")]
		public void ConfigLiteralOverlap_ReportedAsConfigLiteral_NotCodeVocabulary(string token)
		{
			Assert.Equal(ValueVerdict.SkipConfigLiteral, Script(token));
		}

		// Whole-literal ONLY: a code word inside a longer literal is content and must stay checked.
		[Theory]
		[InlineData("klick auf value")]
		[InlineData("value muss gesetzt sein")]
		[InlineData("press keydown to continue")]
		public void CodeWordInsideLongerLiteral_IsStillChecked(string sentence)
		{
			Assert.Equal(ValueVerdict.Check, Script(sentence));
			Assert.True(ScriptChecks(sentence));
		}

		// A key=value options string is not a member of the set; it is not handled by this lever.
		// status=no,scrollbars=yes is NOT a whole-literal member of the code-vocabulary set, so this
		// rule does not fire for it. (In the full pipeline it is dropped by the separate options-string
		// lever — see ValueClassifierOptionsStringTests — not by this set.)
		[Fact]
		public void OptionsString_IsNotACodeVocabularyMember()
		{
			Assert.NotEqual(ValueVerdict.SkipCodeVocabulary, Script("status=no,scrollbars=yes"));
		}

		// Safety property: a real misspelling is not in the set and is never skipped by it.
		[Theory]
		[InlineData("Wolrd")]
		[InlineData("abcword")]
		[InlineData("compact")]
		[InlineData("widgetadapter")]
		public void NonVocabularyWord_IsStillChecked(string token)
		{
			Assert.Equal(ValueVerdict.Check, Script(token));
			Assert.True(ScriptChecks(token));
		}

		// 649 — a representative spread of the universal web/JS/CSS/media/math/crypto identifiers added
		// to the vocabulary. Whole-literal skip, same as the rest of the set.
		[Theory]
		[InlineData("typeof")]
		[InlineData("instanceof")]
		[InlineData("btoa")]
		[InlineData("ctor")]
		[InlineData("func")]
		[InlineData("stringify")]
		[InlineData("destructure")]
		[InlineData("xhr")]
		[InlineData("ecmascript")]
		[InlineData("colspan")]
		[InlineData("rowspan")]
		[InlineData("tabindex")]
		[InlineData("figcaption")]
		[InlineData("srcset")]
		[InlineData("oklch")]
		[InlineData("woff")]
		[InlineData("hls")]
		[InlineData("dts")]
		[InlineData("pssh")]
		[InlineData("transmuxer")]
		[InlineData("jwk")]
		[InlineData("oidc")]
		[InlineData("hypot")]
		[InlineData("cbrt")]
		[InlineData("lgamma")]
		[InlineData("pinv")]
		[InlineData("redux")]
		[InlineData("mathjs")]
		[InlineData("lottie")]
		[InlineData("crc")]
		[InlineData("ufeff")]
		public void Universal649CodeWord_IsSkipped(string token)
		{
			Assert.Equal(ValueVerdict.SkipCodeVocabulary, Script(token));
			Assert.False(ScriptChecks(token));
		}

		// 649 conservatism, pinned: the tokens we deliberately did NOT add — a genuine code typo
		// ("octect"→octet), a real English word ("writable"), German typos that are the whole point of
		// the tool ("Ausstatung", "Maximalendite"), and valid German compounds that are dictionary
		// candidates not code ("Renditeentwicklung") — all stay CHECKED. The code set must never grow to
		// swallow these.
		[Theory]
		[InlineData("octect")]
		[InlineData("writable")]
		[InlineData("Ausstatung")]
		[InlineData("Maximalendite")]
		[InlineData("Renditeentwicklung")]
		public void DeliberatelyExcluded_StaysChecked(string token)
		{
			Assert.Equal(ValueVerdict.Check, Script(token));
			Assert.True(ScriptChecks(token));
		}
	}
}
