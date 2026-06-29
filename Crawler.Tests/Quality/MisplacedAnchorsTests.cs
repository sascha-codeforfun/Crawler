using Crawler.Quality;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.Quality
{
	public class MisplacedAnchorsTests
	{
		private static ContentQualityConfig DefaultConfig() => new()
		{
			ContentQualityExcerptRadius    = 120,
			ContentQualityQuoteFullSentence = false,  // keep tests deterministic
			ContentQualityMaxExcerpt  = 400,
		};

		private static HtmlDocument ParseHtml(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		// ── Check — MISPLACED_ANCHOR_EMPTY ─────────────────────

		[Fact]
		public void Check_EmptyAnchor_ReturnsEmptyIssue()
		{
			var html   = "<p><a href=\"/x\"></a>text</p>";
			var issues = MisplacedAnchors.Check("f.html", ParseHtml(html), DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "MISPLACED_ANCHOR_EMPTY");
		}

		[Fact]
		public void Check_WhitespaceOnlyAnchor_ReturnsEmptyIssue()
		{
			var html   = "<p><a href=\"/x\">   </a>text</p>";
			var issues = MisplacedAnchors.Check("f.html", ParseHtml(html), DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "MISPLACED_ANCHOR_EMPTY");
		}

		[Fact]
		public void Check_AnchorWithEmptyChildElements_ReturnsEmptyIssue()
		{
			// Anchor contains only empty inline elements — no visible text anywhere
			var html   = "<p><a href=\"/x\"><span><b></b></span></a></p>";
			var issues = MisplacedAnchors.Check("f.html", ParseHtml(html), DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "MISPLACED_ANCHOR_EMPTY");
		}

		[Fact]
		public void Check_AnchorWithText_NoEmptyIssue()
		{
			var html   = "<p><a href=\"/x\">Click here</a></p>";
			var issues = MisplacedAnchors.Check("f.html", ParseHtml(html), DefaultConfig()).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "MISPLACED_ANCHOR_EMPTY");
		}

		[Fact]
		public void Check_EmptyAnchor_DetailContainsHref()
		{
			var html   = "<p><a href=\"/target\"></a></p>";
			var issues = MisplacedAnchors.Check("f.html", ParseHtml(html), DefaultConfig()).ToList();
			var issue  = issues.FirstOrDefault(i => i.IssueType == "MISPLACED_ANCHOR_EMPTY");
			Assert.NotNull(issue);
			Assert.Contains("/target", issue!.Detail);
		}

		// ── Check — ADJACENT_ANCHOR (was MISPLACED_ANCHOR_SPLIT, #452) ──
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
		public void Check_AdjacentAnchorsNoSeparator_ReturnsSplitIssue()
		{
			// Two anchors directly adjacent — no whitespace between closing and opening tag
			var html   = "<h3><a href=\"/x\">And</a><a href=\"/x\">roid</a></h3>";
			var issues = MisplacedAnchors.Check("f.html", ParseHtml(html), ConfigWithAdjacentOn()).ToList();
			Assert.Contains(issues, i => i.IssueType == "ADJACENT_ANCHOR");
		}

		[Fact]
		public void Check_AdjacentAnchorsDifferentHref_ReturnsSplitIssue()
		{
			// Different hrefs — structural defect regardless of href
			var html   = "<p><a href=\"/a\">Foo</a><a href=\"/b\">Bar</a></p>";
			var issues = MisplacedAnchors.Check("f.html", ParseHtml(html), ConfigWithAdjacentOn()).ToList();
			Assert.Contains(issues, i => i.IssueType == "ADJACENT_ANCHOR");
		}

		[Fact]
		public void Check_AnchorsWithSpaceBetween_NoSplitIssue()
		{
			var html   = "<p><a href=\"/a\">Foo</a> <a href=\"/b\">Bar</a></p>";
			var issues = MisplacedAnchors.Check("f.html", ParseHtml(html), ConfigWithAdjacentOn()).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "ADJACENT_ANCHOR");
		}

		[Fact]
		public void Check_ThreeAdjacentAnchors_ReportsTwoPairs()
		{
			// A+B adjacent and B+C adjacent — two split issues
			var html   = "<p><a href=\"/x\">A</a><a href=\"/x\">B</a><a href=\"/x\">C</a></p>";
			var issues = MisplacedAnchors.Check("f.html", ParseHtml(html), ConfigWithAdjacentOn())
				.Where(i => i.IssueType == "ADJACENT_ANCHOR").ToList();
			Assert.Equal(2, issues.Count);
		}

		[Fact]
		public void Check_EmptyMiddleAnchorWithSplits_ReportsBothTypes()
		{
			// Mirrors the real-world pattern: text anchor + empty anchor + text anchor,
			// all adjacent, same href.
			var html   = "<h3>" +
			             "<a href=\"/x\">And</a>" +
			             "<a href=\"/x\"></a>" +
			             "<a href=\"/x\">roid</a>" +
			             "</h3>";
			var issues = MisplacedAnchors.Check("f.html", ParseHtml(html), ConfigWithAdjacentOn()).ToList();
			Assert.Contains(issues, i => i.IssueType == "MISPLACED_ANCHOR_EMPTY");
			Assert.Contains(issues, i => i.IssueType == "ADJACENT_ANCHOR");
		}

		[Fact]
		public void Check_FilenamePassedThrough()
		{
			var html   = "<p><a href=\"/x\"></a></p>";
			var issues = MisplacedAnchors.Check("page-042.html", ParseHtml(html), ConfigWithAdjacentOn()).ToList();
			Assert.All(issues, i => Assert.Equal("page-042.html", i.Filename));
		}

		// ── #452 gate + post-filter behavior ─────────────────────────────────

		[Fact]
		public void Check_AdjacentGateOff_NoFindings()
		{
			// Default DefaultConfig() leaves AnchorDetection.DetectAdjacent = false.
			// The same input that fires under ConfigWithAdjacentOn() must produce
			// zero ADJACENT_ANCHOR findings here. Guards the default-off contract.
			var html   = "<h3><a href=\"/x\">And</a><a href=\"/x\">roid</a></h3>";
			var issues = MisplacedAnchors.Check("f.html", ParseHtml(html), DefaultConfig()).ToList();
			Assert.DoesNotContain(issues, i => i.IssueType == "ADJACENT_ANCHOR");
		}

		[Fact]
		public void Check_AdjacentGateOff_EmptyStillFires()
		{
			// The gate scopes ADJACENT only — MISPLACED_ANCHOR_EMPTY is not affected
			// and must still fire under the default config.
			var html   = "<p><a href=\"/x\"></a></p>";
			var issues = MisplacedAnchors.Check("f.html", ParseHtml(html), DefaultConfig()).ToList();
			Assert.Contains(issues, i => i.IssueType == "MISPLACED_ANCHOR_EMPTY");
		}

		// ── #431: ADJACENT_ANCHOR excerpt centres on the </a><a boundary, not the
		//          first anchor's OuterHtml start. Regression guard for the case
		//          where a long body (e.g. an inline SVG) inside the first anchor
		//          pushed the split out of the windowed excerpt, leaving the
		//          operator a wall of markup with no visible split. Synthetic
		//          fixtures only.

		[Fact]
		public void Check_SplitBehindLongBody_ExcerptStillShowsSplit()
		{
			// First anchor carries a long inner body (stand-in for a big inline SVG
			// path) so its OuterHtml start sits far from the split. Pre-#431 the
			// excerpt centred on that start and the </a><a boundary fell outside the
			// window; post-#431 it centres on the boundary so the split is in frame.
			var longBody = new string('x', 600);
			var html = $"<div><a href=\"/a\">{longBody}First</a><a href=\"/b\">Second</a></div>";
			var issue = MisplacedAnchors.Check("f.html", ParseHtml(html), ConfigWithAdjacentOn())
				.First(i => i.IssueType == "ADJACENT_ANCHOR");

			Assert.Contains("</a><a", issue.Context, StringComparison.Ordinal);
		}

		[Fact]
		public void Check_SplitBehindLongBody_ExcerptShowsBothAnchorTexts()
		{
			var longBody = new string('x', 600);
			var html = $"<div><a href=\"/a\">{longBody}First</a><a href=\"/b\">Second</a></div>";
			var issue = MisplacedAnchors.Check("f.html", ParseHtml(html), ConfigWithAdjacentOn())
				.First(i => i.IssueType == "ADJACENT_ANCHOR");

			// Both anchors' text sit adjacent to the boundary, so a boundary-centred
			// window shows both — the operator can read what was split.
			Assert.Contains("First", issue.Context, StringComparison.Ordinal);
			Assert.Contains("Second", issue.Context, StringComparison.Ordinal);
		}

		[Fact]
		public void Check_SplitBehindLongBody_LeadingEllipsisWhenClippedLeft()
		{
			// Long left body guarantees the window is clipped on the left → leading …
			var longBody = new string('x', 600);
			var html = $"<div><a href=\"/a\">{longBody}First</a><a href=\"/b\">Second</a></div>";
			var issue = MisplacedAnchors.Check("f.html", ParseHtml(html), ConfigWithAdjacentOn())
				.First(i => i.IssueType == "ADJACENT_ANCHOR");

			Assert.StartsWith("\u2026", issue.Context);
		}

		[Fact]
		public void Check_ShortSplit_NoEllipsisWhenNotClipped()
		{
			// Whole construct fits within the cap → no clipping → no … on either end.
			var html = "<div><a href=\"/a\">Foo</a><a href=\"/b\">Bar</a></div>";
			var issue = MisplacedAnchors.Check("f.html", ParseHtml(html), ConfigWithAdjacentOn())
				.First(i => i.IssueType == "ADJACENT_ANCHOR");

			Assert.DoesNotContain("\u2026", issue.Context);
			Assert.Contains("</a><a", issue.Context, StringComparison.Ordinal);
		}
	}
}
