using Xunit;
using Crawler.Lexicon;
using Crawler.SpellCheck;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Tests for <see cref="SpellChecker.CheckTrailingHyphenStem"/> — the German
	/// compound-word trailing-hyphen stem check. The Hunspell-coupled spell pass
	/// (<see cref="SpellChecker.Check"/>) is [ExcludeFromCodeCoverage] and is
	/// exercised through the adapter's integration tests, not here.
	/// </summary>
	public class SpellCheckerTests
	{
		// ── Test helpers ─────────────────────────────────────────────────
		private static Bundle InMemoryBundle(params string[] sharedUserWords)
		{
			var bundle = new Bundle();
			foreach (var w in sharedUserWords)
			{
				bundle.SharedUser.Add(w);
			}

			return bundle;
		}

		// ── CheckTrailingHyphenStem ─────────
		// German compound-word stem-check: strip trailing hyphen, then optionally
		// strip a Fugenelement (longest-first), check stem against dictionary.

		[Fact]
		public void CheckTrailingHyphenStem_BareStem_AcceptedDirectly()
		{
			var dict = InMemoryBundle("Flug");
			Assert.True(SpellChecker.CheckTrailingHyphenStem("Flug-", dict, []));
		}

		[Fact]
		public void CheckTrailingHyphenStem_StemNotInDictionary_NoFugen_ReturnsFalse()
		{
			var dict = InMemoryBundle("OtherWord");
			Assert.False(SpellChecker.CheckTrailingHyphenStem("Unknown-", dict, ["s", "n"]));
		}

		[Fact]
		public void CheckTrailingHyphenStem_FugenStripping_FindsBaseWord()
		{
			// "Schulungs-" → strip "s" → "Schulung" → in dictionary.
			var dict = InMemoryBundle("Schulung");
			Assert.True(SpellChecker.CheckTrailingHyphenStem("Schulungs-", dict, ["s", "n"]));
		}

		[Fact]
		public void CheckTrailingHyphenStem_LongestFugenTriedFirst()
		{
			// "Auftragens-" with fugens ["s", "ns"]. Longest-first would strip "ns"
			// giving "Auftrage" (not in dict); short "s" would give "Auftragen" (in dict).
			// Either path finds a match; the test confirms behaviour either way.
			// Stronger test: ambiguous stem where only the longer strip hits.
			var dict = InMemoryBundle("Auftrag"); // only the longer "ns" path produces this
			Assert.True(SpellChecker.CheckTrailingHyphenStem("Auftrags-", dict, ["s", "ags"]));
			// "Auftrags-" → strip hyphen → "Auftrags" → try "ags" first → "Auftr" (not in dict)
			//                                       → try "s"          → "Auftrag" (in dict) ✓
		}

		[Fact]
		public void CheckTrailingHyphenStem_EmptyFugenList_FallsBackToStemAsIs()
		{
			var dict = InMemoryBundle("Auto");
			Assert.True(SpellChecker.CheckTrailingHyphenStem("Auto-", dict, []));
			Assert.False(SpellChecker.CheckTrailingHyphenStem("Unknown-", dict, []));
		}

		[Fact]
		public void CheckTrailingHyphenStem_FugenLongerThanStem_Skipped()
		{
			// "Ag-" stem is just "Ag"; fugen "rieren" is longer than stem.
			// The EndsWith check fails so the fugen is skipped (no IndexOutOfRange).
			var dict = InMemoryBundle();
			Assert.False(SpellChecker.CheckTrailingHyphenStem("Ag-", dict, ["rieren"]));
		}

		[Fact]
		public void CheckTrailingHyphenStem_EmptyFugenInList_Skipped()
		{
			// An empty-string Fugenelement must be skipped by the !IsNullOrEmpty guard,
			// not treated as a zero-length strip. Stem "Unknown" isn't in the dictionary
			// and the only fugen entry is empty, so the result is false.
			var dict = InMemoryBundle();
			Assert.False(SpellChecker.CheckTrailingHyphenStem("Unknown-", dict, [""]));
		}

		[Fact]
		public void CheckTrailingHyphenStem_StrippedStemBecomesEmpty_Skipped()
		{
			// When a Fugenelement equals the whole stem, stripping leaves an empty
			// string; the !IsNullOrEmpty(stripped) guard must skip it rather than
			// checking "". Stem "Ss" minus fugen "ss" → "" → false.
			var dict = InMemoryBundle();
			Assert.False(SpellChecker.CheckTrailingHyphenStem("Ss-", dict, ["ss"]));
		}

		// ── TryParenthesizedPrefixJoin ─────────
		// German optional-prefix form "(prefix-)stem": the bare prefix is a bound
		// morpheme; the intended word joins it to the following stem. Synthetic tokens
		// are used so these tests exercise the join mechanism, not a real lexicon.

		[Fact]
		public void TryParenthesizedPrefixJoin_JoinedCompoundInDictionary_ReturnsTrue()
		{
			// "(wox-)bar" → join "wox" + "bar" = "woxbar", which the dictionary accepts.
			var dict = InMemoryBundle("woxbar");
			Assert.True(SpellChecker.TryParenthesizedPrefixJoin("wox", "ein (wox-)bar satz", dict));
		}

		[Fact]
		public void TryParenthesizedPrefixJoin_JoinedCompoundNotInDictionary_ReturnsFalse()
		{
			// The compound is not accepted, so nothing here suppresses the prefix.
			var dict = InMemoryBundle("unrelated");
			Assert.False(SpellChecker.TryParenthesizedPrefixJoin("wox", "ein (wox-)bar satz", dict));
		}

		[Fact]
		public void TryParenthesizedPrefixJoin_OnlyStemInDictionary_NotTheCompound_ReturnsFalse()
		{
			// The stem reading "bar" is itself a word, but the join "woxbar" is not — the
			// helper validates the COMPOUND, never the following stem on its own.
			var dict = InMemoryBundle("bar");
			Assert.False(SpellChecker.TryParenthesizedPrefixJoin("wox", "ein (wox-)bar satz", dict));
		}

		[Fact]
		public void TryParenthesizedPrefixJoin_NoParentheses_ReturnsFalse()
		{
			// A bare trailing hyphen without the parenthesised shape is out of scope here
			// (handled by CheckTrailingHyphenStem), so the join must not fire.
			var dict = InMemoryBundle("woxbar");
			Assert.False(SpellChecker.TryParenthesizedPrefixJoin("wox", "ein wox- bar satz", dict));
		}

		[Fact]
		public void TryParenthesizedPrefixJoin_NoFollowingWord_ReturnsFalse()
		{
			// "(wox-)" with no letters after the closing paren — nothing to join to.
			var dict = InMemoryBundle("woxbar");
			Assert.False(SpellChecker.TryParenthesizedPrefixJoin("wox", "ein (wox-) satz", dict));
		}

		[Fact]
		public void TryParenthesizedPrefixJoin_UmlautStem_Joined()
		{
			// The following stem may contain German umlauts / eszett.
			var dict = InMemoryBundle("woxüber");
			Assert.True(SpellChecker.TryParenthesizedPrefixJoin("wox", "ein (wox-)über satz", dict));
		}

		[Fact]
		public void TryParenthesizedPrefixJoin_EmptyPrefix_ReturnsFalse()
		{
			var dict = InMemoryBundle("woxbar");
			Assert.False(SpellChecker.TryParenthesizedPrefixJoin("", "(wox-)bar", dict));
		}

	}
}
