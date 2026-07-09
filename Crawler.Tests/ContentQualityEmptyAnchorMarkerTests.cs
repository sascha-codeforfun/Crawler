using System.Linq;
using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ContentQualityTriage.InjectEmptyAnchorMarker — the pure matcher
	/// that injects the WCAG empty-link marker into essentially-empty anchors.
	/// The matcher must mirror the detector's verdict in
	/// MisplacedAnchors.Check (InnerText.Trim() blank, no &lt;img&gt;):
	/// it marks exactly the anchors the detector flags, no more, no less.
	///
	/// Pure (no Console / no Logger), so no test collection is needed. Accessed
	/// via InternalsVisibleTo. The marker rendering / colour is interactive and
	/// validated by operator eyeball, not here.
	/// </summary>
	public class ContentQualityEmptyAnchorMarkerTests
	{
		private const string Marker = "[WCAG-VIOLATION-EMPTY-LINK]";

		private static bool Marked(string excerpt) =>
			ContentQualityTriage.InjectEmptyAnchorMarker(excerpt).Contains(Marker);

		// ── Essentially-empty shapes → marked ─────────────────────────────────

		[Fact]
		public void Literal_AdjacentTags_IsMarked()
		{
			Assert.True(Marked("<a href=\"#x\"></a>"));
		}

		[Fact]
		public void WhitespaceOnly_IsMarked()
		{
			// The screenshot case: whitespace between the tags, which the old
			// literal "></a>" find missed entirely.
			Assert.True(Marked("<a class=\"elementor-icon\" href=\"#start\">    </a>"));
		}

		[Fact]
		public void NewlinesAndTabs_CountAsWhitespace_IsMarked()
		{
			Assert.True(Marked("<a href=\"#x\">\n  \t</a>"));
		}

		[Fact]
		public void EmptyInlineChild_IsMarked()
		{
			Assert.True(Marked("<a href=\"#x\"><i></i></a>"));
		}

		[Fact]
		public void NestedEmptyChildren_IsMarked()
		{
			Assert.True(Marked("<a href=\"#x\"><span><i></i></span></a>"));
		}

		// ── Content-bearing shapes → NOT marked ───────────────────────────────

		[Fact]
		public void TextBearing_IsNotMarked()
		{
			Assert.False(Marked("<a href=\"#x\">Click</a>"));
		}

		[Fact]
		public void TextInsideChild_IsNotMarked()
		{
			Assert.False(Marked("<a href=\"#x\"><span>Go</span></a>"));
		}

		[Fact]
		public void ImageLink_IsNotMarked()
		{
			// Detector excludes anchors wrapping <img> (intentional image links);
			// the injector must match that exclusion.
			Assert.False(Marked("<a href=\"#x\"><img src=\"a.png\"></a>"));
		}

		// ── Identity / non-interference ───────────────────────────────────────

		[Fact]
		public void NonEmptyExcerpt_IsReturnedUnchanged()
		{
			var input = "<a href=\"#x\">Click</a>";
			Assert.Equal(input, ContentQualityTriage.InjectEmptyAnchorMarker(input));
		}

		[Fact]
		public void NullOrEmpty_IsReturnedUnchanged()
		{
			Assert.Equal(string.Empty, ContentQualityTriage.InjectEmptyAnchorMarker(string.Empty));
			Assert.Null(ContentQualityTriage.InjectEmptyAnchorMarker(null!));
		}

		[Fact]
		public void ArticleTag_IsNotMistakenForAnchor()
		{
			// "<article" begins with "<a" but is not an anchor — must be skipped.
			var input = "<article>text</article>";
			Assert.Equal(input, ContentQualityTriage.InjectEmptyAnchorMarker(input));
		}

		// ── Multiple anchors in one excerpt ───────────────────────────────────

		[Fact]
		public void MixedExcerpt_MarksOnlyTheEmptyAnchor()
		{
			var input = "<div><a href=\"#a\">  </a> sep <a href=\"#b\">Real</a></div>";
			var result = ContentQualityTriage.InjectEmptyAnchorMarker(input);

			// Exactly one marker, injected on the empty anchor only.
			Assert.Equal(1, CountOccurrences(result, Marker));
			// The content-bearing anchor is untouched.
			Assert.Contains("Real</a>", result);
			// Marker precedes the preserved whitespace of the empty anchor.
			Assert.Contains(Marker + "  </a>", result);
		}

		[Fact]
		public void TwoEmptyAnchors_BothMarked()
		{
			var input = "<a href=\"#a\"></a><a href=\"#b\">   </a>";
			var result = ContentQualityTriage.InjectEmptyAnchorMarker(input);
			Assert.Equal(2, CountOccurrences(result, Marker));
		}

		// ── Marker placement ──────────────────────────────────────────────────

		[Fact]
		public void Marker_IsInjectedAfterOpeningTagBeforeInner()
		{
			var result = ContentQualityTriage.InjectEmptyAnchorMarker("<a href=\"#x\">  </a>");
			// Opening tag's ">" then marker then the preserved inner then close.
			Assert.Equal("<a href=\"#x\">" + Marker + "  </a>", result);
		}

		// ── Colour-map spans (ComputeEmptyAnchorSpans) ────────────────────────

		[Fact]
		public void Spans_CloseIconAnchor_StructureHrefAttrMarker()
		{
			var html = ContentQualityTriage.InjectEmptyAnchorMarker(
				"<a href=\"#\" class=\"close-icon\" title=\"Close\"></a>");
			var spans = ContentQualityTriage.ComputeEmptyAnchorSpans(html);

			var structure = spans
				.Where(s => s.Kind == ConsoleUi.EmptyAnchorSpanKind.Structure)
				.Select(s => html.Substring(s.Start, s.Length))
				.ToList();
			Assert.Contains("<a", structure);
			Assert.Contains(">", structure);
			Assert.Contains("</a>", structure);

			var href = spans.Single(s => s.Kind == ConsoleUi.EmptyAnchorSpanKind.Href);
			Assert.Equal("href=\"#\"", html.Substring(href.Start, href.Length));

			var marker = spans.Single(s => s.Kind == ConsoleUi.EmptyAnchorSpanKind.Marker);
			Assert.Equal(Marker, html.Substring(marker.Start, marker.Length));

			var attrText = string.Concat(spans
				.Where(s => s.Kind == ConsoleUi.EmptyAnchorSpanKind.Attr)
				.Select(s => html.Substring(s.Start, s.Length)));
			Assert.Contains("class=\"close-icon\"", attrText);
			Assert.Contains("title=\"Close\"", attrText);
		}

		[Fact]
		public void Spans_TrailingMarkup_IsContext()
		{
			var html = ContentQualityTriage.InjectEmptyAnchorMarker(
				"<a href=\"#\"></a><div class=\"wall\">noise</div>");
			var spans = ContentQualityTriage.ComputeEmptyAnchorSpans(html);

			var context = string.Concat(spans
				.Where(s => s.Kind == ConsoleUi.EmptyAnchorSpanKind.Context)
				.Select(s => html.Substring(s.Start, s.Length)));
			Assert.Contains("<div class=\"wall\">noise</div>", context);
		}

		[Fact]
		public void Spans_OrderedContiguousCoverWholeString()
		{
			var html = ContentQualityTriage.InjectEmptyAnchorMarker(
				"lead<a target=\"_blank\" href=\"https://x.example/p\">  </a>tail");
			var spans = ContentQualityTriage.ComputeEmptyAnchorSpans(html);

			var pos = 0;
			var sb = new StringBuilder();
			foreach (var s in spans)
			{
				Assert.Equal(pos, s.Start);   // ordered and contiguous
				sb.Append(html.Substring(s.Start, s.Length));
				pos += s.Length;
			}

			Assert.Equal(html.Length, pos);
			Assert.Equal(html, sb.ToString());
		}

		private static int CountOccurrences(string haystack, string needle)
		{
			int count = 0, idx = 0;
			while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0)
			{
				count++;
				idx += needle.Length;
			}
			return count;
		}
	}
}
