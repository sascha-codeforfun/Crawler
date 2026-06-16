using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ContentQuality.FormatContainerStartTag — the helper introduced in
	/// fileset #290 that renders a container element's start tag (tag name plus a
	/// curated subset of attributes useful for writing an XPath strip) into the
	/// BARE_TEXT_IN_CONTAINER excerpt.
	///
	/// The contract under test:
	///   - The tag name is always rendered.
	///   - class / id / role / data-component attributes are rendered if present,
	///     in that fixed order regardless of source order.
	///   - Empty / missing attributes are skipped.
	///   - Other attributes (style, onclick, aria-*, data-* except data-component)
	///     are omitted to keep the line readable.
	///   - Output is capped at 200 chars and truncation is signalled with "…>".
	///
	/// Operator value: with this rendered into the log line, an operator looking at
	/// "Sie haben die Feststelltaste aktiviert." × 461 occurrences immediately sees
	/// the surrounding container class and can write the right XPath strip without
	/// opening source HTML — the painpoint that surfaced during #288/#289 work.
	/// </summary>
	public class FormatContainerStartTagTests
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

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.Equal("<div>", result);
		}

		[Fact]
		public void RendersTagName_ForSection()
		{
			var node = Parse("<section></section>");

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.Equal("<section>", result);
		}

		// ── Single attribute of interest ─────────────────────────────────

		[Fact]
		public void RendersClass_WhenPresent()
		{
			var node = Parse("<div class=\"caps-lock-warning\"></div>");

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.Equal("<div class=\"caps-lock-warning\">", result);
		}

		[Fact]
		public void RendersId_WhenPresent()
		{
			var node = Parse("<div id=\"main-content\"></div>");

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.Equal("<div id=\"main-content\">", result);
		}

		[Fact]
		public void RendersRole_WhenPresent()
		{
			var node = Parse("<div role=\"alert\"></div>");

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.Equal("<div role=\"alert\">", result);
		}

		[Fact]
		public void RendersDataComponent_WhenPresent()
		{
			var node = Parse("<div data-component=\"hero\"></div>");

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.Equal("<div data-component=\"hero\">", result);
		}

		// ── Attribute ordering ──────────────────────────────────────────
		// class > id > role > data-component, regardless of source order.

		[Fact]
		public void RendersAttributes_InFixedOrder_NotSourceOrder()
		{
			var node = Parse(
				"<div role=\"alert\" data-component=\"hero\" id=\"x\" class=\"y\"></div>");

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.Equal(
				"<div class=\"y\" id=\"x\" role=\"alert\" data-component=\"hero\">",
				result);
		}

		// ── Empty / missing attributes ───────────────────────────────────

		[Fact]
		public void SkipsEmptyClass()
		{
			var node = Parse("<div class=\"\"></div>");

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.Equal("<div>", result);
		}

		[Fact]
		public void SkipsMissingAttributes_RendersOnlyPresent()
		{
			var node = Parse("<div class=\"alpha\"></div>");

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.Equal("<div class=\"alpha\">", result);
		}

		// ── Out-of-interest attributes are omitted ───────────────────────
		// Keep log lines readable; we don't render style, onclick, aria-*, etc.

		[Fact]
		public void OmitsNoninterestingAttributes()
		{
			var node = Parse(
				"<div class=\"a\" style=\"color:red\" onclick=\"x()\" aria-hidden=\"true\"></div>");

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.Equal("<div class=\"a\">", result);
		}

		[Fact]
		public void OmitsOtherDataAttributes_ButKeepsDataComponent()
		{
			// data-component is in the interest list; data-track is not.
			var node = Parse(
				"<div data-component=\"hero\" data-track=\"foo\" data-id=\"1\"></div>");

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.Equal("<div data-component=\"hero\">", result);
		}

		// ── Truncation ──────────────────────────────────────────────────

		[Fact]
		public void TruncatesLongOutput_WithEllipsisAndClosingBracket()
		{
			// Build a class attribute that pushes total tag length past 200 chars.
			var longClass = new string('x', 250);
			var node = Parse($"<div class=\"{longClass}\"></div>");

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.True(result.Length <= 200, $"Output length was {result.Length}, expected ≤ 200");
			Assert.EndsWith("…>", result);
			Assert.StartsWith("<div class=\"xxx", result);
		}

		[Fact]
		public void DoesNotTruncate_WhenUnderCap()
		{
			// Short input — should pass through untouched.
			var node = Parse("<div class=\"caps-lock-warning\" id=\"caps\"></div>");

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.False(result.EndsWith("…>"));
			Assert.True(result.Length < 200);
		}

		// ── Real-world shapes from #290 baseline ─────────────────────────

		[Fact]
		public void RealWorld_CapsLockWarning()
		{
			// The 461-occurrence case from the BARE_TEXT_IN_CONTAINER analysis.
			var node = Parse("<div class=\"caps-lock-warning\">Sie haben die Feststelltaste aktiviert.</div>");

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.Equal("<div class=\"caps-lock-warning\">", result);
		}

		[Fact]
		public void RealWorld_DivWithHeadingMimicClasses()
		{
			// The heading-mimic case explored during #290 investigation.
			var node = Parse("<div class=\"c_block_heading h3\">Wir sind für Sie da</div>");

			var result = ContentQuality.FormatContainerStartTag(node);

			Assert.Equal("<div class=\"c_block_heading h3\">", result);
		}
	}
}
