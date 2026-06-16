using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for the #335 delete-retry gate: FileIo.ClearLogs now routes
	/// File.Delete through DeleteWithRetry,
	/// so a debug-replay log clear does not crash when an operator has a per-run
	/// log open (the locked-file scenario the #334 write gate guards against, but
	/// at the delete step that runs before any write).
	///
	/// DeleteWithRetry is private; these tests exercise it through the public
	/// callers. Silent mode (3×500ms then log-and-continue) is what runs
	/// unattended and is what these tests cover; the interactive prompt path is
	/// validated by operator workflow.
	///
	/// Key property under test: a locked file does NOT throw out of ClearLogs —
	/// the run continues. (Pre-#335 this was an unhandled IOException.)
	/// </summary>
	[Collection("Logger")]
	public class FileIoClearLogsWithRetryTests : IDisposable
	{
		private readonly string _tempLogFile;
		private readonly bool _originalSilent;

		public FileIoClearLogsWithRetryTests()
		{
			_tempLogFile = Path.Combine(Path.GetTempPath(), $"clearlogs-test-{Guid.NewGuid()}.log");
			Logger.Initialize(_tempLogFile, silent: true);

			_originalSilent = CrawlerContext.Silent;
			CrawlerContext.Silent = true; // silent mode for determinism
		}

		public void Dispose()
		{
			CrawlerContext.Silent = _originalSilent;
			if (File.Exists(_tempLogFile))
			{
				File.Delete(_tempLogFile);
			}
		}

		// ── ClearLogs: happy path ───────────────────────────────────────────

		[Fact]
		public void ClearLogs_ExistingFiles_DeletesThem()
		{
			var a = Path.Combine(Path.GetTempPath(), $"clear-a-{Guid.NewGuid()}.log");
			var b = Path.Combine(Path.GetTempPath(), $"clear-b-{Guid.NewGuid()}.log");
			File.WriteAllText(a, "x");
			File.WriteAllText(b, "y");

			try
			{
				FileIo.ClearLogs(a, b);

				Assert.False(File.Exists(a));
				Assert.False(File.Exists(b));
			}
			finally
			{
				if (File.Exists(a))
				{
					File.Delete(a);
				}

				if (File.Exists(b))
				{
					File.Delete(b);
				}
			}
		}

		[Fact]
		public void ClearLogs_MissingFile_NoThrow()
		{
			// A path that does not exist is a no-op — must not throw.
			var missing = Path.Combine(Path.GetTempPath(), $"clear-missing-{Guid.NewGuid()}.log");
			var ex = Record.Exception(() => FileIo.ClearLogs(missing));
			Assert.Null(ex);
		}

		// ── ClearLogs: locked file (the crash this fileset fixes) ───────────

		[Fact]
		public void ClearLogs_SilentMode_LockedFile_DoesNotThrow_FileSurvives()
		{
			// THE regression guard: pre-#335 this threw an unhandled IOException
			// out of ClearLogs and aborted the run. Now it must retry, give up
			// silently after 3 attempts, log, and return without throwing. The
			// locked file survives (and would be overwritten later in a real run).
			var path = Path.Combine(Path.GetTempPath(), $"clear-locked-{Guid.NewGuid()}.log");
			File.WriteAllText(path, "held open");

			try
			{
				using (var lockHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
				{
					var ex = Record.Exception(() => FileIo.ClearLogs(path));
					Assert.Null(ex); // no crash
				}

				// File still present (delete was skipped, not crashed).
				Assert.True(File.Exists(path));
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
		public void ClearLogs_SilentMode_LockedAndUnlocked_Mixed_DeletesUnlockedAndContinues()
		{
			// A locked file in the middle must not prevent later files clearing —
			// the loop continues past a skipped delete.
			var unlocked1 = Path.Combine(Path.GetTempPath(), $"clear-u1-{Guid.NewGuid()}.log");
			var locked = Path.Combine(Path.GetTempPath(), $"clear-lk-{Guid.NewGuid()}.log");
			var unlocked2 = Path.Combine(Path.GetTempPath(), $"clear-u2-{Guid.NewGuid()}.log");
			File.WriteAllText(unlocked1, "1");
			File.WriteAllText(locked, "2");
			File.WriteAllText(unlocked2, "3");

			try
			{
				using (var lockHandle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
				{
					FileIo.ClearLogs(unlocked1, locked, unlocked2);
				}

				Assert.False(File.Exists(unlocked1)); // cleared
				Assert.True(File.Exists(locked));     // skipped (was locked)
				Assert.False(File.Exists(unlocked2)); // cleared despite mid-list lock
			}
			finally
			{
				foreach (var p in new[] { unlocked1, locked, unlocked2 })
				{
					if (File.Exists(p))
					{
						File.Delete(p);
					}
				}
			}
		}

		[Fact]
		public async Task ClearLogs_SilentMode_LockReleasesMidRetry_DeletesSuccessfully()
		{
			var path = Path.Combine(Path.GetTempPath(), $"clear-released-{Guid.NewGuid()}.log");
			File.WriteAllText(path, "briefly held");

			try
			{
				FileStream? lockHandle = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

				// Release between attempt 1 and 2 (backoff is 500ms).
				var releaseTask = Task.Run(async () =>
				{
					await Task.Delay(600);
					lockHandle.Dispose();
					lockHandle = null;
				});

				FileIo.ClearLogs(path);

				await releaseTask;

				Assert.False(File.Exists(path)); // delete succeeded on a later attempt
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

	}
}
