namespace Crawler
{
	using System.Text;

	/// <summary>
	/// Lock-aware file I/O helpers extracted from Tools. Every write/delete here
	/// retries when the target is locked by another process (typically the operator
	/// has the file open in an editor or compare tool), with a two-mode policy:
	/// interactive prompts close-and-retry / skip; silent retries with backoff then
	/// logs and gives up. Depends only on Logger, ConsoleUi and CrawlerContext.
	/// </summary>
	public static class FileIo
	{
		public static void ClearLogs(params string[] logPaths)
		{
			foreach (var logPath in logPaths)
			{
				// Retry-on-lock so a debug replay does not crash when the
				// operator has a per-run log open. Skip is benign — the log is
				// rewritten later this run.
				DeleteWithRetry(logPath, Path.GetFileName(logPath));
			}
		}

		/// <summary>
		/// Writes <paramref name="lines"/> to <paramref name="path"/> as UTF-8 (overwrite),
		/// retrying when the file is locked by another process (typically: operator has the
		/// file open in an editor or compare tool for inspection).
		///
		/// Interactive mode (CrawlerContext.Silent == false): on IOException, prompts
		/// the operator to close the file and press Enter to retry, or [S] to skip.
		/// Loops until the write succeeds or the operator skips.
		///
		/// Silent mode (CrawlerContext.Silent == true): retries up to 3 times with
		/// short backoff (500 ms), then logs the final error and returns false.
		/// Callers that depend on the write succeeding should check the return value.
		///
		/// Non-IOException errors propagate up — they are not lock-related and the
		/// caller's existing error handling should see them.
		///
		/// Previously, log writers swallowed lock errors silently via catch-all blocks,
		/// producing stale/empty output without any visible warning to the operator.
		/// This helper makes lock conditions recoverable in interactive mode and
		/// observably-failed in silent mode.
		///
		/// This and its append/text siblings (<see cref="AppendAllLinesWithRetry"/>,
		/// <see cref="WriteAllTextWithRetry"/>) share one retry core (<see cref="WriteWithRetry"/>);
		/// only the inner write primitive differs. The append sibling is REQUIRED for
		/// <see cref="IssueLogWriter.Append"/>/<see cref="IssueLogWriter.AppendMany"/> —
		/// routing those through the overwrite helper would replace, not append, and
		/// silently destroy already-written records (e.g. log 10 is overwritten by the
		/// content-quality pass, then appended to by the translation-issue pass).
		/// </summary>
		/// <param name="path">Target file path.</param>
		/// <param name="lines">Lines to write.</param>
		/// <param name="displayName">
		/// Human-readable name shown in retry prompts and log messages.
		/// e.g. "02-crawler-index.log".
		/// </param>
		/// <returns>
		/// True if the write completed successfully. False if the operator skipped
		/// (interactive) or the silent retries were exhausted (silent).
		/// </returns>
		public static bool WriteAllLinesWithRetry(
			string path, IEnumerable<string> lines, string displayName)
		{
			// Materialise once — the enumerable could be a lazy LINQ chain that
			// would re-execute on each retry, in some cases producing different
			// results (e.g. ConcurrentBag enumeration order changes per call).
			// IDE0305 suppressed deliberately. The suggested collection
			// expression ([.. lines]) ALWAYS allocates a new copy; the
			// `as IList<string> ?? ToList()` form skips the copy when the source
			// is already a list. These helpers run on every log write — the
			// "simplification" would add an allocation per call. Tidiness < perf here.
#pragma warning disable IDE0305
			var materialised = lines as IList<string> ?? lines.ToList();
#pragma warning restore IDE0305
			return WriteWithRetry(
				() => File.WriteAllLines(path, materialised, Encoding.UTF8),
				displayName);
		}

		/// <summary>
		/// Appends <paramref name="lines"/> to <paramref name="path"/> as UTF-8, retrying
		/// on lock with the same two-mode policy as <see cref="WriteAllLinesWithRetry"/>.
		///
		/// Append twin of the overwrite helper — wraps <see cref="File.AppendAllLines(string, IEnumerable{string}, Encoding)"/>
		/// rather than WriteAllLines, so existing file content is preserved and added to.
		/// Like the framework method, the UTF-8 BOM is emitted only when the file is new
		/// or empty; appends to a non-empty file add no further BOM. Introduced
		/// to let <see cref="IssueLogWriter.Append"/>/<see cref="IssueLogWriter.AppendMany"/>
		/// gain retry-on-lock without changing their append semantics.
		/// </summary>
		/// <returns>True on success; false on operator-skip / silent give-up.</returns>
		public static bool AppendAllLinesWithRetry(
			string path, IEnumerable<string> lines, string displayName)
		{
			// Materialise once — same rationale as WriteAllLinesWithRetry.
			// IDE0305 suppressed — see the note there ([.. lines] would
			// force an allocation the `?? ToList()` form avoids).
#pragma warning disable IDE0305
			var materialised = lines as IList<string> ?? lines.ToList();
#pragma warning restore IDE0305
			return WriteWithRetry(
				() => File.AppendAllLines(path, materialised, Encoding.UTF8),
				displayName);
		}

		/// <summary>
		/// Writes <paramref name="contents"/> to <paramref name="path"/> as UTF-8 (overwrite),
		/// retrying on lock with the same two-mode policy as <see cref="WriteAllLinesWithRetry"/>.
		///
		/// Single-string twin of the overwrite helper — wraps <see cref="File.WriteAllText(string, string?, Encoding)"/>
		/// for writers that build the whole file body as one pre-composed string
		/// (sitemap XML, the spell-error ticket-text file).
		/// </summary>
		/// <returns>True on success; false on operator-skip / silent give-up.</returns>
		public static bool WriteAllTextWithRetry(
			string path, string contents, string displayName)
		{
			// No materialisation needed — the payload is already a fully-formed string.
			return WriteWithRetry(
				() => File.WriteAllText(path, contents, Encoding.UTF8),
				displayName);
		}

		/// <summary>
		/// Shared retry core for the file-write helpers above. Runs <paramref name="write"/>,
		/// catching only IOException (the lock signature). Interactive: prompt close-and-retry
		/// or [S] to skip, looping until success or skip. Silent: 3 attempts × 500 ms backoff,
		/// then log and give up. Non-IOException errors propagate to the caller's existing
		/// handling. <paramref name="write"/> must be idempotent across attempts — callers
		/// materialise lazy sources once before passing the action in.
		/// </summary>
		private static bool WriteWithRetry(Action write, string displayName)
		{
			if (CrawlerContext.Silent)
			{
				const int maxAttempts = 3;
				const int backoffMs = 500;
				for (int attempt = 1; attempt <= maxAttempts; attempt++)
				{
					try
					{
						write();
						return true;
					}
					catch (IOException ex) when (attempt < maxAttempts)
					{
						Logger.LogWarning(
							$"WriteWithRetry: {displayName} locked (attempt {attempt}/{maxAttempts}) — {ex.Message}");
						Thread.Sleep(backoffMs);
					}
					catch (IOException ex)
					{
						Logger.LogError(
							$"WriteWithRetry: {displayName} still locked after {maxAttempts} attempts — {ex.Message}");
						return false;
					}
				}
				return false; // unreachable but keeps compiler happy
			}

			// Interactive — prompt operator on each failure.
			while (true)
			{
				try
				{
					write();
					return true;
				}
				catch (IOException ex)
				{
					ConsoleUi.WriteBlank();
					ConsoleUi.WriteActionRequired($"Cannot write to {displayName} — file may be open in an editor.");
					ConsoleUi.WriteActionRequired($"({ex.Message})");
					ConsoleUi.WriteActionRequired("Please close the file and press Enter to retry, or [S] to skip.");
					var input = ConsoleUi.ReadLine("> ").ToUpperInvariant();
					if (input == "S")
					{
						Logger.LogWarning($"WriteWithRetry: operator skipped write to {displayName}.");
						return false;
					}
				}
			}
		}

		/// <summary>
		/// Deletes <paramref name="path"/> if it exists, retrying on lock with the
		/// same two-mode policy as <see cref="WriteWithRetry"/>. Introduced
		/// because debug-replay log clearing (<see cref="ClearLogs"/>) crashed the
		/// run with an unhandled IOException when an operator had a per-run log open
		/// during an [L] replay — the same locked-file scenario the write gate
		/// guards against, but at the delete step that runs *before* any write.
		///
		/// Skip/give-up is benign here: every cleared log is unconditionally
		/// rewritten later in the same run, so a surviving stale file is overwritten
		/// regardless. The retry therefore favours continuing over aborting — silent
		/// give-up and operator-skip both log and return false, and callers continue.
		///
		/// A missing file is a no-op success (true) — nothing to delete.
		/// </summary>
		/// <returns>True if deleted or already absent; false on skip / silent give-up.</returns>
		private static bool DeleteWithRetry(string path, string displayName)
		{
			if (!File.Exists(path))
			{
				return true; // nothing to delete
			}

			if (CrawlerContext.Silent)
			{
				const int maxAttempts = 3;
				const int backoffMs = 500;
				for (int attempt = 1; attempt <= maxAttempts; attempt++)
				{
					try
					{
						File.Delete(path);
						return true;
					}
					catch (IOException ex) when (attempt < maxAttempts)
					{
						Logger.LogWarning(
							$"DeleteWithRetry: {displayName} locked (attempt {attempt}/{maxAttempts}) — {ex.Message}");
						Thread.Sleep(backoffMs);
					}
					catch (IOException ex)
					{
						// Benign: the file is overwritten later this run regardless.
						Logger.LogWarning(
							$"DeleteWithRetry: {displayName} still locked after {maxAttempts} attempts — " +
							$"skipping delete (will be overwritten later). {ex.Message}");
						return false;
					}
				}
				return false; // unreachable but keeps compiler happy
			}

			// Interactive — prompt operator on each failure.
			while (true)
			{
				try
				{
					File.Delete(path);
					return true;
				}
				catch (IOException ex)
				{
					ConsoleUi.WriteBlank();
					ConsoleUi.WriteActionRequired($"Cannot delete {displayName} — file may be open in an editor.");
					ConsoleUi.WriteActionRequired($"({ex.Message})");
					ConsoleUi.WriteActionRequired("Please close the file and press Enter to retry, or [S] to skip.");
					var input = ConsoleUi.ReadLine("> ").ToUpperInvariant();
					if (input == "S")
					{
						Logger.LogWarning($"DeleteWithRetry: operator skipped delete of {displayName} " +
							"(will be overwritten later).");
						return false;
					}
				}
			}
		}

		// ── CSV file guard ──────────────────────────────────────────────────────
		// Attempts to read all lines from a CSV file that may be locked (e.g. open
		// in Excel). Silent mode: retries up to 3 times then logs and returns false.
		// Console mode: prompts the user to retry or skip.
		public static bool TryReadCsvLines(
			string filePath,
			bool silent,
			out IEnumerable<string> lines,
			Encoding? encoding = null)
		{
			const int MaxSilentRetries = 3;
			const int RetryDelayMs = 2000;
			var enc = encoding ?? Encoding.UTF8;

			for (int attempt = 1; ; attempt++)
			{
				try
				{
					// Read all lines into memory so the file handle is released immediately.
					lines = File.ReadAllLines(filePath, enc);
					return true;
				}
				catch (IOException ex)
				{
					var msg = $"CSV file locked: {filePath} — {ex.Message}";

					if (silent)
					{
						if (attempt >= MaxSilentRetries)
						{
							Logger.LogError($"{msg} — skipping CSV-dependent features.");
							lines = [];
							return false;
						}
						Logger.LogWarning($"{msg} — retrying ({attempt}/{MaxSilentRetries})...");
						Thread.Sleep(RetryDelayMs);
					}
					else
					{
						ConsoleUi.WriteBlank();
						ConsoleUi.WriteWarning($"⚠  {msg}");
						var key = ConsoleUi.ReadLine("File busy — Retry? [Y/N]: ").ToUpperInvariant();
						if (key != "Y")
						{
							Logger.LogWarning("CSV skipped by user — CSV-dependent features will be omitted.");
							lines = [];
							return false;
						}
					}
				}
			}
		}

		public static void WriteList(string filePath, List<string> lines)
		{
			using StreamWriter writer = new(filePath);
			foreach (var line in lines)
			{
				writer.WriteLine(line);
			}
		}
	}
}
