using System.Text.RegularExpressions;
using Xunit;
using Crawler.Snapshots;

namespace Crawler.Tests.Snapshots
{
	/// <summary>
	/// Tests for SnapshotFolder.NewName and SnapshotFolder.Matches.
	/// These drive snapshot folder naming for the whole crawler — any change
	/// in format silently breaks snapshot history. Lock the contract down.
	/// </summary>
	[Collection("Logger")]
	public class SnapshotFolderTests : IDisposable
	{
		private readonly string _tempDir;

		public SnapshotFolderTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"ts-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
			Logger.Initialize(Path.Combine(_tempDir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		// ── Matches ───────────────────────────────────────────

		[Theory]
		[InlineData("2026-05-14-04-58-32", true)]
		[InlineData("2024-01-01-00-00-00", true)]
		[InlineData("9999-12-31-23-59-59", true)]
		public void Matches_AcceptsValidFormat(string name, bool expected)
		{
			Assert.Equal(expected, SnapshotFolder.Matches(name));
		}

		[Theory]
		[InlineData("2026-05-14", false)]                  // wrong length
		[InlineData("2026-05-14-04-58-32-extra", false)]   // wrong length
		[InlineData("2026/05/14/04/58/32", false)]         // wrong separators
		[InlineData("2026_05_14_04_58_32", false)]         // wrong separators
		[InlineData("abcd-ef-gh-ij-kl-mn", false)]         // non-digit positions
		[InlineData("2026a05-14-04-58-32", false)]         // dash positions wrong
		[InlineData("", false)]                            // empty
		[InlineData("not-a-timestamp-at-all", false)]      // wrong shape
		public void Matches_RejectsInvalidFormats(string name, bool expected)
		{
			Assert.Equal(expected, SnapshotFolder.Matches(name));
		}

		[Fact]
		public void Matches_RejectsLengthMismatchOf19WithNonDigits()
		{
			// Exactly 19 chars but dashes in the right positions and non-digits
			// elsewhere → should be rejected by the All(char.IsDigit) check.
			Assert.False(SnapshotFolder.Matches("aaaa-aa-aa-aa-aa-aa"));
		}

		// ── NewName — fresh-crawl branch ───────────────────────────────

		[Fact]
		public void NewName_NotDebugSession_ReturnsFreshTimestamp()
		{
			// When debugDisableCrawl is false, debugTimeStamp and
			// sessionParentDirectory are both ignored.
			var ts = SnapshotFolder.NewName(
				debugDisableCrawl: false,
				debugTimeStamp: "ignored-value",
				sessionParentDirectory: "/does/not/exist");

			Assert.Matches(
				new Regex(@"^\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}$"),
				ts);
		}

		[Fact]
		public void NewName_FreshTimestampHasMatchesShape()
		{
			// The two functions form a contract: NewName produces names
			// that Matches accepts.
			var ts = SnapshotFolder.NewName(debugDisableCrawl: false, debugTimeStamp: "");

			Assert.True(SnapshotFolder.Matches(ts));
		}

		// ── NewName — debug, explicit timestamp ────────────────────────

		[Fact]
		public void NewName_DebugSession_ReturnsExplicitTimestamp()
		{
			// When debugDisableCrawl is true and debugTimeStamp is not "latest",
			// return the explicit value verbatim.
			var ts = SnapshotFolder.NewName(
				debugDisableCrawl: true,
				debugTimeStamp: "2026-01-15-12-00-00");

			Assert.Equal("2026-01-15-12-00-00", ts);
		}

		// ── NewName — "latest" resolution ──────────────────────────────

		[Fact]
		public void NewName_LatestPicksMostRecentTimestampFolder()
		{
			// Create three timestamp-named folders. The function picks by
			// CreationTimeUtc (which we can't easily fake) — but ordering by
			// creation time should match the order we create them in.
			Directory.CreateDirectory(Path.Combine(_tempDir, "2024-01-01-00-00-00"));
			Thread.Sleep(20);
			Directory.CreateDirectory(Path.Combine(_tempDir, "2024-06-15-12-30-45"));
			Thread.Sleep(20);
			Directory.CreateDirectory(Path.Combine(_tempDir, "2024-12-31-23-59-59"));

			var ts = SnapshotFolder.NewName(
				debugDisableCrawl: true,
				debugTimeStamp: "latest",
				sessionParentDirectory: _tempDir);

			Assert.Equal("2024-12-31-23-59-59", ts);
		}

		[Fact]
		public void NewName_LatestIgnoresNonTimestampFolders()
		{
			// A folder named "scratch" coexists with timestamp folders; the
			// Matches filter must skip it.
			Directory.CreateDirectory(Path.Combine(_tempDir, "scratch"));
			Thread.Sleep(20);
			Directory.CreateDirectory(Path.Combine(_tempDir, "2024-01-01-00-00-00"));

			var ts = SnapshotFolder.NewName(
				debugDisableCrawl: true,
				debugTimeStamp: "latest",
				sessionParentDirectory: _tempDir);

			Assert.Equal("2024-01-01-00-00-00", ts);
		}

		[Fact]
		public void NewName_LatestWithEmptyDir_FallsBackToFreshTimestamp()
		{
			// Empty session parent → no valid snapshots → warning logged, fresh
			// timestamp returned.
			var ts = SnapshotFolder.NewName(
				debugDisableCrawl: true,
				debugTimeStamp: "latest",
				sessionParentDirectory: _tempDir);

			Assert.Matches(
				new Regex(@"^\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}$"),
				ts);
		}

		[Fact]
		public void NewName_LatestWithMissingDir_FallsBackToExplicitValue()
		{
			// sessionParentDirectory does not exist → the "latest" branch is
			// skipped entirely (the Directory.Exists guard fails) and the
			// function falls through to "return debugTimeStamp". So the literal
			// string "latest" is what comes back.
			var ts = SnapshotFolder.NewName(
				debugDisableCrawl: true,
				debugTimeStamp: "latest",
				sessionParentDirectory: Path.Combine(_tempDir, "missing-subdir"));

			Assert.Equal("latest", ts);
		}

		[Fact]
		public void NewName_LatestIsCaseInsensitive()
		{
			Directory.CreateDirectory(Path.Combine(_tempDir, "2024-01-01-00-00-00"));

			var ts = SnapshotFolder.NewName(
				debugDisableCrawl: true,
				debugTimeStamp: "LATEST",
				sessionParentDirectory: _tempDir);

			Assert.Equal("2024-01-01-00-00-00", ts);
		}
	}
}
