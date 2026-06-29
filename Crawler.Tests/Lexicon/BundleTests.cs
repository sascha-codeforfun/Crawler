using Xunit;
using Crawler.Lexicon;

namespace Crawler.Tests.Lexicon
{
	/// <summary>
	/// Tests for Bundle.Check / CheckAny and the run-wide
	/// UsageTracker (Reset / SnapshotUser / SnapshotSite). The bundle is a
	/// pure in-memory type: Check accepts whitespace, accepts words in the SharedUser
	/// / SharedSite tiers (recording each consultation in the tracker), and falls
	/// back to the System WordList (null → reject). CheckAny ORs across bundles.
	///
	/// SYNTHETIC fixtures. UsageTracker is process-wide static, so tests
	/// that inspect snapshots Reset() first and use GUID-unique tokens to avoid
	/// cross-test bleed.
	/// </summary>
	public class BundleTests
	{
		private static Bundle BundleWithUser(params string[] userWords)
		{
			var b = new Bundle();
			foreach (var w in userWords)
			{
				b.SharedUser.Add(w);
			}

			return b;
		}

		private static Bundle BundleWithSite(params string[] siteWords)
		{
			var b = new Bundle();
			foreach (var w in siteWords)
			{
				b.SharedSite.Add(w);
			}

			return b;
		}

		// ── Check ───────────────────────────────────────────────────────────

		[Fact]
		public void Check_WhitespaceWord_ReturnsTrue()
		{
			Assert.True(new Bundle().Check("   "));
		}

		// ── NFC normalisation at lookup (D112) ───────────────────────

		[Fact]
		public void Check_DecomposedQuery_MatchesComposedEntry()
		{
			// "für" stored composed (U+00FC); queried decomposed (u + U+0308).
			// Renders identically; without NFC normalisation the byte-mismatch would
			// reject a valid word — the für/gebündelt regression.
			var bundle = BundleWithSite("f\u00FCr");
			Assert.True(bundle.Check("fu\u0308r"));
		}

		[Fact]
		public void Check_ComposedQuery_StillMatchesComposedEntry()
		{
			var bundle = BundleWithSite("f\u00FCr");
			Assert.True(bundle.Check("f\u00FCr"));
		}

		[Fact]
		public void Check_DecomposedWordNotInDictionary_ReturnsFalse()
		{
			// Normalisation must not over-accept: a decomposed token absent from every
			// tier is still rejected (only "für" is known here).
			var bundle = BundleWithSite("f\u00FCr");
			Assert.False(bundle.Check("ba\u0308r")); // "bär" decomposed, not in dict
		}

		[Fact]
		public void Check_WordInSharedUser_ReturnsTrueAndRecordsUsage()
		{
			UsageTracker.Reset();
			var token = $"userword{Guid.NewGuid():N}";
			var bundle = BundleWithUser(token);

			Assert.True(bundle.Check(token));
			Assert.Contains(token, UsageTracker.SnapshotUser());
		}

		[Fact]
		public void Check_WordInSharedSite_ReturnsTrueAndRecordsUsage()
		{
			UsageTracker.Reset();
			var token = $"siteword{Guid.NewGuid():N}";
			var bundle = BundleWithSite(token);

			Assert.True(bundle.Check(token));
			Assert.Contains(token, UsageTracker.SnapshotSite());
		}

		[Fact]
		public void Check_UnknownWordWithNullSystem_ReturnsFalse()
		{
			// System WordList left null; word in neither tier → reject.
			Assert.False(new Bundle().Check($"unknown{Guid.NewGuid():N}"));
		}

		[Fact]
		public void Check_WordInBothTiers_RecordsBoth()
		{
			// Documented behaviour: both tiers are probed even though the answer
			// short-circuits, so a word present in both dictionaries crosses off
			// BOTH entries — otherwise the site entry would look like an orphan.
			UsageTracker.Reset();
			var token = $"both{Guid.NewGuid():N}";
			var bundle = new Bundle();
			bundle.SharedUser.Add(token);
			bundle.SharedSite.Add(token);

			Assert.True(bundle.Check(token));
			Assert.Contains(token, UsageTracker.SnapshotUser());
			Assert.Contains(token, UsageTracker.SnapshotSite());
		}

		// ── CheckAny ────────────────────────────────────────────────────────

		[Fact]
		public void CheckAny_WhitespaceWord_ReturnsTrue()
		{
			Assert.True(Bundle.CheckAny("  ", new[] { new Bundle() }));
		}

		[Fact]
		public void CheckAny_AcceptedBySecondBundle_ReturnsTrue()
		{
			var token = $"multi{Guid.NewGuid():N}";
			var bundles = new[] { new Bundle(), BundleWithUser(token) };

			Assert.True(Bundle.CheckAny(token, bundles));
		}

		[Fact]
		public void CheckAny_AcceptedByNone_ReturnsFalse()
		{
			var bundles = new[] { new Bundle(), new Bundle() };

			Assert.False(Bundle.CheckAny($"nope{Guid.NewGuid():N}", bundles));
		}

		// ── UsageTracker ──────────────────────────────────────────

		[Fact]
		public void Reset_ClearsBothUserAndSiteSnapshots()
		{
			var userToken = $"u{Guid.NewGuid():N}";
			var siteToken = $"s{Guid.NewGuid():N}";
			UsageTracker.RecordUser(userToken);
			UsageTracker.RecordSite(siteToken);

			Assert.Contains(userToken, UsageTracker.SnapshotUser());
			Assert.Contains(siteToken, UsageTracker.SnapshotSite());

			UsageTracker.Reset();

			// Assert our own tokens are gone rather than global emptiness: other
			// test classes call Bundle.Check in parallel and may record
			// their own tokens after our Reset. Our GUID tokens are not re-added.
			Assert.DoesNotContain(userToken, UsageTracker.SnapshotUser());
			Assert.DoesNotContain(siteToken, UsageTracker.SnapshotSite());
		}
	}
}
