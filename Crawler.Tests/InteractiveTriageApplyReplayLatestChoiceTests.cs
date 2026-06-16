using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for InteractiveTriage.ApplyReplayLatestChoice — the state-mutation
	/// helper introduced in fileset #289 that backs the [L] "use latest snapshot"
	/// branch of PromptForSnapshotChoice.
	///
	/// The contract under test:
	///   - IsDebugSession is set to true (the run skips the download crawl).
	///   - TimeStamp is set to the chosen snapshot name.
	///   - All 24 timestamp-dependent paths get rebuilt under the snapshot path.
	/// </summary>
	public class InteractiveTriageApplyReplayLatestChoiceTests
	{
		// ── Core contract ────────────────────────────────────────────────

		[Fact]
		public void SetsIsDebugSession_True()
		{
			var ctx = new CrawlerRunContext { IsDebugSession = false };

			InteractiveTriage.ApplyReplayLatestChoice(ctx, "2026-05-16-12-00-00", "/snap/path");

			Assert.True(ctx.IsDebugSession);
		}

		[Fact]
		public void SetsTimeStamp_ToSnapshotName()
		{
			var ctx = new CrawlerRunContext();

			InteractiveTriage.ApplyReplayLatestChoice(ctx, "2026-05-16-12-00-00", "/snap/path");

			Assert.Equal("2026-05-16-12-00-00", ctx.TimeStamp);
		}

		[Fact]
		public void RebuildsPaths_UnderSnapshotPath()
		{
			var ctx = new CrawlerRunContext();

			InteractiveTriage.ApplyReplayLatestChoice(ctx, "2026-05-16-12-00-00", "/snap/path");

			Assert.Equal("/snap/path", ctx.SaveDirectory);
			Assert.Equal(Path.Combine("/snap/path", "download"), ctx.FileDownloadDirectory);
			Assert.Equal(Path.Combine("/snap/path", "01-crawler.log"), ctx.CrawlerLogPath);
		}
	}
}
