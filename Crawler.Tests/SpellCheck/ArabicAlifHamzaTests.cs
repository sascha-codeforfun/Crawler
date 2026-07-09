namespace Crawler.Tests.SpellCheck
{
	using System;
	using System.Linq;
	using Crawler.SpellCheck;
	using Xunit;

	// SYNTHETIC fixtures with explicit code points. The roundtrip is tested against a
	// MOCK `ar` accept-check (no live dictionary): the mock "accepts" the dictionary-
	// correct forms and "rejects" the error, exactly as the handoff hand-verified against
	// LibreOffice ar (إضغط rejects, اضغط accepts). Clears the bridge after each test.
	public class ArabicAlifHamzaTests : IDisposable
	{
		private const string Idghat = "\u0625\u0636\u063A\u0637";         // إضغط  (waṣl error)
		private const string IdghatBare = "\u0627\u0636\u063A\u0637";     // اضغط  (correct, bare alif)
		private const string Akala = "\u0623\u0643\u0644";                // أكل   ("ate" — legit qaṭʿ)
		private const string Islam = "\u0625\u0633\u0644\u0627\u0645";    // إسلام ("Islam" — legit qaṭʿ)

		// The mock dictionary: accepts the correct forms, rejects everything else
		// (including the as-found error إضغط).
		private static bool MockAr(string w) => w == IdghatBare || w == Akala || w == Islam;

		public void Dispose() => EnrichmentDictionaries.Clear();

		// ── Detector (injected delegate) ────────────────────────────────────

		[Fact]
		public void Detect_WaslError_SurfacesWithBareAlifSuggestion()
		{
			Assert.True(ArabicAlifHamzaDetector.TryDetect(Idghat, MockAr, out var f));
			Assert.Equal(IdghatBare, f.Suggestion);
			Assert.Equal("\u0625", f.InitialChar);       // إ
			Assert.Equal("U+0625", f.CodePointLabel);
		}

		[Fact]
		public void Detect_HamzaAbove_AlsoChecked()
		{
			// أب → اب under a mock where the bare form is the accepted one (exercises the
			// أ U+0623 branch as well as إ).
			static bool mock(string w) => w == "\u0627\u0628"; // accepts اب only
			Assert.True(ArabicAlifHamzaDetector.TryDetect("\u0623\u0628", mock, out var f));
			Assert.Equal("\u0627\u0628", f.Suggestion);
			Assert.Equal("U+0623", f.CodePointLabel);
		}

		[Fact]
		public void Detect_LegitQatc_Akala_Silent()
		{
			// أكل is accepted as-is → no reject→accept flip → silent (false-positive guard).
			Assert.False(ArabicAlifHamzaDetector.TryDetect(Akala, MockAr, out _));
		}

		[Fact]
		public void Detect_LegitQatc_Islam_Silent()
		{
			Assert.False(ArabicAlifHamzaDetector.TryDetect(Islam, MockAr, out _));
		}

		[Fact]
		public void Detect_AlreadyBareAlif_Silent()
		{
			// اضغط does not start with إ/أ — out of scope.
			Assert.False(ArabicAlifHamzaDetector.TryDetect(IdghatBare, MockAr, out _));
		}

		[Fact]
		public void Detect_NonArabic_Silent()
		{
			Assert.False(ArabicAlifHamzaDetector.TryDetect("hello", MockAr, out _));
		}

		[Fact]
		public void Detect_NullDelegate_Silent()
		{
			Assert.False(ArabicAlifHamzaDetector.TryDetect(Idghat, null!, out _));
		}

		// ── Enricher (through the registry + bridge) ────────────────────────

		[Fact]
		public void Enrich_ArRegistered_SurfacesMediumWithSuggestion()
		{
			EnrichmentDictionaries.Register("ar", MockAr);
			var ctx = new SpellEnrichmentContext(Idghat, string.Empty, string.Empty);
			var e = Assert.Single(SpellEnrichments.For(ctx)
				.Where(x => x.Kind == ArabicAlifHamzaDetector.Kind));
			Assert.Equal(EnrichmentConfidence.Medium, e.Confidence);
			Assert.Empty(e.HighlightOffsets);            // prose-only
			Assert.Contains(e.Lines, l => l.Contains(IdghatBare));
		}

		[Fact]
		public void Enrich_NoArDictionary_Silent()
		{
			// Bridge empty (no ar loaded) → the enricher cannot roundtrip → stays silent.
			var ctx = new SpellEnrichmentContext(Idghat, string.Empty, string.Empty);
			Assert.DoesNotContain(SpellEnrichments.For(ctx),
				x => x.Kind == ArabicAlifHamzaDetector.Kind);
		}

		[Fact]
		public void Enrich_ArLocaleVariant_Resolves()
		{
			// "ar-SA" must satisfy the "ar" family gate.
			EnrichmentDictionaries.Register("ar-SA", MockAr);
			var ctx = new SpellEnrichmentContext(Idghat, string.Empty, string.Empty);
			Assert.Contains(SpellEnrichments.For(ctx),
				x => x.Kind == ArabicAlifHamzaDetector.Kind);
		}

		[Fact]
		public void Enrich_LegitQatc_Silent_EvenWithArLoaded()
		{
			EnrichmentDictionaries.Register("ar", MockAr);
			var ctx = new SpellEnrichmentContext(Islam, string.Empty, string.Empty);
			Assert.DoesNotContain(SpellEnrichments.For(ctx),
				x => x.Kind == ArabicAlifHamzaDetector.Kind);
		}
	}
}
