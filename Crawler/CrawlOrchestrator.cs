using System.Text;
using Crawler.Downloader;
using Crawler.Snapshots;
using Crawler.Logging;
using Crawler.Urls;
using Crawler.Html;
using Crawler.Security;

namespace Crawler
{
	// ── CrawlOrchestrator ─────────────────────────────────────────────────────
	//
	// Everything from "post-connectivity-check" through "snapshot fully
	// downloaded and indexed" extracted from Program.RunAsync.
	// The orchestrator owns the snapshot/empty-download prompt dispatch, debug
	// log clearing, working-directory creation, main crawl, redirect analysis,
	// crawler-index integrity check, content-list logs, and post-crawl pass.
	//
	// Like AnalysisPipeline, each major step is a named private method; the
	// top-level RunAsync calls them in order.
	//
	// The pipeline mutates ctx during the two interactive prompts (Step 1 and
	// Step 2). After those, ctx is read-only for the rest of the orchestrator
	// AND for AnalysisPipeline downstream.
	//
	// Returns null when the run should abort — user picked abort at a prompt,
	// or any other early-exit condition. Returns a (currently empty) result
	// envelope otherwise; shape mirrors AnalysisPipelineResult so the caller
	// reads each pipeline call the same way.
	// ─────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Return envelope for CrawlOrchestrator.RunAsync. Empty today — the
	/// orchestrator's outputs (paths, snapshot state) all live in
	/// CrawlerRunContext. Kept as a record for shape consistency with
	/// AnalysisPipelineResult and to give future runs somewhere to grow.
	/// </summary>
	internal sealed record CrawlOrchestratorResult();

	internal static class CrawlOrchestrator
	{
		// ── Top-level orchestrator ───────────────────────────────────────────

		/// <summary>
		/// Runs the full crawl orchestration: snapshot prompt → empty-download
		/// recovery → debug log clearing → directory creation → main crawl →
		/// redirect analysis → first lookup → integrity check → 04/05 content
		/// logs → post-crawl pass + second lookup.
		///
		/// Returns null if the run should abort before analysis (user picked
		/// abort at a prompt). Returns a result envelope otherwise.
		///
		/// On success the snapshot is complete: download/ is populated,
		/// 02-crawler-index.log is current, and ctx paths are stable.
		/// </summary>
		internal static async Task<CrawlOrchestratorResult?> RunAsync(
			CrawlerRunContext ctx,
			Config config,
			string url,
			string urlDirectory,
			string workingFolder)
		{
			// Site identity label for the snapshot prompt heading: composed by Config
			// as "Name — Url" (falls back to bare url defensively). Single source of
			// truth — also used by the main-crawl and post-crawl banners.
			var siteLabel = config.ResolvedSiteLabel;

			if (!Step_PromptSnapshotChoice(ctx, url, siteLabel, workingFolder, urlDirectory))
			{
				return null;
			}

			if (!Step_CheckCmsContentListFreshness(ctx, config))
			{
				return null;
			}

			if (!Step_RecoverFromEmptyDownload(ctx, workingFolder, urlDirectory, config))
			{
				return null;
			}

			Step_ClearDebugLogs(ctx);
			Step_CreateWorkingDirectories(ctx);
			await Step_PerformMainCrawl(ctx, config, url);
			Step_SettleExtensions(ctx, config);
			Step_ReSettleUnverifiedAssets(ctx, config);

			// Index-build phase: the raw timestamped lines still go to the log file,
			// but their console echo is muted in favour of a clean per-step summary
			// (each step prints one aligned row via ConsoleUi). The post-crawl pass is
			// left outside the scope so its live download progress stays visible.
			if (!CrawlerContext.Silent)
			{
				ConsoleUi.WriteHeader("PREPARE");
				ReportProjectSize(ctx.FileDownloadDirectory);
			}
			using (Logger.QuietConsole())
			{
				Step_AnalyseRedirects(ctx);
				Step_CreateLookupFileFirstPass(ctx, config);
				Step_CheckCrawlerIndexIntegrity(ctx, config);
				Step_WriteContentListLogs(ctx, config);
			}

			await Step_PerformPostCrawlPass(ctx, config);

			// All download phases done — present any out-of-root refusals in one block.
			Crawl.WriteContainmentRefusalSummary();

			return new CrawlOrchestratorResult();
		}

		// ── Step 1: Snapshot choice prompt ───────────────────────────────────

		/// <summary>
		/// Returns false if the user picked abort at the snapshot prompt or at
		/// the integrity-check follow-up dialog. On [L] (replay) and [N] (new
		/// crawl) the ctx has been updated by InteractiveTriage; this method
		/// returns true.
		/// </summary>
		private static bool Step_PromptSnapshotChoice(
			CrawlerRunContext ctx, string url, string siteLabel,
			string workingFolder, string urlDirectory)
		{
			if (ctx.IsDebugSession)
			{
				return true;
			}

			var sessionParentDirectory = Path.Combine(workingFolder, urlDirectory);
			if (!Directory.Exists(sessionParentDirectory))
			{
				return true;
			}

			var mostRecent = Directory
				.EnumerateDirectories(sessionParentDirectory)
				.Select(d => new DirectoryInfo(d))
				.Where(d => SnapshotFolder.Matches(d.Name))
				.OrderByDescending(d => d.CreationTimeUtc)
				.FirstOrDefault();

			if (mostRecent == null)
			{
				if (!CrawlerContext.Silent)
				{
					// No previous snapshot — show banner without previous crawl info.
					InteractiveTriage.ShowCrawlStartBanner(url, null, null, 0, false);
				}
				return true;
			}

			var age = DateTime.UtcNow - mostRecent.CreationTimeUtc;
			var ageDescription = age.TotalMinutes < 60
				? $"{(int)age.TotalMinutes} minute(s)"
				: $"{age.TotalHours:F1} hour(s)";
			var isRecent = age.TotalHours < 24;

			if (CrawlerContext.Silent)
			{
				if (isRecent)
				{
					Logger.LogWarning(
						$"Silent mode: starting new crawl despite snapshot from only " +
						$"{ageDescription} ago ({mostRecent.Name}).");
				}
				return true;
			}

			var snapshotChoice = InteractiveTriage.PromptForSnapshotChoice(
				ctx, mostRecent, workingFolder, urlDirectory,
				url, siteLabel, age, ageDescription, isRecent);

			return snapshotChoice != InteractiveTriage.SnapshotChoice.Abort;
		}

		// ── Step 1b: CmsContentList freshness check ──────────────────────────

		/// <summary>
		/// Fires after [N] is chosen, before the main crawl starts. Reads the
		/// configured CmsContentList age and gates against MaxAgeDays. Stale
		/// triggers different behavior by mode:
		///
		///   * Interactive + stale → prompts the operator (Ignore / Abort).
		///     [I] returns true and the run proceeds. [A] returns false; the
		///     orchestrator returns null and Program.RunAsync exits cleanly.
		///   * Silent + stale → cannot prompt, so the main crawl runs (still
		///     valuable) and the post-crawl pass is suppressed via a ctx flag.
		///     Logged to application.log only — 01-crawler.log stays immutable
		///     post-crawl, which would otherwise carry a stale-CSV claim that
		///     a later replay (after the operator refreshed the CSV) would
		///     misrepresent. Operators who want the post-crawl pass to run
		///     unconditionally in silent mode set MaxAgeDays to 0.
		///
		/// No-op when:
		///   * ctx.IsDebugSession (replay) — the post-crawl pass would skip anyway.
		///   * !config.CmsContentList?.PostCrawlPass ?? false — no post-crawl pass configured, age is
		///     informational only and surfaced separately in LogMetadataLookupDiagnostics.
		///   * Not stale (fresh, or check disabled by MaxAgeDays &lt;= 0).
		/// </summary>
		private static bool Step_CheckCmsContentListFreshness(CrawlerRunContext ctx, Config config)
		{
			if (ctx.IsDebugSession)
			{
				return true;
			}

			if (!config.CmsContentList?.PostCrawlPass ?? false)
			{
				return true;
			}

			var freshness = CmsContentListFreshnessCheck.Evaluate(config.CmsContentList);
			if (!freshness.IsStale)
			{
				return true;
			}

			if (CrawlerContext.Silent)
			{
				Logger.LogWarning(
					$"Post-crawl pass will be skipped — CmsContentList is "
					+ $"{freshness.AgeDays} days old (MaxAgeDays={freshness.MaxAgeDays}). "
					+ $"Path: {freshness.Path}");
				if (!string.IsNullOrWhiteSpace(freshness.Comment))
				{
					Logger.LogWarning($"  Comment: {freshness.Comment}");
				}

				Logger.LogWarning(
					"Main crawl will run normally; only the post-crawl pass "
					+ "(05-not-directly-crawlable.log downloads) is suppressed. "
					+ "Set MaxAgeDays to 0 to disable this gate in silent mode.");
				ctx.SuppressPostCrawlPass = true;
				return true;
			}

			var choice = InteractiveTriage.PromptForStaleCmsContentList(freshness);
			if (choice == InteractiveTriage.StaleCmsContentListChoice.Ignore)
			{
				Logger.LogInfo("Operator chose to proceed despite stale CmsContentList.");
				return true;
			}

			// Abort. Print a clear message and pause so the operator can read
			// the warning block and screenshot/share it. PressEnterToExit
			// matches the established pattern from Program.cs and pipeline
			// abort paths.
			Logger.LogInfo("Run aborted by operator (stale CmsContentList).");
			ConsoleUi.WriteBlank();
			ConsoleUi.WriteWarning("Run aborted. Refresh CmsContentList file and re-run.");
			ConsoleUi.PressEnterToExit();
			return false;
		}

		// ── Step 2: Empty-download recovery prompt ───────────────────────────

		/// <summary>
		/// [KEEP] When replaying a debug session, verify the download folder exists
		/// and contains HTML files. If the folder is empty or missing — e.g. the
		/// user deleted files to free disk space — offer a fresh crawl rather than
		/// silently producing empty analysis results.
		///
		/// Returns false if the user picked abort. On [N] (start fresh crawl),
		/// ctx has been mutated by InteractiveTriage (IsDebugSession flipped
		/// off, new timestamp, paths rebuilt); this method returns true.
		/// </summary>
		private static bool Step_RecoverFromEmptyDownload(
			CrawlerRunContext ctx, string workingFolder, string urlDirectory, Config config)
		{
			if (!ctx.IsDebugSession || CrawlerContext.Silent)
			{
				return true;
			}

			var downloadExists = Directory.Exists(ctx.FileDownloadDirectory);
			var downloadIsEmpty = !downloadExists
				|| !Directory.EnumerateFiles(ctx.FileDownloadDirectory, config.FilePattern).Any();

			if (!downloadIsEmpty)
			{
				return true;
			}

			var emptyChoice = InteractiveTriage.PromptForEmptyDownloadRecovery(
				ctx, workingFolder, urlDirectory);

			return emptyChoice != InteractiveTriage.EmptyDownloadChoice.Abort;
		}

		// ── Step 3: Debug log clearing ───────────────────────────────────────

		/// <summary>
		/// When IsDebugSession is true, clears the per-run logs so previous
		/// results don't bleed into the new analysis. New crawls write fresh
		/// logs naturally and don't need this.
		/// </summary>
		private static void Step_ClearDebugLogs(CrawlerRunContext ctx)
		{
			if (!ctx.IsDebugSession)
			{
				return;
			}

			// Logs regenerated every debug run regardless of which steps are skipped.
			FileIo.ClearLogs(
				ctx.RedirectAnalysisPath,
				ctx.ErrorLogPath,
				ctx.ErrorSourcesCsvBasePath + IssueLogWriter.CsvSemicolonSuffix,
				ctx.ErrorSourcesCsvBasePath + IssueLogWriter.CsvCommaSuffix,
				ctx.SeoDataPath);

			// Spell-check outputs — cleared so previous results don't bleed into new run.
			FileIo.ClearLogs(
				ctx.SpellErrorLogPath,
				ctx.UniqueSpellErrorLogPath,
				ctx.SpellLocatedLogPath,
				ctx.WordTicketsDiagnosticLogPath,
				ctx.SpellSuppressionSuggestionsLogPath,
				ctx.SelfLinkAnalysisCsvBasePath + IssueLogWriter.CsvSemicolonSuffix,
				ctx.SelfLinkAnalysisCsvBasePath + IssueLogWriter.CsvCommaSuffix,
				ctx.UserDictionaryOrphanWordsFilePath);

			// Content comparison — only present when CmsContentList is configured.
			FileIo.ClearLogs(
				ctx.FullContentLogPath,
				ctx.ContentCrawlCompareLogFile);
		}

		// ── Step 4: Working directory creation ───────────────────────────────

		/// <summary>
		/// [KEEP] Ensure the download working directory exists — in both normal and
		/// debug/replay mode. Directory.CreateDirectory is a no-op when the folder
		/// already exists.
		///
		/// Runs after the prompts so the correct path is used: in replay mode
		/// download/ already exists from the original crawl; on a new crawl it is
		/// created fresh here before the crawl writes into it.
		/// </summary>
		private static void Step_CreateWorkingDirectories(CrawlerRunContext ctx)
		{
			Directory.CreateDirectory(ctx.FileDownloadDirectory);
		}

		// ── Step 5: Main crawl ───────────────────────────────────────────────

		/// <summary>
		/// Runs the main crawl (proxy auth → Crawl.Initialize → DownloadWebsiteAsync
		/// → completed marker). Skipped entirely on debug-replay sessions where
		/// the download folder is already populated.
		/// </summary>
		private static async Task Step_PerformMainCrawl(
			CrawlerRunContext ctx, Config config, string url)
		{
			if (ctx.IsDebugSession)
			{
				return;
			}

			// Proxy credentials were resolved once at startup (Program.RunAsync →
			// ResolveProxyCredentials) and live on ctx — the single source of truth
			// for every HTTP path. Read them here; do NOT re-read config or prompt
			// (that was the split-brain: preflight and crawl could differ).
			string proxyUser = ctx.ProxyUser;
			string proxyPassword = ctx.ProxyPassword;

			string? effectiveProxyUrl = config.UseProxy ? config.ProxyUrl : null;
			Crawl.Initialize(effectiveProxyUrl, proxyUser, proxyPassword,
				config.MaxConcurrentPageDownloads, config.MaxConcurrentAssetDownloads);
			CrawlAsset.Initialize(effectiveProxyUrl, proxyUser, proxyPassword);

			// Site-identity banner for the main crawl — anchors the operator to which
			// site this crawl is for, matching the snapshot-prompt heading. Interactive
			// only; silent runs are unattended and don't need the visual anchor (they
			// have site identity in their log lines via Logger).
			if (!CrawlerContext.Silent)
			{
				ConsoleUi.WriteHeader();
				ConsoleUi.WriteEmphasis(config.ResolvedSiteLabel);
				ConsoleUi.WriteFooter();
			}

			// Start live progress display.
			CancellationTokenSource? progressCts = CrawlerContext.Silent
				? null : StartCrawlProgressDisplay(ctx.FileDownloadDirectory);

			// [KEEP] Security boundary — build the host allowlist once and hand it to
			// the crawler. Each declared host is admitted exactly (no sibling
			// subdomains, no parent apex, no suffix look-alikes). Malformed
			// UrlSubdomainsAllowed entries are dropped and logged loudly.
			var crawlPolicy = CrawlPolicy.FromConfig(config.Url, config.UrlSubdomainsAllowed);
			foreach (var ignored in crawlPolicy.IgnoredEntries)
			{
				Logger.LogError(
					$"UrlSubdomainsAllowed: ignoring malformed entry '{ignored}' — each entry must be a " +
					"full base URL (scheme + host), e.g. \"https://help.example.com\".");
			}
			Crawl.SetCrawlPolicy(crawlPolicy);
			CrawlAsset.SetCrawlPolicy(crawlPolicy);

			await Crawl.DownloadWebsiteAsync(
				url,
				ctx.SaveDirectory,
				ctx.CrawlerRawLogPath,
				url,
				config.DownloadExclusions,
				config.ModalQueryParameters,
				jsonPathPrefixes: config.ExtendedCrawlJsonPathPrefixes,
				allowedSubdomains: config.UrlSubdomainsAllowed
			);

			// Stop progress display.
			progressCts?.Cancel();
			progressCts?.Dispose();

			// Write completed marker — only if at least one file was saved.
			// Crawl writes the RAW log (00); settle projects it to 01.
			var hasSavedEntries = File.Exists(ctx.CrawlerRawLogPath) &&
				File.ReadLines(ctx.CrawlerRawLogPath, Encoding.UTF8)
					.Any(l => l.Contains("saved", StringComparison.OrdinalIgnoreCase));
			if (hasSavedEntries)
			{
				CrawlLogWriter.Write(url, "completed", "info", ctx.CrawlerRawLogPath);
			}
			else
			{
				Logger.LogWarning("Crawl produced no saved files — 'completed' marker not written.");
			}
		}

		// ── Step 5b: Settle file extensions per policy ────────────────

		/// <summary>
		/// Resolves provisional ".unverified" downloads to their final classification
		/// and projects the raw crawl log (00) into the downstream-facing log (01).
		///
		/// For each "saved" row in 00 whose file is still ".unverified" on disk, the
		/// three identifying signals (requested URL extension, the Content-Type column
		/// recorded in 00, and a byte sniff of the file's head) are evaluated against
		/// <see cref="Config.UnverifiedHtmlPolicy"/>. Files classified as HTML are
		/// renamed to the page extension; the rest keep ".unverified" (quarantined —
		/// counted for 404s, excluded from analyzer globs). Signal disagreements are
		/// recorded in log #23.
		///
		/// 01 is then produced by projecting every 00 row: "saved" rows resolve to the
		/// file's current on-disk name with the Content-Type column dropped (01 keeps
		/// its historical 5-field shape, so downstream readers are untouched); all other
		/// rows (crawled, redirects, completed marker, errors) are copied verbatim — so
		/// the "completed" marker reaches 01 and the snapshot-integrity / salvage check
		/// continues to work.
		///
		/// Skipped on replay (IsDebugSession): a replay reuses the original crawl's
		/// already-settled 01 and does not re-evaluate (a changed policy takes effect on
		/// a new crawl only). Safe to call after each download pass — the second call
		/// finds only the newly-provisional files; 01 is regenerated from the full 00.
		/// </summary>
		private static void Step_SettleExtensions(CrawlerRunContext ctx, Config config, bool force = false)
		{
			if (!File.Exists(ctx.CrawlerRawLogPath))
			{
				return; // nothing crawled — no 00 to settle
			}

			// Settle ONLY a completed crawl. An incomplete crawl (killed mid-download)
			// has no "completed" marker in 00 — settling it would project partial data
			// into 01 and any analysis off that is meaningless. Leaving it unsettled
			// means no 01 is produced, so the snapshot-integrity check flags the crawl
			// as broken and the operator can discard or re-crawl. We do not manufacture
			// a valid-looking 01 over incomplete data.
			if (!RawCrawlIsComplete(ctx.CrawlerRawLogPath))
			{
				return;
			}

			// Produce 01 from 00 once. Skip when 01 already exists (replay of a completed
			// snapshot reuses the original classification — a changed policy needs a
			// re-crawl), unless forced by the post-crawl second pass, which has appended
			// new rows to 00 and must regenerate 01.
			if (!force && File.Exists(ctx.CrawlerLogPath))
			{
				return;
			}

			var policy = config.ResolvedUnverifiedHtmlPolicy;
			var pdfPolicy = config.ResolvedUnverifiedPdfPolicy;
			var imagePolicy = config.AssetQuality.ResolvedUnverifiedImagePolicy;
			var pageExt = config.FileExtension;            // "*.html" → ".html"
			if (string.IsNullOrEmpty(pageExt))
			{
				pageExt = ".html";
			}

			var rawLines = File.ReadAllLines(ctx.CrawlerRawLogPath, Encoding.UTF8);
			var projected = new List<string>(rawLines.Length);
			var mismatches = new List<string?[]>();
			var pdfMismatches = new List<string?[]>();
			var imageMismatches = new List<string?[]>();

			// On a forced re-settle (post-crawl second pass), 01 already holds the rows
			// settled by the first pass. Collect their URLs so we re-classify ONLY the
			// newly-added files this pass — a quarantined file stays .unverified on disk
			// and would otherwise be re-examined and re-logged to #23 on every pass.
			var alreadySettledUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (force && File.Exists(ctx.CrawlerLogPath))
			{
				foreach (var l in File.ReadLines(ctx.CrawlerLogPath, Encoding.UTF8))
				{
					var p = l.Split('|', StringSplitOptions.TrimEntries);
					if (p.Length >= 5 && p[2].Equals("saved", StringComparison.OrdinalIgnoreCase))
					{
						alreadySettledUrls.Add(p[1]);
					}
				}
			}

			foreach (var line in rawLines)
			{
				var parsed = ParseRawSavedRow(line);
				if (parsed is null)
				{
					// Non-"saved" row (crawled / redirect / completed / error) — verbatim.
					projected.Add(line);
					continue;
				}

				var (timestamp, url, fileName, source, contentType) = parsed.Value;
				var downloadPath = Path.Combine(ctx.FileDownloadDirectory, fileName);

				// Settle only files still provisional on disk. An already-renamed file
				// (settled by an earlier call this run) is left alone; we still project
				// its current name into 01 below.
				var finalName = fileName;
				bool alreadySettled = alreadySettledUrls.Contains(url);
				if (!alreadySettled
					&& fileName.EndsWith(FileTypeClassifier.UnverifiedExtension, StringComparison.OrdinalIgnoreCase)
					&& File.Exists(downloadPath))
				{
					// One head read serves both sniffs: HTML scans the whole
					// ~1 KB window, PDF only needs the leading "%PDF-" magic at the
					// front of the same buffer. Open the file once.
					var head = ReadSettleHead(downloadPath);

					var requestedExtIsHtml = RequestedUrlLooksHtml(url, pageExt);
					var headerIsHtml = FileTypeClassifier.IsHtmlContentType(contentType);
					var sniffIsHtml = FileTypeClassifier.LooksLikeHtml(head);

					var verdict = FileTypeClassifier.ClassifyPage(policy, requestedExtIsHtml, headerIsHtml, sniffIsHtml);

					if (verdict.TreatAsHtml)
					{
						// Rename .unverified → page extension, preserving hash+suffix.
						finalName = Path.ChangeExtension(fileName, pageExt.TrimStart('.'));
						var finalPath = Path.Combine(ctx.FileDownloadDirectory, finalName);
						SafeRename(downloadPath, finalPath);
					}
					// else: keep .unverified (quarantined) — finalName unchanged.

					if (verdict.IsMismatch)
					{
						mismatches.Add(
						[
							timestamp,
							url,
							requestedExtIsHtml.ToString(),
							string.IsNullOrEmpty(contentType) ? "n/a" : contentType,
							sniffIsHtml.ToString(),
							policy.ToString(),
							verdict.TreatAsHtml ? "analysed-as-html" : "quarantined"
						]);
					}

					// PDF classification runs only on the still-.unverified
					// remainder — a file already renamed to the page extension is no
					// longer a PDF candidate. Reuses the head bytes read above. The
					// settle action for a PDF is to give it the real ".pdf" extension
					// so PdfQualityAnalyzer (which globs download/*.pdf) finds it and
					// it is no longer .unverified-excluded from the index.
					if (!verdict.TreatAsHtml)
					{
						var requestedExtIsPdf = RequestedUrlLooksPdf(url);
						var headerIsPdf = FileTypeClassifier.IsPdfContentType(contentType);
						var sniffIsPdf = FileTypeClassifier.LooksLikePdf(head);

						var pdfVerdict = FileTypeClassifier.ClassifyPdf(pdfPolicy, requestedExtIsPdf, headerIsPdf, sniffIsPdf);

						if (pdfVerdict.TreatAsPdf)
						{
							finalName = Path.ChangeExtension(fileName, "pdf");
							var finalPdfPath = Path.Combine(ctx.FileDownloadDirectory, finalName);
							SafeRename(downloadPath, finalPdfPath);
						}
						// else: keep .unverified — finalName unchanged.

						if (pdfVerdict.IsMismatch)
						{
							pdfMismatches.Add(
							[
								timestamp,
								url,
								requestedExtIsPdf.ToString(),
								string.IsNullOrEmpty(contentType) ? "n/a" : contentType,
								sniffIsPdf.ToString(),
								pdfPolicy.ToString(),
								pdfVerdict.TreatAsPdf ? "analysed-as-pdf" : "quarantined"
							]);
						}

						// Image classification runs on the remainder after HTML and PDF.
						// Mirrors the PDF branch: same three-signal model against
						// UnverifiedImagePolicy, same reuse of the head bytes, but the
						// final extension is format-dependent (FileTypeClassifier.SniffedImageExtension
						// reads it from the same head — jpg/png/gif/webp). With an image
						// branch wired in, a verified image settles to its real
						// extension at settle time the same way HTML/PDF do, and the
						// interactive promote prompt (Step_ReSettleUnverifiedAssets) is
						// then only reached on TRUE signal-disagreement cases (its
						// original intent), not on every clean image. Pre-this fix every
						// image stayed .unverified regardless of policy — the
						// UnverifiedImagePolicy config was effectively inert because no
						// production code path called ClassifyImage.
						if (!pdfVerdict.TreatAsPdf)
						{
							var requestedExtIsImage = FileTypeClassifier.IsImageExtension(url);
							var headerIsImage = FileTypeClassifier.IsImageContentType(contentType);
							var sniffIsImage = FileTypeClassifier.LooksLikeImage(head);

							var imageVerdict = FileTypeClassifier.ClassifyImage(
								imagePolicy, requestedExtIsImage, headerIsImage, sniffIsImage);

							if (imageVerdict.TreatAsImage)
							{
								// Format determined by the bytes, not the URL — a .jpg URL
								// returning a PNG (rare but possible) settles as .png so the
								// downstream image analyzers see the truth on disk.
								var imageExt = FileTypeClassifier.SniffedImageExtension(head);
								if (imageExt is not null)
								{
									finalName = Path.ChangeExtension(fileName, imageExt);
									var finalImagePath = Path.Combine(ctx.FileDownloadDirectory, finalName);
									SafeRename(downloadPath, finalImagePath);
								}
								// else: bytes did not sniff to a known image format despite the
								// classification verdict — leave .unverified rather than guess.
							}
							// else: keep .unverified — finalName unchanged.

							if (imageVerdict.IsMismatch)
							{
								imageMismatches.Add(
								[
									timestamp,
									url,
									requestedExtIsImage.ToString(),
									string.IsNullOrEmpty(contentType) ? "n/a" : contentType,
									sniffIsImage.ToString(),
									imagePolicy.ToString(),
									imageVerdict.TreatAsImage ? "analysed-as-image" : "quarantined"
								]);
							}
						}
					}
				}
				else if (File.Exists(Path.Combine(ctx.FileDownloadDirectory,
					Path.ChangeExtension(fileName, pageExt.TrimStart('.')))))
				{
					// File was already settled to the page extension on an earlier call.
					finalName = Path.ChangeExtension(fileName, pageExt.TrimStart('.'));
				}
				else if (File.Exists(Path.Combine(ctx.FileDownloadDirectory,
					Path.ChangeExtension(fileName, "pdf"))))
				{
					// File was already settled to ".pdf" on an earlier call.
					// Recover its current name so the force-pass projection into 01
					// reflects the .pdf rename rather than the stale .unverified name.
					finalName = Path.ChangeExtension(fileName, "pdf");
				}
				else if (TryFindSettledImageExtension(ctx.FileDownloadDirectory, fileName)
					is { } settledImageExt)
				{
					// File was already settled to .jpg/.png/.gif/.webp on an earlier
					// call. Same recovery semantics as the HTML and PDF fallbacks
					// above — without this, the force-pass second settle (post-crawl)
					// would project the stale .unverified name from 00 over the
					// first pass's correct extension, leaving 02-index pointing at a
					// filename that no longer exists on disk (the downstream
					// LookUpUrlForFile-cache-miss symptom).
					finalName = Path.ChangeExtension(fileName, settledImageExt);
				}

				// Project to 01: 5-field historical shape (Content-Type column dropped).
				projected.Add($"{timestamp} | {url} | saved | {finalName} | {source}");
			}

			// Produce 01 (downstream-facing). WriteAllLinesWithRetry → recoverable if
			// an operator has 01 open in a viewer.
			FileIo.WriteAllLinesWithRetry(ctx.CrawlerLogPath, projected,
				Path.GetFileName(ctx.CrawlerLogPath));

			// Log #23 is always written so its presence proves the classification check
			// ran — an empty (header-only) file means "checked, no mismatches", which is
			// distinct from "the check never ran". Settle #1 (non-force) writes the
			// header fresh (overwriting any stale file from a prior run) plus any
			// mismatches; settle #2 (force, post-crawl) appends its mismatches without
			// re-writing the header or clobbering the first pass's rows.
			var header = new string?[]
			{
				"timestamp", "url", "requestedHtml", "contentType", "sniffHtml",
				"policy", "action"
			};
			if (!force)
			{
				var rows = new List<string?[]> { header };
				rows.AddRange(mismatches);
				IssueLogWriter.Write(ctx.ContentTypeMismatchLogPath, '|', rows);
			}
			else if (mismatches.Count > 0)
			{
				IssueLogWriter.AppendMany(ctx.ContentTypeMismatchLogPath, '|', mismatches);
			}

			// Log #24 — PDF mismatches. Same always-written / force-aware
			// discipline as #23 but a separate log and PDF-shaped columns, because
			// PDF and HTML identity are expected to diverge.
			var pdfHeader = new string?[]
			{
				"timestamp", "url", "requestedPdf", "contentType", "sniffPdf",
				"policy", "action"
			};
			if (!force)
			{
				var pdfRows = new List<string?[]> { pdfHeader };
				pdfRows.AddRange(pdfMismatches);
				IssueLogWriter.Write(ctx.PdfContentTypeMismatchLogPath, '|', pdfRows);
			}
			else if (pdfMismatches.Count > 0)
			{
				IssueLogWriter.AppendMany(ctx.PdfContentTypeMismatchLogPath, '|', pdfMismatches);
			}

			// Log #26 — image mismatches. Same discipline as #23/#24 but image-shaped
			// columns. Pre-image-wiring every .unverified image was retained for the
			// interactive promote prompt; with classification now running at settle
			// time, only genuine signal-disagreement cases reach this log (and the
			// prompt becomes a quarantine-only fallback rather than the common path).
			var imageHeader = new string?[]
			{
				"timestamp", "url", "requestedImage", "contentType", "sniffImage",
				"policy", "action"
			};
			if (!force)
			{
				var imageRows = new List<string?[]> { imageHeader };
				imageRows.AddRange(imageMismatches);
				IssueLogWriter.Write(ctx.ImageContentTypeMismatchLogPath, '|', imageRows);
			}
			else if (imageMismatches.Count > 0)
			{
				IssueLogWriter.AppendMany(ctx.ImageContentTypeMismatchLogPath, '|', imageMismatches);
			}
		}

		/// <summary>
		/// True when the raw crawl log (00) ends with a "completed" marker — i.e. the
		/// crawl finished normally. Mirrors the last-non-empty-line check used by
		/// <see cref="CrawlLog.Analyse"/> so "completed" means the same thing
		/// here as in the snapshot-integrity check.
		/// </summary>
		private static bool RawCrawlIsComplete(string rawLogPath)
		{
			if (!File.Exists(rawLogPath))
			{
				return false;
			}

			var last = File.ReadLines(rawLogPath, Encoding.UTF8)
				.LastOrDefault(l => l.Trim().Length > 0);
			return last is not null
				&& last.Contains("completed", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Parses a 00-log "saved" row into its fields, or null if the line is not a
		/// "saved" row. Format: timestamp | url | saved | filename | source | contentType.
		/// </summary>
		private static (string Timestamp, string Url, string FileName, string Source, string? ContentType)?
			ParseRawSavedRow(string line)
		{
			var parts = line.Split('|', StringSplitOptions.TrimEntries);
			// timestamp(0) url(1) status(2) filename(3) source(4) contentType(5)
			if (parts.Length < 6)
			{
				return null;
			}

			if (!parts[2].Equals("saved", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			return (parts[0], parts[1], parts[3], parts[4], parts[5]);
		}

		/// <summary>True when a requested URL's shape implies HTML — it ends in a known
		/// page extension, or is extensionless (clean URL / query engine), which the
		/// crawler saves provisionally as a page candidate. The universal base set
		/// (.html / .htm / .htmlx) is always accepted because servers serve HTML at
		/// those URLs regardless of how this run is configured; on top of that, the
		/// run's own configured page extension (<paramref name="configuredPageExt"/>,
		/// e.g. ".aspx" for a "*.aspx" site) is accepted too, so this settle signal
		/// stays meaningful for generic, non-.html sites rather than always voting
		/// "not HTML" for them. Comparison is case-insensitive.</summary>
		private static bool RequestedUrlLooksHtml(string url, string configuredPageExt)
		{
			var path = url.Split('?')[0];
			var ext = Path.GetExtension(path);
			if (string.IsNullOrEmpty(ext))
			{
				return true; // extensionless → page candidate
			}

			return ext.Equals(".html", StringComparison.OrdinalIgnoreCase)
				|| ext.Equals(".htm", StringComparison.OrdinalIgnoreCase)
				|| ext.Equals(".htmlx", StringComparison.OrdinalIgnoreCase)
				|| (!string.IsNullOrEmpty(configuredPageExt)
					&& ext.Equals(configuredPageExt, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>True when a requested URL's shape implies a PDF — its path ends in
		/// ".pdf" (query string stripped first). Counterpart to
		/// <see cref="RequestedUrlLooksHtml"/>; unlike that helper, an extensionless
		/// URL is NOT a PDF candidate (extensionless = page candidate).</summary>
		private static bool RequestedUrlLooksPdf(string url)
		{
			var path = url.Split('?')[0];
			var ext = Path.GetExtension(path);
			return ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>Reads the first <see cref="MarkupFile.SniffByteCount"/> bytes of a
		/// file once, for the settle-phase sniffs. The HTML sniff scans the whole
		/// window; the PDF sniff reads only the leading magic from the front of the
		/// same buffer. Returns an empty array on any read error — both
		/// <see cref="FileTypeClassifier.LooksLikeHtml"/> and <see cref="FileTypeClassifier.LooksLikePdf"/>
		/// treat empty as "not that type".</summary>
		private static byte[] ReadSettleHead(string path)
		{
			try
			{
				using var fs = File.OpenRead(path);
				var buffer = new byte[MarkupFile.SniffByteCount];
				int read = fs.Read(buffer, 0, buffer.Length);
				if (read < buffer.Length)
				{
					Array.Resize(ref buffer, read);
				}

				return buffer;
			}
			catch (IOException ex)
			{
				Logger.LogWarning($"Settle: could not read head of {Path.GetFileName(path)} — {ex.Message}");
				return [];
			}
		}

		/// <summary>Renames a settled download, overwriting any pre-existing target
		/// (a re-settle this run resolving to the same name is benign).</summary>
		// ── Step 5c: Re-settle unverified assets (operator-gated) ────────────
		//
		// A ".unverified" file is an UNASSESSED download — it is not a valid resting
		// state. Crawl-time settle (Step_SettleExtensions) resolves HTML and PDF; an
		// asset whose request shape, declared Content-Type and bytes did not let it
		// settle there (e.g. an image reached via an <a href> download link, which
		// has no image tier at crawl time today) is left ".unverified".
		//
		// This gate offers to assess those leftovers offline — no network, reading
		// the preserved ".header" sidecar for the original Content-Type plus a byte
		// sniff. Security model (deliberately strict, policy-INDEPENDENT):
		//   • Promote ONLY when all three signals AGREE the file is an image
		//     (requested extension, sidecar Content-Type, byte sniff). Any
		//     disagreement is held back — never auto-promoted — because a file that
		//     lies about its type is exactly the suspicious case.
		//   • (a) Never runs in silent mode — promoting an unassessed file into the
		//     index (where it is parsed) must be a conscious act, never automated.
		//   • (b) Requires an explicit operator Y/N after a warning listing what
		//     will be promoted and what is held back.
		//   • On N, or for held-back files, nothing changes: they remain
		//     ".unverified" and are reassessed identically on the next run (no
		//     suppression, no promotion, no state carried).
		//
		// Promoted files are renamed ".unverified" → sniffed extension, so the very
		// next Step_CreateLookupFileFirstPass admits them to 02 via the normal path
		// (the index admits only files no longer ".unverified").
		private static void Step_ReSettleUnverifiedAssets(CrawlerRunContext ctx, Config config)
		{
			// (a) Safeguard: never in silent / non-interactive mode.
			if (CrawlerContext.Silent)
			{
				return;
			}

			if (!Directory.Exists(ctx.FileDownloadDirectory))
			{
				return;
			}

			var unverified = Directory
				.GetFiles(ctx.FileDownloadDirectory, "*" + FileTypeClassifier.UnverifiedExtension,
					SearchOption.TopDirectoryOnly)
				.OrderBy(f => f, StringComparer.Ordinal)
				.ToList();
			if (unverified.Count == 0)
			{
				return;
			}

			// URL lookup for .unverified files must come from 01 directly — the
			// Cache (02-derived) excludes them by design.
			var urlByFile = LoadUnverifiedUrlMap(ctx.CrawlerLogPath);

			var promote = new List<(string Path, string FinalName, string Url, string Ext)>();
			var heldBack = new List<(string Name, string Reason)>();

			foreach (var file in unverified)
			{
				var name = Path.GetFileName(file);
				byte[] head;
				try { head = ReadSettleHead(file); }
				catch { heldBack.Add((name, "unreadable")); continue; }

				urlByFile.TryGetValue(name, out var url);
				url ??= string.Empty;

				var requestedExtIsImage = FileTypeClassifier.IsImageExtension(url);
				var headerIsImage = FileTypeClassifier.IsImageContentType(ReadSidecarContentTypeForSettle(file));
				var sniffIsImage = FileTypeClassifier.LooksLikeImage(head);

				// Agreement-only, policy-independent: all three must say image.
				if (requestedExtIsImage && headerIsImage && sniffIsImage)
				{
					var ext = FileTypeClassifier.SniffedImageExtension(head);
					if (ext is null) { heldBack.Add((name, "sniff inconclusive")); continue; }
					var finalName = Path.ChangeExtension(name, ext);
					promote.Add((file, finalName, url, ext));
				}
				else
				{
					heldBack.Add((name,
						$"signals disagree (ext={requestedExtIsImage}, header={headerIsImage}, sniff={sniffIsImage})"));
				}
			}

			// Console warning + per-file detail, using the shared ConsoleUi palette.
			ConsoleUi.WriteBlank();
			ConsoleUi.WriteEmphasis($"{unverified.Count} unassessed (.unverified) asset(s) found.");
			if (promote.Count > 0)
			{
				ConsoleUi.WriteSuccess($"{promote.Count} can be promoted — all signals agree they are images:");
				foreach (var p in promote)
				{
					ConsoleUi.WriteSubItem($"+ {p.FinalName}  ({(string.IsNullOrEmpty(p.Url) ? "no URL in 01" : p.Url)})");
				}
			}
			if (heldBack.Count > 0)
			{
				ConsoleUi.WriteWarningBlock(
					$"{heldBack.Count} held back — left .unverified for reassessment next run:",
					heldBack.Select(h => $"- {h.Name}  [{h.Reason}]"));
			}
			if (promote.Count == 0)
			{
				ConsoleUi.WriteInfo("Nothing to promote (no file has all signals agreeing). Leaving as-is.");
				return;
			}

			ConsoleUi.WriteBlank();
			ConsoleUi.WriteWarningBlock("WARNING: promoting moves these files into the index and",
			[
				"subjects them to content analysis (including metadata parsing).",
				"Only proceed if you trust their origin."
			]);

			// (b) Explicit Y/N. Default-safe: anything but Yes leaves everything as-is.
			if (!ConsoleTriage.AskYesNo($"  Re-settle {promote.Count} unsettled asset(s) now?"))
			{
				Logger.LogInfo($"Re-settle declined by operator — {unverified.Count} file(s) left .unverified.");
				return;
			}

			int done = 0;
			var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var p in promote)
			{
				var finalPath = Path.Combine(ctx.FileDownloadDirectory, p.FinalName);
				SafeRename(p.Path, finalPath);
				if (File.Exists(finalPath))
				{
					done++;
					renamed[Path.GetFileName(p.Path)] = p.FinalName; // old .unverified → new .jpg
				}
			}

			// Rewrite the matching rows in 01 so the promoted files carry their new
			// extension. Without this, 01 still names them ".unverified", and the
			// downstream CreateLookupFile (which builds 02 from 01) would EXCLUDE
			// them by the .unverified-exclusion rule — leaving them out of the index and the URL
			// cache despite being renamed on disk. Column 4 (filename) is swapped;
			// every other column is preserved verbatim.
			if (renamed.Count > 0)
			{
				RewriteCrawlerLogFilenames(ctx.CrawlerLogPath, renamed);
			}

			ConsoleUi.WriteSuccess($"Re-settled {done}/{promote.Count} asset(s); {heldBack.Count} left .unverified.");
			Logger.LogInfo($"Re-settle: promoted {done}/{promote.Count} asset(s) to image extensions; " +
				$"{heldBack.Count} left .unverified.");
		}

		// Swaps the filename (column 4) of "saved" rows in 01 for promoted files,
		// keying on the old .unverified filename. Preserves all other columns and
		// row order. Runs before CreateLookupFileFirstPass so the rebuilt 02 admits
		// the renamed (no-longer-.unverified) files via the normal path.
		private static void RewriteCrawlerLogFilenames(
			string crawlerLogPath, Dictionary<string, string> oldToNew)
		{
			if (!File.Exists(crawlerLogPath))
			{
				return;
			}

			try
			{
				var lines = File.ReadAllLines(crawlerLogPath, Encoding.UTF8);
				bool changed = false;
				for (int i = 0; i < lines.Length; i++)
				{
					if (!lines[i].Contains("saved", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					// Split preserving the original spacing is not required — 01 is
					// regenerated with " | " separators by settle, so we re-emit the
					// same canonical shape. Parse, swap col 3 (0-based), re-join.
					var parts = lines[i].Split('|');
					if (parts.Length < 4)
					{
						continue;
					}

					var fileName = parts[3].Trim();
					if (oldToNew.TryGetValue(fileName, out var newName))
					{
						parts[3] = $" {newName} ";
						lines[i] = string.Join("|", parts);
						changed = true;
					}
				}
				if (changed)
				{
					FileIo.WriteAllLinesWithRetry(crawlerLogPath, lines, Path.GetFileName(crawlerLogPath));
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Re-settle: could not rewrite 01 filenames — {ex.Message}");
			}
		}

		// Reads filename → URL from 01 for ".unverified" rows only. 01 keeps these
		// rows (they count for 404 detection); the 02-derived Cache drops them.
		private static Dictionary<string, string> LoadUnverifiedUrlMap(string crawlerLogPath)
		{
			var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (!File.Exists(crawlerLogPath))
			{
				return map;
			}

			try
			{
				foreach (var line in File.ReadLines(crawlerLogPath, Encoding.UTF8))
				{
					if (!line.Contains("saved", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					var parts = line.Split('|', StringSplitOptions.TrimEntries);
					if (parts.Length < 4)
					{
						continue;
					}

					var fileName = parts[3];
					if (!fileName.EndsWith(FileTypeClassifier.UnverifiedExtension, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					map[fileName] = parts[1];
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Re-settle: could not read 01 for URL map — {ex.Message}");
			}
			return map;
		}

		// Reads the response Content-Type from a file's ".header" sidecar (offline),
		// media type only. Mirrors AssetQuality's sidecar read; lives here so the
		// gate has no dependency on the analyzer.
		private static string? ReadSidecarContentTypeForSettle(string file)
		{
			var sidecar = Path.ChangeExtension(file, HeaderSidecar.HeaderSidecarExtension.TrimStart('.'));
			if (!File.Exists(sidecar))
			{
				return null;
			}

			try
			{
				bool inResponse = false;
				foreach (var line in File.ReadLines(sidecar, Encoding.UTF8))
				{
					if (line.StartsWith("=== RESPONSE ===", StringComparison.Ordinal)) { inResponse = true; continue; }
					if (!inResponse)
					{
						continue;
					}

					var colon = line.IndexOf(':');
					if (colon <= 0)
					{
						continue;
					}

					if (!line[..colon].Trim().Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					var val = line[(colon + 1)..];
					var semi = val.IndexOf(';');
					return (semi >= 0 ? val[..semi] : val).Trim();
				}
			}
			catch { return null; }
			return null;
		}

		private static void SafeRename(string from, string to)
		{
			try
			{
				if (File.Exists(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
				{
					File.Delete(to);
				}

				File.Move(from, to);
			}
			catch (IOException ex)
			{
				Logger.LogWarning($"Settle: could not rename {Path.GetFileName(from)} → " +
					$"{Path.GetFileName(to)} — {ex.Message}");
			}
		}

		/// <summary>
		/// Probes the download directory for an already-settled image variant of
		/// <paramref name="fileName"/> (which is the raw <c>.unverified</c> name from 00).
		/// Mirrors the HTML and PDF already-settled fallbacks in
		/// <see cref="Step_SettleExtensions"/>: when the first settle pass renamed the
		/// file to its sniffed image extension, the second pass (force=true, post-crawl)
		/// will not re-enter the classification branch (the <c>.unverified</c> file no
		/// longer exists on disk), so the projection into 01 must recover the
		/// post-rename name from the file system rather than fall back to the stale
		/// <c>.unverified</c> from 00. Returns the bare extension (no dot) for the
		/// first variant found, or null if none of the recognised image extensions
		/// exists on disk under <paramref name="fileName"/>'s base.
		/// </summary>
		private static string? TryFindSettledImageExtension(string downloadDirectory, string fileName)
		{
			// Order mirrors FileTypeClassifier.SniffedImageExtension preference. Probe each extension;
			// the first one present on disk wins. In practice only one will exist (the
			// settle pass picked exactly one based on the sniffed magic bytes).
			foreach (var ext in new[] { "jpg", "png", "gif", "webp" })
			{
				var candidate = Path.Combine(downloadDirectory, Path.ChangeExtension(fileName, ext));
				if (File.Exists(candidate))
				{
					return ext;
				}
			}
			return null;
		}

		// ── Step 6: Redirect analysis ────────────────────────────────────────

		private static void Step_AnalyseRedirects(CrawlerRunContext ctx)
		{
			Logger.LogInfo($"Analyzing redirects from {ctx.CrawlerLogPath} ...");
			RedirectAnalyzer.AnalyzeRedirects(ctx.CrawlerLogPath, ctx.RedirectAnalysisPath);
			Logger.LogInfo($"Redirect analysis of {ctx.CrawlerLogPath} complete results saved to {ctx.RedirectAnalysisPath}.");
			ConsoleUi.WriteStepRow("Redirects", "analysed");
		}

		// ── Step 7: First lookup-file pass ───────────────────────────────────

		/// <summary>
		/// [KEEP] First CreateLookupFile pass — runs before log 05 so that
		/// CompareCrawlAndContent can read the index. Contains only "discovery"
		/// entries from the normal crawl. A second pass runs after the post-crawl
		/// pass to add "list" entries from 05-not-directly-crawlable.log.
		/// </summary>
		private static void Step_CreateLookupFileFirstPass(CrawlerRunContext ctx, Config config)
		{
			Logger.LogInfo("Creating lookup file (first pass — normal crawl)...");
			CrawlIndex.CreateLookupFile(ctx.CrawlerLogPath, ctx.CrawlerLogIndexPath,
				config.ResolvedDegreeOfParallelism);
			Logger.LogInfo("Lookup file created.");
			ConsoleUi.WriteStepRow("Crawler index", "built");
		}

		// ── Step 8: Crawler-index integrity check ────────────────────────────

		private static void Step_CheckCrawlerIndexIntegrity(CrawlerRunContext ctx, Config config)
		{
			// Verify every downloaded HTML file has a corresponding index entry.
			// Orphans (no URL mapping) are renamed to .bak for safe recovery.
			CrawlerIndexIntegrityCheck(
				ctx.FileDownloadDirectory,
				ctx.CrawlerLogIndexPath,
				config.FilePattern,
				CrawlerContext.Silent);
		}

		// ── Step 9: Content list 04/05 logs ──────────────────────────────────

		/// <summary>
		/// [KEEP] 04-full-content.log and 05-not-directly-crawlable.log are written
		/// here, on the first (normal) crawl pass only. The post-crawl list download
		/// is handled separately in Step 10 and never regenerates these logs — 05 is
		/// the input to the post-crawl pass, not an output of it.
		/// </summary>
		private static void Step_WriteContentListLogs(CrawlerRunContext ctx, Config config)
		{
			var list = config.CmsContentList;
			var csvPath = list?.Path ?? string.Empty;
			if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
			{
				ConsoleUi.WriteStepRow("Content lists (04/05)", "skipped", dimmed: true);
				return;
			}

			Logger.LogInfo($"Writing 04-full-content.log based on {csvPath}");
			Content.Listing(
				ctx.FullContentLogPath,
				csvPath,
				list!.RowFilter,
				list.ColumnDelimiter,
				list.ColumnIndex,
				list.ValuePrefixReplace,
				list.ValueSuffix,
				list.RowsToExclude,
				list.RowNegativeFilter,
				CrawlerContext.Silent
			);

			Logger.LogInfo($"Writing 05-not-directly-crawlable.log based on {csvPath} in compared to {ctx.CrawlerLogIndexPath}");
			Content.CompareCrawlAndContent(
				ctx.CrawlerLogIndexPath, ctx.FullContentLogPath,
				ctx.ContentCrawlCompareLogFile, config.Url);
			ConsoleUi.WriteStepRow("Content lists (04/05)", "written");
		}

		// ── Step 10: Post-crawl pass + second lookup ─────────────────────────

		/// <summary>
		/// Post-crawl pass: download pages from 05-not-directly-crawlable.log
		/// that were not reached via normal link following. These pages exist in
		/// the CMS content list but have no inbound links from crawled pages.
		/// They are treated identically to normally discovered pages for spell-check,
		/// quality analysis, 404 checking, and canonical analysis.
		///
		/// [KEEP] Must run AFTER log 05 is written and BEFORE the second CreateLookupFile
		/// so that 02-crawler-index.log is built from the complete set of downloaded files.
		/// [KEEP] Uses config.Url as prefix to resolve relative paths from log 05.
		/// Source column in 01-crawler.log will be "list" to distinguish from "discovery".
		/// [KEEP] Guarded by CmsContentList.PostCrawlPass — disabled by default. Enable only when
		/// CmsContentList file is known to be complete and accurate.
		/// [KEEP] Also guarded by !ctx.IsDebugSession — post-crawl pass must not run
		/// during debug replay (DebugDisableCrawl=true) since no new crawl happened
		/// and the download folder is already complete from a previous run.
		/// </summary>
		private static async Task Step_PerformPostCrawlPass(CrawlerRunContext ctx, Config config)
		{
			// Skip-decision extracted into a named, testable helper.
			// The original inline expression was:
			//     if (!config.CmsContentList?.PostCrawlPass ?? false
			//         || ctx.IsDebugSession
			//         || !File.Exists(ctx.ContentCrawlCompareLogFile))
			//         return;
			// which LOOKED like it skipped on any of the three conditions, but
			// the actual C# precedence is `||` higher than `??`, so it parsed as
			//     (!config.CmsContentList?.PostCrawlPass)
			//         ?? (false || ctx.IsDebugSession || !File.Exists(...))
			// With PostCrawlPass=true the left side evaluated to (bool?)false —
			// not null — so `??` short-circuited and the entire right side
			// (including the IsDebugSession check) was DEAD CODE. The post-crawl
			// pass ran in debug-replay sessions regardless of DebugDisableCrawl,
			// producing surprise downloads on what was supposed to be a no-network
			// replay run. The fix and its test are in ShouldSkipPostCrawlPass.
			if (ShouldSkipPostCrawlPass(ctx, config, File.Exists(ctx.ContentCrawlCompareLogFile)))
			{
				return;
			}

			// [KEEP] Stale-CSV gate decision was made earlier in
			// Step_CheckCmsContentListFreshness. In silent mode + stale CSV
			// that step sets SuppressPostCrawlPass=true and we honor it here.
			// Interactive [A] aborts the whole run before reaching this step;
			// interactive [I] proceeds without setting the flag. Fresh CSV
			// or check-disabled also leaves the flag clear.
			if (ctx.SuppressPostCrawlPass)
			{
				Logger.LogInfo("Post-crawl pass skipped (stale CmsContentList, silent mode).");
				return;
			}

			// [KEEP] Ensure Crawl is initialized before post-crawl pass. In a normal
			// non-debug run Initialize was already called in Step 5, but this guard
			// protects against any edge case where the client is not yet set up.
			// Credentials come from ctx (resolved once at startup), NOT config —
			// the post-crawl pass must authenticate identically to the main crawl.
			string? effectiveProxyUrlForList = config.UseProxy ? config.ProxyUrl : null;
			Crawl.Initialize(effectiveProxyUrlForList, ctx.ProxyUser, ctx.ProxyPassword,
				config.MaxConcurrentPageDownloads, config.MaxConcurrentAssetDownloads);

			var listPaths = File.ReadAllLines(ctx.ContentCrawlCompareLogFile, Encoding.UTF8)
				.Where(l => l.Length > 0 && l.StartsWith('/'))
				.Select(l => config.Url.TrimEnd('/') + l.Trim())
				// [KEEP] Security boundary — post-crawl list paths are validated against
				// the primary domain before downloading. Subdomains are not expected here
				// since log 05 is derived from the CMS CSV which is www-domain only.
				.Where(u => Validity.IsInDownloadScope(u, new Uri(config.Url).GetLeftPart(UriPartial.Authority), config.DownloadExclusions))
				.ToList();

			if (listPaths.Count == 0)
			{
				return;
			}

			// Site-identity banner for the post-crawl pass — matches the main-crawl
			// banner so a multi-phase run is visually consistent. Interactive only.
			if (!CrawlerContext.Silent)
			{
				ConsoleUi.WriteHeader();
				ConsoleUi.WriteEmphasis(config.ResolvedSiteLabel);
				ConsoleUi.WriteFooter();
			}

			Logger.LogInfo($"Post-crawl pass: downloading {listPaths.Count} page(s) from 05-not-directly-crawlable.log.");

			CancellationTokenSource? listProgressCts = CrawlerContext.Silent
				? null : StartCrawlProgressDisplay(ctx.FileDownloadDirectory);

			var listTasks = listPaths.Select(u => Crawl.DownloadWebsiteAsync(
				u, ctx.SaveDirectory, ctx.CrawlerRawLogPath, config.Url,
				config.DownloadExclusions,
				config.ModalQueryParameters, "list",
				config.ExtendedCrawlJsonPathPrefixes,
				config.UrlSubdomainsAllowed)).ToList();
			await Task.WhenAll(listTasks);

			listProgressCts?.Cancel();
			listProgressCts?.Dispose();

			// Write second completed marker so crawl integrity check passes.
			// Written to the RAW log (00); settle projects it into 01 below.
			CrawlLogWriter.Write(config.Url, "completed", "post-crawl-pass", ctx.CrawlerRawLogPath);
			Logger.LogInfo("Post-crawl pass complete.");

			// Settle the newly-downloaded list pages and regenerate 01 from the
			// full 00 (first-pass files are already settled and are left untouched).
			Step_SettleExtensions(ctx, config, force: true);

			// [KEEP] Second CreateLookupFile pass — only runs when PostCrawlPass is
			// true and list pages were actually downloaded. Rebuilds the index to include
			// "list" entries so Cache and all downstream analysis see the full page set.
			Logger.LogInfo("Rebuilding lookup file (second pass — includes post-crawl list entries)...");
			CrawlIndex.CreateLookupFile(ctx.CrawlerLogPath, ctx.CrawlerLogIndexPath, config.ResolvedDegreeOfParallelism);
			Logger.LogInfo("Lookup file rebuilt.");
		}

		/// <summary>
		/// Pure skip-decision for <see cref="Step_PerformPostCrawlPass"/>.
		/// Returns true when ANY of these conditions hold:
		///   * The post-crawl pass is disabled (CmsContentList missing or
		///     CmsContentList.PostCrawlPass=false).
		///   * The run is a debug replay (ctx.IsDebugSession=true). No network
		///     traffic must originate from a replay — that's the whole point of
		///     DebugDisableCrawl.
		///   * The compare log doesn't exist (Step_WriteContentListLogs didn't
		///     produce 05-not-directly-crawlable.log, e.g. because the CSV
		///     wasn't configured at all).
		///
		/// Extracted from <see cref="Step_PerformPostCrawlPass"/> when
		/// the inline expression `!config.CmsContentList?.PostCrawlPass ?? false
		/// || ctx.IsDebugSession || ...` was found to parse with `||` binding
		/// TIGHTER than `??`, making the IsDebugSession clause dead code. The
		/// extraction names the decision and lets unit tests cover the
		/// truth-table explicitly (see CrawlOrchestratorPostCrawlPassGuardTests).
		///
		/// File.Exists is passed as a parameter rather than called inline so
		/// the function stays pure and testable without I/O.
		/// </summary>
		internal static bool ShouldSkipPostCrawlPass(
			CrawlerRunContext ctx,
			Config config,
			bool compareLogExists)
		{
			bool postCrawlPassEnabled = config.CmsContentList?.PostCrawlPass ?? false;
			if (!postCrawlPassEnabled)
			{
				return true;   // feature disabled
			}

			if (ctx.IsDebugSession)
			{
				return true;   // replay — no downloads
			}

			if (!compareLogExists)
			{
				return true;   // nothing to compare against
			}

			return false;
		}

		// ── Private helpers (moved from Program.cs) ──────────────────────────

		/// <summary>
		/// Verifies every downloaded HTML file has a corresponding entry in
		/// 02-crawler-index.log. Orphan files are renamed to .ext.bak for safe
		/// recovery in interactive mode; silent mode logs and skips.
		/// Pure detection lives in OrphanFiles.Detect; the interactive prompt
		/// loop lives in InteractiveTriage.ResolveOrphans.
		/// </summary>
		private static void CrawlerIndexIntegrityCheck(
			string downloadDirectory,
			string indexPath,
			string filePattern,
			bool silent)
		{
			var orphans = OrphanFiles.Detect(downloadDirectory, indexPath, filePattern);

			if (orphans.Count == 0)
			{
				Logger.LogInfo("Crawler index integrity check passed — all files accounted for.");
				ConsoleUi.WriteStepRow("Index integrity", "passed");
				return;
			}

			// Derive backup extension from FilePattern: "*.html" → ".html.bak"
			var ext = filePattern.TrimStart('*');  // e.g. ".html"
			var bakExt = ext + ".bak";                // e.g. ".html.bak"

			Logger.LogWarning($"Crawler index integrity: {orphans.Count} file(s) have no index entry.");
			ConsoleUi.WriteStepRow("Index integrity", $"{orphans.Count} orphan(s)", dimmed: true);

			if (silent)
			{
				// Silent mode — warn and leave files untouched.
				foreach (var f in orphans)
				{
					Logger.LogWarning($"  Orphan (silent, skipped): {f}");
				}

				return;
			}

			InteractiveTriage.ResolveOrphans(orphans, downloadDirectory, bakExt);
		}

		/// <summary>
		/// Starts a background task that prints live download progress every 2 seconds.
		/// Returns a CancellationTokenSource — cancel it to stop the display.
		/// </summary>
		private static CancellationTokenSource StartCrawlProgressDisplay(string downloadDirectory)
		{
			var cts = new CancellationTokenSource();
			var token = cts.Token;

			Task.Run(async () =>
			{
				Queue<(DateTime Time, long Bytes)> byteSamples = [];

				while (!token.IsCancellationRequested)
				{
					await Task.Delay(2000, token).ContinueWith(_ => { }); // swallow cancellation
					if (token.IsCancellationRequested)
					{
						break;
					}

					if (!Directory.Exists(downloadDirectory))
					{
						continue;
					}

					var files = Directory.GetFiles(downloadDirectory);
					var totalBytes = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
					var totalMb = totalBytes / 1_048_576.0;
					var now = DateTime.UtcNow;

					byteSamples.Enqueue((now, totalBytes));
					while (byteSamples.Count > 4)
					{
						byteSamples.Dequeue();
					}

					double mbPerSec = 0;
					if (byteSamples.Count >= 2)
					{
						var (oldestTime, oldestBytes) = byteSamples.Peek();
						var elapsed = (now - oldestTime).TotalSeconds;
						if (elapsed > 0)
						{
							mbPerSec = (totalBytes - oldestBytes) / 1_048_576.0 / elapsed;
						}
					}

					lock (Logger.ConsoleLock)
					{
						ConsoleUi.WriteProgress($"{files.Length} files  |  {totalMb:F1} MB  |  {mbPerSec:F2} MB/s");
					}
				}

				// Clear the progress line when done.
				lock (Logger.ConsoleLock)
				{
					ConsoleUi.ClearProgress();
				}
			}, token);

			return cts;
		}

		// ── Project-size readout (best-effort; never blocks the run) ──────────

		/// <summary>
		/// Scans the download tree and prints a size/file-count row plus a
		/// per-extension breakdown row under the PREPARE banner. Best-effort: any
		/// I/O failure is swallowed so a size readout never aborts a run.
		/// </summary>
		private static void ReportProjectSize(string downloadDirectory)
		{
			try
			{
				if (string.IsNullOrEmpty(downloadDirectory) || !Directory.Exists(downloadDirectory))
				{
					return;
				}

				long totalBytes = 0;
				int totalFiles = 0;
				var byExt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
				foreach (var file in Directory.EnumerateFiles(downloadDirectory, "*", SearchOption.AllDirectories))
				{
					totalFiles++;
					try { totalBytes += new FileInfo(file).Length; } catch { /* skip unreadable */ }
					var ext = Path.GetExtension(file);
					if (string.IsNullOrEmpty(ext)) { ext = "(none)"; }
					byExt[ext] = byExt.TryGetValue(ext, out var c) ? c + 1 : 1;
				}

				// The scan itself is not a pipeline step — don't bill its time to the row.
				ConsoleUi.MarkStep();
				ConsoleUi.WriteStepRow("Project size", $"{totalFiles:N0} files · {FormatBytes(totalBytes)}");

				// Sidecar/metadata files (e.g. per-page .header dumps) are kept in the
				// picture but pushed to the tail so content types lead.
				var sidecarExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".header" };
				var content = byExt.Where(kv => !sidecarExts.Contains(kv.Key))
					.OrderByDescending(kv => kv.Value)
					.Take(6);
				var sidecars = byExt.Where(kv => sidecarExts.Contains(kv.Key))
					.OrderByDescending(kv => kv.Value);
				var breakdown = string.Join(" · ",
					content.Concat(sidecars).Select(kv => $"{kv.Key} {kv.Value:N0}"));
				if (breakdown.Length > 0)
				{
					ConsoleUi.WriteStepRow("By type", breakdown, dimmed: true);
				}
			}
			catch
			{
				// best-effort readout
			}
		}

		private static string FormatBytes(long bytes)
		{
			string[] units = { "B", "KB", "MB", "GB", "TB" };
			double size = bytes;
			int u = 0;
			while (size >= 1024 && u < units.Length - 1)
			{
				size /= 1024;
				u++;
			}
			return $"{size:0.#} {units[u]}";
		}
	}
}
