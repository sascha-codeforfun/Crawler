using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery: the whole-value classifier. Every fixture is SYNTHETIC and neutral — invented
	/// to exercise a category, never lifted from any crawled site. Tests assert the category
	/// behaviour (a deep path is skipped), not any specific real value.
	///
	/// The classifier decides prose-candidate vs technical-skip from value SHAPE only, never
	/// from the attribute name.
	/// </summary>
	public class SpellCheckValueClassifierTests
	{
		// ---- technical values: skipped before tokenization ----

		[Theory]
		[InlineData("https://example.com/page")]
		[InlineData("http://host/a")]
		[InlineData("mailto:user@example.com")]
		[InlineData("tel:+10000000")]
		public void SkipsUrlScheme(string value)
		{
			var r = ValueClassifier.Classify(value);
			Assert.False(r.ShouldCheck);
			Assert.Equal(ValueVerdict.SkipUrl, r.Verdict);
		}

		[Theory]
		[InlineData("/section/page.html")]
		[InlineData("alpha/beta/gamma")]
		public void SkipsPath(string value)
		{
			var r = ValueClassifier.Classify(value);
			Assert.False(r.ShouldCheck);
			Assert.Equal(ValueVerdict.SkipPath, r.Verdict);
		}

		[Fact]
		public void SkipsQueryString()
		{
			var r = ValueClassifier.Classify("foo?key=one&other=two");
			Assert.False(r.ShouldCheck);
			Assert.Equal(ValueVerdict.SkipQuery, r.Verdict);
		}

		[Fact]
		public void SkipsTemplatePlaceholder()
		{
			var r = ValueClassifier.Classify("prefix {{token}} suffix");
			Assert.False(r.ShouldCheck);
			Assert.Equal(ValueVerdict.SkipTemplate, r.Verdict);
		}

		[Theory]
		[InlineData("{\"a\":\"b\"}")]
		[InlineData("[one,two]")]
		public void SkipsStructured(string value)
		{
			var r = ValueClassifier.Classify(value);
			Assert.False(r.ShouldCheck);
			Assert.Equal(ValueVerdict.SkipStructured, r.Verdict);
		}

		[Fact]
		public void SkipsLongDigitRun()
		{
			var r = ValueClassifier.Classify("12345678");
			Assert.False(r.ShouldCheck);
			Assert.Equal(ValueVerdict.SkipDigits, r.Verdict);
		}

		[Fact]
		public void SkipsLongHexHash()
		{
			var r = ValueClassifier.Classify("0123456789abcdef0123");
			Assert.False(r.ShouldCheck);
			Assert.Equal(ValueVerdict.SkipHex, r.Verdict);
		}

		[Fact]
		public void SkipsHighEntropyToken()
		{
			// Invented base64-ish key shape; high Shannon entropy, single token.
			var r = ValueClassifier.Classify("aZ9bX2qW7vK3mP1r");
			Assert.False(r.ShouldCheck);
			Assert.Equal(ValueVerdict.SkipHighEntropy, r.Verdict);
		}

		[Theory]
		[InlineData("true")]
		[InlineData("false")]
		[InlineData("null")]
		public void SkipsConfigLiteral(string value)
		{
			var r = ValueClassifier.Classify(value);
			Assert.False(r.ShouldCheck);
			Assert.Equal(ValueVerdict.SkipConfigLiteral, r.Verdict);
		}

		[Theory]
		[InlineData("ab")]
		[InlineData("x")]
		public void SkipsTooShortSingleToken(string value)
		{
			var r = ValueClassifier.Classify(value);
			Assert.False(r.ShouldCheck);
			Assert.Equal(ValueVerdict.SkipTooShort, r.Verdict);
		}

		[Fact]
		public void SkipsEmpty()
		{
			var r = ValueClassifier.Classify("   ");
			Assert.False(r.ShouldCheck);
		}

		// ---- prose values: passed on to the tokenizer + per-token gate ----

		[Fact]
		public void ChecksMultiWordSentence()
		{
			var r = ValueClassifier.Classify("Dies ist ein Satz");
			Assert.True(r.ShouldCheck);
			Assert.Equal(ValueVerdict.Check, r.Verdict);
		}

		[Fact]
		public void ChecksSingleLowEntropyWord()
		{
			// A long compound-style single word — low entropy, letters only: prose candidate.
			var r = ValueClassifier.Classify("Beispielwort");
			Assert.True(r.ShouldCheck);
			Assert.Equal(ValueVerdict.Check, r.Verdict);
		}

		[Fact]
		public void ChecksProseEvenWithEmbeddedHyphenWord()
		{
			var r = ValueClassifier.Classify("ein gut-formuliertes Beispiel");
			Assert.True(r.ShouldCheck);
		}

		// ---- data-* heuristic gate (ClassifyDataAttribute): shape skips layered on the base gate.
		// Every fixture is SYNTHETIC and neutral, invented to exercise a shape category.

		[Theory]
		[InlineData(".foo .bar:nth-of-type(2)")]   // leading class selector + pseudo
		[InlineData("#main .item")]                 // leading id selector
		[InlineData(".panel:hover")]                // leading class + pseudo
		[InlineData(".a > .b")]                      // child combinator
		[InlineData("ul li:first-child")]           // no leading .# but a CSS pseudo
		public void DataAttribute_SkipsCssSelector(string value)
		{
			var r = ValueClassifier.ClassifyDataAttribute(value);
			Assert.False(r.ShouldCheck);
			Assert.Equal(ValueVerdict.SkipCssSelector, r.Verdict);
		}

		[Theory]
		[InlineData("alpha-label beta-label")]      // hyphenated id refs
		[InlineData("field-1 field-2")]             // id refs with digits
		[InlineData("one_a two_b three_c")]         // underscore id refs
		public void DataAttribute_SkipsIdRefList(string value)
		{
			var r = ValueClassifier.ClassifyDataAttribute(value);
			Assert.False(r.ShouldCheck);
			Assert.Equal(ValueVerdict.SkipIdRefList, r.Verdict);
		}

		[Theory]
		[InlineData("showWidget")]                  // camelCase (internal lower→upper)
		[InlineData("innerPanelBox")]               // camelCase, multiple transitions
		[InlineData("MyWidgetName")]                // PascalCase (internal transitions)
		[InlineData("svc.module.one")]              // dotted alphanumeric segments
		[InlineData("alpha.beta")]                  // dotted
		[InlineData("code7x")]                      // letter + digit mixing
		[InlineData("x1y2")]                        // letter + digit mixing
		public void DataAttribute_SkipsMachineSlug(string value)
		{
			var r = ValueClassifier.ClassifyDataAttribute(value);
			Assert.False(r.ShouldCheck);
			Assert.Equal(ValueVerdict.SkipSlug, r.Verdict);
		}

		// SAFETY: prose-or-ambiguous values must stay CHECKED. A bare single token — all-lowercase
		// OR all-caps — with no other machine signal is never skipped (shape cannot tell it from a
		// misspelled word). Multi-word phrases and single-hyphen lowercase compounds also stay.
		[Theory]
		[InlineData("samplevalue")]                 // bare lowercase word
		[InlineData("SAMPLE")]                      // bare ALL-CAPS word (shouted word / brand)
		[InlineData("BRANDNAME")]                   // bare ALL-CAPS, longer
		[InlineData("Sample")]                      // single capitalized word (caps only at index 0)
		[InlineData("alpha-beta")]                  // single-hyphen lowercase compound — ambiguous
		[InlineData("alpha beta")]                  // two-word lowercase phrase
		[InlineData("Alpha Beta")]                  // two-word capitalized phrase
		[InlineData("echte saubere Worte")]         // multi-word prose
		public void DataAttribute_ChecksProseOrAmbiguousValue(string value)
		{
			var r = ValueClassifier.ClassifyDataAttribute(value);
			Assert.True(r.ShouldCheck);
			Assert.Equal(ValueVerdict.Check, r.Verdict);
		}

		[Theory]
		[InlineData("https://example.com/page")]    // base url skip still applies through the data gate
		[InlineData("/section/page")]               // base path skip
		[InlineData("12345678")]                    // base digit skip
		public void DataAttribute_StillAppliesBaseGate(string value)
		{
			var r = ValueClassifier.ClassifyDataAttribute(value);
			Assert.False(r.ShouldCheck);
			Assert.NotEqual(ValueVerdict.SkipCssSelector, r.Verdict);
			Assert.NotEqual(ValueVerdict.SkipIdRefList, r.Verdict);
			Assert.NotEqual(ValueVerdict.SkipSlug, r.Verdict);
		}

		// ---- 607: exact positional-keyword value test (the caller adds the "align" name guard).
		// Accepts only an exact, correctly-spelled keyword in all-lowercase or Title-case.

		[Theory]
		[InlineData("top")]
		[InlineData("right")]
		[InlineData("bottom")]
		[InlineData("left")]
		[InlineData("center")]
		[InlineData("middle")]
		[InlineData("Center")]    // Title-case accepted
		[InlineData("Left")]
		[InlineData("Top")]
		public void IsExactPositionalKeyword_AcceptsExactKeyword(string value)
		{
			Assert.True(ValueClassifier.IsExactPositionalKeyword(value));
		}

		[Theory]
		[InlineData("cEnter")]      // odd internal casing
		[InlineData("CENTER")]      // all-caps (shouted / ambiguous)
		[InlineData("lEft")]
		[InlineData("middel")]      // a MISSPELLING — must stay checkable
		[InlineData("centre")]      // British spelling, not a set member
		[InlineData("centered")]    // not an exact match
		[InlineData("center-block")]// not an exact match
		[InlineData("middle top")]  // multi-word
		[InlineData("links")]       // non-English positional word, not in the set
		[InlineData("")]
		public void IsExactPositionalKeyword_RejectsEverythingElse(string value)
		{
			Assert.False(ValueClassifier.IsExactPositionalKeyword(value));
		}

		// ---- 608-pre: pin the id-ref-list REJECT branches (the prose-protection safety arms of
		// IsIdShapedToken) and null/empty robustness on the value entry points. Test-only.

		[Theory]
		[InlineData("alpha.one beta-two")]   // a token with a dot disqualifies the list
		[InlineData("good-one bad.two")]      // second token has a dot
		public void DataAttribute_IdRefList_RejectsOddCharToken_StaysChecked(string value)
		{
			// A whitespace value where a token carries a non-id char (dot/diacritic) is NOT an
			// id-ref list — it falls through to checked, protecting genuine content.
			var r = ValueClassifier.ClassifyDataAttribute(value);
			Assert.True(r.ShouldCheck);
			Assert.Equal(ValueVerdict.Check, r.Verdict);
		}

		[Theory]
		[InlineData("Alpha-one beta-two")]    // capitalized-leading token disqualifies the list
		[InlineData("Item-1 Item-2")]         // both capitalized
		public void DataAttribute_IdRefList_RejectsCapitalizedToken_StaysChecked(string value)
		{
			// An id-shaped token must lead with a lowercase letter; a capitalized token (the shape
			// of a real word) keeps the whole value checked rather than suppressed as id-refs.
			var r = ValueClassifier.ClassifyDataAttribute(value);
			Assert.True(r.ShouldCheck);
			Assert.Equal(ValueVerdict.Check, r.Verdict);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		public void Classify_NullOrEmpty_DoesNotCheck_AndDoesNotThrow(string? value)
		{
			var r = ValueClassifier.Classify(value!);
			Assert.False(r.ShouldCheck);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("   ")]
		public void ClassifyDataAttribute_NullOrWhitespace_DoesNotCheck_AndDoesNotThrow(string? value)
		{
			var r = ValueClassifier.ClassifyDataAttribute(value!);
			Assert.False(r.ShouldCheck);
		}

		[Theory]
		[InlineData(null, false)]
		[InlineData("   ", false)]
		[InlineData("  center  ", true)]   // surrounding whitespace is trimmed before the exact test
		[InlineData("  Center  ", true)]
		public void IsExactPositionalKeyword_HandlesNullAndWhitespace(string? value, bool expected)
		{
			Assert.Equal(expected, ValueClassifier.IsExactPositionalKeyword(value!));
		}
	}
}
