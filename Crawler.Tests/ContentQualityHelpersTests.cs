using Xunit;
using Crawler.Quality;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ContentQuality's pure helper methods — the small, decidable
	/// pieces that the larger check methods compose. Each helper is pure
	/// (string in, string/structure out) with no I/O.
	///
	/// Introduced in #307 as part of the targeted-coverage pass: identify
	/// each previously-uncovered method, decide whether it's worth testing
	/// or should carry [ExcludeFromCodeCoverage]. These five are the worth-
	/// testing set; the orchestration / logger-output / log-writer wrappers
	/// in the same class are excluded with documented justifications.
	/// </summary>
	public class ContentQualityHelpersTests
	{
		// ── QuoteExcerpt + FindFirstQuoteContext ─────────────────────────
		// Sentence-boundary-aware excerpt builder used for quote findings.

		[Fact]
		public void QuoteExcerpt_FullSentenceFalse_DelegatesToFixedRadiusExcerpt()
		{
			var text = "lorem ipsum dolor sit amet consectetur";
			// pos=15 is inside "dolor"; with full-sentence off, we get a centred fixed-radius excerpt.
			var result = Quotes.QuoteExcerpt(text, pos: 15, fullSentence: false, maxLength: 10);
			Assert.Contains("dolor", result.Replace("…", string.Empty));
		}

		[Fact]
		public void QuoteExcerpt_FullSentence_ExpandsToSentenceBoundaries()
		{
			// pos lands inside the second sentence; excerpt should start after the first '.'
			// and end at the second '.'.
			var text = "First sentence here. Second sentence with quotes. Third.";
			// Position of 'q' in "quotes".
			int pos = text.IndexOf("quotes");
			var result = Quotes.QuoteExcerpt(text, pos, fullSentence: true, maxLength: 200);
			Assert.Contains("Second sentence with quotes.", result);
			Assert.DoesNotContain("First sentence here", result);
		}

		[Fact]
		public void QuoteExcerpt_FullSentence_PrefixEllipsisWhenNotAtStart()
		{
			var text = "First sentence here. Second sentence with quotes. Third.";
			int pos = text.IndexOf("quotes");
			var result = Quotes.QuoteExcerpt(text, pos, fullSentence: true, maxLength: 200);
			Assert.StartsWith("...", result);
		}

		[Fact]
		public void QuoteExcerpt_FullSentence_SuffixEllipsisWhenNotAtEnd()
		{
			var text = "First sentence here. Second sentence with quotes. Third.";
			int pos = text.IndexOf("quotes");
			var result = Quotes.QuoteExcerpt(text, pos, fullSentence: true, maxLength: 200);
			Assert.EndsWith("...", result);
		}

		[Fact]
		public void QuoteExcerpt_FullSentence_NoSurroundingSentences_NoEllipsis()
		{
			// Single sentence, pos in the middle; expansion hits both edges with no
			// surrounding text, so no prefix/suffix ellipsis.
			var text = "Only one sentence with target word here";
			int pos = text.IndexOf("target");
			var result = Quotes.QuoteExcerpt(text, pos, fullSentence: true, maxLength: 200);
			Assert.DoesNotContain("...", result);
			Assert.Contains("target", result);
		}

		[Fact]
		public void QuoteExcerpt_FullSentence_CapsToMaxLength()
		{
			// One long sentence with no boundaries — should still be capped.
			var text = new string('x', 100) + " target " + new string('y', 100);
			int pos = text.IndexOf("target");
			var result = Quotes.QuoteExcerpt(text, pos, fullSentence: true, maxLength: 50);
			// Body capped to maxLength; with possible prefix/suffix ellipsis the
			// final length is bounded — never wildly larger than maxLength.
			Assert.True(result.Length <= 60, $"Expected ~50 chars, got {result.Length}: {result}");
		}

		[Fact]
		public void QuoteExcerpt_FullSentence_NewlinesAndCRsReplacedWithSpaces()
		{
			var text = "First line.\nSecond line with \"target\" mid.\rThird.";
			int pos = text.IndexOf("target");
			var result = Quotes.QuoteExcerpt(text, pos, fullSentence: true, maxLength: 200);
			Assert.DoesNotContain('\n', result);
			Assert.DoesNotContain('\r', result);
		}

		[Fact]
		public void FindFirstQuoteContext_NoOpenersInText_ReturnsEmpty()
		{
			Assert.Equal("", Quotes.FindFirstContext("no quotes here", fullSentence: false, maxLength: 100));
		}

		[Fact]
		public void FindFirstQuoteContext_FirstOpenerWins()
		{
			// German double-low opener „ (U+201E) and an English curly „opener" later.
			var text = "before \u201Eopener one\u201C plus \u201Cother quote\u201D after";
			var result = Quotes.FindFirstContext(text, fullSentence: false, maxLength: 100);
			// First opener is U+201E; excerpt should be centred around it.
			Assert.Contains("\u201E", result);
		}
	}
}
