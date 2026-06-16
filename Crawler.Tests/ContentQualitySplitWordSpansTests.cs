using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ContentQualityTriage.ComputeSplitWordSpans — the pure helper that
	/// computes the three coloured spans (tags / inside / tail) for a
	/// SPLIT_WORD_ANCHOR excerpt. The renderer
	/// (ConsoleUi.WriteWithSplitWordHighlight) only paints these spans, so the
	/// offset logic is unit-tested here without Console.
	///
	/// Tail rule under test: from just after &lt;/a&gt; to the first literal space
	/// (U+0020) or 24 chars, whichever first; every other character (tabs, Unicode
	/// spaces, punctuation, connectors, letters, digits) is consumed. Language-blind.
	/// </summary>
	public class ContentQualitySplitWordSpansTests
	{
		private static string Span(string excerpt, ConsoleUi.SplitSpanKind kind)
		{
			var spans = ContentQualityTriage.ComputeSplitWordSpans(excerpt);
			var s = spans.First(x => x.Kind == kind);
			return excerpt.Substring(s.Start, s.Length);
		}

		private static string Tail(string excerpt) => Span(excerpt, ConsoleUi.SplitSpanKind.Tail);

		// ── Tail content across the settled case set ──────────────────────────

		[Fact]
		public void Tail_LetterRun() => Assert.Equal("ld", Tail("<a>Hello Wor</a>ld more"));

		[Fact]
		public void Tail_DigitRun() => Assert.Equal("15", Tail("<a>08</a>15 Uhr"));

		[Fact]
		public void Tail_UrlLeadingDot() => Assert.Equal(".com", Tail("x http://www.example</a>.com here"));

		[Fact]
		public void Tail_UrlPath() => Assert.Equal("/home.html", Tail("x http://www.example.com</a>/home.html"));

		[Fact]
		public void Tail_HyphenCompound() => Assert.Equal("-Event", Tail("<a>World</a>-Event today"));

		[Fact]
		public void Tail_GermanCompound() => Assert.Equal("-Versicherungsrechner", Tail("<a>Beispiel</a>-Versicherungsrechner"));

		[Fact]
		public void Tail_CyrillicRun() => Assert.Equal("\u0441\u043f\u0430\u0441\u0438\u0431\u043e", Tail("<a>\u041e\u0442\u043b\u0438\u0447\u043d\u043e,</a>\u0441\u043f\u0430\u0441\u0438\u0431\u043e vielen"));

		[Fact]
		public void Tail_TabDoesNotBreak()
		{
			// Only a literal U+0020 space stops the tail. A tab is consumed — an
			// adventurous CMS editor's "</a>\tWorld" colours through the tab.
			Assert.Equal("\tWorld", Tail("<a>Hello</a>\tWorld next"));
		}

		[Fact]
		public void Tail_NbspDoesNotBreak()
		{
			// NBSP (U+00A0) is part of the Unicode-space barrage the tail consumes.
			Assert.Equal("a\u00A0b", Tail("<a>x</a>a\u00A0b next"));
		}

		[Fact]
		public void Tail_CapsAt24Chars()
		{
			var excerpt = "<a>x</a>" + new string('a', 40) + " end";
			var spans = ContentQualityTriage.ComputeSplitWordSpans(excerpt);
			var tail = spans.First(s => s.Kind == ConsoleUi.SplitSpanKind.Tail);
			Assert.Equal(24, tail.Length);
		}

		// ── No-fire / clean cases ─────────────────────────────────────────────

		[Fact]
		public void CleanLink_SpaceAfterClose_ProducesNoSpans()
		{
			// A space immediately after </a> means a clean link, not a split.
			var spans = ContentQualityTriage.ComputeSplitWordSpans("<a>Hello</a> World");
			Assert.Empty(spans);
		}

		[Fact]
		public void Empty_ProducesNoSpans()
		{
			Assert.Empty(ContentQualityTriage.ComputeSplitWordSpans(string.Empty));
			Assert.Empty(ContentQualityTriage.ComputeSplitWordSpans(null!));
		}

		// ── Span shape and ordering ───────────────────────────────────────────

		[Fact]
		public void Spans_AreOrderedTagInsideTagTail()
		{
			var spans = ContentQualityTriage.ComputeSplitWordSpans("<a>Hello Wor</a>ld x");
			var kinds = spans.Select(s => s.Kind).ToArray();
			Assert.Equal(
				new[]
				{
					ConsoleUi.SplitSpanKind.Tag,
					ConsoleUi.SplitSpanKind.Inside,
					ConsoleUi.SplitSpanKind.Tag,
					ConsoleUi.SplitSpanKind.Tail,
				},
				kinds);
		}

		[Fact]
		public void Inside_IsTheLinkText()
		{
			Assert.Equal("Hello Wor", Span("<a>Hello Wor</a>ld x", ConsoleUi.SplitSpanKind.Inside));
		}

		[Fact]
		public void Spans_NonOverlappingAndAscending()
		{
			var spans = ContentQualityTriage.ComputeSplitWordSpans("<a>Hello Wor</a>ld x");
			var prevEnd = -1;
			foreach (var s in spans)
			{
				Assert.True(s.Start >= prevEnd, "spans must be ascending and non-overlapping");
				prevEnd = s.Start + s.Length;
			}
		}

		[Fact]
		public void OpenTagOutOfWindow_StillColoursInsideViaPrecedingBracket()
		{
			// Excerpt centred on the </a> boundary (per #431) so the opening <a…>
			// has scrolled off the left edge — the common real case. The inside link
			// text must STILL colour, anchored to the last ">" before "</a>" (the
			// opening tag's close bracket, which is in-window). The attribute markup
			// before that ">" stays uncoloured; no open-tag span (the "<a" is gone).
			var excerpt = "example.test/path\" rel=\"noopener\">LinkText</a>r more";
			var spans = ContentQualityTriage.ComputeSplitWordSpans(excerpt);

			Assert.Equal("LinkText", Span(excerpt, ConsoleUi.SplitSpanKind.Inside));
			Assert.Equal("r", Tail(excerpt));
			// Close tag present; no open-tag span (only one Tag span, the close).
			Assert.Single(spans.Where(s => s.Kind == ConsoleUi.SplitSpanKind.Tag));
		}

		[Fact]
		public void OutOfWindow_AttributeMarkupBeforeBracket_StaysUncoloured()
		{
			// The inside must start AFTER the ">", never colouring the truncated
			// attribute text (rel="…" etc.) that precedes the link text.
			var excerpt = "data-x=\"0\" rel=\"noopener\">Wort</a>e rest";
			var inside = Span(excerpt, ConsoleUi.SplitSpanKind.Inside);
			Assert.Equal("Wort", inside);
			Assert.DoesNotContain("rel=", inside, System.StringComparison.Ordinal);
		}
	}
}
