using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Unit tests for Tools.AnalysePreviousCrawl — the pure log-analysis helper
	/// extracted from Program.cs in fileset #271.
	///
	/// CLAUDE.md invariant under test:
	///   "01-crawler.log has two `completed` entries per run when
	///    CmsContentList.PostCrawlPass = true: `completed | info` after the normal crawl,
	///    then `completed | post-crawl-pass` after the list pass.
	///    AnalysePreviousCrawl determines snapshot health by checking only the
	///    LAST non-empty line for the word `completed`. A run is considered
	///    incomplete if anything is appended after the final `completed` marker."
	/// </summary>
	public class ToolsAnalysePreviousCrawlTests : IDisposable
	{
		private readonly string _tempDir;

		public ToolsAnalysePreviousCrawlTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"apc-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		private string WriteLog(params string[] lines)
		{
			var path = Path.Combine(_tempDir, $"01-crawler-{Guid.NewGuid():N}.log");
			File.WriteAllLines(path, lines, Encoding.UTF8);
			return path;
		}

		// ── Missing / empty ──────────────────────────────────────────────────

		[Fact]
		public void AnalysePreviousCrawl_MissingFile_ReturnsIncomplete()
		{
			var (isComplete, duration, lastEntry) = Tools.AnalysePreviousCrawl(
				Path.Combine(_tempDir, "no-such.log"), "https://example.com");
			Assert.False(isComplete);
			Assert.Null(duration);
			Assert.Equal("", lastEntry);
		}

		[Fact]
		public void AnalysePreviousCrawl_EmptyFile_ReturnsIncomplete()
		{
			var path = WriteLog();
			var (isComplete, duration, lastEntry) = Tools.AnalysePreviousCrawl(
				path, "https://example.com");
			Assert.False(isComplete);
			Assert.Null(duration);
			Assert.Equal("", lastEntry);
		}

		[Fact]
		public void AnalysePreviousCrawl_OnlyBlankLines_ReturnsIncomplete()
		{
			var path = WriteLog("", "  ", "\t", "");
			var (isComplete, _, lastEntry) = Tools.AnalysePreviousCrawl(
				path, "https://example.com");
			Assert.False(isComplete);
			Assert.Equal("", lastEntry);
		}

		// ── Single completed marker ──────────────────────────────────────────

		[Fact]
		public void AnalysePreviousCrawl_LastLineCompleted_ReturnsComplete()
		{
			var path = WriteLog(
				"2026-04-25 12:00:00 | https://example.com | 200 | crawled",
				"2026-04-25 12:00:30 | https://example.com | OK  | completed | info");
			var (isComplete, _, lastEntry) = Tools.AnalysePreviousCrawl(
				path, "https://example.com");
			Assert.True(isComplete);
			Assert.Contains("completed", lastEntry);
		}

		[Fact]
		public void AnalysePreviousCrawl_LastLineCompletedFollowedByBlank_StillComplete()
		{
			// Trailing blank lines are stripped before "last line" check.
			var path = WriteLog(
				"2026-04-25 12:00:00 | https://example.com | 200 | crawled",
				"2026-04-25 12:00:30 | https://example.com | OK  | completed | info",
				"",
				"   ");
			var (isComplete, _, _) = Tools.AnalysePreviousCrawl(
				path, "https://example.com");
			Assert.True(isComplete);
		}

		// ── Two-completed-markers invariant (CLAUDE.md) ──────────────────────

		[Fact]
		public void AnalysePreviousCrawl_TwoCompletedMarkers_LastIsPostCrawl_Complete()
		{
			// The invariant case: PostCrawlPass run produces TWO completed
			// markers. The second one (post-crawl-pass) is the final marker.
			var path = WriteLog(
				"2026-04-25 12:00:00 | https://example.com | 200 | crawled",
				"2026-04-25 12:00:30 | https://example.com | OK  | completed | info",
				"2026-04-25 12:00:31 | https://example.com | 200 | crawled",
				"2026-04-25 12:00:35 | https://example.com | OK  | completed | post-crawl-pass");
			var (isComplete, _, lastEntry) = Tools.AnalysePreviousCrawl(
				path, "https://example.com");
			Assert.True(isComplete);
			Assert.Contains("post-crawl-pass", lastEntry);
		}

		[Fact]
		public void AnalysePreviousCrawl_CompletedFollowedByMoreEntries_Incomplete()
		{
			// CLAUDE.md: "A run is considered incomplete if anything is
			// appended after the final completed marker."
			var path = WriteLog(
				"2026-04-25 12:00:00 | https://example.com | 200 | crawled",
				"2026-04-25 12:00:30 | https://example.com | OK  | completed | info",
				"2026-04-25 12:00:40 | https://example.com/extra | 200 | crawled");
			var (isComplete, duration, lastEntry) = Tools.AnalysePreviousCrawl(
				path, "https://example.com");
			Assert.False(isComplete);
			Assert.Null(duration);
			Assert.Contains("crawled", lastEntry);
		}

		[Fact]
		public void AnalysePreviousCrawl_NoCompletedMarker_Incomplete()
		{
			var path = WriteLog(
				"2026-04-25 12:00:00 | https://example.com | 200 | crawled",
				"2026-04-25 12:00:05 | https://example.com/page | 200 | saved");
			var (isComplete, _, _) = Tools.AnalysePreviousCrawl(
				path, "https://example.com");
			Assert.False(isComplete);
		}

		// ── Duration calculation ─────────────────────────────────────────────

		[Fact]
		public void AnalysePreviousCrawl_ParsesDurationFromFirstCrawledToLastCompleted()
		{
			var path = WriteLog(
				"2026-04-25 12:00:00 | https://example.com | 200 | crawled",
				"2026-04-25 12:00:15 | https://example.com/p1 | 200 | saved",
				"2026-04-25 12:00:30 | https://example.com | OK  | completed | info");
			var (isComplete, duration, _) = Tools.AnalysePreviousCrawl(
				path, "https://example.com");
			Assert.True(isComplete);
			Assert.NotNull(duration);
			Assert.Equal(TimeSpan.FromSeconds(30), duration);
		}

		[Fact]
		public void AnalysePreviousCrawl_DurationUsesSecondCompletedWithPostCrawlPass()
		{
			// With two completed markers, duration spans the FULL run
			// (first crawled → final post-crawl-pass completed).
			var path = WriteLog(
				"2026-04-25 12:00:00 | https://example.com | 200 | crawled",
				"2026-04-25 12:00:30 | https://example.com | OK  | completed | info",
				"2026-04-25 12:00:31 | https://example.com | 200 | crawled",
				"2026-04-25 12:01:00 | https://example.com | OK  | completed | post-crawl-pass");
			var (_, duration, _) = Tools.AnalysePreviousCrawl(
				path, "https://example.com");
			Assert.NotNull(duration);
			Assert.Equal(TimeSpan.FromSeconds(60), duration);
		}

		[Fact]
		public void AnalysePreviousCrawl_NoMatchingCrawledLine_DurationNull()
		{
			// Completed marker present, but no "crawled" line for the given baseUrl
			// → cannot compute duration; isComplete still true.
			var path = WriteLog(
				"2026-04-25 12:00:30 | https://example.com | OK  | completed | info");
			var (isComplete, duration, _) = Tools.AnalysePreviousCrawl(
				path, "https://example.com");
			Assert.True(isComplete);
			Assert.Null(duration);
		}

		[Fact]
		public void AnalysePreviousCrawl_UnparseableTimestamp_DurationNull()
		{
			var path = WriteLog(
				"not-a-timestamp | https://example.com | 200 | crawled",
				"also-not | https://example.com | OK  | completed | info");
			var (isComplete, duration, _) = Tools.AnalysePreviousCrawl(
				path, "https://example.com");
			Assert.True(isComplete);
			Assert.Null(duration);
		}

		// ── ParseLogTimestamp ────────────────────────────────────────────────

		[Fact]
		public void ParseLogTimestamp_StandardFormat_Parses()
		{
			var result = Tools.ParseLogTimestamp("2026-04-25 12:07:22 | url | status | message");
			Assert.NotNull(result);
			Assert.Equal(new DateTime(2026, 4, 25, 12, 7, 22), result);
		}

		[Fact]
		public void ParseLogTimestamp_NoPipe_TriesParseWholeLine()
		{
			var result = Tools.ParseLogTimestamp("2026-04-25 12:07:22");
			Assert.NotNull(result);
		}

		[Fact]
		public void ParseLogTimestamp_GarbageInput_ReturnsNull()
		{
			var result = Tools.ParseLogTimestamp("garbage | content | here");
			Assert.Null(result);
		}
	}
}
