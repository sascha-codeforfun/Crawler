namespace Crawler.Tests.SpellCheck
{
	using System.Linq;
	using Crawler.SpellCheck;
	using Xunit;

	// SYNTHETIC fixtures. Mixed-alphabet tokens are built from explicit Latin and
	// Cyrillic code points so the byte form is unambiguous in source (a literal
	// "Aвг" would not reveal which 'A' is which).
	public class HomoglyphEnrichmentTests
	{
		// Cyrillic а в г (U+0430/0432/0433) and uppercase initial А (U+0410).
		private const string CyA = "\u0430";
		private const string CyV = "\u0432";
		private const string CyG = "\u0433";

		// ── Detector ───────────────────────────────────────────────────────

		[Fact]
		public void Detect_LatinIntruderInCyrillicWord_FlagsLatinAtPosition0()
		{
			// Latin 'A' (U+0041) standing in for Cyrillic 'А' — the canonical homoglyph.
			Assert.True(HomoglyphDetector.TryDetect("A" + CyV + CyG, out var f));
			Assert.Equal("Latin", f.IntruderAlphabet);
			Assert.Equal("Cyrillic", f.MajorityAlphabet);
			var intruder = Assert.Single(f.Intruders);
			Assert.Equal(0, intruder.Index);
			Assert.Equal("A", intruder.Char);
			Assert.Equal("U+0041", intruder.CodePointLabel);
		}

		[Fact]
		public void Detect_IntruderInMiddle_ReportsCorrectIndex()
		{
			// а A в  → Latin 'A' is the minority, at index 1.
			Assert.True(HomoglyphDetector.TryDetect(CyA + "A" + CyV, out var f));
			Assert.Equal("Latin", f.IntruderAlphabet);
			Assert.Equal(1, Assert.Single(f.Intruders).Index);
		}

		[Fact]
		public void Detect_AllCyrillic_Clean()
		{
			Assert.False(HomoglyphDetector.TryDetect(CyA + CyV + CyG, out _));
		}

		[Fact]
		public void Detect_AllLatinWithDiacritics_Clean()
		{
			// "größe" — ö/ß are Latin; a diacritic is not a second alphabet.
			Assert.False(HomoglyphDetector.TryDetect("gr\u00F6\u00DFe", out _));
		}

		[Fact]
		public void Detect_MultiplicationSign_NotCountedAsLatinLetter()
		{
			// U+00D7 × is in the Latin range (0x00C0–0x024F) but category Sm, not a
			// letter. "× + Cyrillic" must read as single-alphabet, not a mix.
			Assert.False(HomoglyphDetector.TryDetect("\u00D7" + CyA + CyV, out _));
		}

		[Fact]
		public void Detect_DivisionSign_NotCountedAsLatinLetter()
		{
			// U+00F7 ÷ — same range, same Sm category.
			Assert.False(HomoglyphDetector.TryDetect("\u00F7" + CyA + CyV, out _));
		}

		[Fact]
		public void Detect_CyrillicThousandsSign_NotCountedAsCyrillicLetter()
		{
			// U+0482 (So) is in the Cyrillic range but not a letter; a Latin word
			// carrying it must not read as a Cyrillic+Latin mix.
			Assert.False(HomoglyphDetector.TryDetect("ab\u0482", out _));
		}

		[Fact]
		public void Detect_GenuineMixStillFires_AfterLetterGate()
		{
			// Guard: the letter gate must not suppress a real homoglyph.
			Assert.True(HomoglyphDetector.TryDetect("A" + CyV + CyG, out _));
		}

		[Fact]
		public void Detect_CyrillicWithDigitAndDash_Clean()
		{
			// "КОД-2" — single alphabet; digit and dash are ignored, not a mix.
			Assert.False(HomoglyphDetector.TryDetect("\u041A\u041E\u0414-2", out _));
		}

		[Fact]
		public void Detect_CyrillicPlusLatinAcrossDash_Flags()
		{
			// "С-A1" — Cyrillic С + Latin A: a genuine letter mix despite the dash/digit.
			Assert.True(HomoglyphDetector.TryDetect("\u0421-A1", out var f));
			Assert.Equal("Latin", f.IntruderAlphabet);
		}

		[Fact]
		public void Detect_Empty_Clean()
		{
			Assert.False(HomoglyphDetector.TryDetect(string.Empty, out _));
		}

		// ── Enricher ───────────────────────────────────────────────────────

		[Fact]
		public void Enricher_MixedToken_ProducesCertainEnrichmentWithOffset()
		{
			var enricher = new HomoglyphEnricher();
			Assert.True(enricher.TryEnrich(new SpellEnrichmentContext("A" + CyV + CyG, "", ""), out var e));
			Assert.Equal(HomoglyphDetector.Kind, e.Kind);
			Assert.Equal(EnrichmentConfidence.Certain, e.Confidence);
			Assert.Contains(0, e.HighlightOffsets);
			Assert.NotEmpty(e.Lines);
			Assert.Contains("corruption", e.Lines[0]);
		}

		[Fact]
		public void Enricher_CleanToken_Silent()
		{
			var enricher = new HomoglyphEnricher();
			Assert.False(enricher.TryEnrich(new SpellEnrichmentContext(CyA + CyV + CyG, "", ""), out _));
		}

		// ── Runner ─────────────────────────────────────────────────────────

		[Fact]
		public void Runner_MixedToken_OneEnrichmentOffsetsAndTicketNote()
		{
			var ctx = new SpellEnrichmentContext("A" + CyV + CyG, "", "");
			Assert.Single(SpellEnrichments.For(ctx));
			Assert.Equal(new[] { 0 }, SpellEnrichments.OffendersFor(ctx).ToArray());
			Assert.False(string.IsNullOrEmpty(SpellEnrichments.TicketNote(ctx)));
		}

		[Fact]
		public void Runner_CleanToken_EmptyEverywhere()
		{
			var ctx = new SpellEnrichmentContext(CyA + CyV + CyG, "", "");
			Assert.Empty(SpellEnrichments.For(ctx));
			Assert.Empty(SpellEnrichments.OffendersFor(ctx));
			Assert.Equal(string.Empty, SpellEnrichments.TicketNote(ctx));
		}

		[Fact]
		public void Runner_TicketNote_IsSingleLineAndSurvivesSanitization()
		{
			// The note rides into a line-based log; it must carry no newline/tab that
			// the ledger/ticket sanitizer would turn into stray '/' separators.
			var note = SpellEnrichments.TicketNote(new SpellEnrichmentContext("A" + CyV + CyG, "", ""));
			Assert.DoesNotContain('\n', note);
			Assert.DoesNotContain('\r', note);
			Assert.DoesNotContain('\t', note);
			Assert.Contains("corruption", note);
		}
	}
}
