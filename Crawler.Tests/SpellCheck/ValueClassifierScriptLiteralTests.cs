using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins <see cref="ValueClassifier.ClassifyScriptLiteral"/> — the gate for a decoded JavaScript
	/// string literal. It reuses the universal value gate and the data-* shape heuristics (CSS
	/// selector, id-ref list, machine slug) and adds two script-specific single-token skips
	/// (leading underscore, acronym-word), with a deliberate case policy for alpha tokens.
	///
	/// Also guards that the additions did NOT change the behaviour of the existing, untouched
	/// <see cref="ValueClassifier.Classify"/> and <see cref="ValueClassifier.ClassifyDataAttribute"/>.
	///
	/// All fixtures are invented, neutral tokens — no content from any real crawled page.
	/// </summary>
	public class ValueClassifierScriptLiteralTests
	{
		private static ValueVerdict Script(string v) => ValueClassifier.ClassifyScriptLiteral(v).Verdict;
		private static bool ScriptChecks(string v) => ValueClassifier.ClassifyScriptLiteral(v).ShouldCheck;

		// ---- case policy for a single alpha token ----

		[Theory]
		[InlineData("Hello")]   // Title-case prose — a lone leading capital is never a signal
		[InlineData("HELLO")]   // all-caps, no trailing lowercase — kept (shouted word / brand)
		[InlineData("ZZZ")]     // all-caps
		[InlineData("Berlin")]  // Title-case proper noun
		[InlineData("Apfel")]   // Title-case word
		public void AlphaToken_TitleOrAllCaps_IsChecked(string v)
		{
			Assert.True(ScriptChecks(v));
			Assert.Equal(ValueVerdict.Check, Script(v));
		}

		[Theory]
		[InlineData("HEllo")]      // two leading caps then lower — unnatural
		[InlineData("HELlo")]      // caps run then lower
		[InlineData("ABCwidget")]  // acronym glued to a word
		[InlineData("XMLreader")]
		[InlineData("IObox")]      // minimal: exactly two caps then lower
		public void AlphaToken_AcronymWord_IsSkipped(string v)
		{
			Assert.False(ScriptChecks(v));
			Assert.Equal(ValueVerdict.SkipAcronymWord, Script(v));
		}

		// ---- leading underscore ----

		[Theory]
		[InlineData("_top")]
		[InlineData("_init")]
		[InlineData("_id")]
		public void LeadingUnderscore_IsSkipped(string v)
			=> Assert.Equal(ValueVerdict.SkipLeadingUnderscore, Script(v));

		[Fact]
		public void TooShortUnderscoreToken_FallsToBaseGate_NotUnderscoreRule()
			// "_x" is below the prose floor, so the universal gate returns SkipTooShort first; the
			// leading-underscore rule is never reached. Pins that the base gate wins on length.
			=> Assert.Equal(ValueVerdict.SkipTooShort, Script("_x"));

		// ---- reused shape heuristics still fire on the script path ----

		[Theory]
		[InlineData("fooBar")]    // camelCase — internal lower->upper
		[InlineData("nextItem")]
		public void CamelCase_IsSkippedAsSlug(string v)
			=> Assert.Equal(ValueVerdict.SkipSlug, Script(v));

		[Fact]
		public void LetterDigitMix_IsSkippedAsSlug()
			=> Assert.Equal(ValueVerdict.SkipSlug, Script("item42"));

		// ---- universal gate still applies first ----

		[Theory]
		[InlineData("/a/b/c", ValueVerdict.SkipPath)]
		[InlineData("true", ValueVerdict.SkipConfigLiteral)]
		[InlineData("ab", ValueVerdict.SkipTooShort)]
		[InlineData("", ValueVerdict.SkipTooShort)]
		public void UniversalGate_AppliesBeforeScriptHeuristics(string v, ValueVerdict expected)
			=> Assert.Equal(expected, Script(v));

		[Fact]
		public void StructuredLiteral_IsSkippedByBaseGate()
			// A JSON-in-a-string payload is dropped wholesale (its inner prose is not reached).
			=> Assert.Equal(ValueVerdict.SkipStructured, Script("{\"k\": \"v\"}"));

		// ---- prose is kept ----

		[Theory]
		[InlineData("compact")]        // single lowercase word
		[InlineData("widgetadapter")]  // lowercase run-together word — no machine signal, kept
		public void LowercaseWord_IsChecked(string v)
			=> Assert.Equal(ValueVerdict.Check, Script(v));

		[Theory]
		[InlineData("Welcome aboard")]
		[InlineData("two short words")]
		public void MultiWordValue_IsChecked(string v)
			=> Assert.Equal(ValueVerdict.Check, Script(v));

		[Fact]
		public void MultiWordValue_WithOddTokenInside_IsNotDroppedWholesale()
		{
			// An acronym-word sitting inside a sentence must NOT drop the whole value; the single
			// odd token is the tokenizer's concern, not this whole-value gate's.
			Assert.True(ScriptChecks("Visit IObox now"));
			Assert.Equal(ValueVerdict.Check, Script("Visit IObox now"));
		}

		// ---- non-regression: the untouched methods behave exactly as before ----

		[Fact]
		public void Classify_Unchanged_CamelCasePassesUniversalGate()
			// The universal gate is deliberately prose-leaning: it does NOT skip a camelCase token
			// (that is a data-*/script heuristic). This pins that the edit left Classify alone.
			=> Assert.Equal(ValueVerdict.Check, ValueClassifier.Classify("fooBar").Verdict);

		[Fact]
		public void ClassifyDataAttribute_Unchanged_CamelCaseIsSlug()
			=> Assert.Equal(ValueVerdict.SkipSlug, ValueClassifier.ClassifyDataAttribute("fooBar").Verdict);

		[Fact]
		public void ClassifyDataAttribute_Unchanged_DoesNotApplyScriptSkips()
		{
			// The data-* gate has no leading-underscore or acronym-word rule; those are script-only.
			Assert.Equal(ValueVerdict.Check, ValueClassifier.ClassifyDataAttribute("_top").Verdict);
			Assert.Equal(ValueVerdict.Check, ValueClassifier.ClassifyDataAttribute("IObox").Verdict);
		}
	}
}
