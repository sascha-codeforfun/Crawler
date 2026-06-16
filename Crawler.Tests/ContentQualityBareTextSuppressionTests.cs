using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ContentQuality.CheckBareText covering the #47 dedup behaviour:
	/// when a text node inside a container element consists entirely of
	/// architect-class invisible characters (after Trim of normal whitespace),
	/// BARE_TEXT_IN_CONTAINER is suppressed because INVISIBLE_CHAR_IN_BODY
	/// already names the actual problem more usefully.
	/// </summary>
	public class ContentQualityBareTextSuppressionTests
	{
		private static ContentQualityConfig DefaultConfig() => new()
		{
			CheckBareTextInContainers = true,
			ContentQualityContainerElements = ["div", "section", "article"],
			ContentQualityBlockElements = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "td", "th"],
			ContentQualityExcerptRadius = 80,
		};

		private static HtmlDocument LoadDoc(string fragment)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(fragment);
			return doc;
		}

		// ── Baseline: real bare text still fires ─────────────────────────

		[Fact]
		public void CheckBareText_NormalVisibleText_Fires()
		{
			var doc = LoadDoc("<div>Just some bare text here</div>");
			var issues = ContentQuality.CheckBareText("test.html", doc, DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("BARE_TEXT_IN_CONTAINER", issues[0].IssueType);
		}

		[Fact]
		public void CheckBareText_VisibleTextWithSurroundingWhitespace_Fires()
		{
			// Trim handles leading/trailing whitespace; the visible content
			// itself drives the finding.
			var doc = LoadDoc("<div>   bare text   </div>");
			var issues = ContentQuality.CheckBareText("test.html", doc, DefaultConfig()).ToList();
			Assert.Single(issues);
		}

		[Fact]
		public void CheckBareText_TextInsideBlockWrapper_DoesNotFire()
		{
			// Block elements like <p> are the correct wrapper for prose —
			// BARE_TEXT_IN_CONTAINER does not flag this case.
			var doc = LoadDoc("<div><p>well-wrapped text</p></div>");
			var issues = ContentQuality.CheckBareText("test.html", doc, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		// ── #47 suppression: all-invisible text ──────────────────────────

		[Fact]
		public void CheckBareText_TextAllZwsp_Suppressed()
		{
			// Trim doesn't strip ZWSP (U+200B), so without #47 this would
			// fire BARE_TEXT alongside INVISIBLE_CHAR_IN_BODY.
			var doc = LoadDoc("<div>\u200B\u200B\u200B</div>");
			var issues = ContentQuality.CheckBareText("test.html", doc, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckBareText_TextAllZwnj_Suppressed()
		{
			var doc = LoadDoc("<div>\u200C\u200C</div>");
			var issues = ContentQuality.CheckBareText("test.html", doc, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckBareText_TextAllZwnbspBom_Suppressed()
		{
			var doc = LoadDoc("<div>\uFEFF\uFEFF</div>");
			var issues = ContentQuality.CheckBareText("test.html", doc, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckBareText_TextAllC1Controls_Suppressed()
		{
			var doc = LoadDoc("<div>\u0080\u0090</div>");
			var issues = ContentQuality.CheckBareText("test.html", doc, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckBareText_TextMixedInvisibleAndWhitespace_Suppressed()
		{
			// Whitespace trims away; remaining content is only invisibles.
			var doc = LoadDoc("<div>   \u200B  \u200C   </div>");
			var issues = ContentQuality.CheckBareText("test.html", doc, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		// ── Mixed visible + invisible: still fires (NOT suppressed) ─────

		[Fact]
		public void CheckBareText_VisibleWithInvisibleAdjacent_StillFires()
		{
			// At least one visible character — BARE_TEXT is the correct
			// finding regardless of accompanying invisibles. INVISIBLE_CHAR
			// will also fire (different finding); that's fine.
			var doc = LoadDoc("<div>hello\u200Bworld</div>");
			var issues = ContentQuality.CheckBareText("test.html", doc, DefaultConfig()).ToList();
			Assert.Single(issues);
		}

		[Fact]
		public void CheckBareText_VisibleSurroundedByInvisibles_StillFires()
		{
			var doc = LoadDoc("<div>\u200B\u200Bhello\u200B\u200B</div>");
			var issues = ContentQuality.CheckBareText("test.html", doc, DefaultConfig()).ToList();
			Assert.Single(issues);
		}

		[Fact]
		public void CheckBareText_OneVisibleCharOneInvisible_StillFires()
		{
			// Edge of suppression — one visible char keeps the finding alive.
			var doc = LoadDoc("<div>a\u200B</div>");
			var issues = ContentQuality.CheckBareText("test.html", doc, DefaultConfig()).ToList();
			Assert.Single(issues);
		}
	}
}
