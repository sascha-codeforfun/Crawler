namespace Crawler.Tests.SpellCheck
{
	using System.Linq;
	using Crawler.SpellCheck;
	using Xunit;

	// SYNTHETIC fixtures. Confusable tokens are built from explicit code points so the
	// byte form is unambiguous in source (a literal "paddingｰtop" would not reveal
	// which dash is which). FF70 = HALFWIDTH KATAKANA-HIRAGANA PROLONGED SOUND MARK
	// (the real "paddingｰtop" culprit); 2011 = NON-BREAKING HYPHEN; 2212 = MINUS SIGN.
	public class ConfusablePunctuationTests
	{
		private const string Ff70 = "\uFF70";
		private const string ScriptSource = "bundle.js \u00B7 reach 5"; // code context
		private const string ContentSource = "p[#text]";                // prose context

		// ── Detector ───────────────────────────────────────────────────────

		[Fact]
		public void Detect_Ff70BetweenLatin_FlagsAndSuggestsAsciiHyphen()
		{
			Assert.True(ConfusablePunctuationDetector.TryDetect("padding" + Ff70 + "top", out var f));
			Assert.Equal("padding-top", f.Suggestion);
			var hit = Assert.Single(f.Hits);
			Assert.Equal(7, hit.Index);         // after "padding"
			Assert.Equal(0xFF70, hit.CodePoint);
			Assert.Equal('-', hit.Canonical);
			Assert.Equal("U+FF70", hit.CodePointLabel);
		}

		[Fact]
		public void Detect_NonBreakingHyphen_Surfaces()
		{
			// U+2011 between Latin letters — an authoritative → 002D confusable.
			Assert.True(ConfusablePunctuationDetector.TryDetect("margin\u2011left", out var f));
			Assert.Equal("margin-left", f.Suggestion);
		}

		[Fact]
		public void Detect_MinusSign_Surfaces()
		{
			// U+2212 MINUS SIGN — authoritative entry, exercises the full map.
			Assert.True(ConfusablePunctuationDetector.TryDetect("x\u2212y", out var f));
			Assert.Equal("x-y", f.Suggestion);
		}

		[Fact]
		public void Detect_FullwidthHyphen_Surfaces()
		{
			// U+FF0D — dash-gap local addition (NFKC folds it to '-', confusables does not).
			Assert.True(ConfusablePunctuationDetector.TryDetect("border\uFF0Dbox", out var f));
			Assert.Equal("border-box", f.Suggestion);
		}

		[Fact]
		public void Detect_HorizontalBar_Surfaces()
		{
			// U+2015 — dash-gap local addition.
			Assert.True(ConfusablePunctuationDetector.TryDetect("a\u2015b", out var f));
			Assert.Equal("a-b", f.Suggestion);
		}

		[Fact]
		public void Detect_EmDash_Surfaces()
		{
			// U+2014 — dash-gap local addition. Detected in code context by design; the
			// enricher's code-source gate keeps it off legitimate prose typography.
			Assert.True(ConfusablePunctuationDetector.TryDetect("foo\u2014bar", out var f));
			Assert.Equal("foo-bar", f.Suggestion);
		}

		[Fact]
		public void Detect_PlainAsciiHyphen_Silent()
		{
			Assert.False(ConfusablePunctuationDetector.TryDetect("padding-top", out _));
		}

		[Fact]
		public void Detect_Ff70Leading_Silent()
		{
			// Not flanked by a Latin letter on the left — not an embedded confusable.
			Assert.False(ConfusablePunctuationDetector.TryDetect(Ff70 + "padding", out _));
		}

		[Fact]
		public void Detect_Ff70Trailing_Silent()
		{
			// Not flanked on the right.
			Assert.False(ConfusablePunctuationDetector.TryDetect("padding" + Ff70, out _));
		}

		[Fact]
		public void Detect_DigitFlank_Silent()
		{
			// Flanked by a digit, not a letter — out of first-ship scope (letters only).
			Assert.False(ConfusablePunctuationDetector.TryDetect("a2\u2212b", out _));
		}

		[Fact]
		public void Detect_SeparatorShape_Silent()
		{
			// Confusable followed by a space (boundary, not embedded) — the separator
			// shape is deliberately out of scope for this detector.
			Assert.False(ConfusablePunctuationDetector.TryDetect("a\u2212 b", out _));
		}

		[Fact]
		public void Detect_TwoConfusables_FlagsBoth()
		{
			Assert.True(ConfusablePunctuationDetector.TryDetect(
				"grid" + Ff70 + "template" + Ff70 + "columns", out var f));
			Assert.Equal(2, f.Hits.Count);
			Assert.Equal("grid-template-columns", f.Suggestion);
		}

		// ── Enricher (through the registry) ─────────────────────────────────

		[Fact]
		public void Enrich_CodeSource_SurfacesCertainWithSuggestionAndOffset()
		{
			var ctx = new SpellEnrichmentContext("padding" + Ff70 + "top", string.Empty, ScriptSource);
			var e = Assert.Single(SpellEnrichments.For(ctx)
				.Where(x => x.Kind == ConfusablePunctuationDetector.Kind));
			Assert.Equal(EnrichmentConfidence.Certain, e.Confidence);
			Assert.Equal(new[] { 7 }, e.HighlightOffsets.ToArray());
			Assert.Contains(e.Lines, l => l.Contains("padding-top"));
		}

		[Fact]
		public void Enrich_ContentSource_Silent()
		{
			// The context-gate guard: the SAME token in prose must NOT surface (a
			// confusable hyphen between Latin letters can be legitimate typography,
			// e.g. German "E‑Mail"). Analogue of the homoglyph no-flip guard.
			var ctx = new SpellEnrichmentContext("padding" + Ff70 + "top", "de", ContentSource);
			Assert.DoesNotContain(SpellEnrichments.For(ctx),
				x => x.Kind == ConfusablePunctuationDetector.Kind);
		}

		[Fact]
		public void Enrich_TicketNote_CarriesSuggestion()
		{
			var ctx = new SpellEnrichmentContext("padding" + Ff70 + "top", string.Empty, ScriptSource);
			Assert.Contains("padding-top", SpellEnrichments.TicketNote(ctx));
		}
	}
}
