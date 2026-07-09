using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for FileIo.WriteAllLinesWithRetry, introduced in fileset #295.
	///
	/// The helper handles the locked-file scenario uniformly across log writers:
	/// - Interactive mode: prompts the operator to close the file and retry, or skip.
	/// - Silent mode: retries up to 3 times with 500ms backoff, then logs and fails.
	///
	/// Tests focus on silent mode since interactive mode requires console mocking.
	/// The interactive prompt path is exercised in real operator workflows; the
	/// silent path is what runs unattended and most needs regression protection.
	/// </summary>
	[Collection("Logger")]
	public class FileIoWriteAllLinesWithRetryTests : IDisposable
	{
		private readonly string _tempLogFile;
		private readonly bool _originalSilent;

		public FileIoWriteAllLinesWithRetryTests()
		{
			_tempLogFile = Path.Combine(Path.GetTempPath(), $"writeretry-test-{Guid.NewGuid()}.log");
			Logger.Initialize(_tempLogFile, silent: true);

			_originalSilent = CrawlerContext.Silent;
			CrawlerContext.Silent = true; // run all tests in silent mode for determinism
		}

		public void Dispose()
		{
			CrawlerContext.Silent = _originalSilent;
			if (File.Exists(_tempLogFile))
			{
				File.Delete(_tempLogFile);
			}
		}

		// ── Happy path ──────────────────────────────────────────────────

		[Fact]
		public void WriteAllLinesWithRetry_UnlockedFile_WritesAndReturnsTrue()
		{
			var path = Path.Combine(Path.GetTempPath(), $"writeretry-happy-{Guid.NewGuid()}.txt");
			try
			{
				var result = FileIo.WriteAllLinesWithRetry(path, ["alpha", "beta", "gamma"], "test.txt");

				Assert.True(result);
				Assert.True(File.Exists(path));
				var lines = File.ReadAllLines(path);
				Assert.Equal(new[] { "alpha", "beta", "gamma" }, lines);
			}
			finally
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
		}

		[Fact]
		public void WriteAllLinesWithRetry_OverwritesExistingFile()
		{
			var path = Path.Combine(Path.GetTempPath(), $"writeretry-overwrite-{Guid.NewGuid()}.txt");
			try
			{
				File.WriteAllText(path, "old content");

				var result = FileIo.WriteAllLinesWithRetry(path, ["new content"], "test.txt");

				Assert.True(result);
				Assert.Equal("new content", File.ReadAllLines(path)[0]);
			}
			finally
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
		}

		// ── Silent mode + persistent lock ────────────────────────────────

		[Fact]
		public void WriteAllLinesWithRetry_SilentMode_PersistentLock_ReturnsFalse()
		{
			// Setup: open the file with an exclusive lock that the helper cannot
			// acquire. The helper should retry 3 times (1.5s of backoff) then
			// give up cleanly and return false. The pre-existing file content
			// must remain intact.
			var path = Path.Combine(Path.GetTempPath(), $"writeretry-locked-{Guid.NewGuid()}.txt");
			File.WriteAllText(path, "locked content remains");

			try
			{
				// Open with exclusive write lock — File.WriteAllLines from another
				// thread will fail with IOException until this handle closes.
				using var lockHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

				var result = FileIo.WriteAllLinesWithRetry(path, ["should not appear"], "test.txt");

				Assert.False(result);
				// File handle still open here — content unchanged from before the call.
			}
			finally
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
		}

		[Fact]
		public void WriteAllLinesWithRetry_SilentMode_PersistentLock_PreservesOriginalContent()
		{
			var path = Path.Combine(Path.GetTempPath(), $"writeretry-preserved-{Guid.NewGuid()}.txt");
			File.WriteAllText(path, "original content");

			try
			{
				using (var lockHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
				{
					FileIo.WriteAllLinesWithRetry(path, ["overwrite attempt"], "test.txt");
				}

				// After lock released, the original content should still be there
				// (the failed write must not have corrupted the file).
				var content = File.ReadAllText(path);
				Assert.Equal("original content", content);
			}
			finally
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
		}

		// ── Silent mode + lock that releases mid-retry ───────────────────

		[Fact]
		public async Task WriteAllLinesWithRetry_SilentMode_LockReleasesMidRetry_WritesSuccessfully()
		{
			// Setup: hold a lock briefly, release before retries are exhausted.
			// The helper should observe the file unlocked on retry 2 or 3 and
			// complete the write successfully.
			var path = Path.Combine(Path.GetTempPath(), $"writeretry-released-{Guid.NewGuid()}.txt");
			File.WriteAllText(path, "old");

			try
			{
				FileStream? lockHandle = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

				// Schedule unlock on a background thread after ~600ms (between
				// attempt 1 and attempt 2 — backoff is 500ms).
				var releaseTask = Task.Run(async () =>
				{
					await Task.Delay(600);
					lockHandle.Dispose();
					lockHandle = null;
				});

				var result = FileIo.WriteAllLinesWithRetry(path, ["new content"], "test.txt");

				await releaseTask;

				Assert.True(result);
				Assert.Equal("new content", File.ReadAllLines(path)[0]);

				lockHandle?.Dispose();
			}
			finally
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
		}

		// ── Materialisation: same content written each retry attempt ────

		[Fact]
		public void WriteAllLinesWithRetry_MaterialisesLazyEnumerable_BeforeFirstAttempt()
		{
			// The helper should materialise its input enumerable once before the
			// retry loop, so a lazy LINQ chain doesn't re-execute on each retry
			// (which could yield different results, e.g. parallel non-determinism).
			var path = Path.Combine(Path.GetTempPath(), $"writeretry-lazy-{Guid.NewGuid()}.txt");
			int materialisationCount = 0;
			IEnumerable<string> CountedEnumerable()
			{
				materialisationCount++;
				yield return "line1";
				yield return "line2";
			}
			try
			{
				var result = FileIo.WriteAllLinesWithRetry(path, CountedEnumerable(), "test.txt");

				Assert.True(result);
				// Materialised exactly once.
				Assert.Equal(1, materialisationCount);
			}
			finally
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
		}

		// ── Empty input ─────────────────────────────────────────────────

		[Fact]
		public void WriteAllLinesWithRetry_EmptyInput_CreatesEmptyFile()
		{
			var path = Path.Combine(Path.GetTempPath(), $"writeretry-empty-{Guid.NewGuid()}.txt");
			try
			{
				var result = FileIo.WriteAllLinesWithRetry(path, [], "test.txt");

				Assert.True(result);
				Assert.True(File.Exists(path));
				Assert.Empty(File.ReadAllLines(path));
			}
			finally
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
		}
	}
}
