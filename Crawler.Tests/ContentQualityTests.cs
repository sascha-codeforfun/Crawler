using HtmlAgilityPack;
using Xunit;
using Crawler.Quality;

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

		private static ContentQualityConfig DefaultConfig() => new()
		{
			ContentQualityExcerptRadius    = 120,
			ContentQualityQuoteFullSentence = false,  // keep tests deterministic
			ContentQualityQuoteMaxExcerpt  = 400,
		};

		// ── CheckQuotes — system mixing ───────────────────────────────────────

		[Fact]
		public void CheckQuotes_SingleSystem_NoMixIssue()
		{
			// Only German-double openers — must be in a block element for per-block detection
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<p>„Hallo”</p>");
			var issues = Quotes.Check("f.html", doc, QuoteConfig()).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_SYSTEM_MIX");
		}

		[Fact]
		public void CheckQuotes_TwoDoubleSystems_ReportsMix()
		{
			// German and English openers in the same block
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<p>\u201EHallo\u201C \u201CHello\u201D</p>");
			var issues = Quotes.Check("f.html", doc, QuoteConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_SYSTEM_MIX");
		}

		[Fact]
		public void CheckQuotes_SinglePlusSingleDouble_NoMixIssue()
		{
			// Single-quote system coexisting with double — should NOT flag as mix
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<p>„Hallo” and ‘Hi’</p>");
			var issues = Quotes.Check("f.html", doc, QuoteConfig()).ToList();
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
			var issues = Quotes.Check("f.html", doc, QuoteConfig()).ToList();
			// Neither block has a mix internally — no QUOTE_SYSTEM_MIX expected.
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_SYSTEM_MIX");
		}

		[Fact]
		public void CheckQuotes_MixWithinSingleBlock_Flagged()
		{
			// Both systems within one <p> — should flag.
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<p>\u201EHallo\u201C \u201CHello\u201D</p>");
			var issues = Quotes.Check("f.html", doc, QuoteConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_SYSTEM_MIX");
		}

		[Fact]
		public void CheckQuotes_TextOutsideBlockElements_NotChecked()
		{
			// Text directly in <div> (not a block element) is not quote-checked.
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<div>\u201EHallo\u201C \u201CHello\u201D</div>");
			var issues = Quotes.Check("f.html", doc, QuoteConfig()).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_SYSTEM_MIX");
		}

		[Fact]
		public void CheckQuotes_SingleDeclaredLanguage_AnchorsSystemCheck()
		{
			// A page declaring exactly one language resolves to a single-element
			// language set, so the quote system check is anchored to that language.
			// Here <html lang="de"> makes the German „…“ pair correct (no mix, no
			// wrong-close), exercising the single-language resolution path where
			// pageLanguage is the declared language rather than null.
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<html lang=\"de\"><body><p>\u201EHallo\u201C</p></body></html>");
			var issues = Quotes.Check("f.html", doc, QuoteConfig()).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_SYSTEM_MIX");
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_WRONG_CLOSE");
		}

		[Fact]
		public void CheckQuotes_BlockUnderNoscript_Excluded()
		{
			// Block elements whose parent is <script>, <style>, or <noscript> are
			// filtered out of the quote check (line: ParentNode.Name is not
			// script/style/noscript). HtmlAgilityPack parses <noscript> as real
			// markup, so a <p> directly inside it is surfaced as a child element
			// with ParentNode.Name == "noscript" — the shape that drives the
			// exclusion's false branch. The <p> carries both German and English
			// openers; were it checked it would flag a system mix, so its absence
			// confirms the exclusion fired. (Well-formed html/body wrapper matters:
			// a bare <noscript> fragment can parse differently.)
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml(
				"<!DOCTYPE html><html lang=\"de\"><body>" +
				"<noscript><p>\u201EHallo\u201C \u201CHello\u201D</p></noscript>" +
				"</body></html>");
			var issues = Quotes.Check("f.html", doc, QuoteConfig()).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_SYSTEM_MIX");
		}

		// ── CheckBareText ────────────────────────────────────────────────────

		[Fact]
		public void CheckBareText_TextDirectlyInDiv_Flagged()
		{
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<div>Bare text here</div>");
			var config = DefaultConfig();
			var issues = DefectBareText.CheckBareText("f.html", doc, config).ToList();
			Assert.Contains(issues, i => i.IssueType == "BARE_TEXT_IN_CONTAINER");
		}

		[Fact]
		public void CheckBareText_TextInParagraph_NotFlagged()
		{
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<div><p>Proper paragraph</p></div>");
			var config = DefaultConfig();
			var issues = DefectBareText.CheckBareText("f.html", doc, config).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "BARE_TEXT_IN_CONTAINER");
		}

		[Fact]
		public void CheckBareText_WhitespaceOnly_NotFlagged()
		{
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<div>   \n   <p>Content</p></div>");
			var config = DefaultConfig();
			var issues = DefectBareText.CheckBareText("f.html", doc, config).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "BARE_TEXT_IN_CONTAINER");
		}

		[Fact]
		public void CheckBareText_TextInSection_Flagged()
		{
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml("<section>Bare text in section</section>");
			var config = DefaultConfig();
			var issues = DefectBareText.CheckBareText("f.html", doc, config).ToList();
			Assert.Contains(issues, i => i.IssueType == "BARE_TEXT_IN_CONTAINER");
		}

		// ── CheckQuotePairing — correct pairs ─────────────────────────────────

		[Fact]
		public void CheckQuotePairing_GermanDoubleCorrect_NoIssue()
		{
			// „Hallo“ — correct German pair: U+201E opens, U+201C closes (66-Zeichen oben)
			var issues = Quotes.CheckPairing("f.html", "\u201EHallo\u201C", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_EnglishDoubleCorrect_NoIssue()
		{
			// "Hello" — correct English double pair
			var issues = Quotes.CheckPairing("f.html", "\u201CHello\u201D", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_GermanGuillemetCorrect_NoIssue()
		{
			// «Hallo» — correct German guillemet pair (« opens, » closes)
			var issues = Quotes.CheckPairing("f.html", "\u00ABHallo\u00BB", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_GermanSingleCorrect_NoIssue()
		{
			// ‚Hallo' — correct German single pair
			var issues = Quotes.CheckPairing("f.html", "\u201AHallo\u2019", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		// ── CheckQuotePairing — wrong closer ──────────────────────────────────

		[Fact]
		public void CheckQuotePairing_GermanOpenerWrongCloser_ReportsWrongClose()
		{
			// „ opened with German-double but closed with » (Guillemet closer)
			var issues = Quotes.CheckPairing("f.html", "\u201EHallo\u00BB", DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_WRONG_CLOSE");
		}

		[Fact]
		public void CheckQuotePairing_WrongClose_DetailMentionsBothSystems()
		{
			// „ opened with German-double but closed with » (Guillemet closer)
			var issues = Quotes.CheckPairing("f.html", "\u201EHallo\u00BB", DefaultConfig()).ToList();
			var wrongClose = issues.FirstOrDefault(i => i.IssueType == "QUOTE_WRONG_CLOSE");
			Assert.NotNull(wrongClose);
			Assert.Contains("German-double", wrongClose!.Detail);
		}

		// ── D060 — U+201E…U+201D resolves by page language ────────────────────
		// „…” is correct for the Slavic-double languages (pl/ro/bg/cs) and wrong for
		// German (which closes „…“ with U+201C). The shared U+201E opener is
		// disambiguated by page language, so the same byte sequence is accepted or
		// flagged depending only on the language passed.

		[Fact]
		public void CheckQuotePairing_De_LowNineThenHighNine_ReportsWrongClose()
		{
			// REGRESSION LOCK: „Begriff” on a de page → U+201D is the wrong closer
			// (German closes with U+201C). Must stay flagged after Slavic-double exists.
			var issues = Quotes.CheckPairing(
				"f.html", "\u201EBegriff\u201D", DefaultConfig(), "de").ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_WRONG_CLOSE");
		}

		[Fact]
		public void CheckQuotePairing_Undeclared_LowNineThenHighNine_ReportsWrongClose()
		{
			// No page language (empty default → null): the shared opener falls back to
			// German-double, so „Begriff” still flags. Undeclared pages are not silently
			// granted Slavic typography.
			var issues = Quotes.CheckPairing(
				"f.html", "\u201EBegriff\u201D", DefaultConfig(), null).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_WRONG_CLOSE");
		}

		[Fact]
		public void CheckQuotePairing_De_CorrectGerman_NoWrongClose()
		{
			// „Begriff“ on a de page is correct (U+201C closer) → clean.
			var issues = Quotes.CheckPairing(
				"f.html", "\u201EBegriff\u201C", DefaultConfig(), "de").ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_WRONG_CLOSE");
		}

		[Theory]
		[InlineData("pl")]
		[InlineData("ro")]
		[InlineData("bg")]
		[InlineData("cs")]
		public void CheckQuotePairing_SlavicLanguage_LowNineThenHighNine_Clean(string language)
		{
			// „Wyraz” is correct typography for each Slavic-double language → no
			// QUOTE_WRONG_CLOSE and no QUOTE_UNMATCHED.
			var issues = Quotes.CheckPairing(
				"f.html", "\u201EWyraz\u201D", DefaultConfig(), language).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_WRONG_CLOSE");
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_UNMATCHED");
		}

		[Fact]
		public void CheckQuotePairing_Pl_GermanStyleClose_IsNotClean()
		{
			// The inverse guard: on a pl page, „Wyraz“ closing with U+201C is NOT
			// Polish typography. U+201C is not a Slavic-double closer (Slavic closes
			// with U+201D) and is itself an English-double opener, so it is pushed
			// rather than consumed — the block surfaces as unmatched openers, not a
			// wrong-closer. Either way the wrong typography is detected (not clean);
			// this confirms the pl anchor does not blanket-accept any closer under „.
			var issues = Quotes.CheckPairing(
				"f.html", "\u201EWyraz\u201C", DefaultConfig(), "pl").ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_UNMATCHED");
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_WRONG_CLOSE");
		}

		// ── CheckQuotePairing — wrong opener (closer with no opener) ──────────

		[Fact]
		public void CheckQuotePairing_CloserWithNoOpener_ReportsWrongOpen()
		{
			// " with no preceding opener — straight quote used as opener
			var issues = Quotes.CheckPairing("f.html", "Hello \u201D world", DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_WRONG_OPEN");
		}

		// ── CheckQuotePairing — apostrophe disambiguation ─────────────────────

		[Fact]
		public void CheckQuotePairing_ApostropheAfterLetter_NotFlaggedAsCloser()
		{
			// geht's — U+2019 between letters is apostrophe, not a quote closer
			var issues = Quotes.CheckPairing("f.html", "Das geht\u2019s nicht", DefaultConfig()).ToList();
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
			var issues = Quotes.CheckPairing(
				"f.html", "Was ist mit \u2018ner Limonade?", DefaultConfig(), "de").ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_EnglishContraction_NotFlagged()
		{
			// funktioniert's — U+2019 + s is a configured elision
			var issues = Quotes.CheckPairing("f.html", "So einfach funktioniert\u2019s:", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_ElisionSuffixAtEndOfText_NotFlagged()
		{
			// Boundary-after Rule 1a: the matched suffix is satisfied by the
			// end-of-text arm (i + 1 + e.Length >= text.Length) rather than the
			// non-letter-after arm. Here "'s" ends the string with nothing after it,
			// so the boundary is end-of-text — the elision is still recognised and
			// the apostrophe is not treated as an opening single quote.
			var issues = Quotes.CheckPairing(
				"f.html", "So einfach funktioniert\u2019s", DefaultConfig(), "de").ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_SingleQuoteAroundPhrase_CloserNotApostrophe()
		{
			// 'a good time' — closer preceded by 'e' but followed by space, not a letter
			// So it should NOT be treated as apostrophe
			var issues = Quotes.CheckPairing("f.html", "\u2018a good time\u2019", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_RightSingleAfterSpace_FlaggedAsCloser()
		{
			// U+2019 not preceded by a letter — treated as a closer with no opener
			var issues = Quotes.CheckPairing("f.html", "Hello \u2019 world", DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_WRONG_OPEN");
		}

		[Fact]
		public void CheckQuotePairing_EmptySuffixEntry_GuardedOut_DoesNotMatchEverything()
		{
			// Adversarial config: a malformed elision profile containing an empty
			// suffix string. The Rule 1a guard (e.Length > 0) must skip the empty
			// entry. Without that guard, "".Equals("") would be true at every
			// apostrophe whose following char is a non-letter or end-of-text, turning
			// EVERY such apostrophe into a false elision and silently swallowing real
			// findings. Here a lone U+2019 between spaces must still flag as
			// QUOTE_WRONG_OPEN — proving the empty entry was guarded out and the
			// legitimate "s" entry (which does not match before a space) did not fire.
			var cfg = DefaultConfig();
			cfg.ContentQualityApostropheElisions = new(StringComparer.OrdinalIgnoreCase)
			{
				["_default"] = new ApostropheElisionProfile
				{
					ApostropheChars = ['\u2018', '\u2019'],
					SuffixElisions = ["", "s"],
				},
			};

			var issues = Quotes.CheckPairing(
				"f.html", "Hello \u2019 world", cfg, "xx").ToList();
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
			var issues = Quotes.CheckPairing(
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
			var issues = Quotes.CheckPairing(
				"f.html", text, DefaultConfig(), "fr").ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_UnknownLanguage_FallsBackToDefault()
		{
			// Page language "xx" is not in the profile dictionary; "_default"
			// is used. The default profile has empty elision lists, but Rule 2
			// (between-letters) still catches the common case.
			var issues = Quotes.CheckPairing(
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
			var issues = Quotes.CheckPairing(
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
			var issues = Quotes.CheckPairing(
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
			var issues = Quotes.CheckPairing(
				"f.html", "Was ist mit \u2018ner Limonade?", DefaultConfig(), "fr").ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_UNMATCHED");
		}

		[Fact]
		public void CheckQuotePairing_DefaultProfileSelected_WhenLanguageAbsent()
		{
			// Profile resolution: when the elision dictionary HAS a "_default"
			// entry but no entry for the page language, that "_default" profile is
			// used (not the built-in empty profile). Page language "xx" is absent;
			// the "_default" suffix list carries "nem", so the front-elision
			// 'nem (apostrophe, "nem", word boundary) is recognised via Rule 1a
			// and suppressed → clean. With the built-in empty profile the suffix
			// would not match and U+2018 would surface, so a clean result proves
			// the "_default" branch was taken.
			var cfg = DefaultConfig();
			cfg.ContentQualityApostropheElisions = new(StringComparer.OrdinalIgnoreCase)
			{
				["_default"] = new ApostropheElisionProfile
				{
					ApostropheChars = ['\u2018', '\u2019'],
					SuffixElisions = ["nem", "ner", "ne"],
				},
			};

			var issues = Quotes.CheckPairing(
				"f.html", "mit \u2018nem Auto", cfg, "xx").ToList();
			Assert.Empty(issues);
		}

		// ── CheckQuotePairing — elision (boundary-after) & genuine flags ──────
		// The detector classifies an apostrophe-char as an elision (skip) only
		// when a suffix entry matches up to a word boundary, a prefix rule fires,
		// or it sits between letters. A quoted word that merely STARTS with a
		// suffix letter ('show → 's'+how) is not an elision, so its opening quote
		// pairs normally and produces no finding. (The former second-pass
		// verification that downgraded such cases to QUOTE_AMBIGUOUS was removed:
		// the source classification is now correct, leaving nothing to downgrade.)

		[Fact]
		public void CheckQuotePairing_EnglishSingleQuotedPhrase_PairsCleanly()
		{
			// 'show password': 's' is a suffix entry, but "show" continues with
			// letters after it, so boundary-after refuses the elision. The opening
			// U+2018 is a real opener and pairs with the closing U+2019 → no
			// finding. (Previously eaten as elision, orphan flagged, softened to
			// AMBIGUOUS; now fixed at the source.)
			var text = "Use the \u2018show password\u2019 function.";
			var issues = Quotes.CheckPairing("f.html", text, DefaultConfig(), "en").ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_OpeningQuoteBeforeSuffixWord_PairsCleanly()
		{
			// 'Request money' / 'Show my QR code': opening U+2018 before a word
			// starting with a suffix letter ('re'quest, 's'how). Letters follow
			// the suffix, so not an elision → both open as quotes and pair → no
			// finding.
			var text = "click \u2018Request money\u2019 then \u2018Show my QR code\u2019.";
			var issues = Quotes.CheckPairing("f.html", text, DefaultConfig(), "en").ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_ContractionAndQuotedPhrase_NeitherFlagged()
		{
			// The das-neue card verbatim: "you're" (U+2019 between letters) plus a
			// clean 'show password' pair. Boundary-after + Rule 2 mean neither the
			// contraction nor the quoted phrase produces a finding.
			var text = "Whether you\u2019re surfing, use the \u2018show password\u2019 function.";
			var issues = Quotes.CheckPairing("f.html", text, DefaultConfig(), "en").ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckQuotePairing_GenuineWrongOpen_StaysHighConfidence()
		{
			// A real orphan U+2019 with no possible English-single opener
			// anywhere in the block should stay as QUOTE_WRONG_OPEN even with
			// verification enabled — proximity pairing finds no pair → no
			// downgrade. Construct: closer at start of block, no opener anywhere.
			var text = "\u2019 stray closer with nothing before it.";
			var issues = Quotes.CheckPairing("f.html", text, DefaultConfig(), "en").ToList();
			Assert.Single(issues);
			Assert.Equal("QUOTE_WRONG_OPEN", issues[0].IssueType);
		}

		[Fact]
		public void CheckQuotePairing_CleanPairBesideGenuineOrphan_OnlyOrphanFlagged()
		{
			// 'show password' pairs cleanly (boundary-after); an unrelated stray
			// U+201D at the end has no English-double opener → exactly one
			// finding, the genuine orphan.
			var text = "Use \u2018show password\u2019 here. Also \u201D stray.";
			var issues = Quotes.CheckPairing("f.html", text, DefaultConfig(), "en").ToList();
			Assert.Single(issues);
			Assert.Equal("QUOTE_WRONG_OPEN", issues[0].IssueType);
		}

		[Fact]
		public void CheckQuotePairing_SuffixWordAcrossSystems_NoSpuriousPair()
		{
			// U+2018 (English-single opener) ... U+201D (English-DOUBLE closer):
			// different systems, must not pair. "something" starts with 's' but
			// continues with letters, so boundary-after treats U+2018 as a real
			// opener (left unmatched) and U+201D as an orphan closer — both flag,
			// and neither is AMBIGUOUS (that tier no longer exists).
			var text = "Open \u2018something here\u201D close.";
			var issues = Quotes.CheckPairing("f.html", text, DefaultConfig(), "en").ToList();
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
			var issues = Quotes.CheckPairing("f.html", text, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		// ── CheckQuotePairing — unmatched opener ───────────────────────────────

		[Fact]
		public void CheckQuotePairing_UnmatchedOpener_AlwaysReported()
		{
			// Per-block scoping means any unmatched opener is reported regardless of position.
			// The 500-char distance cutoff was removed — block boundary is the natural limit.
			var issues = Quotes.CheckPairing("f.html", "Some text \u201ENo closer here", DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_UNMATCHED");
		}

		[Fact]
		public void CheckQuotePairing_UnmatchedOpenerLongBlock_StillReported()
		{
			// Even an opener far from the end of a long block is reported — no distance cutoff.
			var farText = "\u201EOpener" + new string('x', 600);
			var issues = Quotes.CheckPairing("f.html", farText, DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_UNMATCHED");
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
			var flags = Quotes.LocateFlags(text, DefaultConfig(), "en");
			Assert.Contains(flags, f => f.Type == "QUOTE_WRONG_OPEN" && text[f.Pos] == '\u2019');
		}

		[Fact]
		public void LocateQuoteFlags_PairedQuotes_ReturnNoPairingTrigger()
		{
			// Cleanly paired single quotes — no pairing flag, hence no located trigger.
			var text  = "\u2018a good time\u2019";
			var flags = Quotes.LocateFlags(text, DefaultConfig(), "en");
			Assert.DoesNotContain(flags, f => f.Type == "QUOTE_WRONG_OPEN");
		}

		[Fact]
		public void LocateSystemMixMismatch_DivergentDoubleOpener_ReturnsItsPosition()
		{
			// German-double opener „ then English-double opener “ — the “ is the
			// divergent double-system opener that makes the block "mix systems".
			var text = "\u201EHallo\u201C und \u201Chi\u201D";
			var pos  = Quotes.LocateSystemMixMismatch(text);
			Assert.True(pos >= 0);
			Assert.Equal('\u201C', text[pos]);
		}

		[Fact]
		public void LocateSystemMixMismatch_SingleSystemOnly_ReturnsMinusOne()
		{
			// Only one double system present (plus singles, which are excluded) → no mix.
			var text = "\u201Cone\u201D and \u2018two\u2019";
			Assert.Equal(-1, Quotes.LocateSystemMixMismatch(text));
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
			var pos        = Quotes.LocateSystemMixMismatch(text);
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
			var issues = Quotes.CheckPairing(
				"f.html", text, ConfigWithWordFinalSPossessive(true), "en").ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_WRONG_OPEN");
		}

		[Fact]
		public void CheckQuotePairing_WordFinalSPossessiveDisabled_StillFlagsOrphan()
		{
			// With the flag off, the orphan U+2019 after 's' is the original false
			// positive — proving the rule, not some other change, suppresses it.
			var text   = "for advertising campaigns\u2019 success";
			var issues = Quotes.CheckPairing(
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
			var issues = Quotes.CheckPairing(
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
			var issues = Quotes.CheckPairing(
				"f.html", text, DefaultConfig(), "de").ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_WRONG_OPEN");
		}

		// ── Coverage hardening ────────────────────────
		// Locks behaviours a live corpus confirmed correct and closes
		// branches the coverage run showed unexercised.

		[Fact]
		public void CheckQuotePairing_CleanGermanPairBesideOrphan_OnlyOrphanFlagged()
		{
			// A clean „…" pair (U+201C as the German closer, the both-opener-and-
			// closer glyph, resolved by the context-wins rule) sits beside a
			// genuine orphan U+2019 with spaces on both sides. The pair produces
			// no finding; the unrelated orphan stays a real QUOTE_WRONG_OPEN.
			var text   = "\u201EHallo\u201C und \u2019 Ende";
			var issues = Quotes.CheckPairing("f.html", text, DefaultConfig(), "de").ToList();
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
			var flags = Quotes.LocateFlags(text, DefaultConfig(), "de");
			Assert.Contains(flags, f => f.Type == "QUOTE_SYSTEM_MIX");
			Assert.Contains(flags, f => f.Type == "QUOTE_UNMATCHED");
		}

		[Fact]
		public void LocateQuoteFlags_EmptyText_ReturnsEmpty()
		{
			Assert.Empty(Quotes.LocateFlags(string.Empty, DefaultConfig(), "en"));
		}

		[Fact]
		public void LocateSystemMixMismatch_EmptyText_ReturnsMinusOne()
		{
			Assert.Equal(-1, Quotes.LocateSystemMixMismatch(string.Empty));
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
			var issues = Quotes.CheckPairing(
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
			var issues = Quotes.CheckPairing(
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
			var issues = Quotes.CheckPairing("f.html", text, DefaultConfig(), "de").ToList();
			Assert.Equal(2, issues.Count(i => i.IssueType == "QUOTE_UNMATCHED"));
		}

		[Fact]
		public void CheckQuotePairing_GermanOpenerStraightQuoteCloser_ReportsMixedKind()
		{
			// Live defect (mobiles-bezahlen, glossar, betrugsversuche): German „ opener
			// closed by a STRAIGHT ASCII quote (U+0022). D053: opening typographic then
			// closing straight is a KIND mismatch — QUOTE_MIXED_KIND (previously
			// misreported as "unclosed opener", because U+0022 was invisible to the walk).
			var text   = "die App \u201EMobiles Bezahlen\u0022 noch nicht";
			var issues = Quotes.CheckPairing("f.html", text, DefaultConfig(), "de").ToList();
			Assert.Contains(issues, i => i.IssueType == "QUOTE_MIXED_KIND" && i.Detail.Contains("German-double"));
			Assert.DoesNotContain(issues, i => i.IssueType == "QUOTE_UNMATCHED");
		}

		// ── CheckWordCollisions ───────────────────────────────────────────────

		[Fact]
		public void CheckWordCollisions_SpanThenBareText_LowerUpperSeam_Flags()
		{
			// Confirmed live shape: editor abuses <span class="h2"> as a heading,
			// bare text follows with no space → "Basismodul" + "Inhalte" merge.
			var doc = Doc("<p><span class=\"h2\">Basismodul</span>Inhalte des Moduls</p>");
			var issues = DefectWordCollisions.CheckWordCollisions("f.html", doc, DefaultConfig()).ToList();
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
			var issues = DefectWordCollisions.CheckWordCollisions("f.html", doc, DefaultConfig()).ToList();
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
			var issues = DefectWordCollisions.CheckWordCollisions("f.html", doc, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckWordCollisions_Detail_CarriesSourceRankPrefix()
		{
			// D047: each finding's Detail is prefixed with a "[N]" document-order
			// rank (the inline node's position) so BuildGroups can recover page
			// order; the log otherwise freezes findings in ConcurrentBag/LIFO
			// order. The single span here is the first inline node, so rank 0.
			var trailing = DefectWordCollisions.CheckWordCollisions(
				"f.html", Doc("<p><span class=\"h2\">Basismodul</span>Inhalte des Moduls</p>"),
				DefaultConfig()).ToList();
			Assert.Single(trailing);
			Assert.StartsWith("[0] Inline <span> abuts bare text", trailing[0].Detail);

			var leading = DefectWordCollisions.CheckWordCollisions(
				"f.html", Doc("<p>inhalte<span class=\"h2\">Basismodul</span></p>"),
				DefaultConfig()).ToList();
			Assert.Single(leading);
			Assert.StartsWith("[0] Bare text abuts inline <span>", leading[0].Detail);
		}

		[Fact]
		public void CheckWordCollisions_WhitespaceAtSeam_DoesNotFire()
		{
			// A separating space (or the Version-1 <br> shape that leaves a trailing
			// space inside the span) breaks the seam — no collision.
			var doc = Doc("<p><span class=\"h2\">Basismodul </span>Inhalte</p>");
			var issues = DefectWordCollisions.CheckWordCollisions("f.html", doc, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckWordCollisions_NextSiblingStartsWithSpace_DoesNotFire()
		{
			var doc = Doc("<p><span class=\"h2\">Basismodul</span> Inhalte</p>");
			var issues = DefectWordCollisions.CheckWordCollisions("f.html", doc, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}
	}
}
