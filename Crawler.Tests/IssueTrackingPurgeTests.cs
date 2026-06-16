using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// [#331] Tests for IssueTracking.PurgeExpiredFixed — the retention window
	/// that removes resolved ("fixed") records past N days so IssueTracking.log
	/// does not accumulate historical noise forever. Only "fixed" is purged;
	/// wontfix/config and open statuses are always kept.
	/// </summary>
	public class IssueTrackingPurgeTests
	{
		private static string DaysAgo(int n) =>
			DateTime.Now.Date.AddDays(-n).ToString("yyyy-MM-dd");

		private static IssueTracking.IssueRecord Rec(
			string url, string status, string lastSeen) =>
			new()
			{
				Type = "QUALITY",
				Url = url,
				Status = status,
				DateLastSeen = lastSeen,
			};

		private static List<IssueTracking.IssueRecord> Sample() =>
		[
			Rec("u1", "fixed",   DaysAgo(40)),   // old fixed
			Rec("u2", "fixed",   DaysAgo(5)),    // recent fixed
			Rec("u3", "new",     DaysAgo(40)),   // old, but open — keep
			Rec("u4", "wontfix", DaysAgo(99)),   // deliberate — keep
			Rec("u5", "fixed",   ""),            // empty date
		];

		[Fact]
		public void Disabled_Zero_KeepsEverything()
		{
			var result = IssueTracking.PurgeExpiredFixed(Sample(), 0);
			Assert.Equal(5, result.Count);
		}

		[Fact]
		public void PositiveWindow_DropsOnlyExpiredFixed()
		{
			var result = IssueTracking.PurgeExpiredFixed(Sample(), 30);

			Assert.DoesNotContain(result, r => r.Url == "u1"); // 40d fixed → dropped
			Assert.Contains(result, r => r.Url == "u2");       // 5d fixed → kept
			Assert.Contains(result, r => r.Url == "u3");       // open → kept
			Assert.Contains(result, r => r.Url == "u4");       // wontfix → kept
			Assert.Equal(4, result.Count);
		}

		[Fact]
		public void PositiveWindow_EmptyDateFixed_IsKept()
		{
			// Cannot age a record with no parseable DateLastSeen — keep it
			// under a positive window rather than guess.
			var result = IssueTracking.PurgeExpiredFixed(Sample(), 30);
			Assert.Contains(result, r => r.Url == "u5");
		}

		[Fact]
		public void PurgeAll_MinusOne_DropsEveryFixed()
		{
			var result = IssueTracking.PurgeExpiredFixed(Sample(), -1);

			Assert.DoesNotContain(result, r => r.Status == "fixed");
			Assert.Equal(2, result.Count);                     // only u3, u4 remain
			Assert.Contains(result, r => r.Url == "u3");
			Assert.Contains(result, r => r.Url == "u4");
		}

		[Fact]
		public void PurgeAll_AlsoDropsEmptyDateFixed()
		{
			var result = IssueTracking.PurgeExpiredFixed(Sample(), -1);
			Assert.DoesNotContain(result, r => r.Url == "u5"); // empty-date fixed → dropped
		}

		[Theory]
		[InlineData(-1)]
		[InlineData(-5)]
		[InlineData(-100)]
		public void AnyNegative_BehavesAsPurgeAll(int retention)
		{
			var result = IssueTracking.PurgeExpiredFixed(Sample(), retention);
			Assert.DoesNotContain(result, r => r.Status == "fixed");
		}

		[Fact]
		public void NeverPurges_NonFixedStatuses()
		{
			// Even very old, every non-fixed status survives any window.
			var data = new List<IssueTracking.IssueRecord>
			{
				Rec("a", "new",      DaysAgo(999)),
				Rec("b", "pending",  DaysAgo(999)),
				Rec("c", "overdue",  DaysAgo(999)),
				Rec("d", "reopened", DaysAgo(999)),
				Rec("e", "wontfix",  DaysAgo(999)),
				Rec("f", "config",   DaysAgo(999)),
			};
			var result = IssueTracking.PurgeExpiredFixed(data, 1);
			Assert.Equal(6, result.Count);
		}

		[Fact]
		public void Boundary_ExactlyAtCutoff_IsKept()
		{
			// DateLastSeen exactly N days ago is NOT older than the cutoff
			// (strict <), so it is kept; N+1 is dropped.
			var data = new List<IssueTracking.IssueRecord>
			{
				Rec("at",   "fixed", DaysAgo(30)),
				Rec("past", "fixed", DaysAgo(31)),
			};
			var result = IssueTracking.PurgeExpiredFixed(data, 30);
			Assert.Contains(result, r => r.Url == "at");
			Assert.DoesNotContain(result, r => r.Url == "past");
		}
	}
}
