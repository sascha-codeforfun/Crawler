using System.Globalization;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Supplementary tests for IssueTracking.PurgeExpiredFixed focused on the
	/// producer/consumer date contract, complementing IssueTrackingPurgeTests
	/// (which covers the retention-window logic itself).
	///
	/// Audit context: DateLastSeen is *written* as DateTime.Now.ToString("yyyy-MM-dd")
	/// — a date-only, local-time, fixed-format string. It is *read back* with
	/// DateTime.TryParseExact("yyyy-MM-dd", InvariantCulture) and compared at
	/// .Date granularity against DateTime.Now.Date.AddDays(-N). Because both ends
	/// are date-only and local, DST transitions and a stable machine timezone
	/// cannot move a calendar date. The exact-parse means anything not in the
	/// canonical written form is treated as unparseable and conservatively kept.
	/// These tests pin that contract so a future change to the write format (e.g.
	/// adding a time component, or switching to UtcNow on one side only) is caught.
	/// </summary>
	public class IssueTrackingPurgeDateContractTests
	{
		private static IssueTracking.IssueRecord Fixed(string lastSeen) =>
			new()
			{
				Type = "QUALITY",
				Url = "u",
				Status = "fixed",
				DateLastSeen = lastSeen,
			};

		// ── Canonical written format round-trips correctly ────────────────────

		[Fact]
		public void CanonicalFormat_OldDate_IsPurged()
		{
			// Exactly the format Today() produces.
			var old = DateTime.Now.Date.AddDays(-40).ToString("yyyy-MM-dd");
			var kept = IssueTracking.PurgeExpiredFixed([Fixed(old)], retentionDays: 30);
			Assert.Empty(kept);
		}

		[Fact]
		public void CanonicalFormat_RecentDate_IsKept()
		{
			var recent = DateTime.Now.Date.AddDays(-5).ToString("yyyy-MM-dd");
			var kept = IssueTracking.PurgeExpiredFixed([Fixed(recent)], retentionDays: 30);
			Assert.Single(kept);
		}

		// ── Non-canonical formats are rejected by the exact parse → kept ───────

		[Fact]
		public void DateWithTimeComponent_IsNotCanonical_SoKeptAsUnparseable()
		{
			// After the TryParseExact("yyyy-MM-dd") hardening, a value carrying a
			// time component is NOT in the canonical written form, so it does not
			// parse and falls into the safe "unparseable → kept" bucket. (Before
			// the hardening this parsed via loose TryParse and compared on .Date;
			// either way the record is retained — this pins the current contract.)
			var withTime = DateTime.Now.Date.AddDays(-40).AddHours(23).AddMinutes(59)
				.ToString("yyyy-MM-dd HH:mm:ss");
			var kept = IssueTracking.PurgeExpiredFixed([Fixed(withTime)], retentionDays: 30);
			Assert.Single(kept);
		}

		// ── Boundary: the day exactly at cutoff is retained (matches < cutoff) ─

		[Fact]
		public void ExactlyRetentionDaysOld_IsKept()
		{
			// cutoff = today - N; purge condition is seen.Date < cutoff (strict).
			// A record dated exactly N days ago sits ON the cutoff → kept.
			var atCutoff = DateTime.Now.Date.AddDays(-30).ToString("yyyy-MM-dd");
			var kept = IssueTracking.PurgeExpiredFixed([Fixed(atCutoff)], retentionDays: 30);
			Assert.Single(kept);
		}

		[Fact]
		public void OneDayPastCutoff_IsPurged()
		{
			var pastCutoff = DateTime.Now.Date.AddDays(-31).ToString("yyyy-MM-dd");
			var kept = IssueTracking.PurgeExpiredFixed([Fixed(pastCutoff)], retentionDays: 30);
			Assert.Empty(kept);
		}

		// ── Unparseable date is conservatively kept (cannot age) ───────────────

		[Theory]
		[InlineData("not-a-date")]
		[InlineData("")]
		[InlineData("2026-13-99")]   // impossible month/day
		public void UnparseableDate_IsKept(string garbage)
		{
			var kept = IssueTracking.PurgeExpiredFixed([Fixed(garbage)], retentionDays: 30);
			Assert.Single(kept);
		}

		// ── Culture robustness ─────────────────────────────────────────────────
		// DateTime.TryParse without an explicit culture uses the current thread
		// culture. The canonical "yyyy-MM-dd" form is unambiguous (ISO-8601) and
		// parses identically under cultures with different date separators, so an
		// operator machine set to, say, de-DE must purge the same records.

		[Fact]
		public void IsoDate_ParsesConsistentlyUnderNonInvariantCulture()
		{
			var original = Thread.CurrentThread.CurrentCulture;
			try
			{
				Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
				var old = DateTime.Now.Date.AddDays(-40).ToString("yyyy-MM-dd");
				var kept = IssueTracking.PurgeExpiredFixed([Fixed(old)], retentionDays: 30);
				Assert.Empty(kept);
			}
			finally
			{
				Thread.CurrentThread.CurrentCulture = original;
			}
		}
	}
}
