using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for FileIo.AppendAllLinesWithRetry and FileIo.WriteAllTextWithRetry,
	/// the append and single-string siblings of WriteAllLinesWithRetry introduced
	/// in fileset #334.
	///
	/// All three share one retry core (FileIo.WriteWithRetry); only the inner write
	/// primitive differs. These tests focus on the two behaviours that the shared
	/// core does NOT already guarantee and that a wrong helper choice would break:
	///
	///   1. AppendAllLinesWithRetry must APPEND, not overwrite. Routing an append
	///      caller through the overwrite helper would silently destroy already-
	///      written records (the real hazard: log 10 is overwritten by the
	///      content-quality pass, then appended to by the translation-issue pass).
	///
	///   2. WriteAllTextWithRetry must write the whole string body verbatim.
	///
	/// Lock/retry semantics (silent 3×500ms give-up, return-false-on-persistent-lock,
	/// original-content-preserved-on-failed-write) are validated in parallel with the
	/// existing FileIoWriteAllLinesWithRetryTests; the same core path is exercised
	/// here once per sibling to confirm the wiring, not re-exhaustively.
	///
	/// As with the sibling suite, tests run in silent mode for determinism — the
	/// interactive prompt path requires console mocking and is exercised in real
	/// operator workflows.
	/// </summary>
	[Collection("Logger")]
	public class FileIoAppendAndTextWithRetryTests : IDisposable
	{
		private readonly string _tempLogFile;
		private readonly bool _originalSilent;

		public FileIoAppendAndTextWithRetryTests()
		{
			_tempLogFile = Path.Combine(Path.GetTempPath(), $"appendtext-test-{Guid.NewGuid()}.log");
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

		// ── AppendAllLinesWithRetry: append semantics (the data-loss guard) ──

		[Fact]
		public void AppendAllLinesWithRetry_NewFile_WritesAndReturnsTrue()
		{
			var path = Path.Combine(Path.GetTempPath(), $"append-new-{Guid.NewGuid()}.txt");
			try
			{
				var result = FileIo.AppendAllLinesWithRetry(path, ["alpha", "beta"], "test.txt");

				Assert.True(result);
				Assert.True(File.Exists(path));
				Assert.Equal(new[] { "alpha", "beta" }, File.ReadAllLines(path));
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
		public void AppendAllLinesWithRetry_ExistingFile_AppendsDoesNotOverwrite()
		{
			// THE key test: append must preserve prior content. If this helper were
			// (mis)wired to File.WriteAllLines, "first" / "second" would be replaced
			// by "third" / "fourth" and this assertion would catch it.
			var path = Path.Combine(Path.GetTempPath(), $"append-existing-{Guid.NewGuid()}.txt");
			try
			{
				File.WriteAllLines(path, ["first", "second"]);

				var result = FileIo.AppendAllLinesWithRetry(path, ["third", "fourth"], "test.txt");

				Assert.True(result);
				Assert.Equal(
					new[] { "first", "second", "third", "fourth" },
					File.ReadAllLines(path));
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
		public void AppendAllLinesWithRetry_SilentMode_PersistentLock_ReturnsFalse_PreservesContent()
		{
			var path = Path.Combine(Path.GetTempPath(), $"append-locked-{Guid.NewGuid()}.txt");
			File.WriteAllLines(path, ["keep me"]);

			try
			{
				using (var lockHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
				{
					var result = FileIo.AppendAllLinesWithRetry(path, ["should not appear"], "test.txt");
					Assert.False(result);
				}

				// After lock released: original content intact, no partial append.
				Assert.Equal(new[] { "keep me" }, File.ReadAllLines(path));
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
		public void AppendAllLinesWithRetry_MaterialisesLazyEnumerable_BeforeFirstAttempt()
		{
			var path = Path.Combine(Path.GetTempPath(), $"append-lazy-{Guid.NewGuid()}.txt");
			int materialisationCount = 0;
			IEnumerable<string> CountedEnumerable()
			{
				materialisationCount++;
				yield return "x";
				yield return "y";
			}
			try
			{
				var result = FileIo.AppendAllLinesWithRetry(path, CountedEnumerable(), "test.txt");

				Assert.True(result);
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

		[Fact]
		public async Task AppendAllLinesWithRetry_SilentMode_LockReleasesMidRetry_AppendsSuccessfully()
		{
			var path = Path.Combine(Path.GetTempPath(), $"append-released-{Guid.NewGuid()}.txt");
			File.WriteAllLines(path, ["existing"]);

			try
			{
				FileStream? lockHandle = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

				// Release between attempt 1 and attempt 2 (backoff is 500ms).
				var releaseTask = Task.Run(async () =>
				{
					await Task.Delay(600);
					lockHandle.Dispose();
					lockHandle = null;
				});

				var result = FileIo.AppendAllLinesWithRetry(path, ["added"], "test.txt");

				await releaseTask;

				Assert.True(result);
				Assert.Equal(new[] { "existing", "added" }, File.ReadAllLines(path));

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

		// ── WriteAllTextWithRetry: verbatim single-string write ─────────────

		[Fact]
		public void WriteAllTextWithRetry_NewFile_WritesVerbatimAndReturnsTrue()
		{
			var path = Path.Combine(Path.GetTempPath(), $"text-new-{Guid.NewGuid()}.txt");
			try
			{
				const string body = "line one\nline two\n";
				var result = FileIo.WriteAllTextWithRetry(path, body, "test.txt");

				Assert.True(result);
				Assert.Equal(body, File.ReadAllText(path));
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
		public void WriteAllTextWithRetry_ExistingFile_OverwritesWholeBody()
		{
			var path = Path.Combine(Path.GetTempPath(), $"text-overwrite-{Guid.NewGuid()}.txt");
			try
			{
				File.WriteAllText(path, "stale and longer content");

				var result = FileIo.WriteAllTextWithRetry(path, "fresh", "test.txt");

				Assert.True(result);
				Assert.Equal("fresh", File.ReadAllText(path));
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
		public void WriteAllTextWithRetry_EmptyString_CreatesEmptyFile()
		{
			// Mirrors the TicketText "no entries" early-out, which writes
			// string.Empty to truncate the file to zero bytes.
			var path = Path.Combine(Path.GetTempPath(), $"text-empty-{Guid.NewGuid()}.txt");
			try
			{
				var result = FileIo.WriteAllTextWithRetry(path, string.Empty, "test.txt");

				Assert.True(result);
				Assert.True(File.Exists(path));
				Assert.Equal(string.Empty, File.ReadAllText(path));
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
		public void WriteAllTextWithRetry_SilentMode_PersistentLock_ReturnsFalse_PreservesContent()
		{
			var path = Path.Combine(Path.GetTempPath(), $"text-locked-{Guid.NewGuid()}.txt");
			File.WriteAllText(path, "original content");

			try
			{
				using (var lockHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
				{
					var result = FileIo.WriteAllTextWithRetry(path, "overwrite attempt", "test.txt");
					Assert.False(result);
				}

				Assert.Equal("original content", File.ReadAllText(path));
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
