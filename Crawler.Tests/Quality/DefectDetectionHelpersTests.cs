using HtmlAgilityPack;
using Xunit;
using Crawler.Quality;

namespace Crawler.Tests.Quality
{
	public class DefectDetectionHelpersTests
	{
		private static HtmlNode Parse(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc.DocumentNode.FirstChild;
		}

		// ── Tag name only ────────────────────────────────────────────────

		[Fact]
		public void RendersTagName_NoAttributes()
		{
			var node = Parse("<div></div>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.Equal("<div>", result);
		}

		[Fact]
		public void RendersTagName_ForSection()
		{
			var node = Parse("<section></section>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.Equal("<section>", result);
		}

		// ── Single attribute of interest ─────────────────────────────────

		[Fact]
		public void RendersClass_WhenPresent()
		{
			var node = Parse("<div class=\"caps-lock-warning\"></div>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.Equal("<div class=\"caps-lock-warning\">", result);
		}

		[Fact]
		public void RendersId_WhenPresent()
		{
			var node = Parse("<div id=\"main-content\"></div>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.Equal("<div id=\"main-content\">", result);
		}

		[Fact]
		public void RendersRole_WhenPresent()
		{
			var node = Parse("<div role=\"alert\"></div>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.Equal("<div role=\"alert\">", result);
		}

		[Fact]
		public void RendersDataComponent_WhenPresent()
		{
			var node = Parse("<div data-component=\"hero\"></div>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.Equal("<div data-component=\"hero\">", result);
		}

		// ── Attribute ordering ──────────────────────────────────────────
		// class > id > role > data-component, regardless of source order.

		[Fact]
		public void RendersAttributes_InFixedOrder_NotSourceOrder()
		{
			var node = Parse(
				"<div role=\"alert\" data-component=\"hero\" id=\"x\" class=\"y\"></div>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.Equal(
				"<div class=\"y\" id=\"x\" role=\"alert\" data-component=\"hero\">",
				result);
		}

		// ── Empty / missing attributes ───────────────────────────────────

		[Fact]
		public void SkipsEmptyClass()
		{
			var node = Parse("<div class=\"\"></div>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.Equal("<div>", result);
		}

		[Fact]
		public void SkipsMissingAttributes_RendersOnlyPresent()
		{
			var node = Parse("<div class=\"alpha\"></div>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.Equal("<div class=\"alpha\">", result);
		}

		// ── Out-of-interest attributes are omitted ───────────────────────
		// Keep log lines readable; we don't render style, onclick, aria-*, etc.

		[Fact]
		public void OmitsNoninterestingAttributes()
		{
			var node = Parse(
				"<div class=\"a\" style=\"color:red\" onclick=\"x()\" aria-hidden=\"true\"></div>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.Equal("<div class=\"a\">", result);
		}

		[Fact]
		public void OmitsOtherDataAttributes_ButKeepsDataComponent()
		{
			// data-component is in the interest list; data-track is not.
			var node = Parse(
				"<div data-component=\"hero\" data-track=\"foo\" data-id=\"1\"></div>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.Equal("<div data-component=\"hero\">", result);
		}

		// ── Truncation ──────────────────────────────────────────────────

		[Fact]
		public void TruncatesLongOutput_WithEllipsisAndClosingBracket()
		{
			// Build a class attribute that pushes total tag length past 200 chars.
			var longClass = new string('x', 250);
			var node = Parse($"<div class=\"{longClass}\"></div>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.True(result.Length <= 200, $"Output length was {result.Length}, expected ≤ 200");
			Assert.EndsWith("…>", result);
			Assert.StartsWith("<div class=\"xxx", result);
		}

		[Fact]
		public void DoesNotTruncate_WhenUnderCap()
		{
			// Short input — should pass through untouched.
			var node = Parse("<div class=\"caps-lock-warning\" id=\"caps\"></div>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.False(result.EndsWith("…>"));
			Assert.True(result.Length < 200);
		}

		// ── Real-world shapes from #290 baseline ─────────────────────────

		[Fact]
		public void RealWorld_CapsLockWarning()
		{
			// The 461-occurrence case from the BARE_TEXT_IN_CONTAINER analysis.
			var node = Parse("<div class=\"caps-lock-warning\">Sie haben die Feststelltaste aktiviert.</div>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.Equal("<div class=\"caps-lock-warning\">", result);
		}

		[Fact]
		public void RealWorld_DivWithHeadingMimicClasses()
		{
			// The heading-mimic case explored during #290 investigation.
			var node = Parse("<div class=\"c_block_heading h3\">Wir sind für Sie da</div>");

			var result = DefectDetectionHelpers.FormatContainerStartTag(node);

			Assert.Equal("<div class=\"c_block_heading h3\">", result);
		}

		// ── IsArchitectClassInvisible ────────────────────────────────────

		[Fact]
		public void IsArchitectClassInvisible_NormalChars_ReturnFalse()
		{
			Assert.False(DefectDetectionHelpers.IsArchitectClassInvisible('a'));
			Assert.False(DefectDetectionHelpers.IsArchitectClassInvisible('ä'));
			Assert.False(DefectDetectionHelpers.IsArchitectClassInvisible('€'));
		}

		[Fact]
		public void IsArchitectClassInvisible_NormalWhitespace_ReturnFalse()
		{
			Assert.False(DefectDetectionHelpers.IsArchitectClassInvisible('\r'));
			Assert.False(DefectDetectionHelpers.IsArchitectClassInvisible('\n'));
			Assert.False(DefectDetectionHelpers.IsArchitectClassInvisible('\t'));
			Assert.False(DefectDetectionHelpers.IsArchitectClassInvisible(' '));
		}

		[Theory]
		[InlineData('\u200B')] // ZWSP
		[InlineData('\u200C')] // ZWNJ
		[InlineData('\u200D')] // ZWJ
		[InlineData('\u2060')] // WJ
		[InlineData('\uFEFF')] // BOM / ZWNBSP
		[InlineData('\u2028')] // LINE SEPARATOR
		[InlineData('\u2029')] // PARAGRAPH SEPARATOR
		[InlineData('\u202A')] // LRE (bidi)
		[InlineData('\u202E')] // RLO (bidi)
		[InlineData('\u2066')] // LRI (bidi isolate)
		[InlineData('\u2069')] // PDI (bidi isolate)
		[InlineData('\u0000')] // NUL (C0)
		[InlineData('\u0085')] // NEL (C1)
		[InlineData('\u007F')] // DEL
		[InlineData('\u00AD')] // SHY
		[InlineData('\u061C')] // ALM
		[InlineData('\u200E')] // LRM
		[InlineData('\u200F')] // RLM
		[InlineData('\u2062')] // INVISIBLE TIMES (math)
		public void IsArchitectClassInvisible_KnownInvisibles_ReturnTrue(char ch)
		{
			Assert.True(DefectDetectionHelpers.IsArchitectClassInvisible(ch));
		}
	}
}
