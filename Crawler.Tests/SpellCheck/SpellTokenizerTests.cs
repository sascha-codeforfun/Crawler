using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	public class SpellTokenizerTests
	{
		// ── TokenizeText ──────────────────────────────────────────────────────

		[Fact]
		public void TokenizeText_SplitsPlainWords()
		{
			var tokens = SpellTokenizer.TokenizeText("hello world").ToList();
			Assert.Contains("hello", tokens);
			Assert.Contains("world", tokens);
		}

		[Fact]
		public void TokenizeText_PreservesHyphenatedAndUmlautWords()
		{
			var tokens = SpellTokenizer.TokenizeText("Größenänderung well-known").ToList();
			Assert.Contains("Größenänderung", tokens);
			Assert.Contains("well-known", tokens);
		}

		// ── IdentifyWord ──────────────────────────────────────────────────────

		[Theory]
		[InlineData("Hello", true)]
		[InlineData("well-known", true)]
		[InlineData("Straße", true)]
		[InlineData("привет", true)]       // Cyrillic
		[InlineData("Ελληνικά", true)]     // Greek
		[InlineData("مرحبا", true)]        // Arabic
		[InlineData("café", true)]         // precomposed diacritic (NFC)
		[InlineData("cafe\u0301", true)]   // decomposed diacritic (NFD): e + combining acute
		[InlineData("abc123", false)]   // digits are not word chars here
		[InlineData("café123", false)]  // digits still rejected, even with a diacritic
		[InlineData("123", false)]
		[InlineData("a_b", false)]      // underscore not allowed
		public void IdentifyWord_MatchesExpectedShape(string token, bool expected)
		{
			Assert.Equal(expected, SpellTokenizer.IdentifyWord(token));
		}
	}
}
