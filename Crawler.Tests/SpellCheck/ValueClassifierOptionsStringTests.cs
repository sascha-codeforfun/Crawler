using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the options/feature/query-string skip in <see cref="ValueClassifier.ClassifyScriptLiteral"/>:
	/// a SCRIPT literal whose ENTIRE value is one or more "key=value" pairs joined by ',', ';' or '&'
	/// (e.g. the "status=no,scrollbars=yes" feature string handed to window.open) is dropped wholesale
	/// with <see cref="ValueVerdict.SkipOptionsString"/> rather than tokenized.
	///
	/// The shape keeps prose safe: a key with a space, a value with a space, or a segment without '='
	/// all fail, so a sentence that merely contains an '=' stays checked. Like the pre-existing
	/// query-string skip, option VALUES are treated as tokens, not prose, and are not spell-checked.
	///
	/// All fixtures are invented, neutral tokens — no content from any real crawled page.
	/// </summary>
	public class ValueClassifierOptionsStringTests
	{
		private static ValueVerdict Script(string v) => ValueClassifier.ClassifyScriptLiteral(v).Verdict;
		private static bool ScriptChecks(string v) => ValueClassifier.ClassifyScriptLiteral(v).ShouldCheck;

		[Theory]
		[InlineData("status=no,scrollbars=yes")]
		[InlineData("width=800, height=600, resizable=yes")] // spaces after the comma are tolerated
		[InlineData("mode=prod")]
		[InlineData("a=1;b=2")]
		public void OptionsString_IsSkippedWholesale(string literal)
		{
			Assert.Equal(ValueVerdict.SkipOptionsString, Script(literal));
			Assert.False(ScriptChecks(literal));
		}

		// Some option/query-shaped values are skipped by the UNIVERSAL gate before the options-string
		// rule is even reached: a dense, digit-laden feature string trips the entropy skip, and an
		// '&'-joined "key=value" list is a query string. They are still dropped (the goal) — just by an
		// earlier, broader rule. Pinned here so the interaction is explicit, not a surprise.
		[Theory]
		[InlineData("width=800,height=600,resizable=yes", ValueVerdict.SkipHighEntropy)]
		[InlineData("foo=bar&baz=qux", ValueVerdict.SkipQuery)]
		public void DenseOrQueryShaped_AreSkippedByTheUniversalGateFirst(string literal, ValueVerdict expected)
		{
			Assert.Equal(expected, Script(literal));
			Assert.False(ScriptChecks(literal));
		}

		// Prose that merely contains an '=' must stay checked.
		[Theory]
		[InlineData("Breite = 100")]              // space in the key part
		[InlineData("Preis=100 Euro und mehr")]   // space in the value part
		[InlineData("a=b, this is text")]         // a segment with no '='
		[InlineData("Willkommen bei uns")]        // no '=' at all
		public void ProseContainingEquals_IsStillChecked(string literal)
		{
			Assert.Equal(ValueVerdict.Check, Script(literal));
			Assert.True(ScriptChecks(literal));
		}

		// A plain code word (no '=') is untouched by this rule — handled by the vocabulary set instead.
		[Fact]
		public void PlainWord_IsNotAnOptionsString()
		{
			Assert.NotEqual(ValueVerdict.SkipOptionsString, Script("keydown"));
		}
	}
}
