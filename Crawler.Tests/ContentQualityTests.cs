using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ContentQuality internal check methods.
	/// All methods under test are pure logic — no filesystem, no Logger required.
	/// ContentQualityConfig is constructed inline per test to control exactly
	/// which checks are active.
	/// </summary>
	public class ContentQualityTests
	{
		// ── Helpers ───────────────────────────────────────────────────────────

		private static ContentQualityConfig QuoteConfig(
			bool mixing  = true,
			bool pairing = true) => new()
		{
			CheckQuoteSystemMixing = mixing,
			CheckQuotePairing      = pairing,
			CheckLigatures         = false,
			CheckLanguageMismatch  = false,
			CheckSplitWordAnchors  = false,
		};

		private static HtmlDocument Doc(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		private static HtmlDocument ParseHtml(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		private static ContentQualityConfig DefaultConfig() => new()
		{
			ContentQualityExcerptRadius    = 120,
			ContentQualityQuoteFullSentence = false,  // keep tests deterministic
			ContentQualityQuoteMaxExcerpt  = 400,
		};

		// ── CheckLigatures ────────────────────────────────────────────────────

		[Fact]
		public void CheckLigatures_NoLigatures_ReturnsEmpty()
		{
			var issues = ContentQuality.CheckLigatures("f.html", "The office is open.", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckLigatures_FiLigature_ReturnsOneIssue()
		{
			var issues = ContentQuality.CheckLigatures("f.html", "o\uFB01ce", DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("LIGATURE", issues[0].IssueType);
			Assert.Contains("U+FB01", issues[0].Detail);
		}

		[Fact]
		public void CheckLigatures_FlLigature_ReturnsOneIssue()
		{
			var issues = ContentQuality.CheckLigatures("f.html", "o\uFB02oor", DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("U+FB02", issues[0].Detail);
		}

		[Fact]
		public void CheckLigatures_MultipleLigatures_ReturnsAllHits()
		{
			// fi and ffl ligatures in same string
			var issues = ContentQuality.CheckLigatures("f.html", "\uFB01nd the \uFB04uent", DefaultConfig()).ToList();
			Assert.Equal(2, issues.Count);
		}

		[Fact]
		public void CheckLigatures_FilenamePassedThrough()
		{
			var issues = ContentQuality.CheckLigatures("page-001.html", "o\uFB01ce", DefaultConfig()).ToList();
			Assert.Equal("page-001.html", issues[0].Filename);
		}

		// ── CheckSplitWordAnchors ─────────────────────────────────────────────

		[Fact]
		public void CheckSplitWordAnchors_NoSplit_ReturnsEmpty()
		{
			var issues = ContentQuality.CheckSplitWordAnchors("f.html", "<p>Click <a href=\"/\">here</a> for more.</p>", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckSplitWordAnchors_SplitAfterClose_ReturnsIssue()
		{
			// </a> followed by letter then space — classic CMS split
			var issues = ContentQuality.CheckSplitWordAnchors("f.html", "<p>Autofil</a>l form</p>", DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("SPLIT_WORD_ANCHOR", issues[0].IssueType);
			Assert.Contains("l", issues[0].Detail);
		}

		[Fact]
		public void CheckSplitWordAnchors_AccentedLetterAfterClose_ReturnsIssue()
		{
			// Accented char (U+00C0–U+024F range) after </a>
			var issues = ContentQuality.CheckSplitWordAnchors("f.html", "<p>Anmeldung</a>ü form</p>", DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("SPLIT_WORD_ANCHOR", issues[0].IssueType);
		}

		[Fact]
		public void CheckSplitWordAnchors_MultipleSplits_ReturnsAll()
		{
			var issues = ContentQuality.CheckSplitWordAnchors("f.html", "<p>Autofil</a>l form and Anmeldung</a>s page</p>", DefaultConfig()).ToList();
			Assert.Equal(2, issues.Count);
		}

		[Fact]
		public void CheckSplitWordAnchors_MultiCharTail_ReturnsIssue()
		{
			// #451: the headline case — a MULTI-character orphan. The previous
			// single-char regex (</a>(X)\s) silently missed this, the common case.
			var issues = ContentQuality.CheckSplitWordAnchors("f.html", "<p>Hello Wor</a>ld more</p>", DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("SPLIT_WORD_ANCHOR", issues[0].IssueType);
			Assert.Contains("ld", issues[0].Detail, StringComparison.Ordinal);
		}

		[Fact]
		public void CheckSplitWordAnchors_DigitTail_ReturnsIssue()
		{
			// Digit run after </a> — "08</a>15 Uhr": the 15 belongs to the number.
			var issues = ContentQuality.CheckSplitWordAnchors("f.html", "<p>08</a>15 Uhr</p>", DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("15", issues[0].Detail, StringComparison.Ordinal);
		}

		[Fact]
		public void CheckSplitWordAnchors_CyrillicTail_ReturnsIssue()
		{
			// \p{L} covers non-Latin scripts — Cyrillic run after </a>.
			var issues = ContentQuality.CheckSplitWordAnchors("f.html", "<p>x</a>\u0441\u043f\u0430\u0441\u0438\u0431\u043e here</p>", DefaultConfig()).ToList();
			Assert.Single(issues);
		}

		[Fact]
		public void CheckSplitWordAnchors_LeadingPunctuationTail_DoesNotFire()
		{
			// Deferred case: a tail that LEADS with punctuation (".com") must NOT
			// fire — distinguishing it from a sentence-ending period needs its own
			// rule (a later change). The run must start with a letter/digit.
			var dotCom = ContentQuality.CheckSplitWordAnchors("f.html", "<p>ex</a>.com here</p>", DefaultConfig()).ToList();
			var hyphen = ContentQuality.CheckSplitWordAnchors("f.html", "<p>ex</a>-Event here</p>", DefaultConfig()).ToList();
			Assert.Empty(dotCom);
			Assert.Empty(hyphen);
		}

		[Fact]
		public void CheckSplitWordAnchors_TrailingSentencePunctuation_DoesNotFire()
		{
			// A period or comma after a link is normal typography, not a split.
			var period = ContentQuality.CheckSplitWordAnchors("f.html", "<p>click <a href=\"/\">here</a>. Next</p>", DefaultConfig()).ToList();
			var comma  = ContentQuality.CheckSplitWordAnchors("f.html", "<p><a href=\"/\">link</a>, next</p>", DefaultConfig()).ToList();
			Assert.Empty(period);
			Assert.Empty(comma);
		}

		// ── CheckLanguageMismatch ─────────────────────────────────────────────

		[Fact]
		public void CheckLanguageMismatch_Matching_ReturnsEmpty()
		{
			var doc = Doc("<html lang=\"de\"><head><meta name=\"language\" content=\"de\"></head></html>");
			var issues = ContentQuality.CheckLanguageMismatch("f.html", doc).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckLanguageMismatch_Mismatch_ReturnsIssue()
		{
			var doc = Doc("<html lang=\"de\"><head><meta name=\"language\" content=\"en\"></head></html>");
			var issues = ContentQuality.CheckLanguageMismatch("f.html", doc).ToList();
			Assert.Single(issues);
			Assert.Equal("LANGUAGE_MISMATCH", issues[0].IssueType);
		}

		[Fact]
		public void CheckLanguageMismatch_SubcodeNormalised_MatchingBaseCode_ReturnsEmpty()
		{
			// de-DE vs de should not trigger — both normalise to "de"
			var doc = Doc("<html lang=\"de-DE\"><head><meta name=\"language\" content=\"de\"></head></html>");
			var issues = ContentQuality.CheckLanguageMismatch("f.html", doc).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckLanguageMismatch_NoMetaTag_ReturnsEmpty()
		{
			var doc = Doc("<html lang=\"de\"><head></head></html>");
			var issues = ContentQuality.CheckLanguageMismatch("f.html", doc).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckLanguageMismatch_NoHtmlLang_ReturnsEmpty()
		{
			var doc = Doc("<html><head><meta name=\"language\" content=\"de\"></head></html>");
			var issues = ContentQuality.CheckLanguageMismatch("f.html", doc).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckLanguageMismatch_DetailMentionsBothLangs()
		{
			var doc = Doc("<html lang=\"de\"><head><meta name=\"language\" content=\"en\"></head></html>");
			var issues = ContentQuality.CheckLanguageMismatch("f.html", doc).ToList();
			Assert.Contains("de", issues[0].Detail);
			Assert.Contains("en", issues[0].Detail);
		}

		// ── CheckControlCharsInContent (fileset #286) ──────────────────────

		[Fact]
		public void CheckControlCharsInContent_PlainContent_NoIssues()
		{
			var doc = Doc(
				"<html><head>" +
				"<title>Normal Title</title>" +
				"<meta name=\"description\" content=\"A normal description.\">" +
				"</head></html>");
			var issues = ContentQuality.CheckControlCharsInContent("f.html", doc).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckControlCharsInContent_NewlineInMetaDescription_Flagged()
		{
			// The original Czech-page failure: meta description contains a
			// literal newline character from CMS copy-paste.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"description\" content=\"hassle-free\nshopping\">" +
				"</head></html>");
			var issues = ContentQuality.CheckControlCharsInContent("f.html", doc).ToList();
			Assert.Single(issues);
			Assert.Equal("CONTROL_CHARS_IN_CONTENT", issues[0].IssueType);
			Assert.Contains("LF", issues[0].Detail);
			Assert.Contains("description", issues[0].Detail);
		}

		[Fact]
		public void CheckControlCharsInContent_TabInTitle_Flagged()
		{
			var doc = Doc("<html><head><title>Two\tTabs</title></head></html>");
			var issues = ContentQuality.CheckControlCharsInContent("f.html", doc).ToList();
			Assert.Single(issues);
			Assert.Contains("TAB", issues[0].Detail);
			Assert.Contains("title", issues[0].Detail);
		}

		[Fact]
		public void CheckControlCharsInContent_ZeroWidthSpace_Flagged()
		{
			// U+200B in a description — invisible to authors, breaks tooling.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"description\" content=\"clean\u200Btext\">" +
				"</head></html>");
			var issues = ContentQuality.CheckControlCharsInContent("f.html", doc).ToList();
			Assert.Single(issues);
			Assert.Contains("U+200B", issues[0].Detail);
		}

		[Fact]
		public void CheckControlCharsInContent_BidiOverride_Flagged()
		{
			// U+202E RLO (right-to-left override) — Trojan Source territory.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"keywords\" content=\"safe\u202Eevil\">" +
				"</head></html>");
			var issues = ContentQuality.CheckControlCharsInContent("f.html", doc).ToList();
			Assert.Single(issues);
			Assert.Contains("U+202E", issues[0].Detail);
		}

		[Fact]
		public void CheckControlCharsInContent_LineSeparator_Flagged()
		{
			// Fileset #286b regression: U+2028 LINE SEPARATOR appeared in
			// real-world CMS-pasted content (HTML byte sequence E2 80 A8)
			// between paragraphs of meta description text. Invisible to the
			// CMS author but problematic in downstream tooling.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"description\" content=\"section.\u2028 Tip: Start the setup\">" +
				"</head></html>");
			var issues = ContentQuality.CheckControlCharsInContent("f.html", doc).ToList();
			Assert.Single(issues);
			Assert.Contains("U+2028", issues[0].Detail);
			Assert.Contains("LINE SEPARATOR", issues[0].Detail);
		}

		[Fact]
		public void CheckControlCharsInContent_ParagraphSeparator_Flagged()
		{
			// U+2029 PARAGRAPH SEPARATOR — sibling of U+2028.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"description\" content=\"para1\u2029para2\">" +
				"</head></html>");
			var issues = ContentQuality.CheckControlCharsInContent("f.html", doc).ToList();
			Assert.Single(issues);
			Assert.Contains("U+2029", issues[0].Detail);
		}

		[Fact]
		public void CheckControlCharsInContent_HighUnicode_NotFlagged()
		{
			// Legitimate non-ASCII (German umlauts, French accents, Polish
			// diacritics, Czech háčky) must NOT trigger the check.
			var doc = Doc(
				"<html><head>" +
				"<title>Größe — l'été — Łódź — žluťoučký</title>" +
				"</head></html>");
			var issues = ContentQuality.CheckControlCharsInContent("f.html", doc).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckControlCharsInContent_ContextShowsVisibleMarker()
		{
			// The Context field of the emitted issue must replace invisible
			// chars with visible markers so the operator can see WHERE the
			// problem is, not just that something happened.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"description\" content=\"line1\nline2\">" +
				"</head></html>");
			var issues = ContentQuality.CheckControlCharsInContent("f.html", doc).ToList();
			Assert.Single(issues);
			Assert.Contains("[LF]", issues[0].Context);
			Assert.Contains("line1", issues[0].Context);
			Assert.Contains("line2", issues[0].Context);
		}

		[Fact]
		public void CheckControlCharsInContent_ContextShowsOperatorFriendlyMarker_LineSeparator()
		{
			// Obscure codepoints render with the human-readable
			// kind name AND the codepoint, so non-technical CMS editors can
			// understand the issue. Real-world scenario:
			// U+2028 between "section." and " Tip: Start...".
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"description\" content=\"section.\u2028 Tip: Start the setup\">" +
				"</head></html>");
			var issues = ContentQuality.CheckControlCharsInContent("f.html", doc).ToList();
			Assert.Single(issues);
			// Marker tells the editor what KIND of invisible character is there.
			Assert.Contains("[INVISIBLE LINE SEPARATOR U+2028]", issues[0].Context);
			// Surrounding content is preserved so the editor can locate the issue.
			Assert.Contains("section.", issues[0].Context);
			Assert.Contains("Tip: Start", issues[0].Context);
		}

		[Fact]
		public void CheckControlCharsInContent_ContextShowsOperatorFriendlyMarker_ZeroWidthSpace()
		{
			// U+200B — a common copy-paste import from Word and PDFs.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"description\" content=\"clean\u200Btext\">" +
				"</head></html>");
			var issues = ContentQuality.CheckControlCharsInContent("f.html", doc).ToList();
			Assert.Single(issues);
			Assert.Contains("[INVISIBLE ZERO-WIDTH SPACE U+200B]", issues[0].Context);
		}

		[Fact]
		public void CheckControlCharsInContent_ContextShowsOperatorFriendlyMarker_BidiControl()
		{
			// U+202E RLO — the Trojan Source vector. Operator-friendly marker
			// makes the security concern visible without requiring the editor
			// to know what U+202E means.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"keywords\" content=\"safe\u202Eevil\">" +
				"</head></html>");
			var issues = ContentQuality.CheckControlCharsInContent("f.html", doc).ToList();
			Assert.Single(issues);
			Assert.Contains("[INVISIBLE BIDI CONTROL U+202E]", issues[0].Context);
		}

		private static HtmlAgilityPack.HtmlDocument HtmlDoc(string bodyText)
		{
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml($"<html><body><p>{bodyText}</p></body></html>");
			return doc;
		}

		// ── CheckQuotes — system mixing ───────────────────────────────────────

		[Fact]
		public void CheckQuotes_SingleSystem_NoMixIssue()
		{
			// Only German-double openers — must be in a block element for per-block detection
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<p>„Hallo”</p>");
			var issues = ContentQuality.CheckQuotes("f.html", doc, QuoteConfig()).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_SYSTEM_MIX");
		}

		[Fact]
		public void CheckQuotes_TwoDoubleSystems_ReportsMix()
		{
			// German and English openers in the same block
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<p>\u201EHallo\u201C \u201CHello\u201D</p>");
			var issues = ContentQuality.CheckQuotes("f.html", doc, QuoteConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_SYSTEM_MIX");
		}

		[Fact]
		public void CheckQuotes_SinglePlusSingleDouble_NoMixIssue()
		{
			// Single-quote system coexisting with double — should NOT flag as mix
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<p>„Hallo” and ‘Hi’</p>");
			var issues = ContentQuality.CheckQuotes("f.html", doc, QuoteConfig()).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_SYSTEM_MIX");
		}

		// ── CheckQuotes — per-block isolation ──────────────────────────────────

		[Fact]
		public void CheckQuotes_MixInDifferentBlocks_EachBlockIndependent()
		{
			// German quotes in <p>, English in <li> — each block checked separately.
			// Both should flag their own mix only if they have both systems internally.
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<p>\u201EHallo\u201C</p><li>\u201CHello\u201D</li>");
			var issues = ContentQuality.CheckQuotes("f.html", doc, QuoteConfig()).ToList();
			// Neither block has a mix internally — no QUOTE_SYSTEM_MIX expected.
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_SYSTEM_MIX");
		}

		[Fact]
		public void CheckQuotes_MixWithinSingleBlock_Flagged()
		{
			// Both systems within one <p> — should flag.
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<p>\u201EHallo\u201C \u201CHello\u201D</p>");
			var issues = ContentQuality.CheckQuotes("f.html", doc, QuoteConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_SYSTEM_MIX");
		}

		[Fact]
		public void CheckQuotes_TextOutsideBlockElements_NotChecked()
		{
			// Text directly in <div> (not a block element) is not quote-checked.
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<div>\u201EHallo\u201C \u201CHello\u201D</div>");
			var issues = ContentQuality.CheckQuotes("f.html", doc, QuoteConfig()).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_SYSTEM_MIX");
		}

		// ── CheckBareText ────────────────────────────────────────────────────

		[Fact]
		public void CheckBareText_TextDirectlyInDiv_Flagged()
		{
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<div>Bare text here</div>");
			var config = DefaultConfig();
			var issues = ContentQuality.CheckBareText("f.html", doc, config).ToList();
			Assert.Contains(issues, i => i.IssueType == "BARE_TEXT_IN_CONTAINER");
		}

		[Fact]
		public void CheckBareText_TextInParagraph_NotFlagged()
		{
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<div><p>Proper paragraph</p></div>");
			var config = DefaultConfig();
			var issues = ContentQuality.CheckBareText("f.html", doc, config).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "BARE_TEXT_IN_CONTAINER");
		}

		[Fact]
		public void CheckBareText_WhitespaceOnly_NotFlagged()
		{
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<div>   \n   <p>Content</p></div>");
			var config = DefaultConfig();
			var issues = ContentQuality.CheckBareText("f.html", doc, config).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "BARE_TEXT_IN_CONTAINER");
		}

		[Fact]
		public void CheckBareText_TextInSection_Flagged()
		{
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<section>Bare text in section</section>");
			var config = DefaultConfig();
			var issues = ContentQuality.CheckBareText("f.html", doc, config).ToList();
			Assert.Contains(issues, i => i.IssueType == "BARE_TEXT_IN_CONTAINER");
		}

		// ── CheckQuotePairing — correct pairs ─────────────────────────────────

		[Fact]
		public void CheckQuotePairing_GermanDoubleCorrect_NoIssue()
		{
			// „Hallo“ — correct German pair: U+201E opens, U+201C closes (66-Zeichen oben)
			var issues = ContentQuality.CheckQuotePairing("f.html", "\u201EHallo\u201C", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_EnglishDoubleCorrect_NoIssue()
		{
			// "Hello" — correct English double pair
			var issues = ContentQuality.CheckQuotePairing("f.html", "\u201CHello\u201D", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_GermanGuillemetCorrect_NoIssue()
		{
			// «Hallo» — correct German guillemet pair (« opens, » closes)
			var issues = ContentQuality.CheckQuotePairing("f.html", "\u00ABHallo\u00BB", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_GermanSingleCorrect_NoIssue()
		{
			// ‚Hallo' — correct German single pair
			var issues = ContentQuality.CheckQuotePairing("f.html", "\u201AHallo\u2019", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		// ── CheckQuotePairing — wrong closer ──────────────────────────────────

		[Fact]
		public void CheckQuotePairing_GermanOpenerWrongCloser_ReportsWrongClose()
		{
			// „ opened with German-double but closed with » (Guillemet closer)
			var issues = ContentQuality.CheckQuotePairing("f.html", "\u201EHallo\u00BB", DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_WRONG_CLOSE");
		}

		[Fact]
		public void CheckQuotePairing_WrongClose_DetailMentionsBothSystems()
		{
			// „ opened with German-double but closed with » (Guillemet closer)
			var issues = ContentQuality.CheckQuotePairing("f.html", "\u201EHallo\u00BB", DefaultConfig()).ToList();
			var wrongClose = issues.FirstOrDefault(i => i.IssueType == "QUOTE_WRONG_CLOSE");
			Assert.NotNull(wrongClose);
			Assert.Contains("German-double", wrongClose!.Detail);
		}

		// ── CheckQuotePairing — wrong opener (closer with no opener) ──────────

		[Fact]
		public void CheckQuotePairing_CloserWithNoOpener_ReportsWrongOpen()
		{
			// " with no preceding opener — straight quote used as opener
			var issues = ContentQuality.CheckQuotePairing("f.html", "Hello \u201D world", DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_WRONG_OPEN");
		}

		// ── CheckQuotePairing — apostrophe disambiguation ─────────────────────

		[Fact]
		public void CheckQuotePairing_ApostropheAfterLetter_NotFlaggedAsCloser()
		{
			// geht's — U+2019 between letters is apostrophe, not a quote closer
			var issues = ContentQuality.CheckQuotePairing("f.html", "Das geht\u2019s nicht", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_GermanElision_NotFlaggedAsOpener()
		{
			// 'ner — German colloquial elision, not a quote opener.
			// Under language-driven profiles, this elision is recognised only
			// when the page language is "de"; falls back to Rule 2 (between
			// letters) otherwise — but 'ner has a space before so Rule 2 does
			// not save it. The de profile MUST be active to pass.
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", "Was ist mit \u2018ner Limonade?", DefaultConfig(), "de").ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_EnglishContraction_NotFlagged()
		{
			// funktioniert's — U+2019 + s is a configured elision
			var issues = ContentQuality.CheckQuotePairing("f.html", "So einfach funktioniert\u2019s:", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_SingleQuoteAroundPhrase_CloserNotApostrophe()
		{
			// 'a good time' — closer preceded by 'e' but followed by space, not a letter
			// So it should NOT be treated as apostrophe
			var issues = ContentQuality.CheckQuotePairing("f.html", "\u2018a good time\u2019", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_RightSingleAfterSpace_FlaggedAsCloser()
		{
			// U+2019 not preceded by a letter — treated as a closer with no opener
			var issues = ContentQuality.CheckQuotePairing("f.html", "Hello \u2019 world", DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_WRONG_OPEN");
		}

		// ── CheckQuotePairing — language-driven elision profiles ──────────────
		// Fileset #283: per-language apostrophe profiles. Selection key is the
		// page's lowercase 2-letter language code; "_default" is the fallback.

		[Fact]
		public void CheckQuotePairing_FrenchPrefixElision_NotFlagged()
		{
			// Generalised from the original false-positive cascade: an apostrophe
			// in l'accès inside guillemets was being misread as a quote closer,
			// which corrupted the stack and made the closing » look orphaned.
			// The fr profile's PrefixElisions list resolves "l" before the
			// apostrophe → recognised as elision, stack stays clean.
			var text  = "\u00ABla confirmation de l\u2019acc\u00E8s en ligne\u00BB";
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", text, DefaultConfig(), "fr").ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_FrenchMultipleApostrophesInsideGuillemets_NoFalsePositives()
		{
			// Heavier excerpt-style block. Multiple French elisions of different
			// prefixes (l', d', n', s'), all inside guillemets. Stack must close
			// cleanly with no QUOTE_WRONG_CLOSE or QUOTE_WRONG_OPEN.
			var text = "\u00ABl\u2019acc\u00E8s n\u2019est pas s\u2019il d\u2019un \u00E9chec\u00BB";
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", text, DefaultConfig(), "fr").ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_UnknownLanguage_FallsBackToDefault()
		{
			// Page language "xx" is not in the profile dictionary; "_default"
			// is used. The default profile has empty elision lists, but Rule 2
			// (between-letters) still catches the common case.
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", "Das geht\u2019s nicht", DefaultConfig(), "xx").ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_FrenchPrefixOnlyAtWordBoundary()
		{
			// Prefix matching must be word-anchored: the character BEFORE the
			// prefix (if any) must be a non-letter, otherwise "tablel'art" would
			// false-match prefix "l" against the trailing "l" of "tablel".
			// In "tablel'art" the "l" prefix is preceded by "e" (a letter) →
			// must NOT match Rule 1b. Falls to Rule 2: "l" before, "a" after,
			// both letters → between-letters apostrophe → recognised correctly.
			// The test confirms Rule 1b's word-anchor doesn't accidentally match
			// AND that Rule 2 catches the apostrophe regardless (no false positive).
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", "Le tablel\u2019art existe", DefaultConfig(), "fr").ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_ApostropheBetweenLettersInsideQuote_NotFlagged()
		{
			// Rule 2 (between-letters) is now language-agnostic and applies
			// regardless of stack state. Previously the stack-nonempty branch
			// would treat the apostrophe as a closer, producing QUOTE_WRONG_CLOSE.
			// Test uses _default (no language) to confirm the rule itself.
			var text = "\u00ABl\u2019acc\u00E8s\u00BB";
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", text, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_FrenchProfile_GermanElisionInfixNotMatched()
		{
			// Profile isolation: the German colloquial elision 'ner should NOT
			// be recognised on a French-language page. Here "'ner" appears
			// with a space before — Rule 2 cannot save it (space before, letter
			// after is not symmetric). With the fr profile active, suffix list
			// is empty and the German "ner" entry is in the de profile only.
			// Expected: U+2018 treated as opener, no closer in block → unmatched.
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", "Was ist mit \u2018ner Limonade?", DefaultConfig(), "fr").ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_UNMATCHED");
		}

		// ── CheckQuotePairing — verification pass (fileset #285) ──────────────
		// Second-pass proximity verification downgrades flags to QUOTE_AMBIGUOUS
		// when the offending character can be paired cleanly under stricter
		// apostrophe rules. Original tier preserved when the verification pass
		// cannot resolve the flag.

		private static ContentQualityConfig DefaultConfigWithVerification(bool enabled = true)
		{
			var cfg = DefaultConfig();
			cfg.CheckQuotePairingVerification = enabled;
			return cfg;
		}

		[Fact]
		public void CheckQuotePairing_EnglishSingleQuotedPhrase_DowngradedToAmbiguous()
		{
			// The false positive that motivated fileset #285: 'show password' in
			// English text. Cheap pass eats opening U+2018 as elision (because
			// 's' is an English suffix), then flags the closing U+2019 as
			// QUOTE_WRONG_OPEN. Verification pass uses Rule 1a-strict (letter-
			// before required), correctly classifies U+2018 as opener, pairs it
			// with U+2019 → flag is downgraded to QUOTE_AMBIGUOUS.
			var text = "Use the \u2018show password\u2019 function.";
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", text, DefaultConfigWithVerification(), "en").ToList();
			Assert.Single(issues);
			Assert.Equal("QUOTE_AMBIGUOUS", issues[0].IssueType);
			Assert.Contains("QUOTE_WRONG_OPEN", issues[0].Detail);
			Assert.Contains("resolved by proximity", issues[0].Detail);
		}

		[Fact]
		public void CheckQuotePairing_VerificationDisabled_KeepsOriginalType()
		{
			// Same text as the test above, but verification disabled. Expect
			// the original QUOTE_WRONG_OPEN flag with no downgrade.
			var text = "Use the \u2018show password\u2019 function.";
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", text, DefaultConfigWithVerification(enabled: false), "en").ToList();
			Assert.Single(issues);
			Assert.Equal("QUOTE_WRONG_OPEN", issues[0].IssueType);
		}

		[Fact]
		public void CheckQuotePairing_GenuineWrongOpen_StaysHighConfidence()
		{
			// A real orphan U+2019 with no possible English-single opener
			// anywhere in the block should stay as QUOTE_WRONG_OPEN even with
			// verification enabled — proximity pairing finds no pair → no
			// downgrade. Construct: closer at start of block, no opener anywhere.
			var text = "\u2019 stray closer with nothing before it.";
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", text, DefaultConfigWithVerification(), "en").ToList();
			Assert.Single(issues);
			Assert.Equal("QUOTE_WRONG_OPEN", issues[0].IssueType);
		}

		[Fact]
		public void CheckQuotePairing_PartialResolution_MixedTypes()
		{
			// Block with two independent flag triggers: one that the verification
			// pass can resolve, one it cannot. Expected: a mix — one
			// QUOTE_AMBIGUOUS and one high-confidence type.
			// First trigger: 'show password' → verification pairs U+2018/U+2019
			//                cleanly → AMBIGUOUS
			// Second trigger: stray U+201D closer at end → no opener → stays as
			//                QUOTE_WRONG_OPEN (English-double has no opener here
			//                because U+201C never appeared)
			var text = "Use \u2018show password\u2019 here. Also \u201D stray.";
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", text, DefaultConfigWithVerification(), "en").ToList();
			Assert.Equal(2, issues.Count);
			Assert.Contains(issues, i => i.IssueType == "QUOTE_AMBIGUOUS");
			Assert.Contains(issues, i => i.IssueType == "QUOTE_WRONG_OPEN");
		}

		[Fact]
		public void CheckQuotePairing_ProximityPairingRespectsSystemBoundaries()
		{
			// U+2018 (English-single opener) paired against U+201D (English-
			// DOUBLE closer) should NOT pair — different systems. The cheap
			// pass would flag the U+201D as orphan; the verification pass also
			// finds no pair (its per-system stack for English-double is empty)
			// → flag stays high-confidence QUOTE_WRONG_OPEN.
			// The "s" suffix swallows the U+2018 in the cheap pass (same as
			// the 'show password' case) — but Rule 1a-strict in the verification
			// pass correctly treats U+2018 as an English-single opener with no
			// matching English-single closer in the block. So neither U+2018 nor
			// U+201D end up paired.
			var text = "Open \u2018something here\u201D close.";
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", text, DefaultConfigWithVerification(), "en").ToList();
			// Two flags expected from the cheap pass (or at least one for the
			// stray U+201D); whichever fire must NOT be QUOTE_AMBIGUOUS.
			Assert.NotEmpty(issues);
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_AMBIGUOUS");
		}

		// ── CheckQuotePairing — paragraph boundary reset ──────────────────────

		[Fact]
		public void CheckQuotePairing_MultiSentenceQuote_NotFlagged()
		{
			// Multi-sentence German quote — stack must NOT reset at sentence boundaries.
			// Per-block scoping handles isolation — the block IS the natural boundary.
			var text = "\u201EErster Satz. Zweiter Satz. Dritter Satz.\u201C";
			var issues = ContentQuality.CheckQuotePairing("f.html", text, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		// ── CheckQuotePairing — unmatched opener ───────────────────────────────

		[Fact]
		public void CheckQuotePairing_UnmatchedOpener_AlwaysReported()
		{
			// Per-block scoping means any unmatched opener is reported regardless of position.
			// The 500-char distance cutoff was removed — block boundary is the natural limit.
			var issues = ContentQuality.CheckQuotePairing("f.html", "Some text \u201ENo closer here", DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_UNMATCHED");
		}

		[Fact]
		public void CheckQuotePairing_UnmatchedOpenerLongBlock_StillReported()
		{
			// Even an opener far from the end of a long block is reported — no distance cutoff.
			var farText = "\u201EOpener" + new string('x', 600);
			var issues = ContentQuality.CheckQuotePairing("f.html", farText, DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_UNMATCHED");
		}

		// ── CheckMisplacedAnchors — MISPLACED_ANCHOR_EMPTY ─────────────────────

		[Fact]
		public void CheckMisplacedAnchors_EmptyAnchor_ReturnsEmptyIssue()
		{
			var html   = "<p><a href=\"/x\"></a>text</p>";
			var issues = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "MISPLACED_ANCHOR_EMPTY");
		}

		[Fact]
		public void CheckMisplacedAnchors_WhitespaceOnlyAnchor_ReturnsEmptyIssue()
		{
			var html   = "<p><a href=\"/x\">   </a>text</p>";
			var issues = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "MISPLACED_ANCHOR_EMPTY");
		}

		[Fact]
		public void CheckMisplacedAnchors_AnchorWithEmptyChildElements_ReturnsEmptyIssue()
		{
			// Anchor contains only empty inline elements — no visible text anywhere
			var html   = "<p><a href=\"/x\"><span><b></b></span></a></p>";
			var issues = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "MISPLACED_ANCHOR_EMPTY");
		}

		[Fact]
		public void CheckMisplacedAnchors_AnchorWithText_NoEmptyIssue()
		{
			var html   = "<p><a href=\"/x\">Click here</a></p>";
			var issues = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), DefaultConfig()).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "MISPLACED_ANCHOR_EMPTY");
		}

		[Fact]
		public void CheckMisplacedAnchors_EmptyAnchor_DetailContainsHref()
		{
			var html   = "<p><a href=\"/target\"></a></p>";
			var issues = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), DefaultConfig()).ToList();
			var issue  = issues.FirstOrDefault(i => i.IssueType == "MISPLACED_ANCHOR_EMPTY");
			Assert.NotNull(issue);
			Assert.Contains("/target", issue!.Detail);
		}

		// ── CheckMisplacedAnchors — ADJACENT_ANCHOR (was MISPLACED_ANCHOR_SPLIT, #452) ──
		// All ADJACENT_ANCHOR tests must opt the detector in via the new gate
		// (AnchorDetection.DetectAdjacent), which defaults FALSE in production —
		// adjacency is a structural fact, not a verdict, and most sites opt in only
		// after deciding their design rules it in. DefaultConfig() reflects the
		// production default; each test that exercises the detector enables the gate
		// explicitly so the dependency is visible on the test surface, not hidden in
		// a shared helper.

		private static ContentQualityConfig ConfigWithAdjacentOn()
		{
			var cfg = DefaultConfig();
			cfg.AnchorDetection.DetectAdjacent = true;
			return cfg;
		}

		[Fact]
		public void CheckMisplacedAnchors_AdjacentAnchorsNoSeparator_ReturnsSplitIssue()
		{
			// Two anchors directly adjacent — no whitespace between closing and opening tag
			var html   = "<h3><a href=\"/x\">And</a><a href=\"/x\">roid</a></h3>";
			var issues = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), ConfigWithAdjacentOn()).ToList();
			Assert.Contains(issues, i => i.IssueType == "ADJACENT_ANCHOR");
		}

		[Fact]
		public void CheckMisplacedAnchors_AdjacentAnchorsDifferentHref_ReturnsSplitIssue()
		{
			// Different hrefs — structural defect regardless of href
			var html   = "<p><a href=\"/a\">Foo</a><a href=\"/b\">Bar</a></p>";
			var issues = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), ConfigWithAdjacentOn()).ToList();
			Assert.Contains(issues, i => i.IssueType == "ADJACENT_ANCHOR");
		}

		[Fact]
		public void CheckMisplacedAnchors_AnchorsWithSpaceBetween_NoSplitIssue()
		{
			var html   = "<p><a href=\"/a\">Foo</a> <a href=\"/b\">Bar</a></p>";
			var issues = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), ConfigWithAdjacentOn()).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "ADJACENT_ANCHOR");
		}

		[Fact]
		public void CheckMisplacedAnchors_ThreeAdjacentAnchors_ReportsTwoPairs()
		{
			// A+B adjacent and B+C adjacent — two split issues
			var html   = "<p><a href=\"/x\">A</a><a href=\"/x\">B</a><a href=\"/x\">C</a></p>";
			var issues = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), ConfigWithAdjacentOn())
				.Where(i => i.IssueType == "ADJACENT_ANCHOR").ToList();
			Assert.Equal(2, issues.Count);
		}

		[Fact]
		public void CheckMisplacedAnchors_EmptyMiddleAnchorWithSplits_ReportsBothTypes()
		{
			// Mirrors the real-world pattern: text anchor + empty anchor + text anchor,
			// all adjacent, same href.
			var html   = "<h3>" +
			             "<a href=\"/x\">And</a>" +
			             "<a href=\"/x\"></a>" +
			             "<a href=\"/x\">roid</a>" +
			             "</h3>";
			var issues = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), ConfigWithAdjacentOn()).ToList();
			Assert.Contains(issues, i => i.IssueType == "MISPLACED_ANCHOR_EMPTY");
			Assert.Contains(issues, i => i.IssueType == "ADJACENT_ANCHOR");
		}

		[Fact]
		public void CheckMisplacedAnchors_FilenamePassedThrough()
		{
			var html   = "<p><a href=\"/x\"></a></p>";
			var issues = ContentQuality.CheckMisplacedAnchors("page-042.html", ParseHtml(html), ConfigWithAdjacentOn()).ToList();
			Assert.All(issues, i => Assert.Equal("page-042.html", i.Filename));
		}

		// ── #452 gate + post-filter behavior ─────────────────────────────────

		[Fact]
		public void CheckMisplacedAnchors_AdjacentGateOff_NoFindings()
		{
			// Default DefaultConfig() leaves AnchorDetection.DetectAdjacent = false.
			// The same input that fires under ConfigWithAdjacentOn() must produce
			// zero ADJACENT_ANCHOR findings here. Guards the default-off contract.
			var html   = "<h3><a href=\"/x\">And</a><a href=\"/x\">roid</a></h3>";
			var issues = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), DefaultConfig()).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "ADJACENT_ANCHOR");
		}

		[Fact]
		public void CheckMisplacedAnchors_AdjacentGateOff_EmptyStillFires()
		{
			// The gate scopes ADJACENT only — MISPLACED_ANCHOR_EMPTY is not affected
			// and must still fire under the default config.
			var html   = "<p><a href=\"/x\"></a></p>";
			var issues = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "MISPLACED_ANCHOR_EMPTY");
		}

		// ── #431: ADJACENT_ANCHOR excerpt centres on the </a><a boundary, not the
		//          first anchor's OuterHtml start. Regression guard for the case
		//          where a long body (e.g. an inline SVG) inside the first anchor
		//          pushed the split out of the windowed excerpt, leaving the
		//          operator a wall of markup with no visible split. Synthetic
		//          fixtures only.

		[Fact]
		public void CheckMisplacedAnchors_SplitBehindLongBody_ExcerptStillShowsSplit()
		{
			// First anchor carries a long inner body (stand-in for a big inline SVG
			// path) so its OuterHtml start sits far from the split. Pre-#431 the
			// excerpt centred on that start and the </a><a boundary fell outside the
			// window; post-#431 it centres on the boundary so the split is in frame.
			var longBody = new string('x', 600);
			var html = $"<div><a href=\"/a\">{longBody}First</a><a href=\"/b\">Second</a></div>";
			var issue = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), ConfigWithAdjacentOn())
				.First(i => i.IssueType == "ADJACENT_ANCHOR");

			Assert.Contains("</a><a", issue.Context, StringComparison.Ordinal);
		}

		[Fact]
		public void CheckMisplacedAnchors_SplitBehindLongBody_ExcerptShowsBothAnchorTexts()
		{
			var longBody = new string('x', 600);
			var html = $"<div><a href=\"/a\">{longBody}First</a><a href=\"/b\">Second</a></div>";
			var issue = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), ConfigWithAdjacentOn())
				.First(i => i.IssueType == "ADJACENT_ANCHOR");

			// Both anchors' text sit adjacent to the boundary, so a boundary-centred
			// window shows both — the operator can read what was split.
			Assert.Contains("First", issue.Context, StringComparison.Ordinal);
			Assert.Contains("Second", issue.Context, StringComparison.Ordinal);
		}

		[Fact]
		public void CheckMisplacedAnchors_SplitBehindLongBody_LeadingEllipsisWhenClippedLeft()
		{
			// Long left body guarantees the window is clipped on the left → leading …
			var longBody = new string('x', 600);
			var html = $"<div><a href=\"/a\">{longBody}First</a><a href=\"/b\">Second</a></div>";
			var issue = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), ConfigWithAdjacentOn())
				.First(i => i.IssueType == "ADJACENT_ANCHOR");

			Assert.StartsWith("\u2026", issue.Context);
		}

		[Fact]
		public void CheckMisplacedAnchors_ShortSplit_NoEllipsisWhenNotClipped()
		{
			// Whole construct fits within the cap → no clipping → no … on either end.
			var html = "<div><a href=\"/a\">Foo</a><a href=\"/b\">Bar</a></div>";
			var issue = ContentQuality.CheckMisplacedAnchors("f.html", ParseHtml(html), ConfigWithAdjacentOn())
				.First(i => i.IssueType == "ADJACENT_ANCHOR");

			Assert.DoesNotContain("\u2026", issue.Context);
			Assert.Contains("</a><a", issue.Context, StringComparison.Ordinal);
		}

		// ── CentredExcerpt (position overload) — direct unit tests ──────────────

		[Fact]
		public void CentredExcerpt_Position_CentresWindowOnOffset()
		{
			var source = new string('L', 500) + "MARK" + new string('R', 500);
			var centre = 500 + 2; // middle of MARK
			var ex = ContentQuality.CentredExcerpt(source, centre, 100);
			Assert.Contains("MARK", ex, StringComparison.Ordinal);
			Assert.StartsWith("\u2026", ex);   // clipped left
			Assert.EndsWith("\u2026", ex);      // clipped right
		}

		[Fact]
		public void CentredExcerpt_Position_NoEllipsisWhenWholeSourceFits()
		{
			var source = "short source string";
			var ex = ContentQuality.CentredExcerpt(source, 5, 400);
			Assert.Equal(source, ex);           // unclipped → returned verbatim, no …
		}

		[Fact]
		public void CentredExcerpt_Position_ClampsOutOfRangeCentre()
		{
			var source = new string('a', 100);
			// Centre past the end must not throw and must still return a bounded window.
			var ex = ContentQuality.CentredExcerpt(source, 9999, 40);
			Assert.True(ex.Length <= 41); // 40 body + at most one leading …
			Assert.EndsWith("a", ex);     // window sits at the tail, no trailing …
		}

		[Fact]
		public void CentredExcerpt_Position_NewlinesReplacedWithSpaces()
		{
			var source = "line1\nline2\r\nline3";
			var ex = ContentQuality.CentredExcerpt(source, 8, 400);
			Assert.DoesNotContain('\n', ex);
			Assert.DoesNotContain('\r', ex);
		}

				// ── CheckUnwantedPatterns ─────────────────────────────────────────────

		[Fact]
		public void CheckUnwantedPatterns_NoMatch_ReturnsEmpty()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Template", Name = "Unfilled", Patterns = ["%("], CaseSensitive = true }
			};
			var issues = ContentQuality.CheckUnwantedPatterns("f.html", "<p>Clean content</p>", patterns, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckUnwantedPatterns_Match_ReturnsIssue()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Template", Name = "Unfilled", Patterns = ["%("], CaseSensitive = true }
			};
			var issues = ContentQuality.CheckUnwantedPatterns("f.html", "<p>%(unfilled_var)</p>", patterns, DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("UNWANTED_PATTERN", issues[0].IssueType);
		}

		[Fact]
		public void CheckUnwantedPatterns_CaseInsensitive_MatchesRegardlessOfCase()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Test", Name = "Keyword", Patterns = ["todo"], CaseSensitive = false }
			};
			var issues = ContentQuality.CheckUnwantedPatterns("f.html", "<p>TODO: fix this</p>", patterns, DefaultConfig()).ToList();
			Assert.Single(issues);
		}

		[Fact]
		public void CheckUnwantedPatterns_CaseSensitive_NoMatchOnWrongCase()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Test", Name = "Keyword", Patterns = ["todo"], CaseSensitive = true }
			};
			var issues = ContentQuality.CheckUnwantedPatterns("f.html", "<p>TODO: fix this</p>", patterns, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckUnwantedPatterns_MultipleOccurrences_ReportsAll()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Template", Name = "Unfilled", Patterns = ["%("], CaseSensitive = true }
			};
			var issues = ContentQuality.CheckUnwantedPatterns("f.html", "%(var1) and %(var2)", patterns, DefaultConfig()).ToList();
			Assert.Equal(2, issues.Count);
		}

		[Fact]
		public void CheckUnwantedPatterns_UnconfiguredGroup_Skipped()
		{
			// IsConfigured returns false when Patterns is empty
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Test", Name = "Empty", Patterns = [], CaseSensitive = true }
			};
			var issues = ContentQuality.CheckUnwantedPatterns("f.html", "anything", patterns, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckUnwantedPatterns_OpenEnvelopeWithReferenceHits_CoalescesToOneIssue()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"], Reference = "CMS-Editor-Error" },
				new() { Category = "Security", Name = "CMS-Editor-Error", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["produkt.", "p_name"] }
			};
			// Opener %( present, closer )% absent (the broken case); produkt. and p_name sit
			// in the unbroken run after the opener → all three collapse into one finding.
			var issues = ContentQuality.CheckUnwantedPatterns(
				"f.html", "<x>%(produkt.278.p_name)</x>", patterns, DefaultConfig()).ToList();

			var issue = Assert.Single(issues);
			Assert.Equal("UNWANTED_PATTERN", issue.IssueType);
			Assert.Contains("CMS-Parameter-Leak", issue.Detail);
			Assert.Contains("missing closing ')%'", issue.Detail);
			Assert.Contains("— patterns: %(, produkt., p_name", issue.Detail);
		}

		[Fact]
		public void CheckUnwantedPatterns_OpenEnvelopeNoReferenceHitsInRange_EachFiresAlone()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"], Reference = "CMS-Editor-Error" },
				new() { Category = "Security", Name = "CMS-Editor-Error", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["produkt.", "p_name"] }
			};
			// produkt. is past the whitespace bounding the open envelope's region → not folded.
			var issues = ContentQuality.CheckUnwantedPatterns(
				"f.html", "%(foo.bar) produkt.", patterns, DefaultConfig()).ToList();

			Assert.Equal(2, issues.Count);
			Assert.Contains(issues, x => x.Detail.Contains("— pattern: %("));
			Assert.Contains(issues, x => x.Detail.Contains("— pattern: produkt."));
			Assert.DoesNotContain(issues, x => x.Detail.Contains("open placeholder"));
		}

		[Fact]
		public void CheckUnwantedPatterns_EnvelopeWithoutReference_DoesNotCoalesce()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"] },   // no Reference → no coalescing
				new() { Category = "Security", Name = "CMS-Editor-Error", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["produkt.", "p_name"] }
			};
			var issues = ContentQuality.CheckUnwantedPatterns(
				"f.html", "<x>%(produkt.278.p_name)</x>", patterns, DefaultConfig()).ToList();

			// Envelope fires (pattern: %() and both editor hits fire separately — three cards.
			Assert.Equal(3, issues.Count);
			Assert.DoesNotContain(issues, x => x.Detail.Contains("open placeholder"));
		}

		[Fact]
		public void CheckUnwantedPatterns_BalancedEnvelope_NotCoalesced()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"], Reference = "CMS-Editor-Error" },
				new() { Category = "Security", Name = "CMS-Editor-Error", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["produkt.", "p_name"] }
			};
			// Both delimiters present → balanced, not the broken case → no coalescing.
			var issues = ContentQuality.CheckUnwantedPatterns(
				"f.html", "<x>%(produkt.278.p_name)%</x>", patterns, DefaultConfig()).ToList();

			Assert.DoesNotContain(issues, x => x.Detail.Contains("open placeholder"));
			Assert.Contains(issues, x => x.Detail.Contains("— patterns: %(, )%"));
			Assert.Equal(3, issues.Count);
		}

		[Fact]
		public void CheckUnwantedPatterns_RegionStopsAtMarkupBoundary()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"], Reference = "Inner" },
				new() { Category = "Security", Name = "Inner", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["institut.", "name"] }
			};
			// The 'name' inside the placeholder folds; the 'name' after </p> is past the '<'
			// boundary and must NOT be folded — it surfaces on its own.
			var issues = ContentQuality.CheckUnwantedPatterns(
				"f.html", "%(institut.name)</p>name", patterns, DefaultConfig()).ToList();

			Assert.Contains(issues, x => x.Detail.Contains("open placeholder")
				&& x.Detail.Contains("institut."));
			Assert.Contains(issues, x => x.Detail == "Security: Inner — pattern: name");
		}

		[Fact]
		public void ExtractHighlightPatterns_MergedEnvelopeDetail_ReturnsAllPatterns()
		{
			// The merged Detail must round-trip through the highlighter so every folded
			// pattern is marked on the card and in the ticket.
			var word = "UNWANTED_PATTERN:Security: CMS-Parameter-Leak — open placeholder, " +
				"missing closing ')%' — patterns: %(, produkt., p_name";
			var result = ContentQualityTriage.ExtractHighlightPatterns(word);
			Assert.Equal(new[] { "%(", "produkt.", "p_name" }, result);
		}

		// ── WriteTranslationIssues end-to-end (fileset #286) ──────────────────

		[Fact]
		public void WriteTranslationIssues_ExcerptWithNewline_StaysSingleLineInFile()
		{
			// Regression test for the original Czech-page failure. The
			// content excerpt contained a literal newline from CMS copy-paste;
			// the writer used to compose a single string that File.AppendAllLines
			// would split into multiple physical lines. The IssueLogWriter sanitizes
			// the excerpt before composing, so the log file always contains
			// exactly one physical line per record.
			var path = Path.GetTempFileName();
			try
			{
				ContentQuality.WriteTranslationIssues(path, "productname.html",
				[
					("meta[@name=description]",
					 "For a hassle-free holiday \n- Our Product Name.",
					 "en")
				]);
				var lines = File.ReadAllLines(path);
				// Single physical line — the embedded newline was sanitized.
				Assert.Single(lines);
				// Field 0 is the filename — not a fragment of the content.
				var fields = lines[0].Split('|');
				Assert.Equal("productname.html", fields[0]);
				Assert.Equal("POTENTIAL_TRANSLATION", fields[1]);
				// Field 3 contains the WHOLE excerpt minus the newline.
				Assert.Contains("hassle-free holiday", fields[3]);
				Assert.Contains("Our Product Name.", fields[3]);
			}
			finally { File.Delete(path); }
		}

		// ── LocateQuoteFlags / LocateSystemMixMismatch (triage highlighting) ────

		[Fact]
		public void LocateQuoteFlags_WrongOpen_ReturnsOrphanCloserPosition()
		{
			// A bare orphan closer (space before) flags QUOTE_WRONG_OPEN; the located
			// position must be that U+2019 so triage can mark exactly it.
			var text  = "Hello \u2019 world";
			var flags = ContentQuality.LocateQuoteFlags(text, DefaultConfig(), "en");
			Assert.Contains(flags, f => f.Type == "QUOTE_WRONG_OPEN" && text[f.Pos] == '\u2019');
		}

		[Fact]
		public void LocateQuoteFlags_PairedQuotes_ReturnNoPairingTrigger()
		{
			// Cleanly paired single quotes — no pairing flag, hence no located trigger.
			var text  = "\u2018a good time\u2019";
			var flags = ContentQuality.LocateQuoteFlags(text, DefaultConfig(), "en");
			Assert.DoesNotContain(flags, f => f.Type == "QUOTE_WRONG_OPEN");
		}

		[Fact]
		public void LocateSystemMixMismatch_DivergentDoubleOpener_ReturnsItsPosition()
		{
			// German-double opener „ then English-double opener “ — the “ is the
			// divergent double-system opener that makes the block "mix systems".
			var text = "\u201EHallo\u201C und \u201Chi\u201D";
			var pos  = ContentQuality.LocateSystemMixMismatch(text);
			Assert.True(pos >= 0);
			Assert.Equal('\u201C', text[pos]);
		}

		[Fact]
		public void LocateSystemMixMismatch_SingleSystemOnly_ReturnsMinusOne()
		{
			// Only one double system present (plus singles, which are excluded) → no mix.
			var text = "\u201Cone\u201D and \u2018two\u2019";
			Assert.Equal(-1, ContentQuality.LocateSystemMixMismatch(text));
		}

		[Fact]
		public void LocateSystemMixMismatch_GermanCloserSharingGlyph_NotNominated_StrayOpenerIs()
		{
			// „X“ is a clean German pair; its closer is U+201C, which is ALSO the
			// English-double opener glyph. The mismatch must point at the STRAY
			// U+201C opener that follows — not the correctly-paired German closer.
			// (Regression guard for the context-wins fix: the locator now runs the
			// same stack walk as detection, so the popped closer is never nominated.)
			var text       = "\u201EX\u201C dann \u201CY";
			var firstU201C = text.IndexOf('\u201C');                 // German closer of „X“
			var strayU201C = text.IndexOf('\u201C', firstU201C + 1); // stray English opener
			var pos        = ContentQuality.LocateSystemMixMismatch(text);
			Assert.Equal(strayU201C, pos);
			Assert.NotEqual(firstU201C, pos);
		}

		// ── WordFinalSPossessive (English word-final s' possessive) ────────────

		private static ContentQualityConfig ConfigWithWordFinalSPossessive(bool on)
		{
			var cfg = DefaultConfig();
			cfg.ContentQualityApostropheElisions = new(StringComparer.OrdinalIgnoreCase)
			{
				["en"] = new ApostropheElisionProfile
				{
					SuffixElisions = ["s", "t", "d", "ll", "ve", "re", "m"],
					WordFinalSPossessive = on,
				},
			};
			return cfg;
		}

		[Theory]
		[InlineData("for our visitors\u2019 benefit")]
		[InlineData("our Ads\u2019 reach")]
		[InlineData("Users\u2019 behaviour can be analysed")]
		[InlineData("for advertising campaigns\u2019 success")]
		public void CheckQuotePairing_EnglishWordFinalSPossessive_NotFlagged(string text)
		{
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", text, ConfigWithWordFinalSPossessive(true), "en").ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_WRONG_OPEN");
		}

		[Fact]
		public void CheckQuotePairing_WordFinalSPossessiveDisabled_StillFlagsOrphan()
		{
			// With the flag off, the orphan U+2019 after 's' is the original false
			// positive — proving the rule, not some other change, suppresses it.
			var text   = "for advertising campaigns\u2019 success";
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", text, ConfigWithWordFinalSPossessive(false), "en").ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_WRONG_OPEN");
		}

		[Fact]
		public void CheckQuotePairing_WordFinalSPossessive_GenuineSingleQuotedSWordStillCloses()
		{
			// A real single-quoted word ending in s must still close cleanly — the
			// rule fires only for orphans, so the opener-present case is untouched
			// (no QUOTE_UNMATCHED manufactured, no QUOTE_WRONG_OPEN).
			var text   = "He said \u2018genius\u2019 today";
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", text, ConfigWithWordFinalSPossessive(true), "en").ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_WRONG_OPEN");
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_UNMATCHED");
		}

		[Fact]
		public void CheckQuotePairing_WordFinalSPossessive_NonEnabledProfileUnaffected()
		{
			// The de profile does not enable the rule (default false) → an orphan
			// s' still flags. Suppression is opt-in per profile, not automatic.
			var text   = "Das ist Klaus\u2019 Auto";
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", text, DefaultConfig(), "de").ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_WRONG_OPEN");
		}

		// ── Coverage hardening ────────────────────────
		// Locks behaviours a live corpus confirmed correct and closes
		// branches the coverage run showed unexercised.

		[Fact]
		public void CheckQuotePairing_Verification_SharedGlyphPairInBlock_OrphanStillFlagged()
		{
			// Coverage for ProximityPair's shared-character branch (U+201C acting as
			// the German closer of „…", the both-opener-and-closer glyph). It was
			// unexercised because every verification test used single quotes. Here a
			// clean „…" pair sits alongside a genuine orphan U+2019, so verification
			// runs and ProximityPair pairs the „…" via its context-wins path. That
			// pairing agrees with the cheap pass, so it adds no downgrade — the
			// assertion guards that the unrelated orphan stays a real finding.
			var text   = "\u201EHallo\u201C und \u2019 Ende";
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", text, DefaultConfigWithVerification(), "de").ToList();
			Assert.Single(issues);
			Assert.Equal("QUOTE_WRONG_OPEN", issues[0].IssueType);
		}

		[Fact]
		public void LocateQuoteFlags_SystemMixWithUnmatched_ReturnsBothTypes()
		{
			// A clean German „X“ pair followed by a stray English-double opener “Y:
			// the block both mixes systems and leaves an unmatched opener. Locks that
			// LocateQuoteFlags returns BOTH a QUOTE_SYSTEM_MIX position (the #467
			// branch the coverage run showed unexercised) and the pairing flag.
			var text  = "\u201EX\u201C \u201CY";
			var flags = ContentQuality.LocateQuoteFlags(text, DefaultConfig(), "de");
			Assert.Contains(flags, f => f.Type == "QUOTE_SYSTEM_MIX");
			Assert.Contains(flags, f => f.Type == "QUOTE_UNMATCHED");
		}

		[Fact]
		public void LocateQuoteFlags_EmptyText_ReturnsEmpty()
		{
			Assert.Empty(ContentQuality.LocateQuoteFlags(string.Empty, DefaultConfig(), "en"));
		}

		[Fact]
		public void LocateSystemMixMismatch_EmptyText_ReturnsMinusOne()
		{
			Assert.Equal(-1, ContentQuality.LocateSystemMixMismatch(string.Empty));
		}

		[Fact]
		public void CheckQuotePairing_NoApostropheProfilesConfigured_UsesBuiltInDefault()
		{
			// Emergency fallback: when no apostrophe-elision profiles are configured
			// at all (not even "_default"), the matcher falls back to a built-in
			// ApostropheElisionProfile (U+2018/U+2019 as apostrophe chars). Locks
			// that this path is reached and behaves — a clean single-quoted phrase
			// pairs without a flag.
			var cfg = DefaultConfig();
			cfg.ContentQualityApostropheElisions = new Dictionary<string, ApostropheElisionProfile>();
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", "\u2018a good time\u2019", cfg, "en").ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_WordFinalSPossessive_GenuineOrphanAfterS_DeliberatelySuppressed()
		{
			// DECISION LOCK — accepted trade-off, not a bug. With the flag on, ANY
			// orphan U+2019 after 's' is read as possessive, INCLUDING a genuine
			// quote closer whose opener is missing or in another block. We accept
			// missing that rare case to kill the common possessive false positive
			// (the flag-off test above proves the matcher would otherwise flag it).
			// Changing this must be a conscious decision — hence this guard.
			var text   = "the chapter Genetics\u2019 was removed";
			var issues = ContentQuality.CheckQuotePairing(
				"f.html", text, ConfigWithWordFinalSPossessive(true), "en").ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_WRONG_OPEN");
		}

		[Fact]
		public void CheckQuotePairing_SameGlyphBothSides_ReportsTwoUnmatchedOpeners()
		{
			// Live defect (datenschutz, formatabkuendigung): U+201C used as BOTH the
			// opening AND closing glyph — "X" rendered with two left-double quotes.
			// U+201C never closes U+201C, so each is pushed as an English-double
			// opener and both end unmatched.
			var text   = "Land \u201CCountry\u201C (Feldname)";
			var issues = ContentQuality.CheckQuotePairing("f.html", text, DefaultConfig(), "de").ToList();
			Assert.Equal(2, issues.Count(i => i.IssueType == "QUOTE_UNMATCHED"));
		}

		[Fact]
		public void CheckQuotePairing_GermanOpenerStraightQuoteCloser_ReportsUnmatched()
		{
			// Live defect (mobiles-bezahlen, glossar, betrugsversuche): German „
			// opener with a STRAIGHT ASCII quote (U+0022) as the visual closer.
			// U+0022 is not a typographic closer in any system, so „ is unmatched.
			var text   = "die App \u201EMobiles Bezahlen\u0022 noch nicht";
			var issues = ContentQuality.CheckQuotePairing("f.html", text, DefaultConfig(), "de").ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_UNMATCHED" && i.Detail.Contains("German-double"));
		}

		// ── CheckWordCollisions ───────────────────────────────────────────────

		[Fact]
		public void CheckWordCollisions_SpanThenBareText_LowerUpperSeam_Flags()
		{
			// Confirmed live shape: editor abuses <span class="h2"> as a heading,
			// bare text follows with no space → "Basismodul" + "Inhalte" merge.
			var doc = Doc("<p><span class=\"h2\">Basismodul</span>Inhalte des Moduls</p>");
			var issues = ContentQuality.CheckWordCollisions("f.html", doc, DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("WORD_COLLISION", issues[0].IssueType);
			// Context is the raw html around the seam (for code-with-highlight rendering).
			Assert.Contains("</span>Inhalte", issues[0].Context);
			Assert.Contains("Basismodul", issues[0].Context);
		}

		[Fact]
		public void CheckWordCollisions_BareTextThenSpan_LowerUpperSeam_Flags()
		{
			// Leading-seam direction: bare text abuts the inline element.
			var doc = Doc("<p>inhalte<span class=\"h2\">Basismodul</span></p>");
			var issues = ContentQuality.CheckWordCollisions("f.html", doc, DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("WORD_COLLISION", issues[0].IssueType);
			Assert.Contains("inhalte<span", issues[0].Context);
			Assert.Contains("Basismodul</span>", issues[0].Context);
		}

		[Fact]
		public void CheckWordCollisions_MidWordEmphasis_LowerLowerSeam_DoesNotFire()
		{
			// Legitimate inline emphasis splitting one word: <b>bezah</b>len →
			// "bezahlen". lowercase→lowercase seam must NOT be flagged.
			var doc = Doc("<p><b>bezah</b>len Sie hier</p>");
			var issues = ContentQuality.CheckWordCollisions("f.html", doc, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckWordCollisions_WhitespaceAtSeam_DoesNotFire()
		{
			// A separating space (or the Version-1 <br> shape that leaves a trailing
			// space inside the span) breaks the seam — no collision.
			var doc = Doc("<p><span class=\"h2\">Basismodul </span>Inhalte</p>");
			var issues = ContentQuality.CheckWordCollisions("f.html", doc, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckWordCollisions_NextSiblingStartsWithSpace_DoesNotFire()
		{
			var doc = Doc("<p><span class=\"h2\">Basismodul</span> Inhalte</p>");
			var issues = ContentQuality.CheckWordCollisions("f.html", doc, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}
	}
}
