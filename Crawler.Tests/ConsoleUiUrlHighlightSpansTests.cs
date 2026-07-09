using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ConsoleUi.ComputeUrlHighlightSpans — the pure helper that turns
	/// Config.TriageUrlHighlight rules into coloured spans over a URL. The renderer
	/// (ConsoleUi.WriteWithUrlHighlight) only paints these spans, so all matching
	/// logic is unit-tested here without Console.
	///
	/// Rules under test:
	///   - Value is slash-bounded ("/x/"); only the segment "x" between the slashes
	///     is coloured, never the slashes themselves.
	///   - A fragment must match a whole slash-bounded segment, so "/en/" does NOT
	///     light the "en" inside "enterprise".
	///   - Matching is confined to the URL path (scheme/host/query excluded).
	///   - Every occurrence is highlighted.
	///   - Consecutive segments ("/en/global/") share the middle slash, which stays
	///     uncoloured and separates the two colours.
	///   - Output is sorted ascending by Start and non-overlapping.
	/// All URLs are generic.
	/// </summary>
	public class ConsoleUiUrlHighlightSpansTests
	{
		private static UrlHighlightRule Rule(string value, int slot)
			=> new() { Values = [value], Highlight = slot };

		private static List<ConsoleUi.UrlHighlightSpan> Compute(string url, params UrlHighlightRule[] rules)
			=> ConsoleUi.ComputeUrlHighlightSpans(url, rules);

		// Returns the substring each span covers, for readable assertions.
		private static string Text(string url, ConsoleUi.UrlHighlightSpan s) => url.Substring(s.Start, s.Length);

		[Fact]
		public void SingleFragment_ColoursSegmentOnly_NotSlashes()
		{
			var url = "https://www.example.com/en/brands/item/page.html";
			var spans = Compute(url, Rule("/en/", 1));
			var span = Assert.Single(spans);
			Assert.Equal("en", Text(url, span));
			Assert.Equal(1, span.Slot);
			// The chars just outside the span are the bounding slashes.
			Assert.Equal('/', url[span.Start - 1]);
			Assert.Equal('/', url[span.Start + span.Length]);
		}

		[Fact]
		public void FragmentDoesNotMatchInsideWord()
		{
			// "/en/" must not light the "en" inside "enterprise" — it is not a
			// whole slash-bounded segment. This is the core anti-false-positive case.
			var url = "https://www.example.com/enterprise/page/";
			Assert.Empty(Compute(url, Rule("/en/", 1)));
		}

		[Fact]
		public void TwoRules_TwoColours_SameUrl()
		{
			var url = "https://www.example.com/en/brands/foo/page.html";
			var spans = Compute(url, Rule("/en/", 1), Rule("/foo/", 4));
			Assert.Equal(2, spans.Count);
			Assert.Equal("en", Text(url, spans[0]));
			Assert.Equal(1, spans[0].Slot);
			Assert.Equal("foo", Text(url, spans[1]));
			Assert.Equal(4, spans[1].Slot);
		}

		[Fact]
		public void ConsecutiveSegments_ShareSlash_BothColoured_SlashUncoloured()
		{
			var url = "https://www.example.com/en/global/page/";
			var spans = Compute(url, Rule("/en/", 1), Rule("/global/", 2));
			Assert.Equal(2, spans.Count);
			Assert.Equal("en", Text(url, spans[0]));
			Assert.Equal("global", Text(url, spans[1]));
			// The single shared slash sits between the two spans and is not covered.
			int gapStart = spans[0].Start + spans[0].Length;
			Assert.Equal('/', url[gapStart]);
			Assert.Equal(gapStart + 1, spans[1].Start); // exactly one uncoloured slash between
		}

		[Fact]
		public void EveryOccurrenceHighlighted()
		{
			var url = "https://www.example.com/en/docs/en/page/";
			var spans = Compute(url, Rule("/en/", 3));
			Assert.Equal(2, spans.Count);
			Assert.All(spans, s => Assert.Equal("en", Text(url, s)));
			Assert.All(spans, s => Assert.Equal(3, s.Slot));
			Assert.True(spans[0].Start < spans[1].Start);
		}

		[Fact]
		public void QueryStringIgnored()
		{
			// "/en/" appears only in the query — must not match (path-only).
			var url = "https://www.example.com/de/page/?lang=/en/";
			var spans = Compute(url, Rule("/en/", 1));
			Assert.Empty(spans);
		}

		[Fact]
		public void HostIgnored()
		{
			// A host that happens to contain the fragment shape must not match.
			var url = "https://en.example.com/de/page/";
			var spans = Compute(url, Rule("/en/", 1));
			Assert.Empty(spans);
		}

		[Fact]
		public void PathOnlyHit_StopsAtQuery()
		{
			var url = "https://www.example.com/en/page/?x=/en/";
			var spans = Compute(url, Rule("/en/", 1));
			// Exactly the one in the path, not the one in the query.
			var span = Assert.Single(spans);
			Assert.True(span.Start < url.IndexOf('?'));
		}

		[Fact]
		public void TrailingSegmentWithSlash_Matches()
		{
			var url = "https://www.example.com/section/page/";
			var spans = Compute(url, Rule("/page/", 5));
			var span = Assert.Single(spans);
			Assert.Equal("page", Text(url, span));
		}

		[Fact]
		public void TrailingSegmentWithoutSlash_DoesNotMatch()
		{
			// "/page.htm" has no trailing slash, so "/page/" cannot match it.
			var url = "https://www.example.com/section/page.htm";
			Assert.Empty(Compute(url, Rule("/page/", 5)));
		}

		[Fact]
		public void NoRules_Empty() => Assert.Empty(Compute("https://www.example.com/en/page/"));

		[Fact]
		public void EmptyUrl_Empty() => Assert.Empty(Compute("", Rule("/en/", 1)));

		[Fact]
		public void MalformedRule_NoSlashes_Ignored()
		{
			// A rule that is not slash-bounded is ignored by the computer
			// (ValidateConfig rejects it at load; the computer is defensive).
			var url = "https://www.example.com/en/page/";
			Assert.Empty(Compute(url, Rule("en", 1)));
		}

		[Fact]
		public void OverlapResolved_NonOverlappingAscending()
		{
			// Two rules whose matched segments would overlap: keep the first by
			// position, skip the overlapper. Result stays sorted & non-overlapping.
			var url = "https://www.example.com/en/page/";
			var spans = Compute(url, Rule("/en/", 1), Rule("/en/", 2));
			// Same segment matched by both rules at the same place → one span kept.
			var span = Assert.Single(spans);
			Assert.Equal("en", Text(url, span));
		}

		[Fact]
		public void Spans_AreSortedAndNonOverlapping()
		{
			var url = "https://www.example.com/a/mid/b/end/c/";
			var spans = Compute(url, Rule("/a/", 1), Rule("/b/", 2), Rule("/c/", 3));
			Assert.Equal(3, spans.Count);
			// Ascending, non-overlapping.
			for (int i = 1; i < spans.Count; i++)
			{
				Assert.True(spans[i - 1].Start + spans[i - 1].Length <= spans[i].Start);
			}
		}

		[Fact]
		public void RelativePathNoScheme_StillMatches()
		{
			// No scheme/host — whole string is treated as path.
			var url = "/en/page/";
			var span = Assert.Single(Compute(url, Rule("/en/", 1)));
			Assert.Equal("en", Text(url, span));
		}

		// ── Values grouping: one rule, several fragments, one colour ──────────

		private static UrlHighlightRule Group(int slot, params string[] values)
			=> new() { Values = [.. values], Highlight = slot };

		[Fact]
		public void GroupedValues_EachFragmentColoured_SameSlot()
		{
			// One rule listing several fragments colours every matching segment
			// with that rule's slot — the ergonomic case (e.g. "all my languages
			// grey") without one rule per fragment.
			var url = "https://www.example.com/de/home/produkte/page/";
			var spans = Compute(url, Group(5, "/de/", "/fr/", "/cs/", "/pl/"));
			var span = Assert.Single(spans);
			Assert.Equal("de", Text(url, span));
			Assert.Equal(5, span.Slot);
		}

		[Fact]
		public void GroupedValues_MultipleHitsAcrossPath()
		{
			var url = "https://www.example.com/de/x/cs/page/";
			var spans = Compute(url, Group(5, "/de/", "/cs/"));
			Assert.Equal(2, spans.Count);
			Assert.All(spans, s => Assert.Equal(5, s.Slot));
			Assert.Equal("de", Text(url, spans[0]));
			Assert.Equal("cs", Text(url, spans[1]));
		}

		[Fact]
		public void GroupedValues_NonMatchingFragmentsAreNoOps()
		{
			// A group can list fragments not present on a given page (e.g. a global
			// language list applied to a site that only uses some) — absent ones
			// simply contribute nothing.
			var url = "https://www.example.com/de/page/";
			var span = Assert.Single(Compute(url, Group(5, "/de/", "/fr/", "/cs/", "/tr/")));
			Assert.Equal("de", Text(url, span));
		}

		[Fact]
		public void TwoGroups_DifferentColours()
		{
			var url = "https://www.example.com/en/home/firmenkunden/page/";
			var spans = Compute(url,
				Group(5, "/de/", "/fr/"),       // none present
				Group(2, "/en/", "/firmenkunden/"));
			Assert.Equal(2, spans.Count);
			Assert.All(spans, s => Assert.Equal(2, s.Slot));
			Assert.Equal("en", Text(url, spans[0]));
			Assert.Equal("firmenkunden", Text(url, spans[1]));
		}

		[Fact]
		public void EmptyValuesList_NoSpans()
		{
			var url = "https://www.example.com/en/page/";
			Assert.Empty(Compute(url, new UrlHighlightRule { Values = [], Highlight = 1 }));
		}

		[Fact]
		public void GroupWithOneMalformedFragment_OthersStillMatch()
		{
			// A bad fragment in the list is skipped by the computer (ValidateConfig
			// rejects it at load); valid siblings in the same group still match.
			var url = "https://www.example.com/de/page/";
			var span = Assert.Single(Compute(url, Group(5, "de", "/de/")));
			Assert.Equal("de", Text(url, span));
		}
	}
}
