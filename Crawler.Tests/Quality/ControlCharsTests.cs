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
			var issues = ControlChars.Check("f.html", doc).ToList();
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
			var issues = ControlChars.Check("f.html", doc).ToList();
			Assert.Single(issues);
			Assert.Equal("CONTROL_CHARS_IN_CONTENT", issues[0].IssueType);
			Assert.Contains("LF", issues[0].Detail);
			Assert.Contains("description", issues[0].Detail);
		}

		[Fact]
		public void Check_TabInTitle_Flagged()
		{
			var doc = Doc("<html><head><title>Two\tTabs</title></head></html>");
			var issues = ControlChars.Check("f.html", doc).ToList();
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
			var issues = ControlChars.Check("f.html", doc).ToList();
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
			var issues = ControlChars.Check("f.html", doc).ToList();
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
			var issues = ControlChars.Check("f.html", doc).ToList();
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
			var issues = ControlChars.Check("f.html", doc).ToList();
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
			var issues = ControlChars.Check("f.html", doc).ToList();
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
			var issues = ControlChars.Check("f.html", doc).ToList();
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
			var issues = ControlChars.Check("f.html", doc).ToList();
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
			var issues = ControlChars.Check("f.html", doc).ToList();
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
			var issues = ControlChars.Check("f.html", doc).ToList();
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
	}
}
