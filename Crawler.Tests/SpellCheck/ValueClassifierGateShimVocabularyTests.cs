using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// 657 — pins the tokens added to the hardcoded <see cref="ValueClassifier.ClassifyScriptLiteral"/>
	/// code vocabulary: the iframe family, the IE @font-face shims (iefix/iebug), the precompile build
	/// verbs, and the standalone boolean/logic-gate operators (XOR/XNOR/NAND/NOR). Each is a whole-literal,
	/// case-insensitive <see cref="ValueVerdict.SkipCodeVocabulary"/>; none can reach inside a multi-word
	/// literal, so prose stays checked. ("writable" was already covered by the 649 set.)
	///
	/// The list deliberately omits AND/OR/NOT (correctly-spelled English; the en dictionary passes them)
	/// and AOI/OAI (VLSI-domain, not universal) — those choices are recorded at the set, not asserted here.
	/// </summary>
	public class ValueClassifierGateShimVocabularyTests
	{
		private static ValueVerdict Script(string v) => ValueClassifier.ClassifyScriptLiteral(v).Verdict;
		private static bool ScriptChecks(string v) => ValueClassifier.ClassifyScriptLiteral(v).ShouldCheck;

		[Theory]
		[InlineData("iframe")]
		[InlineData("iframes")]
		[InlineData("iebug")]
		[InlineData("iefix")]
		[InlineData("precompile")]
		[InlineData("precompiled")]
		[InlineData("precompiler")]
		[InlineData("XOR")]
		[InlineData("XNOR")]
		[InlineData("NAND")]
		[InlineData("NOR")]
		public void NewToken_IsSkippedAsCodeVocabulary(string token) =>
			Assert.Equal(ValueVerdict.SkipCodeVocabulary, Script(token));

		[Theory]
		[InlineData("xor")]     // lowercase
		[InlineData("Xor")]     // mixed
		[InlineData("IFRAME")]  // uppercase
		[InlineData("Iframe")]  // title
		public void Match_IsCaseInsensitive(string token) =>
			Assert.Equal(ValueVerdict.SkipCodeVocabulary, Script(token));

		[Fact]
		public void TokenInsideLongerLiteral_StaysChecked()
		{
			// Whole-literal only: a gate/shim token riding inside a sentence is content, never suppressed.
			Assert.True(ScriptChecks("iframe ist nicht geladen"));
			Assert.True(ScriptChecks("XOR der beiden Werte"));
		}
	}
}
