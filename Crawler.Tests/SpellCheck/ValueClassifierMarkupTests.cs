using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery 631: ClassifyScriptLiteral skips HTML/DOM markup being constructed in JS (a literal,
	/// often a concatenation FRAGMENT, carrying tag delimiters and/or name="…" attribute assignments).
	/// Threshold is two or more structural markers, so genuine prose that merely mentions a single tag
	/// or attribute stays CHECKED. All fixtures are synthetic and neutral. The flagged words this gate
	/// removes (href, onclick, class, src, …) are generic HTML/JS tokens, never site-specific data.
	/// </summary>
	public class SpellCheckValueClassifierMarkupTests
	{
		// ---- markup construction → SkipMarkup ----

		[Theory]
		// Smallest leaking fragment: one tag opener + one attribute = 2 markers.
		[InlineData("<div id=\"")]
		// A typical concatenation fragment: several tags + attributes.
		[InlineData("\" class=\"outer wrapper visible\"><div class=\"inner\"><a href=\"#\" title=\"")]
		// Closing-tag + attribute fragment.
		[InlineData("-icon\" onclick=\"return doClose(this);\"></a><div class=\"box\"><img src=\"")]
		// Two attributes, no tag.
		[InlineData("class=\"a\" id=\"b\"")]
		// Two bare tags, no attribute (pure structural construction).
		[InlineData("<ul><li>")]
		public void SkipsMarkupConstruction(string literal)
		{
			var r = ValueClassifier.ClassifyScriptLiteral(literal);
			Assert.False(r.ShouldCheck);
			Assert.Equal(ValueVerdict.SkipMarkup, r.Verdict);
		}

		// ---- prose with at most one stray marker → still CHECKED ----

		[Theory]
		// A bare '<' in prose is not a tag (no letter after '<').
		[InlineData("Der Preis liegt bei unter < 100 Euro pro Monat heute")]
		// A single tag mention in an otherwise prose sentence (1 marker).
		[InlineData("Bitte nutzen Sie das <div> Element fuer das Layout hier")]
		// A single attribute reference in prose (1 marker).
		[InlineData("Setzen Sie das Attribut class=\"foo\" im Vorlagentext ein")]
		// Ordinary prose with quotes but no markup markers.
		[InlineData("Er sagte freundlich \"Guten Tag\" und ging dann weiter")]
		public void KeepsProseWithAtMostOneMarker(string literal)
		{
			var r = ValueClassifier.ClassifyScriptLiteral(literal);
			Assert.True(r.ShouldCheck);
			Assert.Equal(ValueVerdict.Check, r.Verdict);
		}

		[Fact]
		public void MarkupSkip_DoesNotAffectTextNodeProse_OnlyScriptGateChanged()
		{
			// The universal Classify gate (text nodes / attributes) is unchanged: a multi-word value
			// that happens to contain markup markers is still a prose candidate there — only the script
			// literal gate adds the markup skip.
			var universal = ValueClassifier.Classify("<div class=\"x\"><a href=\"#\">");
			Assert.True(universal.ShouldCheck);
		}
	}
}
