using Xunit;
using Crawler.Quality;
using HtmlAgilityPack;

namespace Crawler.Tests.Quality
{
	public class ControlCharsTests
	{
		private static HtmlDocument Doc(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

				[Fact]
		public void Check_PlainContent_NoIssues()
		{
			var doc = Doc(
				"<html><head>" +
				"<title>Normal Title</title>" +
				"<meta name=\"description\" content=\"A normal description.\">" +
				"</head></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_NewlineInMetaDescription_Flagged()
		{
			// The original Czech-page failure: meta description contains a
			// literal newline character from CMS copy-paste.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"description\" content=\"hassle-free\nshopping\">" +
				"</head></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("CONTROL_CHARS_IN_CONTENT", issues[0].IssueType);
			Assert.Contains("LF", issues[0].Detail);
			Assert.Contains("description", issues[0].Detail);
		}

		[Fact]
		public void Check_TabInTitle_Flagged()
		{
			var doc = Doc("<html><head><title>Two\tTabs</title></head></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("TAB", issues[0].Detail);
			Assert.Contains("title", issues[0].Detail);
		}

		[Fact]
		public void Check_ZeroWidthSpace_Flagged()
		{
			// U+200B in a description — invisible to authors, breaks tooling.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"description\" content=\"clean\u200Btext\">" +
				"</head></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("U+200B", issues[0].Detail);
		}

		[Fact]
		public void Check_BidiOverride_Flagged()
		{
			// U+202E RLO (right-to-left override) — Trojan Source territory.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"keywords\" content=\"safe\u202Eevil\">" +
				"</head></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("U+202E", issues[0].Detail);
		}

		[Fact]
		public void Check_LineSeparator_Flagged()
		{
			// Fileset #286b regression: U+2028 LINE SEPARATOR appeared in
			// real-world CMS-pasted content (HTML byte sequence E2 80 A8)
			// between paragraphs of meta description text. Invisible to the
			// CMS author but problematic in downstream tooling.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"description\" content=\"section.\u2028 Tip: Start the setup\">" +
				"</head></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("U+2028", issues[0].Detail);
			Assert.Contains("LINE SEPARATOR", issues[0].Detail);
		}

		[Fact]
		public void Check_ParagraphSeparator_Flagged()
		{
			// U+2029 PARAGRAPH SEPARATOR — sibling of U+2028.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"description\" content=\"para1\u2029para2\">" +
				"</head></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("U+2029", issues[0].Detail);
		}

		[Fact]
		public void Check_HighUnicode_NotFlagged()
		{
			// Legitimate non-ASCII (German umlauts, French accents, Polish
			// diacritics, Czech háčky) must NOT trigger the check.
			var doc = Doc(
				"<html><head>" +
				"<title>Größe — l'été — Łódź — žluťoučký</title>" +
				"</head></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_ContextShowsVisibleMarker()
		{
			// The Context field of the emitted issue must replace invisible
			// chars with visible markers so the operator can see WHERE the
			// problem is, not just that something happened.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"description\" content=\"line1\nline2\">" +
				"</head></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("[LF]", issues[0].Context);
			Assert.Contains("line1", issues[0].Context);
			Assert.Contains("line2", issues[0].Context);
		}

		[Fact]
		public void Check_ContextShowsOperatorFriendlyMarker_LineSeparator()
		{
			// Obscure codepoints render with the human-readable
			// kind name AND the codepoint, so non-technical CMS editors can
			// understand the issue. Real-world scenario:
			// U+2028 between "section." and " Tip: Start...".
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"description\" content=\"section.\u2028 Tip: Start the setup\">" +
				"</head></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			// Marker tells the editor what KIND of invisible character is there.
			Assert.Contains("[INVISIBLE LINE SEPARATOR U+2028]", issues[0].Context);
			// Surrounding content is preserved so the editor can locate the issue.
			Assert.Contains("section.", issues[0].Context);
			Assert.Contains("Tip: Start", issues[0].Context);
		}

		[Fact]
		public void Check_ContextShowsOperatorFriendlyMarker_ZeroWidthSpace()
		{
			// U+200B — a common copy-paste import from Word and PDFs.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"description\" content=\"clean\u200Btext\">" +
				"</head></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("[INVISIBLE ZERO-WIDTH SPACE U+200B]", issues[0].Context);
		}

		[Fact]
		public void Check_ContextShowsOperatorFriendlyMarker_BidiControl()
		{
			// U+202E RLO — the Trojan Source vector. Operator-friendly marker
			// makes the security concern visible without requiring the editor
			// to know what U+202E means.
			var doc = Doc(
				"<html><head>" +
				"<meta name=\"keywords\" content=\"safe\u202Eevil\">" +
				"</head></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("[INVISIBLE BIDI CONTROL U+202E]", issues[0].Context);
		}

		private static HtmlAgilityPack.HtmlDocument HtmlDoc(string bodyText)
		{
			var doc = new HtmlAgilityPack.HtmlDocument();
			doc.LoadHtml($"<html><body><p>{bodyText}</p></body></html>");
			return doc;
		}

				// Scans a string for the first control/invisible char; returns codepoint + label.

		[Fact]
		public void FindFirstControlChar_EmptyString_ReturnsNull()
		{
			Assert.Null(ControlChars.FindFirstControlChar(""));
		}

		[Fact]
		public void FindFirstControlChar_NoControls_ReturnsNull()
		{
			Assert.Null(ControlChars.FindFirstControlChar("plain ASCII text"));
		}

		[Theory]
		[InlineData('\r', "CR (U+000D)")]
		[InlineData('\n', "LF (U+000A)")]
		[InlineData('\t', "TAB (U+0009)")]
		public void FindFirstControlChar_KnownShortForm_ReturnsShortLabel(char ch, string expected)
		{
			var result = ControlChars.FindFirstControlChar($"hello{ch}world");
			Assert.NotNull(result);
			Assert.Equal((int)ch, result.Value.Codepoint);
			Assert.Equal(expected, result.Value.Name);
		}

		[Theory]
		[InlineData('\u200B', "ZWSP (U+200B)")]
		[InlineData('\u200C', "ZWNJ (U+200C)")]
		[InlineData('\u200D', "ZWJ (U+200D)")]
		[InlineData('\uFEFF', "BOM/ZWNBSP (U+FEFF)")]
		[InlineData('\u2028', "LINE SEPARATOR (U+2028)")]
		[InlineData('\u2029', "PARAGRAPH SEPARATOR (U+2029)")]
		[InlineData('\u007F', "DEL (U+007F)")]
		[InlineData('\u00AD', "SHY (U+00AD)")]
		[InlineData('\u061C', "ALM (U+061C)")]
		[InlineData('\u200E', "LRM (U+200E)")]
		[InlineData('\u200F', "RLM (U+200F)")]
		[InlineData('\u2060', "WJ (U+2060)")]
		[InlineData('\u2062', "invisible math (U+2062)")]
		public void FindFirstControlChar_KnownInvisible_ReturnsLabel(char ch, string expected)
		{
			var result = ControlChars.FindFirstControlChar($"x{ch}y");
			Assert.NotNull(result);
			Assert.Equal(expected, result.Value.Name);
		}

		[Theory]
		[InlineData('\u202A')]
		[InlineData('\u202E')]
		[InlineData('\u2066')]
		[InlineData('\u2069')]
		public void FindFirstControlChar_BidiRange_ReturnsFormattedLabel(char ch)
		{
			var result = ControlChars.FindFirstControlChar($"x{ch}y");
			Assert.NotNull(result);
			Assert.Contains("bidi", result.Value.Name);
		}

		[Fact]
		public void FindFirstControlChar_C0ControlNonShortForm_ReturnsFormattedC0()
		{
			// U+0007 (BEL) is C0 but not CR/LF/TAB.
			var result = ControlChars.FindFirstControlChar("a\u0007b");
			Assert.NotNull(result);
			Assert.Equal(0x0007, result.Value.Codepoint);
			Assert.StartsWith("C0 control (U+", result.Value.Name);
		}

		[Fact]
		public void FindFirstControlChar_C1Control_ReturnsFormattedC1()
		{
			var result = ControlChars.FindFirstControlChar("a\u0090b");
			Assert.NotNull(result);
			Assert.StartsWith("C1 control (U+", result.Value.Name);
		}

		[Fact]
		public void FindFirstControlChar_ReturnsFirstOnly_NotSubsequent()
		{
			// Two control chars; only the first is reported.
			var result = ControlChars.FindFirstControlChar("a\rb\nc");
			Assert.NotNull(result);
			Assert.Equal('\r', (char)result.Value.Codepoint);
		}

		// —— Editor-class body scan (D107) ——————————————————————
		// CONTROL_CHARS_IN_CONTENT now also scans editor-authored prose
		// containers (ContentQualityBlockElements), the complementary half of
		// the architect-class INVISIBLE_CHAR_IN_BODY detector.

		[Fact]
		public void Check_InvisibleInParagraph_Flagged()
		{
			var doc = Doc("<html><body><p>clean\u200Btext</p></body></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("CONTROL_CHARS_IN_CONTENT", issues[0].IssueType);
			Assert.Contains("U+200B", issues[0].Detail);
			Assert.Contains("<p>", issues[0].Detail);
		}

		[Fact]
		public void Check_InvisibleInHeading_Flagged()
		{
			var doc = Doc("<html><body><h2>head\u200Cing</h2></body></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("U+200C", issues[0].Detail);
			Assert.Contains("<h2>", issues[0].Detail);
		}

		[Fact]
		public void Check_InvisibleInDefinitionList_Flagged()
		{
			// dd/dt are editor-authored prose; the default config now scans them.
			var doc = Doc("<html><body><dl><dt>te\u200Brm</dt><dd>de\u200Bf</dd></dl></body></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Equal(2, issues.Count);
			Assert.Contains(issues, i => i.Detail.Contains("<dt>"));
			Assert.Contains(issues, i => i.Detail.Contains("<dd>"));
		}

		[Fact]
		public void Check_InvisibleInNonProseContainer_NotFlagged()
		{
			// <div> is not an editor-prose container; that text is the architect-
			// class detector's domain (CSV), not triage. Editor must leave it alone.
			var doc = Doc("<html><body><div>foo\u200Bbar</div></body></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_InvisibleInNestedInlineChild_NotFlaggedByEditor()
		{
			// The ZWSP's direct parent is <b>, not <p>; keyed on direct parent the
			// editor leaves nested-inline text to the architect-class detector.
			var doc = Doc("<html><body><p>ok <b>in\u200Bside</b> ok</p></body></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_BodyContextShowsMarker()
		{
			var doc = Doc("<html><body><p>clean\u200Btext</p></body></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("[INVISIBLE ZERO-WIDTH SPACE U+200B]", issues[0].Context);
			Assert.Contains("clean", issues[0].Context);
		}

		// —— D108: body excludes CR/LF/TAB; expanded codepoint coverage ——————

		[Fact]
		public void Check_NewlineInParagraph_NotFlagged()
		{
			// Source newlines/tabs in body prose are insignificant whitespace (HTML
			// collapses them); flagging floods every indented list. Title/meta still
			// flag CR/LF/TAB — see Check_NewlineInMetaDescription_Flagged.
			var doc = Doc("<html><body><p>line1\nline2</p></body></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_TabInListItem_NotFlagged()
		{
			var doc = Doc("<html><body><ul><li>a\tb</li></ul></body></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_SoftHyphenInParagraph_Flagged()
		{
			// SHY is detected (suppressible per-codepoint via ContentQualityIssueSuppressions).
			var doc = Doc("<html><body><p>soft\u00ADhyphen</p></body></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("U+00AD", issues[0].Detail);
		}

		[Fact]
		public void Check_BidiMarkInParagraph_Flagged()
		{
			var doc = Doc("<html><body><p>a\u200Eb</p></body></html>");
			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("U+200E", issues[0].Detail);
		}

		[Fact]
		public void FindFirstControlChar_WhitespaceControlsExcluded_BodyStance()
		{
			// includeWhitespaceControls:false is the body stance — CR/LF/TAB are not
			// invisibles there, but zero-widths etc. still are.
			Assert.Null(ControlChars.FindFirstControlChar("a\nb", includeWhitespaceControls: false));
			Assert.Null(ControlChars.FindFirstControlChar("a\tb", includeWhitespaceControls: false));
			var zwsp = ControlChars.FindFirstControlChar("a\u200Bb", includeWhitespaceControls: false);
			Assert.NotNull(zwsp);
			Assert.Equal("ZWSP (U+200B)", zwsp.Value.Name);
		}
	}
}
