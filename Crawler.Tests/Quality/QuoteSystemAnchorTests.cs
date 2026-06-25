using Crawler.Quality;
using Xunit;

namespace Crawler.Tests.Quality
{
	/// <summary>
	/// D052 — language-anchored QUOTE_SYSTEM_MIX offender selection. On a single-language
	/// page the offender is the opener whose system isn't valid for that language,
	/// regardless of textual order (fixing the first-opener-wins inversion where, on a
	/// German page, an English pair appearing first was crowned "correct" and the German
	/// pair flagged). Unanchored (multi/undeclared) keeps the pre-D052 first-divergent
	/// rule — nothing is suppressed.
	/// </summary>
	public class QuoteSystemAnchorTests
	{
		// The real [17/50] shape: English pair first, then the correct German pair.
		[Fact]
		public void Anchored_De_EnglishFirstThenGerman_FlagsEnglishOpener()
		{
			// “Ueber” (English U+201C…U+201D) then „Foto“ (German U+201E…U+201C)
			var text = "im Bereich \u201CUeber\u201D die \u201EFoto\u201C aus";
			var englishOpen = text.IndexOf('\u201C');
			Assert.Equal(englishOpen, Quotes.LocateSystemMixMismatch(text, "de"));
		}

		// Order independence: flip the pairs; the English one is still the offender.
		[Fact]
		public void Anchored_De_GermanFirstThenEnglish_StillFlagsEnglishOpener()
		{
			var text = "die \u201EFoto\u201C im \u201CUeber\u201D aus";
			var firstU201C = text.IndexOf('\u201C');                  // German closer of „Foto“
			var englishOpen = text.IndexOf('\u201C', firstU201C + 1); // English opener
			Assert.Equal(englishOpen, Quotes.LocateSystemMixMismatch(text, "de"));
		}

		[Fact]
		public void Anchored_De_CleanGerman_ReturnsMinusOne()
		{
			var text = "Der \u201EBegriff\u201C hier.";   // „Begriff“ — correct German, no mix
			Assert.Equal(-1, Quotes.LocateSystemMixMismatch(text, "de"));
		}

		[Fact]
		public void Anchored_De_GuillemetMix_FlagsGuillemet()
		{
			// Correct German „X“ plus French guillemets «Y» → on a de page the guillemet is wrong.
			var text = "\u201EX\u201C und \u00ABY\u00BB";
			var guillemet = text.IndexOf('\u00AB');
			Assert.Equal(guillemet, Quotes.LocateSystemMixMismatch(text, "de"));
		}

		[Fact]
		public void Anchored_De_TwoWrongSystems_NotSuppressed()
		{
			// English “…” plus guillemets «…» — neither valid for de, but still a genuine
			// mix. Must surface (fall through to first-divergent), never suppress.
			var text = "\u201CX\u201D und \u00ABY\u00BB";
			Assert.True(Quotes.LocateSystemMixMismatch(text, "de") >= 0);
		}

		[Fact]
		public void Unanchored_KeepsFirstDivergent_FlagsGermanOpener()
		{
			// Same shape as [17/50] but no anchor (multi-language/undeclared): the
			// pre-D052 first-divergent rule applies → the German (second) opener returns.
			var text = "im Bereich \u201CUeber\u201D die \u201EFoto\u201C aus";
			var germanOpen = text.IndexOf('\u201E');
			Assert.Equal(germanOpen, Quotes.LocateSystemMixMismatch(text, null));
		}

		// D060 — the U+201E opener is shared by German-double and Slavic-double; the
		// page language decides which. On a Slavic-double page, „…” is the page's own
		// system and a French guillemet is the offender; on a de page the same „…”
		// block resolves to German-double and is clean (closer correctness is the
		// pairing walk's job, not the mix locator's).
		[Fact]
		public void Anchored_Pl_SlavicPlusGuillemet_FlagsGuillemet()
		{
			var text = "\u201EWyraz\u201D oraz \u00ABInne\u00BB";
			var guillemet = text.IndexOf('\u00AB');
			Assert.Equal(guillemet, Quotes.LocateSystemMixMismatch(text, "pl"));
		}

		[Fact]
		public void Anchored_Pl_SingleSlavicSystem_ReturnsMinusOne()
		{
			// One system only (Slavic-double, valid for pl) → no mix.
			var text = "To jest \u201EWyraz\u201D tutaj.";
			Assert.Equal(-1, Quotes.LocateSystemMixMismatch(text, "pl"));
		}

		[Fact]
		public void Anchored_De_SharedOpenerBlock_ResolvesGermanNoMix()
		{
			// „Foto” on a de page resolves the shared U+201E opener to German-double,
			// so only one system is present → the mix locator returns -1 (the wrong
			// CLOSER is reported by the pairing walk, not here).
			var text = "die \u201EFoto\u201D hier.";
			Assert.Equal(-1, Quotes.LocateSystemMixMismatch(text, "de"));
		}
	}
}
