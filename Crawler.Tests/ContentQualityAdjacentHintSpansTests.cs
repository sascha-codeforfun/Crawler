using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ContentQualityTriage.ComputeAdjacentHintSpans — the pure helper
	/// that computes coloured hint spans for an ADJACENT_ANCHOR excerpt: every
	/// literal "&lt;/a&gt;&lt;a" collision plus the structural-fact hints
	/// (matching hrefs, matching titles, placeholder hrefs, anchor-text scope when
	/// same-href). The renderer
	/// (ConsoleUi.WriteWithAdjacentAnchorHintHighlight) only paints these spans
	/// so the structural logic is unit-tested here without Console.
	///
	/// Span-kind contract:
	///   Collision       — literal "&lt;/a&gt;&lt;a" (red, the alarm)
	///   HrefMatch       — both anchors' real hrefs are identical strings
	///   HrefPlaceholder — href="#" / "" / "javascript:..."
	///   HrefBaseline    — present href that isn't a match/placeholder
	///   TitleMatch      — both anchors' titles present and identical
	///   AnchorTextHint  — anchor inner text when paired same-real-href
	/// </summary>
	public class ContentQualityAdjacentHintSpansTests
	{
		private static string SpanText(string excerpt, ConsoleUi.AdjacentHintSpanKind kind, int index = 0)
		{
			var spans = ContentQualityTriage.ComputeAdjacentHintSpans(excerpt);
			var s = spans.Where(x => x.Kind == kind).ElementAt(index);
			return excerpt.Substring(s.Start, s.Length);
		}

		private static int CountSpans(string excerpt, ConsoleUi.AdjacentHintSpanKind kind)
		{
			var spans = ContentQualityTriage.ComputeAdjacentHintSpans(excerpt);
			return spans.Count(x => x.Kind == kind);
		}

		private static int TotalSpans(string excerpt) =>
			ContentQualityTriage.ComputeAdjacentHintSpans(excerpt).Count;

		// ── Collision: the basic </a><a alarm ─────────────────────────────────

		[Fact]
		public void Collision_BasicPair()
		{
			Assert.Equal("</a><a", SpanText(
				"<a href=\"/x\">A</a><a href=\"/y\">B</a>",
				ConsoleUi.AdjacentHintSpanKind.Collision));
		}

		[Fact]
		public void Collision_TwoCollisionsInCluster()
		{
			// <a>A</a><a></a><a>B</a> — Android-cluster shape, two collisions.
			Assert.Equal(2, CountSpans(
				"<a href=\"/x\">A</a><a href=\"/x\"></a><a href=\"/x\">B</a>",
				ConsoleUi.AdjacentHintSpanKind.Collision));
		}

		[Fact]
		public void Collision_None_ReturnsEmpty()
		{
			Assert.Empty(ContentQualityTriage.ComputeAdjacentHintSpans(
				"<a href=\"/x\">A</a>  <a href=\"/y\">B</a>"));
		}

		// ── HrefMatch: same real hrefs across pair ────────────────────────────

		[Fact]
		public void HrefMatch_BothSidesPainted()
		{
			Assert.Equal(2, CountSpans(
				"<a href=\"/docs\">Click </a><a href=\"/docs\">here</a>",
				ConsoleUi.AdjacentHintSpanKind.HrefMatch));
		}

		[Fact]
		public void HrefMatch_ValueIsTheHrefContents()
		{
			Assert.Equal("/docs", SpanText(
				"<a href=\"/docs\">Click </a><a href=\"/docs\">here</a>",
				ConsoleUi.AdjacentHintSpanKind.HrefMatch));
		}

		[Fact]
		public void HrefMatch_AlsoPaintsAnchorTextHint()
		{
			Assert.Equal(2, CountSpans(
				"<a href=\"/docs\">Click </a><a href=\"/docs\">here</a>",
				ConsoleUi.AdjacentHintSpanKind.AnchorTextHint));
		}

		[Fact]
		public void HrefMatch_AnchorTextHintContent()
		{
			var first = SpanText(
				"<a href=\"/docs\">Click </a><a href=\"/docs\">here</a>",
				ConsoleUi.AdjacentHintSpanKind.AnchorTextHint, index: 0);
			var second = SpanText(
				"<a href=\"/docs\">Click </a><a href=\"/docs\">here</a>",
				ConsoleUi.AdjacentHintSpanKind.AnchorTextHint, index: 1);
			Assert.Equal("Click ", first);
			Assert.Equal("here", second);
		}

		// ── HrefBaseline: distinct hrefs ──────────────────────────────────────

		[Fact]
		public void HrefBaseline_DistinctHrefs()
		{
			Assert.Equal(2, CountSpans(
				"<a href=\"/std\">A</a><a href=\"/gold\">B</a>",
				ConsoleUi.AdjacentHintSpanKind.HrefBaseline));
		}

		[Fact]
		public void HrefBaseline_NoMatchEmitted()
		{
			Assert.Equal(0, CountSpans(
				"<a href=\"/std\">A</a><a href=\"/gold\">B</a>",
				ConsoleUi.AdjacentHintSpanKind.HrefMatch));
		}

		[Fact]
		public void HrefBaseline_NoAnchorTextHintEmitted()
		{
			// AnchorTextHint is reserved for same-real-href pairs only.
			Assert.Equal(0, CountSpans(
				"<a href=\"/std\">A</a><a href=\"/gold\">B</a>",
				ConsoleUi.AdjacentHintSpanKind.AnchorTextHint));
		}

		// ── HrefPlaceholder: # and javascript: ────────────────────────────────

		[Fact]
		public void HrefPlaceholder_HashOnBoth()
		{
			Assert.Equal(2, CountSpans(
				"<a href=\"#\">A</a><a href=\"#\">B</a>",
				ConsoleUi.AdjacentHintSpanKind.HrefPlaceholder));
		}

		[Fact]
		public void HrefPlaceholder_NoMatchWhenBothPlaceholders()
		{
			// Even if both placeholders are byte-identical "#", they don't get the
			// HrefMatch (real-destination) colour — placeholders are placeholders.
			Assert.Equal(0, CountSpans(
				"<a href=\"#\">A</a><a href=\"#\">B</a>",
				ConsoleUi.AdjacentHintSpanKind.HrefMatch));
		}

		[Fact]
		public void HrefPlaceholder_EmptyHref_NoVisibleSpan()
		{
			// href="" is structurally a placeholder, but the value between the
			// quotes is zero characters — there is nothing to paint. The visual
			// layer surfaces facts via coloured spans; where there is no surface
			// (zero-length value), the fact remains in the markup but the eye gets
			// no colour claim. This is honest: visualization is a hint mechanism,
			// not a substitute for reading the markup. The red </a><a collision
			// still fires; the operator sees the empty href context by reading
			// the excerpt directly.
			Assert.Equal(0, CountSpans(
				"<a href=\"\">A</a><a href=\"\">B</a>",
				ConsoleUi.AdjacentHintSpanKind.HrefPlaceholder));
		}

		[Fact]
		public void HrefPlaceholder_JavascriptScheme()
		{
			Assert.Equal(2, CountSpans(
				"<a href=\"javascript:doX()\">A</a><a href=\"javascript:doY()\">B</a>",
				ConsoleUi.AdjacentHintSpanKind.HrefPlaceholder));
		}

		// ── TitleMatch: matching title across pair ────────────────────────────

		[Fact]
		public void TitleMatch_BothPresentAndEqual()
		{
			Assert.Equal(2, CountSpans(
				"<a href=\"/x\" title=\"Docs\">A</a><a href=\"/y\" title=\"Docs\">B</a>",
				ConsoleUi.AdjacentHintSpanKind.TitleMatch));
		}

		[Fact]
		public void TitleMatch_NoSpanWhenTitlesDiffer()
		{
			Assert.Equal(0, CountSpans(
				"<a href=\"/x\" title=\"Docs\">A</a><a href=\"/y\" title=\"Other\">B</a>",
				ConsoleUi.AdjacentHintSpanKind.TitleMatch));
		}

		[Fact]
		public void TitleMatch_NoSpanWhenOneTitleAbsent()
		{
			Assert.Equal(0, CountSpans(
				"<a href=\"/x\" title=\"Docs\">A</a><a href=\"/y\">B</a>",
				ConsoleUi.AdjacentHintSpanKind.TitleMatch));
		}

		[Fact]
		public void TitleMatch_AttributeOrder_TitleBeforeHref()
		{
			Assert.Equal(2, CountSpans(
				"<a title=\"Docs\" href=\"/x\">A</a><a title=\"Docs\" href=\"/y\">B</a>",
				ConsoleUi.AdjacentHintSpanKind.TitleMatch));
		}

		// ── Mixed: real href on one side, placeholder on the other ────────────

		[Fact]
		public void Mixed_RealAndPlaceholder_ClassifiedIndependently()
		{
			var spans = ContentQualityTriage.ComputeAdjacentHintSpans(
				"<a href=\"/real\">A</a><a href=\"#\">B</a>");
			Assert.Contains(spans, s => s.Kind == ConsoleUi.AdjacentHintSpanKind.HrefBaseline);
			Assert.Contains(spans, s => s.Kind == ConsoleUi.AdjacentHintSpanKind.HrefPlaceholder);
			// And NO match (one side is placeholder).
			Assert.DoesNotContain(spans, s => s.Kind == ConsoleUi.AdjacentHintSpanKind.HrefMatch);
		}

		// ── Quote-style variations ────────────────────────────────────────────

		[Fact]
		public void Quote_SingleQuotedHref()
		{
			Assert.Equal(2, CountSpans(
				"<a href='/docs'>Click </a><a href='/docs'>here</a>",
				ConsoleUi.AdjacentHintSpanKind.HrefMatch));
		}

		// ── Kontaktbereich icon-widget shape — real-world case ────────────────

		[Fact]
		public void Kontaktbereich_ThreeAdjacentPlaceholderAnchors()
		{
			// Real-world shape from an icon widget.
			const string excerpt =
				"<div class=\"contact\"><a href=\"#\" title=\"Contactarea\">Contactarea</a>"
				+ "<a href=\"#\" title=\"Contactarea\">Contactarea</a>"
				+ "<a href=\"#\" title=\"Contactarea\">Contactarea</a>";
			// Two collisions (3 anchors, 2 boundaries between them).
			Assert.Equal(2, CountSpans(excerpt, ConsoleUi.AdjacentHintSpanKind.Collision));
			// Each side of each collision contributes a HrefPlaceholder span; the
			// middle anchor participates in BOTH pairs and would normally emit
			// twice — DedupeOrdered suppresses the duplicate so we have 3 unique
			// placeholder spans (one per anchor).
			Assert.Equal(3, CountSpans(excerpt, ConsoleUi.AdjacentHintSpanKind.HrefPlaceholder));
			// Title matches across both pairs → 3 unique title spans (same dedupe).
			Assert.Equal(3, CountSpans(excerpt, ConsoleUi.AdjacentHintSpanKind.TitleMatch));
			// Placeholders never produce AnchorTextHint (no real-destination match).
			Assert.Equal(0, CountSpans(excerpt, ConsoleUi.AdjacentHintSpanKind.AnchorTextHint));
		}

		// ── Android-cluster shape — three anchors, all same real href ─────────

		[Fact]
		public void AndroidCluster_SameRealHrefAcrossThreeAnchors()
		{
			const string excerpt =
				"<a href=\"https://play.google.com/store/apps/details?id=de.x\" target=\"_blank\">And</a>"
				+ "<a href=\"https://play.google.com/store/apps/details?id=de.x\" target=\"_blank\"></a>"
				+ "<a href=\"https://play.google.com/store/apps/details?id=de.x\" target=\"_blank\">roid</a>";
			Assert.Equal(2, CountSpans(excerpt, ConsoleUi.AdjacentHintSpanKind.Collision));
			// Three anchors, all same real href → 3 unique HrefMatch spans.
			Assert.Equal(3, CountSpans(excerpt, ConsoleUi.AdjacentHintSpanKind.HrefMatch));
			// AnchorTextHint on each anchor's inner text. Middle anchor is empty
			// (no text content), so only outer two contribute; dedupe leaves 2.
			var textHints = CountSpans(excerpt, ConsoleUi.AdjacentHintSpanKind.AnchorTextHint);
			Assert.True(textHints >= 2, $"expected ≥2 AnchorTextHint, got {textHints}");
		}

		// ── Empty / null / no-collision corner cases ─────────────────────────

		[Fact]
		public void Empty_ReturnsEmpty()
			=> Assert.Empty(ContentQualityTriage.ComputeAdjacentHintSpans(""));

		[Fact]
		public void Null_ReturnsEmpty()
			=> Assert.Empty(ContentQualityTriage.ComputeAdjacentHintSpans(null!));

		[Fact]
		public void NoAdjacency_NoSpans()
			=> Assert.Empty(ContentQualityTriage.ComputeAdjacentHintSpans(
				"<a href=\"/x\">Plain link</a> some text then <a href=\"/y\">another</a>"));

		// ── Spans-ordered invariant ───────────────────────────────────────────

		[Fact]
		public void Spans_AreOrderedByStart()
		{
			var spans = ContentQualityTriage.ComputeAdjacentHintSpans(
				"<a href=\"/docs\" title=\"D\">Click </a><a href=\"/docs\" title=\"D\">here</a>");
			Assert.True(TotalSpans(
				"<a href=\"/docs\" title=\"D\">Click </a><a href=\"/docs\" title=\"D\">here</a>") > 1);
			for (var i = 1; i < spans.Count; i++)
			{
				Assert.True(spans[i].Start >= spans[i - 1].Start,
					$"span {i} starts at {spans[i].Start}, expected >= {spans[i - 1].Start}");
			}
		}

		[Fact]
		public void Spans_DoNotOverlap()
		{
			var spans = ContentQualityTriage.ComputeAdjacentHintSpans(
				"<a href=\"/docs\" title=\"D\">Click </a><a href=\"/docs\" title=\"D\">here</a>");
			for (var i = 1; i < spans.Count; i++)
			{
				var prevEnd = spans[i - 1].Start + spans[i - 1].Length;
				Assert.True(spans[i].Start >= prevEnd,
					$"span {i} at {spans[i].Start} overlaps previous ending at {prevEnd}");
			}
		}
	}
}
