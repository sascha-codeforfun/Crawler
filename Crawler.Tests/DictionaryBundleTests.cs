using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for DictionaryBundle.Check / CheckAny and the run-wide
	/// DictionaryUsageTracker (Reset / SnapshotUser / SnapshotSite). The bundle is a
	/// pure in-memory type: Check accepts whitespace, accepts words in the SharedUser
	/// / SharedSite tiers (recording each consultation in the tracker), and falls
	/// back to the System WordList (null → reject). CheckAny ORs across bundles.
	///
	/// SYNTHETIC fixtures. DictionaryUsageTracker is process-wide static, so tests
	/// that inspect snapshots Reset() first and use GUID-unique tokens to avoid
	/// cross-test bleed.
	/// </summary>
	public class DictionaryBundleTests
	{
		private static DictionaryBundle BundleWithUser(params string[] userWords)
		{
			var b = new DictionaryBundle();
			foreach (var w in userWords)
			{
				b.SharedUser.Add(w);
			}

			return b;
		}

		private static DictionaryBundle BundleWithSite(params string[] siteWords)
		{
			var b = new DictionaryBundle();
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
			Assert.True(new DictionaryBundle().Check("   "));
		}

		[Fact]
		public void Check_WordInSharedUser_ReturnsTrueAndRecordsUsage()
		{
			DictionaryUsageTracker.Reset();
			var token = $"userword{Guid.NewGuid():N}";
			var bundle = BundleWithUser(token);

			Assert.True(bundle.Check(token));
			Assert.Contains(token, DictionaryUsageTracker.SnapshotUser());
		}

		[Fact]
		public void Check_WordInSharedSite_ReturnsTrueAndRecordsUsage()
		{
			DictionaryUsageTracker.Reset();
			var token = $"siteword{Guid.NewGuid():N}";
			var bundle = BundleWithSite(token);

			Assert.True(bundle.Check(token));
			Assert.Contains(token, DictionaryUsageTracker.SnapshotSite());
		}

		[Fact]
		public void Check_UnknownWordWithNullSystem_ReturnsFalse()
		{
			// System WordList left null; word in neither tier → reject.
			Assert.False(new DictionaryBundle().Check($"unknown{Guid.NewGuid():N}"));
		}

		// ── CheckAny ────────────────────────────────────────────────────────

		[Fact]
		public void CheckAny_WhitespaceWord_ReturnsTrue()
		{
			Assert.True(DictionaryBundle.CheckAny("  ", new[] { new DictionaryBundle() }));
		}

		[Fact]
		public void CheckAny_AcceptedBySecondBundle_ReturnsTrue()
		{
			var token = $"multi{Guid.NewGuid():N}";
			var bundles = new[] { new DictionaryBundle(), BundleWithUser(token) };

			Assert.True(DictionaryBundle.CheckAny(token, bundles));
		}

		[Fact]
		public void CheckAny_AcceptedByNone_ReturnsFalse()
		{
			var bundles = new[] { new DictionaryBundle(), new DictionaryBundle() };

			Assert.False(DictionaryBundle.CheckAny($"nope{Guid.NewGuid():N}", bundles));
		}

		// ── DictionaryUsageTracker ──────────────────────────────────────────

		[Fact]
		public void Reset_ClearsBothUserAndSiteSnapshots()
		{
			var userToken = $"u{Guid.NewGuid():N}";
			var siteToken = $"s{Guid.NewGuid():N}";
			DictionaryUsageTracker.RecordUser(userToken);
			DictionaryUsageTracker.RecordSite(siteToken);

			Assert.Contains(userToken, DictionaryUsageTracker.SnapshotUser());
			Assert.Contains(siteToken, DictionaryUsageTracker.SnapshotSite());

			DictionaryUsageTracker.Reset();

			// Assert our own tokens are gone rather than global emptiness: other
			// test classes call DictionaryBundle.Check in parallel and may record
			// their own tokens after our Reset. Our GUID tokens are not re-added.
			Assert.DoesNotContain(userToken, DictionaryUsageTracker.SnapshotUser());
			Assert.DoesNotContain(siteToken, DictionaryUsageTracker.SnapshotSite());
		}
	}
}
