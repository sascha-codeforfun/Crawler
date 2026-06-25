using Crawler.Quality;
using Xunit;

namespace Crawler.Tests.Quality
{
	public class WordSplitsTests
	{
		private static ContentQualityConfig DefaultConfig() => new()
		{
			ContentQualityExcerptRadius    = 120,
			ContentQualityQuoteFullSentence = false,  // keep tests deterministic
			ContentQualityQuoteMaxExcerpt  = 400,
		};

		// ── Check ─────────────────────────────────────────────

		[Fact]
		public void Check_NoSplit_ReturnsEmpty()
		{
			var issues = WordSplits.Check("f.html", "<p>Click <a href=\"/\">here</a> for more.</p>", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_SplitAfterClose_ReturnsIssue()
		{
			// </a> followed by letter then space — classic CMS split
			var issues = WordSplits.Check("f.html", "<p>Autofil</a>l form</p>", DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("SPLIT_WORD_ANCHOR", issues[0].IssueType);
			Assert.Contains("l", issues[0].Detail);
		}

		[Fact]
		public void Check_AccentedLetterAfterClose_ReturnsIssue()
		{
			// Accented char (U+00C0–U+024F range) after </a>
			var issues = WordSplits.Check("f.html", "<p>Anmeldung</a>ü form</p>", DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("SPLIT_WORD_ANCHOR", issues[0].IssueType);
		}

		[Fact]
		public void Check_MultipleSplits_ReturnsAll()
		{
			var issues = WordSplits.Check("f.html", "<p>Autofil</a>l form and Anmeldung</a>s page</p>", DefaultConfig()).ToList();
			Assert.Equal(2, issues.Count);
		}

		[Fact]
		public void Check_MultiCharTail_ReturnsIssue()
		{
			// #451: the headline case — a MULTI-character orphan. The previous
			// single-char regex (</a>(X)\s) silently missed this, the common case.
			var issues = WordSplits.Check("f.html", "<p>Hello Wor</a>ld more</p>", DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("SPLIT_WORD_ANCHOR", issues[0].IssueType);
			Assert.Contains("ld", issues[0].Detail, StringComparison.Ordinal);
		}

		[Fact]
		public void Check_DigitTail_ReturnsIssue()
		{
			// Digit run after </a> — "08</a>15 Uhr": the 15 belongs to the number.
			var issues = WordSplits.Check("f.html", "<p>08</a>15 Uhr</p>", DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("15", issues[0].Detail, StringComparison.Ordinal);
		}

		[Fact]
		public void Check_CyrillicTail_ReturnsIssue()
		{
			// \p{L} covers non-Latin scripts — Cyrillic run after </a>.
			var issues = WordSplits.Check("f.html", "<p>x</a>\u0441\u043f\u0430\u0441\u0438\u0431\u043e here</p>", DefaultConfig()).ToList();
			Assert.Single(issues);
		}

		[Fact]
		public void Check_LeadingPunctuationTail_DoesNotFire()
		{
			// Deferred case: a tail that LEADS with punctuation (".com") must NOT
			// fire — distinguishing it from a sentence-ending period needs its own
			// rule (a later change). The run must start with a letter/digit.
			var dotCom = WordSplits.Check("f.html", "<p>ex</a>.com here</p>", DefaultConfig()).ToList();
			var hyphen = WordSplits.Check("f.html", "<p>ex</a>-Event here</p>", DefaultConfig()).ToList();
			Assert.Empty(dotCom);
			Assert.Empty(hyphen);
		}

		[Fact]
		public void Check_TrailingSentencePunctuation_DoesNotFire()
		{
			// A period or comma after a link is normal typography, not a split.
			var period = WordSplits.Check("f.html", "<p>click <a href=\"/\">here</a>. Next</p>", DefaultConfig()).ToList();
			var comma  = WordSplits.Check("f.html", "<p><a href=\"/\">link</a>, next</p>", DefaultConfig()).ToList();
			Assert.Empty(period);
			Assert.Empty(comma);
		}

	}
}
