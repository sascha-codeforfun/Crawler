using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for SelfLinkScanner.ExtractHtmlContextSnippet — the pure helper that
	/// extracts a bounded window of surrounding HTML around a matched anchor node.
	///
	/// FindSelfLinks itself depends on CrawlIndex.LookUpUrlForFile (the crawler URL
	/// index) and the filesystem, so the deterministic, index-arithmetic-heavy
	/// snippet helper is the unit worth covering directly. These tests focus on
	/// the boundary maths: clamping at start/end of content, centering when the
	/// match is larger than the window, and graceful fallback when the needle
	/// is not found verbatim.
	/// </summary>
	public class SelfLinkScannerSnippetTests
	{
		private static HtmlNode AnchorFrom(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc.DocumentNode.SelectSingleNode("//a");
		}

		[Fact]
		public void NullNode_ReturnsEmpty()
		{
			var result = SelfLinkScanner.ExtractHtmlContextSnippet("some content", null!, 10, 40);
			Assert.Equal(string.Empty, result);
		}

		[Fact]
		public void EmptyContent_ReturnsEmpty()
		{
			var anchor = AnchorFrom("<a href='/x'>link</a>");
			var result = SelfLinkScanner.ExtractHtmlContextSnippet(string.Empty, anchor, 10, 40);
			Assert.Equal(string.Empty, result);
		}

		[Fact]
		public void SnippetNeverExceedsMaxLength()
		{
			var content = new string('x', 500) + "<a href='/self'>here</a>" + new string('y', 500);
			var anchor = AnchorFrom("<a href='/self'>here</a>");

			var result = SelfLinkScanner.ExtractHtmlContextSnippet(content, anchor, 120, 240);

			Assert.True(result.Length <= 240, $"snippet length {result.Length} exceeded max 240");
		}

		[Fact]
		public void MatchNearStart_ClampsLeftEdgeWithoutThrowing()
		{
			// Anchor at the very start — start index would go negative without clamping.
			var content = "<a href='/self'>here</a>" + new string('y', 500);
			var anchor = AnchorFrom("<a href='/self'>here</a>");

			var result = SelfLinkScanner.ExtractHtmlContextSnippet(content, anchor, 120, 240);

			Assert.False(string.IsNullOrEmpty(result));
			Assert.True(result.Length <= 240);
			// The anchor sits at offset 0, so the window must include it.
			Assert.Contains("href='/self'", result);
		}

		[Fact]
		public void MatchNearEnd_ClampsRightEdgeWithoutThrowing()
		{
			var content = new string('y', 500) + "<a href='/self'>here</a>";
			var anchor = AnchorFrom("<a href='/self'>here</a>");

			var result = SelfLinkScanner.ExtractHtmlContextSnippet(content, anchor, 120, 240);

			Assert.False(string.IsNullOrEmpty(result));
			Assert.True(result.Length <= 240);
			Assert.Contains("href='/self'", result);
		}

		[Fact]
		public void ContentShorterThanMax_AndNeedleNotFound_ReturnsWholeContent()
		{
			// Anchor's OuterHtml won't appear verbatim in this unrelated content,
			// and content is shorter than maxSnippetLength → whole content returned.
			var content = "no anchor here at all";
			var anchor = AnchorFrom("<a href='/self'>here</a>");

			var result = SelfLinkScanner.ExtractHtmlContextSnippet(content, anchor, 120, 240);

			Assert.Equal(content, result);
		}

		[Fact]
		public void NeedleNotFound_ContentLongerThanMax_TruncatesToMax()
		{
			var content = new string('z', 1000); // anchor markup absent
			var anchor = AnchorFrom("<a href='/self'>here</a>");

			var result = SelfLinkScanner.ExtractHtmlContextSnippet(content, anchor, 120, 240);

			Assert.Equal(240, result.Length);
		}

		[Fact]
		public void MatchExactlyAtMiddle_ReturnsWindowAroundIt()
		{
			var left = new string('a', 300);
			var right = new string('b', 300);
			var content = left + "<a href='/self'>X</a>" + right;
			var anchor = AnchorFrom("<a href='/self'>X</a>");

			var result = SelfLinkScanner.ExtractHtmlContextSnippet(content, anchor, 50, 120);

			Assert.True(result.Length <= 120);
			// Window should straddle the anchor: both some 'a' and some 'b' context.
			Assert.Contains("a", result);
			Assert.Contains("b", result);
		}
	}
}
