using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for CmsContentListFreshnessCheck.Evaluate — the pure helper that
	/// decides whether a configured CSV is stale relative to its MaxAgeDays.
	///
	/// The injectable "now" overload is used throughout so age computation is
	/// deterministic regardless of when the test runs.
	/// </summary>
	[Collection("Logger")]
	public class CmsContentListFreshnessTests : IDisposable
	{
		private readonly string _tempDir;

		public CmsContentListFreshnessTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"cms-list-fresh-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		private string WriteCsv(DateTime lastWriteTime)
		{
			var path = Path.Combine(_tempDir, $"list-{Guid.NewGuid():N}.csv");
			File.WriteAllText(path, "Path;Module\n/home;X\n", Encoding.UTF8);
			File.SetLastWriteTime(path, lastWriteTime);
			return path;
		}

		// ── Null / empty Path → not configured ────────────────────────────

		[Fact]
		public void Evaluate_NullConfig_ReturnsNotConfigured()
		{
			var result = CmsContentListFreshnessCheck.Evaluate(null, DateTime.Now);
			Assert.False(result.IsConfigured);
			Assert.False(result.FileExists);
			Assert.False(result.IsStale);
			Assert.False(result.IsUsableForPostCrawlPass);
		}

		[Fact]
		public void Evaluate_EmptyPath_ReturnsNotConfigured()
		{
			var cfg = new CmsContentListConfig { Path = "", MaxAgeDays = 7 };
			var result = CmsContentListFreshnessCheck.Evaluate(cfg, DateTime.Now);
			Assert.False(result.IsConfigured);
		}

		[Fact]
		public void Evaluate_WhitespacePath_ReturnsNotConfigured()
		{
			var cfg = new CmsContentListConfig { Path = "   ", MaxAgeDays = 7 };
			var result = CmsContentListFreshnessCheck.Evaluate(cfg, DateTime.Now);
			Assert.False(result.IsConfigured);
		}

		// ── Configured but file missing ───────────────────────────────────

		[Fact]
		public void Evaluate_MissingFile_ReportsFileMissingWithoutThrowing()
		{
			var cfg = new CmsContentListConfig
			{
				Path = Path.Combine(_tempDir, "does-not-exist.csv"),
				MaxAgeDays = 7,
			};
			var result = CmsContentListFreshnessCheck.Evaluate(cfg, DateTime.Now);
			Assert.True(result.IsConfigured);
			Assert.False(result.FileExists);
			Assert.False(result.IsStale);
			Assert.False(result.IsUsableForPostCrawlPass);
		}

		// ── Age check enabled (MaxAgeDays > 0) ────────────────────────────

		[Fact]
		public void Evaluate_FreshFile_IsNotStaleAndIsUsable()
		{
			var now = new DateTime(2026, 5, 18, 10, 0, 0);
			var path = WriteCsv(now.AddDays(-2));  // 2 days old
			var cfg = new CmsContentListConfig { Path = path, MaxAgeDays = 7 };

			var result = CmsContentListFreshnessCheck.Evaluate(cfg, now);

			Assert.True(result.IsConfigured);
			Assert.True(result.FileExists);
			Assert.False(result.IsStale);
			Assert.Equal(2, result.AgeDays);
			Assert.True(result.IsUsableForPostCrawlPass);
		}

		[Fact]
		public void Evaluate_FileExactlyAtThreshold_IsNotStale()
		{
			// Boundary: age == MaxAgeDays must NOT be stale. Strictly-older-than.
			var now = new DateTime(2026, 5, 18, 10, 0, 0);
			var path = WriteCsv(now.AddDays(-7));
			var cfg = new CmsContentListConfig { Path = path, MaxAgeDays = 7 };

			var result = CmsContentListFreshnessCheck.Evaluate(cfg, now);

			Assert.Equal(7, result.AgeDays);
			Assert.False(result.IsStale);
		}

		[Fact]
		public void Evaluate_FileOneDayPastThreshold_IsStale()
		{
			var now = new DateTime(2026, 5, 18, 10, 0, 0);
			// 8 full days = past 7-day threshold
			var path = WriteCsv(now.AddDays(-8).AddMinutes(-1));
			var cfg = new CmsContentListConfig { Path = path, MaxAgeDays = 7 };

			var result = CmsContentListFreshnessCheck.Evaluate(cfg, now);

			Assert.True(result.AgeDays >= 8);
			Assert.True(result.IsStale);
			Assert.False(result.IsUsableForPostCrawlPass);
		}

		[Fact]
		public void Evaluate_VeryOldFile_IsStale()
		{
			var now = new DateTime(2026, 5, 18, 10, 0, 0);
			var path = WriteCsv(now.AddDays(-365));
			var cfg = new CmsContentListConfig { Path = path, MaxAgeDays = 7 };

			var result = CmsContentListFreshnessCheck.Evaluate(cfg, now);

			Assert.True(result.IsStale);
			Assert.True(result.AgeDays >= 364);
		}

		// ── Age check disabled (MaxAgeDays <= 0) ──────────────────────────

		[Fact]
		public void Evaluate_MaxAgeDaysZero_DisablesCheck()
		{
			var now = new DateTime(2026, 5, 18, 10, 0, 0);
			var path = WriteCsv(now.AddDays(-365));
			var cfg = new CmsContentListConfig { Path = path, MaxAgeDays = 0 };

			var result = CmsContentListFreshnessCheck.Evaluate(cfg, now);

			Assert.True(result.CheckDisabled);
			Assert.False(result.IsStale);
			Assert.True(result.IsUsableForPostCrawlPass);
		}

		[Fact]
		public void Evaluate_MaxAgeDaysNegative_DisablesCheck()
		{
			var now = new DateTime(2026, 5, 18, 10, 0, 0);
			var path = WriteCsv(now.AddDays(-365));
			var cfg = new CmsContentListConfig { Path = path, MaxAgeDays = -1 };

			var result = CmsContentListFreshnessCheck.Evaluate(cfg, now);

			Assert.True(result.CheckDisabled);
			Assert.False(result.IsStale);
		}

		// ── Comment passthrough ───────────────────────────────────────────

		[Fact]
		public void Evaluate_PassesCommentThrough()
		{
			var now = new DateTime(2026, 5, 18, 10, 0, 0);
			var path = WriteCsv(now.AddDays(-30));
			var cfg = new CmsContentListConfig
			{
				Path = path,
				MaxAgeDays = 7,
				Comment = "Refresh weekly from CMS XYZ Admin → Reports.",
			};

			var result = CmsContentListFreshnessCheck.Evaluate(cfg, now);

			Assert.Equal(cfg.Comment, result.Comment);
		}

		[Fact]
		public void Evaluate_NullComment_BecomesEmptyString()
		{
			// Defensive: the property has a default of string.Empty, but a
			// hand-constructed instance could carry null. The helper must not
			// surface null into a downstream caller that may format it.
			var now = new DateTime(2026, 5, 18, 10, 0, 0);
			var path = WriteCsv(now.AddDays(-2));
			var cfg = new CmsContentListConfig { Path = path, MaxAgeDays = 7, Comment = null! };

			var result = CmsContentListFreshnessCheck.Evaluate(cfg, now);

			Assert.Equal(string.Empty, result.Comment);
		}
	}
}
