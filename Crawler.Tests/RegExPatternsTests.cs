using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for RegExPatterns — the small set of compiled/generated regex
	/// helpers used throughout normalization and tokenization. These are pure
	/// string functions with no I/O, so they need no fixture. They are central
	/// to spell-check token extraction, so the edge cases (hyphenation,
	/// umlauts, URL stripping, ISO-639-1 validation) are worth pinning down.
	/// </summary>
	public class RegExPatternsTests
	{
		// ── NormalizeHtmlTags ─────────────────────────────────────────────────

		[Fact]
		public void NormalizeHtmlTags_InsertsSpaceBetweenAdjacentTags()
		{
			// "><" between two tags becomes "> <" so text nodes don't fuse.
			var result = RegExPatterns.NormalizeHtmlTags("<p>a</p><p>b</p>");
			Assert.Contains("> <", result);
		}

		[Fact]
		public void NormalizeHtmlTags_ConvertsTabsToSpaces()
		{
			var result = RegExPatterns.NormalizeHtmlTags("a\tb");
			Assert.DoesNotContain("\t", result);
		}

		// ── TokenizeText ──────────────────────────────────────────────────────

		[Fact]
		public void TokenizeText_SplitsPlainWords()
		{
			var tokens = RegExPatterns.TokenizeText("hello world").ToList();
			Assert.Contains("hello", tokens);
			Assert.Contains("world", tokens);
		}

		[Fact]
		public void TokenizeText_PreservesHyphenatedAndUmlautWords()
		{
			var tokens = RegExPatterns.TokenizeText("Größenänderung well-known").ToList();
			Assert.Contains("Größenänderung", tokens);
			Assert.Contains("well-known", tokens);
		}

		// ── RemoveUrls ────────────────────────────────────────────────────────

		[Theory]
		[InlineData("see https://example.com/page now", "https://example.com/page")]
		[InlineData("visit www.example.com today", "www.example.com")]
		[InlineData("http://x.io/a?b=c end", "http://x.io/a?b=c")]
		public void RemoveUrls_StripsUrls(string input, string urlFragment)
		{
			var result = RegExPatterns.RemoveUrls(input);
			Assert.DoesNotContain(urlFragment, result);
		}

		[Fact]
		public void RemoveUrls_LeavesPlainTextUntouched()
		{
			const string text = "no links in this sentence";
			Assert.Equal(text, RegExPatterns.RemoveUrls(text));
		}

		// ── IdentifyWord ──────────────────────────────────────────────────────

		[Theory]
		[InlineData("Hello", true)]
		[InlineData("well-known", true)]
		[InlineData("Straße", true)]
		[InlineData("abc123", false)]   // digits are not word chars here
		[InlineData("123", false)]
		[InlineData("a_b", false)]      // underscore not allowed
		public void IdentifyWord_MatchesExpectedShape(string token, bool expected)
		{
			Assert.Equal(expected, RegExPatterns.IdentifyWord(token));
		}

		// ── IsISO6391 ─────────────────────────────────────────────────────────

		[Theory]
		[InlineData("en", true)]
		[InlineData("de", true)]
		[InlineData("EN", false)]   // pattern is lower-case only
		[InlineData("eng", false)]  // three letters
		[InlineData("e", false)]
		[InlineData("e1", false)]
		public void IsISO6391_ValidatesTwoLetterLowercaseCodes(string code, bool expected)
		{
			Assert.Equal(expected, RegExPatterns.IsISO6391(code));
		}

		// ── IsValidFilePattern ────────────────────────────────────────────────

		[Theory]
		// Valid: "*." + 1-8 alphanumeric chars.
		[InlineData("*.html", true)]
		[InlineData("*.htm", true)]
		[InlineData("*.aspx", true)]
		[InlineData("*.xhtml", true)]
		[InlineData("*.h", true)]            // 1-char extension
		[InlineData("*.HTML", true)]         // uppercase allowed
		[InlineData("*.htm5", true)]         // digits allowed
		[InlineData("*.abcdefgh", true)]     // 8 chars — the cap, allowed
											 // Invalid.
		[InlineData("html", false)]          // no "*." — GetFiles treats as literal filename
		[InlineData(".html", false)]         // missing wildcard
		[InlineData("*", false)]             // bare wildcard, no extension
		[InlineData("*.", false)]            // empty extension
		[InlineData("*.*", false)]           // matches everything
		[InlineData("*.ht ml", false)]      // space
		[InlineData("*.ht.ml", false)]      // dot in extension
		[InlineData("*.abcdefghi", false)]   // 9 chars — exceeds the 8 cap
		[InlineData("*.markuppage", false)]  // 10 chars — implausible
		[InlineData("**.html", false)]       // double wildcard
		[InlineData("", false)]              // empty
		public void IsValidFilePattern_AcceptsOnlyStarDotExtensionGlobs(string pattern, bool expected)
		{
			Assert.Equal(expected, RegExPatterns.IsValidFilePattern(pattern));
		}
	}
}
