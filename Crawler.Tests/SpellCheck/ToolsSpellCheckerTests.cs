using Crawler.SpellCheck;
using Crawler.Lexicon;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for the ToolsSpellChecker adapter — the thin seam that exposes
	/// Tools.CheckSpelling as a RunCheck delegate. Coverage targets the adapter's
	/// own logic only: the constructor wiring, the per-language primary-bundle
	/// selection (allBundles hit vs constructor-bundle fallback) and the
	/// projection of each error into a CheckMiss.
	///
	/// Fixtures are in-memory bundles (SharedUser HashSet, System left null) so a
	/// known word is accepted and any other letter token is a miss — no Hunspell
	/// dic/aff fixtures are needed. Tools.CheckSpelling itself is
	/// [ExcludeFromCodeCoverage]; these tests exercise the adapter, not it.
	///
	/// Note: CheckSpelling's value list carries the language(s) a word missed
	/// under, not spelling suggestions, so CheckMiss.Suggestions ends up being
	/// the language string — asserted explicitly below.
	/// </summary>
	public class SpellCheckToolsSpellCheckerTests
	{
		private static readonly IReadOnlyList<string> NoPrefixes = Array.Empty<string>();
		private static readonly IReadOnlyList<string> NoFugen = Array.Empty<string>();

		// In-memory bundle: every supplied word is accepted (SharedUser tier),
		// everything else is a miss (System dictionary intentionally null).
		private static Bundle Bundle(params string[] acceptedWords)
		{
			var bundle = new Bundle();
			foreach (var w in acceptedWords)
			{
				bundle.SharedUser.Add(w);
			}
			return bundle;
		}

		private static ToolsSpellChecker MakeChecker(
			Bundle ctorBundle,
			Dictionary<string, Bundle>? allBundles = null)
			=> new(ctorBundle, allBundles ?? new Dictionary<string, Bundle>(), NoPrefixes, NoFugen);

		// ── projection ──────────────────────────────────────────────────────

		[Fact]
		public void Check_MisspelledWord_YieldsMissWithWordAndLanguage()
		{
			var checker = MakeChecker(Bundle(), new() { ["en"] = Bundle() });

			var miss = Assert.Single(checker.Check("zzqx", "en").ToList());
			Assert.Equal("zzqx", miss.Word);
			Assert.Equal("en", miss.Suggestions); // value list is the language(s), joined
		}

		[Fact]
		public void Check_AcceptedWord_NoMiss()
		{
			var checker = MakeChecker(Bundle("hello"), new() { ["en"] = Bundle("hello") });
			Assert.Empty(checker.Check("hello", "en"));
		}

		// ── primary-bundle selection ────────────────────────────────────────

		[Fact]
		public void Check_LanguagePresentInAllBundles_UsesThatBundle()
		{
			// Constructor bundle accepts nothing (would flag "hello"); the "en"
			// bundle accepts it. An empty result proves the "en" bundle was used.
			var checker = MakeChecker(Bundle(), new() { ["en"] = Bundle("hello") });
			Assert.Empty(checker.Check("hello", "en"));
		}

		[Fact]
		public void Check_LanguageAbsentFromAllBundles_FallsBackToCtorBundle()
		{
			// Map has only "en" (empty, would flag "hello"); the constructor
			// bundle accepts "hello". Checking under absent "de" must fall back to
			// the constructor bundle — an empty result proves the fallback path.
			var checker = MakeChecker(Bundle("hello"), new() { ["en"] = Bundle() });
			Assert.Empty(checker.Check("hello", "de"));
		}

		// ── aggregation ─────────────────────────────────────────────────────

		[Fact]
		public void Check_DuplicateMisspelling_ReportedOnce()
		{
			var checker = MakeChecker(Bundle(), new() { ["en"] = Bundle() });

			var miss = Assert.Single(checker.Check("zzqx zzqx", "en").ToList());
			Assert.Equal("zzqx", miss.Word);
		}

		[Fact]
		public void Check_MultipleDistinctMisspellings_AllReported()
		{
			var checker = MakeChecker(Bundle(), new() { ["en"] = Bundle() });

			var misses = checker.Check("zzqx qwxz", "en").ToList();
			Assert.Equal(2, misses.Count);
			Assert.Contains(misses, m => m.Word == "zzqx");
			Assert.Contains(misses, m => m.Word == "qwxz");
		}

		[Fact]
		public void Check_MixedAcceptedAndMissed_OnlyMissReported()
		{
			var checker = MakeChecker(Bundle(), new() { ["en"] = Bundle("hello") });

			var miss = Assert.Single(checker.Check("hello zzqx", "en").ToList());
			Assert.Equal("zzqx", miss.Word);
		}
	}
}
