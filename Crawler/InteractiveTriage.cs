namespace Crawler
{
	// ── InteractiveTriage ─────────────────────────────────────────────────────
	//
	// Centralised home for interactive prompt flows that were previously
	// scattered through Program.RunAsync. Every method here assumes it is
	// being called in interactive mode — silent-vs-interactive dispatch is the
	// caller's responsibility, so these methods never branch on
	// CrawlerContext.Silent themselves. This keeps "InteractiveTriage" honest
	// about its name and keeps Program.cs as flow-control only.
	//
	// Methods that mutate run state take a CrawlerRunContext; methods that
	// only return a decision take plain parameters.
	// ─────────────────────────────────────────────────────────────────────────

	internal static class InteractiveTriage
	{
		// ── Snapshot choice ───────────────────────────────────────────────────

		/// <summary>
		/// Outcome of the snapshot prompt — three mutually exclusive results.
		/// </summary>
		internal enum SnapshotChoice
		{
			/// <summary>User picked [N] — proceed with a fresh crawl.</summary>
			NewCrawl,
			/// <summary>User picked [L] — replay the most recent snapshot. Context paths have been rebuilt.</summary>
			ReplayLatest,
			/// <summary>User picked any other key — abort the run.</summary>
			Abort,
		}

		/// <summary>
		/// Shows the most-recent-snapshot info and prompts [N]/[L]/abort.
		/// On [L]: verifies snapshot integrity, flips IsDebugSession on, and
		/// rebuilds all timestamp-dependent paths in <paramref name="ctx"/> to
		/// point at the chosen snapshot. Returns Abort if the integrity check
		/// dialog was aborted by the user.
		/// On [N]: verifies previous snapshot integrity (the user may still
		/// abort there), then shows the crawl-start banner.
		/// Any other key → Abort.
		/// </summary>
		internal static SnapshotChoice PromptForSnapshotChoice(
			CrawlerRunContext ctx,
			DirectoryInfo mostRecent,
			string workingFolder,
			string urlDirectory,
			string url,
			string siteLabel,
			TimeSpan age,
			string ageDescription,
			bool isRecent)
		{
			var sessionParentDirectory = Path.Combine(workingFolder, urlDirectory);

			ConsoleKey choice;
			lock (Logger.ConsoleLock)
			{
				// [KEEP] Frame the snapshot info block in DarkCyan separators so it
				// stands out clearly when crawling multiple sites in sequence (e.g.
				// multi-language runs). Footer is emitted BEFORE ReadKey so the
				// frame is visually closed while the user is reading the prompt —
				// otherwise the closing separator only appears after the keypress,
				// leaving the section visually open during the wait.
				// The prompt sits below the closed frame as an "action below info"
				// dialog convention; this is intentional, not a layout bug.
				// WriteHeader() emits the leading blank line itself; trailing blank
				// kept after ReadKey for breathing room before whatever logs next.
				// The WARNING line stays DarkYellow (palette: DarkYellow =
				// warnings/notes); DarkCyan framing is the section-header role.
				ConsoleUi.WriteHeader("SNAPSHOT");
				// Site identity heading — anchors the prompt (and everything after it)
				// to the site being operated on, so an operator running several sites
				// in sequence is never left guessing which one this snapshot/crawl is
				// for. Shows "Name — Url" (matching the site picker line) when a site
				// was resolved, else the bare url.
				ConsoleUi.WriteEmphasis(siteLabel);
				if (isRecent)
				{
					ConsoleUi.WriteWarning($"WARNING: Last crawl snapshot for {url} is only {ageDescription} old.");
				}

				ConsoleUi.WriteLine($"Last snapshot : {mostRecent.Name} ({ageDescription} ago)");
				ConsoleUi.WriteFooter();
				choice = ConsoleUi.ReadKey("[N] New crawl   [L] Use latest snapshot   [any other key] Abort > ");
				ConsoleUi.WriteBlank();
			}

			if (choice == ConsoleKey.L)
			{
				// Check snapshot integrity before using it.
				if (!CheckSnapshotIntegrity(mostRecent, url))
				{
					return SnapshotChoice.Abort;
				}

				using (Logger.QuietConsole())
				{
					Logger.LogInfo($"User chose latest snapshot: {mostRecent.Name}.");
				}

				// Replay reuses the original crawl's file classification — it does
				// NOT re-evaluate HTML vs .unverified. Tell the operator so a changed
				// UnverifiedHtmlPolicy is not silently ignored: it takes effect on a new
				// crawl only.
				ConsoleUi.WriteInfo(
					"Replay uses the existing classification from the original crawl; " +
					"file types (HTML vs unverified) are not re-evaluated.");
				ConsoleUi.WriteInfo(
					"To apply a changed UnverifiedHtmlPolicy, run a new crawl.");

				// Offer clean-sweep — operator convenience for iterating on config
				// changes against a stable snapshot. Sweeps the snapshot folder
				// keeping only the crawl ground truth (`download/`, `00-crawler.log`,
				// `01-crawler.log`). Everything else (derived logs and
				// subtrees like base64assets/, etc.) is fully
				// re-derivable from those via replay.
				var snapshotPath = Path.Combine(workingFolder, urlDirectory, mostRecent.Name);
				if (PromptForCleanSweep())
				{
					using (Logger.QuietConsole())
					{
						CleanSweepSnapshot(snapshotPath);
					}
				}

				ApplyReplayLatestChoice(ctx, mostRecent.Name, snapshotPath);
				return SnapshotChoice.ReplayLatest;
			}

			if (choice == ConsoleKey.N)
			{
				// Check previous snapshot integrity before starting new crawl.
				if (!CheckSnapshotIntegrity(mostRecent, url))
				{
					return SnapshotChoice.Abort;
				}

				// Re-determine most recent after possible deletion.
				var mostRecentAfter = Directory
					.EnumerateDirectories(sessionParentDirectory)
					.Select(d => new DirectoryInfo(d))
					.Where(d => Snapshots.SnapshotFolder.Matches(d.Name))
					.OrderByDescending(d => d.CreationTimeUtc)
					.FirstOrDefault();

				// Gather previous crawl info for start banner.
				TimeSpan? prevDuration = null;
				if (mostRecentAfter != null)
				{
					var prevLog = Path.Combine(mostRecentAfter.FullName, LogFileNames.Crawler);
					var (_, dur, _) = Snapshots.CrawlLog.Analyse(prevLog, url);
					prevDuration = dur;
				}

				Logger.LogInfo($"User chose new crawl. Previous snapshot: {mostRecentAfter?.Name ?? "none"} ({ageDescription} ago).");

				ShowCrawlStartBanner(url, mostRecentAfter?.Name, prevDuration,
					age.TotalHours, mostRecentAfter != null);

				return SnapshotChoice.NewCrawl;
			}

			Logger.LogInfo("Crawl aborted by user.");
			return SnapshotChoice.Abort;
		}

		// ── Empty-download recovery ──────────────────────────────────────────

		/// <summary>
		/// Outcome of the empty-download prompt.
		/// </summary>
		internal enum EmptyDownloadChoice
		{
			/// <summary>User picked [N] — switch to a fresh crawl. Context paths have been rebuilt with a new timestamp.</summary>
			StartFreshCrawl,
			/// <summary>User picked any other key — abort the run.</summary>
			Abort,
		}

		/// <summary>
		/// Shown when a debug/replay session has an empty or missing download
		/// folder. On [N]: flips IsDebugSession off, computes a new timestamp,
		/// rebuilds all paths. Any other key → Abort.
		/// </summary>
		internal static EmptyDownloadChoice PromptForEmptyDownloadRecovery(
			CrawlerRunContext ctx,
			string workingFolder,
			string urlDirectory)
		{
			lock (Logger.ConsoleLock)
			{
				ConsoleUi.WriteBlank();
				ConsoleUi.WriteWarning($"WARNING: Download folder is empty or missing: {ctx.FileDownloadDirectory}");
				ConsoleUi.WriteLine("Analysis will produce no results without downloaded files.");
				var emptyChoice = ConsoleUi.ReadKey("[N] Start a fresh crawl   [any other key] Abort > ");
				ConsoleUi.WriteBlank();

				if (emptyChoice == ConsoleKey.N)
				{
					ctx.IsDebugSession = false;
					ctx.TimeStamp = Snapshots.SnapshotFolder.NewName(false, string.Empty,
						Path.Combine(workingFolder, urlDirectory));
					ctx.RebuildTimestampPaths(Path.Combine(workingFolder, urlDirectory, ctx.TimeStamp));
					Logger.LogInfo("Empty download folder — switching to fresh crawl.");
					return EmptyDownloadChoice.StartFreshCrawl;
				}

				Logger.LogInfo("Aborted — empty download folder.");
				return EmptyDownloadChoice.Abort;
			}
		}

		// ── Snapshot integrity (incomplete-crawl dialog) ─────────────────────

		/// <summary>
		/// Checks a snapshot for completeness. If incomplete, shows a dialog
		/// with size info and lets the user delete the snapshot, keep it, or
		/// abort. Returns false if the user chose to abort.
		/// Silent-mode equivalent: <see cref="Snapshots.CrawlLog.Summarise"/>
		/// returns the pure check, and the caller decides whether to warn
		/// and proceed.
		/// </summary>
		internal static bool CheckSnapshotIntegrity(DirectoryInfo snapshot, string baseUrl)
		{
			var (isComplete, fileCount, totalMb) = Snapshots.CrawlLog.Summarise(snapshot, baseUrl);
			if (isComplete)
			{
				return true;
			}

			var warning = $"Incomplete crawl detected: {snapshot.Name} — " +
				$"{fileCount:N0} files, {totalMb:F1} MB";
			Logger.LogWarning(warning);

			ConsoleUi.WriteBlank();
			ConsoleUi.WriteActionBlock("WARNING — Incomplete crawl detected",
				[$"Snapshot : {snapshot.FullName}",
				 $"Files    : {fileCount:N0} files",
				 $"Size     : {totalMb:F1} MB",
				  "Last entry: no \"completed\" marker found"]);

			// Prompt loop routed through ConsoleTriage.Ask. Invalid keys
			// re-prompt with a warning rather than falling through to the [K] Keep
			// branch. Choices line is rendered by Ask; previously it sat as the
			// last line inside the WriteActionBlock above and as a separate "> "
			// prompt.
			var key = ConsoleTriage.Ask(
				prompt: string.Empty,
				choices:
				[
					new ChoiceOption(ConsoleKey.D, "Delete entire snapshot"),
					new ChoiceOption(ConsoleKey.K, "Keep and continue"),
					new ChoiceOption(ConsoleKey.A, "Abort"),
				]);
			ConsoleUi.WriteBlank();

			if (key == ConsoleKey.A)
			{
				Logger.LogInfo("Aborted by user after incomplete snapshot warning.");
				return false;
			}

			if (key == ConsoleKey.D)
			{
				try
				{
					Directory.Delete(snapshot.FullName, recursive: true);
					Logger.LogWarning($"Deleted incomplete snapshot: {snapshot.Name} " +
						$"({fileCount:N0} files, {totalMb:F1} MB).");
					ConsoleUi.WriteSuccess("Snapshot deleted.");
					ConsoleUi.WriteBlank();
				}
				catch (Exception ex)
				{
					Logger.LogError($"Could not delete snapshot: {ex.Message}");
				}
			}
			else
			{
				// [K] Keep — only path reaching here now that Ask re-prompts on
				// invalid keys (pre-migration: any non-A, non-D key landed here).
				Logger.LogInfo($"User chose to keep incomplete snapshot: {snapshot.Name}.");
			}

			return true;
		}

		// ── Crawl start banner ───────────────────────────────────────────────

		/// <summary>
		/// Shows the crawl start banner with previous crawl info.
		/// Pure display, no prompt — lives here because only PromptForSnapshotChoice
		/// calls it and they share the same area of concern.
		/// </summary>
		internal static void ShowCrawlStartBanner(
			string baseUrl, string? previousSnapshotName, TimeSpan? previousDuration,
			double previousAgeHours, bool hadPreviousSnapshot)
		{
			ConsoleUi.WriteBlank();
			ConsoleUi.WriteHeader($"Crawling : {baseUrl}");
			ConsoleUi.WriteInfo($"Started  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			if (hadPreviousSnapshot && previousSnapshotName != null)
			{
				var ageStr = previousAgeHours < 1
					? $"{(int)(previousAgeHours * 60)} min ago"
					: $"{previousAgeHours:F1} hours ago";
				var durStr = previousDuration.HasValue
					? $"{(int)previousDuration.Value.TotalMinutes} min {previousDuration.Value.Seconds} sec"
					: "duration unknown";
				ConsoleUi.WriteInfo($"Last crawl: {ageStr} — took {durStr}");
			}
			else
			{
				ConsoleUi.WriteInfo("Last crawl: none found");
			}
			ConsoleUi.WriteFooter();
			ConsoleUi.WriteBlank();
		}

		// ── Orphan resolution (crawler index integrity) ──────────────────────

		/// <summary>
		/// Interactive [R]/[S]/[A]/[Q] loop for resolving orphan files
		/// detected by <see cref="Downloader.OrphanFiles.Detect"/>. Renames chosen files
		/// to .bak in place. Caller is responsible for the silent-mode path
		/// (log-and-skip) and for the empty-orphans early return.
		/// </summary>
		internal static void ResolveOrphans(
			List<string> orphans, string downloadDirectory, string bakExt)
		{
			bool renameAll = false;

			ConsoleUi.WriteBlank();
			ConsoleUi.WriteActionBlock(
				$"CRAWLER INDEX INTEGRITY: {orphans.Count} orphan file(s) found.",
				["These files have no URL entry in 02-crawler-index.log."]);
			ConsoleUi.WriteBlank();

			int renamed = 0;
			foreach (var filename in orphans)
			{
				if (renameAll)
				{
					Downloader.OrphanFiles.Quarantine(downloadDirectory, filename, bakExt);
					renamed++;
					Logger.LogWarning($"  Renamed (batch): {filename} → {filename}{".bak"}");
					continue;
				}

				ConsoleUi.WriteDivider();
				ConsoleUi.WriteLine($"Orphan file: {filename}");
				ConsoleUi.WriteLine("Likely cause: fragment URL download, manual placement, or partial crawl.");

				// Prompt loop routed through ConsoleTriage.Ask. Invalid
				// keys re-prompt with a warning rather than falling through to the
				// Skipped branch. Choices line is rendered by Ask; previously it
				// was duplicated in the upfront WriteActionBlock above and in the
				// per-orphan "> " prompt.
				var key = ConsoleTriage.Ask(
					prompt: string.Empty,
					choices:
					[
						new ChoiceOption(ConsoleKey.R, "Rename to .bak"),
						new ChoiceOption(ConsoleKey.S, "Skip"),
						new ChoiceOption(ConsoleKey.A, "Rename all"),
						new ChoiceOption(ConsoleKey.Q, "Stop"),
					]);
				ConsoleUi.WriteBlank();

				if (key == ConsoleKey.Q)
				{
					break;
				}

				if (key == ConsoleKey.A)
				{
					renameAll = true;
					Downloader.OrphanFiles.Quarantine(downloadDirectory, filename, bakExt);
					renamed++;
					Logger.LogWarning($"  Renamed: {filename} → {filename}{".bak"}");
				}
				else if (key == ConsoleKey.R)
				{
					Downloader.OrphanFiles.Quarantine(downloadDirectory, filename, bakExt);
					renamed++;
					Logger.LogWarning($"  Renamed: {filename} → {filename}{".bak"}");
				}
				else
				{
					// [S] Skip — only path reaching here now that Ask re-prompts on
					// invalid keys (pre-migration: any non-Q, non-A, non-R key landed here).
					Logger.LogInfo($"  Skipped: {filename}");
				}
			}

			ConsoleUi.WriteFooter();
			if (renamed > 0)
			{
				Logger.LogWarning($"  Integrity check complete: {renamed} file(s) renamed to {bakExt}. Re-crawl to resolve.");
			}
			else
			{
				Logger.LogInfo($"  Integrity check complete: all orphans skipped.");
			}

			ConsoleUi.WriteBlank();
		}

		// ── State-mutation helpers ───────────────────────────────────────────

		/// <summary>
		/// Applies the context mutations associated with the [L] "use latest
		/// snapshot" choice: flags the run as a debug/replay session, sets the
		/// timestamp to the chosen snapshot name, and rebuilds all 24 timestamp-
		/// dependent paths via <see cref="CrawlerRunContext.RebuildTimestampPaths"/>.
		/// </summary>
		internal static void ApplyReplayLatestChoice(
			CrawlerRunContext ctx,
			string snapshotName,
			string snapshotPath)
		{
			ctx.IsDebugSession = true;
			ctx.TimeStamp = snapshotName;
			ctx.RebuildTimestampPaths(snapshotPath);
		}

		/// <summary>
		/// Prompts the operator after [L] is chosen: "Clean sweep — delete all
		/// derived logs and keep only download/ + 00-crawler.log + 01-crawler.log?
		/// [Y/N]". Used to iterate on config changes against a stable snapshot
		/// without log accumulation from prior runs polluting the next pivot.
		///
		/// Returns true only on explicit 'Y'. Anything else (including Enter) is
		/// treated as no — destructive action requires explicit opt-in.
		/// </summary>
		internal static bool PromptForCleanSweep()
		{
			ConsoleUi.WriteHeader("CLEAN SWEEP");
			ConsoleUi.WriteLine("Delete all derived files in the snapshot folder,");
			ConsoleUi.WriteLine("keeping only `download/`, `00-crawler.log`, and `01-crawler.log`?");
			ConsoleUi.WriteLine("This gives a clean baseline for config-change testing. Everything");
			ConsoleUi.WriteLine("else is re-derivable from those on replay.");
			ConsoleUi.WriteFooter();
			var key = ConsoleUi.ReadKey("Sweep? [Y/N] > ");
			return key == ConsoleKey.Y;
		}

		/// <summary>
		/// Outcome of the stale-CSV freshness prompt.
		/// </summary>
		internal enum StaleCmsContentListChoice
		{
			/// <summary>Operator pressed [I] — proceed with the crawl despite staleness.</summary>
			Ignore,
			/// <summary>Operator pressed [A] — abort the run so the CSV can be refreshed.</summary>
			Abort,
		}

		/// <summary>
		/// Prompts the operator after [N] is chosen when the configured
		/// CmsContentList is older than its MaxAgeDays threshold. Shows the
		/// path, file date, age, threshold, and the operator-facing Comment
		/// from config so the operator has the context they need to judge
		/// whether the staleness matters for the specific CMS.
		///
		/// Two explicit choices: [I] Ignore (proceed) or [A] Abort. Anything
		/// else re-prompts — silent fall-through is hostile.
		///
		/// Does NOT write to 01-crawler.log — the gate decision is observable
		/// only through application.log (Logger). The crawler log is immutable
		/// after the initial crawl and must not carry workflow decisions that
		/// could become false on a later CSV refresh.
		/// </summary>
		internal static StaleCmsContentListChoice PromptForStaleCmsContentList(CmsContentListFreshness freshness)
		{
			lock (Logger.ConsoleLock)
			{
				ConsoleUi.WriteHeader();
				ConsoleUi.WriteWarning("CmsContentList is older than MaxAgeDays.");
				ConsoleUi.WriteLine($"  Path:        {freshness.Path}");
				ConsoleUi.WriteLine($"  File date:   {freshness.FileDate:yyyy-MM-dd HH:mm}");
				ConsoleUi.WriteLine($"  Age:         {freshness.AgeDays} day(s)");
				ConsoleUi.WriteLine($"  MaxAgeDays:  {freshness.MaxAgeDays}");
				if (!string.IsNullOrWhiteSpace(freshness.Comment))
				{
					ConsoleUi.WriteBlank();
					ConsoleUi.WriteLine("  Comment:");
					foreach (var line in freshness.Comment.Split('\n'))
					{
						ConsoleUi.WriteLine($"    {line.TrimEnd('\r')}");
					}
				}
				ConsoleUi.WriteBlank();
				ConsoleUi.WriteLine("The post-crawl pass will request URLs from this CSV. Stale entries");
				ConsoleUi.WriteLine("will produce 404s for pages that no longer exist on the site.");
				ConsoleUi.WriteFooter();

				while (true)
				{
					var key = ConsoleUi.ReadKey("[I] Ignore (proceed with crawl)   [A] Abort (fix CSV first) > ");
					ConsoleUi.WriteBlank();
					if (key == ConsoleKey.I)
					{
						return StaleCmsContentListChoice.Ignore;
					}

					if (key == ConsoleKey.A)
					{
						return StaleCmsContentListChoice.Abort;
					}

					ConsoleUi.WriteWarning("Press [I] to ignore or [A] to abort.");
				}
			}
		}

		/// <summary>
		/// Deletes everything in the snapshot folder except the non-derivable
		/// crawl ground truth: the <c>download/</c> subtree (raw crawled bodies
		/// plus their <c>.header</c> sidecars), <c>00-crawler.log</c> (the raw
		/// crawl log carrying the per-saved Content-Type that the settle phase
		/// reads), and <c>01-crawler.log</c> (settle's projection of 00 into the
		/// URL index). None of the three can be reproduced without re-crawling;
		/// everything else is re-derivable from them via replay.
		///
		/// Errors per file/directory are logged as warnings and the sweep
		/// continues — a stuck or locked individual entry must not abort the
		/// whole operation.
		/// </summary>
		internal static void CleanSweepSnapshot(string snapshotPath)
		{
			if (!Directory.Exists(snapshotPath))
			{
				Logger.LogWarning($"CleanSweepSnapshot: snapshot path does not exist: {snapshotPath}");
				return;
			}

			Logger.LogInfo($"CleanSweepSnapshot: cleaning {snapshotPath}");
			int filesDeleted = 0;
			int dirsDeleted = 0;
			int linksSkipped = 0;
			int filesSkipped = 0;
			int errors = 0;

			foreach (var entry in Directory.EnumerateFileSystemEntries(snapshotPath))
			{
				var name = Path.GetFileName(entry);
				if (ShouldKeepEntry(name))
				{
					Logger.LogInfo($"  Keep: {name}");
					continue;
				}

				// Containment defense: refuse to recurse into symlinks or junctions.
				// Directory.Delete(..., recursive: true) follows reparse points on
				// .NET 8 and would delete the link target's contents — that target
				// could lie outside the snapshot folder. Skip with warning; the
				// operator can resolve manually if a real link exists.
				try
				{
					var attrs = File.GetAttributes(entry);
					if ((attrs & FileAttributes.ReparsePoint) != 0)
					{
						linksSkipped++;
						Logger.LogWarning($"  Skipped reparse point (symlink/junction): {name} — manual review recommended");
						continue;
					}
				}
				catch (Exception ex)
				{
					errors++;
					Logger.LogWarning($"  Could not inspect attributes of {name}: {ex.Message}");
					continue;
				}

				try
				{
					if (Directory.Exists(entry))
					{
						Directory.Delete(entry, recursive: true);
						dirsDeleted++;
						Logger.LogInfo($"  Deleted directory: {name}");
					}
					else
					{
						// Retry-on-lock, same gate as the writers: if the file is open
						// in Excel, prompt the operator to close-and-retry or [S]kip
						// rather than hard-erroring. A skip is counted as skipped, not
						// an error — the leftover is re-derivable and swept next time.
						if (FileIo.DeleteWithRetry(entry, name))
						{
							filesDeleted++;
							Logger.LogInfo($"  Deleted file: {name}");
						}
						else
						{
							filesSkipped++;
							Logger.LogWarning($"  Skipped (left open): {name}");
						}
					}
				}
				catch (Exception ex)
				{
					errors++;
					Logger.LogWarning($"  Could not delete {name}: {ex.Message}");
				}
			}

			Logger.LogInfo($"CleanSweepSnapshot: complete — {filesDeleted} file(s), {dirsDeleted} directory(ies) deleted, " +
				$"{linksSkipped} link(s) skipped, {filesSkipped} locked file(s) left, {errors} error(s).");

			// Console summary (the per-file wall above is muted by the caller's quiet scope).
			if (!CrawlerContext.Silent)
			{
				ConsoleUi.WriteHeader("SWEEP");
			}
			ConsoleUi.WriteStepRow("Deleted", $"{filesDeleted} file(s) · {dirsDeleted} dir(s)");
			int totalSkipped = linksSkipped + filesSkipped;
			if (totalSkipped > 0 || errors > 0)
			{
				ConsoleUi.WriteStepRow("Skipped / errors",
					$"{totalSkipped} skipped · {errors} error(s)", dimmed: true);
			}
		}

		private static readonly HashSet<string> CleanSweepKeepEntries =
			new(StringComparer.OrdinalIgnoreCase) { "download", LogFileNames.CrawlerRaw, LogFileNames.Crawler };

		/// <summary>
		/// True if the named entry (file or directory, basename only) must be
		/// preserved by <see cref="CleanSweepSnapshot"/>. The keep-set is the
		/// non-derivable crawl ground truth: the <c>download/</c> subtree (raw
		/// bodies plus their <c>.header</c> sidecars) and both crawl logs —
		/// <c>00-crawler.log</c> (raw, carries the per-saved Content-Type the
		/// settle phase reads) and <c>01-crawler.log</c> (settle's projection of
		/// 00). None can be reproduced without re-crawling. Everything else in
		/// the snapshot is re-derivable from these via replay.
		/// </summary>
		internal static bool ShouldKeepEntry(string entryName)
		{
			return CleanSweepKeepEntries.Contains(entryName);
		}
	}
}
