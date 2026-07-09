using System.Linq;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ContentQualityTriage.ComputeLigatureSpans — the pure helper that
	/// locates typographic-ligature glyphs (U+FB00–U+FB06) in a triage excerpt and
	/// emits one 1-char highlight span per occurrence. The renderer
	/// (ConsoleUi.WriteWithLigatureSpans) only paints these spans, so the offset
	/// logic is unit-tested here without Console.
	///
	/// Every ligature is an offender — unlike quotes, there is no trigger/context
	/// distinction — so each span is emitted with IsTrigger: true and Length 1.
	/// </summary>
	public class ContentQualityLigatureSpansTests
	{
		// The seven ligatures Ligatures.Check flags.
		private const char Ff = '\uFB00';   // ﬀ
		private const char Fi = '\uFB01';   // ﬁ
		private const char Fl = '\uFB02';   // ﬂ
		private const char Ffi = '\uFB03';  // ﬃ
		private const char Ffl = '\uFB04';  // ﬄ
		private const char LongSt = '\uFB05'; // ﬅ
		private const char St = '\uFB06';   // ﬆ

		private static System.Collections.Generic.List<ConsoleUi.QuoteHighlightSpan> Spans(string text)
			=> ContentQualityTriage.ComputeLigatureSpans(text);

		[Fact]
		public void SingleLigature_OneSpanAtGlyph()
		{
			var text = $"qualifi{Fi}ziertes";   // the real-world fi case
			var spans = Spans(text);
			var span = Assert.Single(spans);
			Assert.Equal(text.IndexOf(Fi), span.Start);
			Assert.Equal(1, span.Length);
			Assert.True(span.IsTrigger);
		}

		[Fact]
		public void MultipleLigatures_OneSpanEach_InOrder()
		{
			var text = $"{Fi}nanzielles Ri{Fi}ko o{Ff}en";
			var spans = Spans(text);
			Assert.Equal(3, spans.Count);
			// Ascending by Start, each length 1, each a trigger.
			Assert.True(spans.Zip(spans.Skip(1), (a, b) => a.Start < b.Start).All(x => x));
			Assert.All(spans, s => Assert.Equal(1, s.Length));
			Assert.All(spans, s => Assert.True(s.IsTrigger));
			// Each marked offset really is a ligature glyph.
			Assert.All(spans, s => Assert.Contains(text[s.Start], ConsoleUi.HighlightLigatureChars));
		}

		[Fact]
		public void AllSevenLigatures_AllMarked()
		{
			var text = new string(new[] { Ff, Fi, Fl, Ffi, Ffl, LongSt, St });
			var spans = Spans(text);
			Assert.Equal(7, spans.Count);
			Assert.Equal(Enumerable.Range(0, 7), spans.Select(s => s.Start));
		}

		[Fact]
		public void PlainAsciiFi_NotMarked()
		{
			// The whole point: ordinary "fi" (two ASCII letters) is NOT a ligature
			// and must not be highlighted — only the U+FB01 glyph is the defect.
			Assert.Empty(Spans("qualifiziertes finanzielles Risiko"));
		}

		[Fact]
		public void NoLigatures_Empty() => Assert.Empty(Spans("Ihre Vorteile: kein Risiko, alles digital."));

		[Fact]
		public void EmptyText_Empty() => Assert.Empty(Spans(string.Empty));

		[Fact]
		public void NullText_Empty() => Assert.Empty(Spans(null!));

		[Fact]
		public void LigatureAtStartAndEnd_BothMarked()
		{
			var text = $"{Fl}ug nach Düsseldor{Ff}";
			var spans = Spans(text);
			Assert.Equal(2, spans.Count);
			Assert.Equal(0, spans[0].Start);
			Assert.Equal(text.Length - 1, spans[1].Start);
		}
	}
}
