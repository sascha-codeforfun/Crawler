using System.Diagnostics.CodeAnalysis;
using System.Text;
using Crawler.Logging;
using Crawler.Lexicon;
using Crawler.Html;
using Crawler.Urls;

namespace Crawler
{
	// ── AnalysisPipeline ──────────────────────────────────────────────────────
	//
	// The pipeline operates on a complete, indexed downloaded snapshot. All
	// path mutation happened earlier (during interactive prompts in phase 1);
	// ctx fields are read-only here.
	//
	// Three issue collections — pdfIssues, base64Issues, and canonicalIssues —
	// must survive to the end-of-run merge in Program.cs. They are returned
	// via AnalysisPipelineResult rather than mutating shared state.
	//
	// ─────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Values that must survive AnalysisPipeline.RunAsync to reach the
	/// end-of-run IssueTracking merge in Program.cs. Everything else is
	/// either in ctx, on disk, or already handled by the spell-check and
	/// content-quality triage helpers.
	///
	/// CanonicalIssues are surfaced here (previously merged mid-
	/// pipeline) — see Step_AnalyseCanonicals for the rationale.
	/// </summary>
	internal sealed record AnalysisPipelineResult(
		List<IssueTracking.IssueRecord> PdfIssues,
		List<IssueTracking.IssueRecord> Base64Issues,
		List<IssueTracking.IssueRecord> CanonicalIssues,
		List<IssueTracking.IssueRecord> AssetIssues);

	internal static class AnalysisPipeline
	{
		// ── Spell-check shared state ─────────────────────────────────────────
		// Populated by Step_LoadDictionaries, read by Step_RunSpellCheck and
		// its helpers. Static because the spell-check helpers are static and
		// these need to be reachable without threading them through every call.

		private static readonly Dictionary<string, Bundle> _dictionaryBundles =
			new(StringComparer.OrdinalIgnoreCase);
		private static List<string> _spellCheckExcludedUrls = [];
		private static readonly object _fileLock = new();

		// ── Top-level orchestrator ───────────────────────────────────────────

		/// <summary>
		/// Runs the full post-crawl analysis pipeline. Returns null if the
		/// run should abort before the end-of-run merge (e.g. dictionary
		/// validation failed). Returns the issue collections that need to
		/// survive to the end-of-run merge otherwise.
		/// </summary>
		[RequiresUnreferencedCode("Calls steps that use reflection-based config loading.")]
		internal static async Task<AnalysisPipelineResult?> RunAsync(
			CrawlerRunContext ctx,
			Config config,
			string url,
			string urlDirectory,
			string workingFolder,
			ParallelOptions options)
		{
			Step_LoadUrlCache(ctx);

			// Surface ticket-metadata-lookup config mistakes early — empty
			// fields in tickets are otherwise silent at runtime. Gated on
			// !Silent because we want this visible in human-facing runs.
			if (!CrawlerContext.Silent)
			{
				SpellMetadataLookup.LogMetadataLookupDiagnostics(config);
			}

			// Analysis phase: the raw timestamped step lines still go to the log file,
			// but their console echo is muted so the operator sees a clean per-step
			// summary banner instead (each step prints one aligned row via ConsoleUi).
			if (!CrawlerContext.Silent)
			{
				ConsoleUi.WriteHeader("ANALYSIS");
			}

			List<IssueTracking.IssueRecord> canonicalIssues;
			List<IssueTracking.IssueRecord> pdfIssues;
			List<IssueTracking.IssueRecord> base64Issues;
			List<IssueTracking.IssueRecord> assetIssues;
			using (Logger.QuietConsole())
			{
				Step_GenerateSitemap(ctx, config);
				Step_Process404s(ctx, url, config);
				await Step_ExtractSeoData(ctx, config);
				canonicalIssues = Step_AnalyseCanonicals(ctx, config);

				pdfIssues = Step_AnalysePdfQuality(ctx);
				base64Issues = Step_ExtractBase64Assets(ctx, config);
				assetIssues = Step_AnalyseAssetQuality(ctx, config);

				Step_AnalyseResourceBloat(ctx, config);
				Step_AnalyseResourceBloatBaseline(ctx, config);

				Step_BoilerplatePrune(ctx, config);
				Step_ScanSelfLinks(ctx, config);
				Step_RunContentQualityChecks(ctx, config);
			}

			Step_RunContentQualityTriage(ctx, config);

			// ── SPELL-CHECK phase ────────────────────────────────────────────
			// Load + run are muted on console (file log keeps the timestamped lines);
			// a compact block reports the configured languages and finding count.
			if (!CrawlerContext.Silent)
			{
				ConsoleUi.WriteHeader("SPELL-CHECK");
			}
			bool dictionariesLoaded;
			using (Logger.QuietConsole())
			{
				dictionariesLoaded = Step_LoadDictionaries(urlDirectory, config);
			}
			// Step_LoadDictionaries can fail config-validation. Bail out before
			// spell-check rather than continuing with no dictionaries.
			if (!dictionariesLoaded)
			{
				return null;
			}
			ConsoleUi.WriteStepRow("Languages",
				_dictionaryBundles.Count > 0 ? string.Join(" · ", _dictionaryBundles.Keys) : "(none)");
			ConsoleUi.WriteStepRow("Dictionaries", $"{_dictionaryBundles.Count} loaded");

			using (Logger.QuietConsole())
			{
				await Step_RunSpellCheck(ctx, config);
			}
			ConsoleUi.WriteStepRow("Spell-check", $"{ctx.LastSpellTickets?.Count ?? 0} finding(s)");

			// Opt-in diagnostic, default OFF: harvest all inline <script> bodies into one blob (log 28)
			// and scan it as a single stream (log 29). Fully isolated from the per-page engine above.
			if (config.SpellCheckEngine.SpellCheckJavaScript.BulkScanPageScript)
			{
				int blocks, findings;
				using (Logger.QuietConsole())
				{
					(blocks, findings) = Step_BulkScanPageScript(ctx, config);
				}
				ConsoleUi.WriteStepRow("Bulk page-script scan", $"{findings} finding(s) · {blocks} block(s)");
			}

			// Opt-in diagnostic, default OFF: spell-check the raw external .js files in the crawl
			// directory (the big bundles), reusing the inline scanner's core. Filename provenance,
			// one log (30). Config validation halts the run if this is on with no dictionaries.
			if (config.SpellCheckEngine.SpellCheckJavaScript.ScanScriptFilesInDownload)
			{
				int files, jsFindings;
				using (Logger.QuietConsole())
				{
					(files, jsFindings) = Step_ScanJsFiles(ctx, config);
				}
				ConsoleUi.WriteStepRow("JS-file scan", $"{jsFindings} finding(s) · {files} file(s)");
			}

			Step_RunSpellCheckTriage(ctx, config, urlDirectory);

			// ── OUTPUT phase ─────────────────────────────────────────────────
			if (!CrawlerContext.Silent)
			{
				ConsoleUi.WriteHeader("OUTPUT");
			}
			using (Logger.QuietConsole())
			{
				Step_GenerateTicketDrafts(ctx, config);
				Step_CopySitemapToSiteRoot(ctx, workingFolder, urlDirectory);
			}

			// Dead last: dictionary maintenance does not influence this run's results
			// (it tidies the user/site dictionaries for the next run). Orphans come from
			// the cross-off usage recorded during spell-check above.
			Step_MaintainDictionaries(ctx, config, urlDirectory);

			return new AnalysisPipelineResult(pdfIssues, base64Issues, canonicalIssues, assetIssues);
		}

		// ── Step 1: Index load ───────────────────────────────────────────────

		private static void Step_LoadUrlCache(CrawlerRunContext ctx)
		{
			ConsoleUi.MarkStep();
			using (Logger.QuietConsole())
			{
				Cache.Load(ctx.CrawlerLogIndexPath);
			}
		}

		// ── Step 2: Sitemap ──────────────────────────────────────────────────

		private static void Step_GenerateSitemap(CrawlerRunContext ctx, Config config)
		{
			Logger.LogInfo("Generate sitemap.");
			Sitemap.Generate(
				ctx.FileDownloadDirectory,
				ctx.SitemapPath,
				config.SitemapGeneratorExclusions,
				config.SitemapGenerateForcedInclusions,
				config.FilePattern,
				config.ResolvedDegreeOfParallelism);
			ConsoleUi.WriteStepRow("Sitemap", $"{CountSitemapEntries(ctx.SitemapPath)} entries");
		}

		// ── Step 3: 404 processing ───────────────────────────────────────────

		private static void Step_Process404s(CrawlerRunContext ctx, string url, Config config)
		{
			Logger.LogInfo("Processing 404s.");
			var list404 = CrawlIndex.GetLinesContainingKey(ctx.CrawlerLogPath, "NotFound");
			FileIo.WriteAllLinesWithRetry(ctx.ErrorLogPath, list404, Path.GetFileName(ctx.ErrorLogPath));

			if (list404.Count > 0)
			{
				var listAllPagesHtml = list404
					.Select(item => item.Trim().Replace(url, string.Empty))
					.ToList();
				var listPagesContaining404Link = HtmlSearch.FindFilesContaining(
					listAllPagesHtml, ctx.FileDownloadDirectory, config.FilePattern);
				ErrorSourcesLog.Write(listPagesContaining404Link, ctx.ErrorSourcesCsvBasePath);
			}
			else
			{
				Logger.LogInfo("No 404s found in crawler log.");
			}
			ConsoleUi.WriteStepRow(
				"Broken links (404)",
				list404.Count > 0 ? $"{list404.Count} source(s)" : "none",
				dimmed: list404.Count == 0);
		}

		// ── Step 4: SEO data extraction ──────────────────────────────────────

		private static async Task Step_ExtractSeoData(CrawlerRunContext ctx, Config config)
		{
			if (!ctx.IsDebugSession || !File.Exists(ctx.SeoDataPath))
			{
				Logger.LogInfo("Extracting SEO relevant data from HTML.");
				await SEO.ExtractDataAsync(ctx.FileDownloadDirectory, ctx.SeoDataPath, config.FilePattern);
				ConsoleUi.WriteStepRow("SEO data", $"{CountDataLines(ctx.SeoDataPath)} page(s)");
			}
			else
			{
				Logger.LogInfo("Skipping SEO extraction (debug session, 08-seo-data.csv exists).");
				ConsoleUi.WriteStepRow("SEO data", "skipped (cached)", dimmed: true);
			}
		}

		// ── Step 5: Canonical link analysis ──────────────────────────────────
		// Returns canonical issues for end-of-run merge with the full detection
		// set. See note inside Step_AnalyseCanonicals.

		// Canonical issues are returned to caller (like PDF and
		// Base64 issues) and merged into IssueTracking only at end-of-run.
		// Previously this step did an intermediate Save with Merge(existing,
		// canonicalIssuesOnly), which incorrectly classified all non-canonical
		// existing records as 'fixed' (because they weren't in the partial
		// detection set), triggering spurious 'fixed → reopened' transitions
		// when the end-of-run Merge re-detected them with the full set.
		// See Step_AnalysePdfQuality for the documented version of this same
		// concern.

		private static List<IssueTracking.IssueRecord> Step_AnalyseCanonicals(CrawlerRunContext ctx, Config config)
		{
			Logger.LogInfo("Running canonical link analysis.");
			var canonicalIssues = CanonicalAnalyzer.Analyse(
				ctx.FileDownloadDirectory,
				ctx.CrawlerLogIndexPath,
				ctx.CanonicalCsvBasePath,
				config.Url,
				config.UrlSubdomainsAllowed,
				config.FilePattern);
			if (canonicalIssues.Count > 0)
			{
				Logger.LogInfo($"Canonical analysis: {canonicalIssues.Count} issue(s) for end-of-run merge.");
			}
			ConsoleUi.WriteStepRow("Canonical links", $"{canonicalIssues.Count} issue(s)");
			return canonicalIssues;
		}

		// ── Step 6: PDF quality analysis ─────────────────────────────────────

		private static List<IssueTracking.IssueRecord> Step_AnalysePdfQuality(CrawlerRunContext ctx)
		{
			Logger.LogInfo("Running PDF quality analysis.");
			// [KEEP] pdfIssues returned from this step so it's available at end-of-run merge.
			// Do NOT merge into IssueTracking here — the end-of-run Merge handles all
			// issue types together, preventing premature "fixed" status from the main
			// Merge not finding PDFQUALITY issues in its detectedIssues list.
			var pdfIssues = PdfQualityAnalyzer.Analyse(
				ctx.FileDownloadDirectory, ctx.PdfQualityCsvBasePath, ctx.PdfRemediationCsvBasePath);
			Logger.LogInfo($"PDF quality analysis complete: {pdfIssues.Count} issue(s) found.");
			ConsoleUi.WriteStepRow("PDF quality", $"{pdfIssues.Count} issue(s)");
			return pdfIssues;
		}

		// ── Step 6b: Asset quality analysis (images) ─────────────────────────

		private static List<IssueTracking.IssueRecord> Step_AnalyseAssetQuality(
			CrawlerRunContext ctx, Config config)
		{
			// [KEEP] assetIssues returned from this step so it's available at the
			// end-of-run merge — same pattern as pdfIssues. Do NOT merge into
			// IssueTracking here, or the main Merge (not finding ASSET issues in
			// its detectedIssues list) would prematurely mark them fixed.
			if (!config.AssetQuality.IsEnabled)
			{
				Logger.LogInfo("Asset quality analysis disabled — skipping.");
				ConsoleUi.WriteStepRow("Asset quality", "disabled", dimmed: true);
				return [];
			}
			Logger.LogInfo("Running asset quality analysis.");
			var assetIssues = AssetQuality.Analyse(
				ctx.FileDownloadDirectory, ctx.AssetQualityCsvBasePath, config.AssetQuality);
			Logger.LogInfo($"Asset quality analysis complete: {assetIssues.Count} issue(s) found.");
			ConsoleUi.WriteStepRow("Asset quality", $"{assetIssues.Count} finding(s)");
			return assetIssues;
		}

		// ── Step 7: Base64 asset extraction ──────────────────────────────────

		private static List<IssueTracking.IssueRecord> Step_ExtractBase64Assets(
			CrawlerRunContext ctx, Config config)
		{
			// Base64 asset extraction — runs outside the download pipeline.
			// Scans configured file types for embedded data URIs, decodes them,
			// and saves to base64assets/ for manual review. Safe to re-run.
			// [KEEP] base64Issues returned from this step so it survives to the
			// end-of-run IssueTracking merge — same pattern as pdfIssues.
			var base64Issues = new List<IssueTracking.IssueRecord>();
			if (config.Base64AssetFileExtensions.Count > 0)
			{
				Logger.LogInfo("Extracting embedded Base64 assets...");
				base64Issues = Base64AssetExtractor.Extract(
					ctx.FileDownloadDirectory,
					ctx.Base64AssetsDirectory,
					ctx.Base64AssetsLogPath,
					config.Base64AssetFileExtensions,
					config.BloatThresholdBase64Kilobytes * 1024);
				ConsoleUi.WriteStepRow("Base64 assets", $"{base64Issues.Count} over threshold");
			}
			else
			{
				ConsoleUi.WriteStepRow("Base64 assets", "disabled", dimmed: true);
			}
			return base64Issues;
		}

		// ── Step 8: Resource bloat (page-centric) ────────────────────────────

		private static void Step_AnalyseResourceBloat(CrawlerRunContext ctx, Config config)
		{
			// Resource bloat analysis — page-centric view of JS/CSS size,
			// Base64 inlined assets, inlined CSS, and JSON blobs in HTML.
			// Runs after Base64 extraction so log 19 is available.
			Logger.LogInfo("Analysing resource bloat...");
			ResourceBloatAnalyzer.Analyse(
				ctx.FileDownloadDirectory,
				ctx.Base64AssetsLogPath,
				ctx.ResourceBloatLogPath,
				config.Url,
				config.FileExtension,
				config.BloatThresholdBase64Kilobytes * 1024,
				config.BloatThresholdJsKilobytes * 1024,
				config.BloatThresholdCssKilobytes * 1024);
		}

		// ── Step 9: Resource bloat (baseline-adjusted) ───────────────────────

		private static void Step_AnalyseResourceBloatBaseline(CrawlerRunContext ctx, Config config)
		{
			// Resource bloat baseline analysis — reads log 20 and produces
			// log 21 with baseline-adjusted heavy hitters for executives.
			Logger.LogInfo("Analysing resource bloat above baseline...");
			ResourceBloatBaselineAnalyzer.Analyse(
				ctx.FileDownloadDirectory,
				ctx.ResourceBloatLogPath,
				ctx.ResourceBloatBaselineLogPath,
				config.Url,
				config.FileExtension,
				config.BloatThresholdBase64Kilobytes * 1024,
				config.BloatThresholdJsKilobytes * 1024,
				config.BloatThresholdCssKilobytes * 1024,
				config.BloatThresholdAboveBaselineKilobytes * 1024);
		}

		// ── Step 11b: Boilerplate prune (shared pruned tree) ─────────────────

		private static void Step_BoilerplatePrune(CrawlerRunContext ctx, Config config)
		{
			// Writes the shared PRUNED tree: each downloaded page minus its declared boilerplate,
			// kept whole on each group's check pages. Consumed by the content-quality checks
			// (Step_RunContentQualityChecks); spell is rewired onto it in a later leg.
			Logger.LogInfo("Pruning boilerplate to shared pruned tree.");
			var resolver = new Crawler.Boilerplate.BoilerplateResolver(config.SpellCheckEngine.BoilerplateGroups);
			Crawler.Boilerplate.BoilerplateSimplifier.Run(
				ctx.FileDownloadDirectory,
				ctx.FilePrunedDirectory,
				resolver,
				CrawlIndex.LookUpUrlForFile,
				config.FilePattern,
				config.ResolvedDegreeOfParallelism);
			var boilerGroups = config.SpellCheckEngine.BoilerplateGroups;
			ConsoleUi.WriteStepRow("Boilerplate prune",
				$"{CountFiles(ctx.FilePrunedDirectory, config.FilePattern)} page(s) · " +
				$"{boilerGroups.Count} group(s) · {boilerGroups.Sum(g => g.BoilerplateSelectors.Count)} rule(s)");
		}

		// ── Step 12: Self-link scan ──────────────────────────────────────────

		private static void Step_ScanSelfLinks(CrawlerRunContext ctx, Config config)
		{
			// Self-link scan runs after the boilerplate prune — it reads the pruned HTML.
			// Pruned (not simplified/raw) on purpose: chrome self-links (nav active-item,
			// breadcrumb-to-self, footer) are legitimate, not the CMS authoring mistake this
			// hunts — editors lacking a real target sometimes link a page to itself to satisfy
			// the mandatory-link rule. Scanning pruned drops chrome as false positives, leaving
			// those content self-links.
			// [KEEP] Must not run before the prune completes or the pruned
			// folder will be empty and the log will contain no results.
			Logger.LogInfo("Checking for self linking pages.");
			SelfLinkScanner.FindSelfLinks(
				ctx.FilePrunedDirectory,
				ctx.SelfLinkAnalysisCsvBasePath,
				config.QueryStringsToIgnoreForSelfLinkDetermination,
				config.FilePattern,
				config.ResolvedDegreeOfParallelism,
				200);
			ConsoleUi.WriteStepRow("Self-links", $"{CountDataLines(ctx.SelfLinkAnalysisCsvBasePath + IssueLogWriter.CsvSemicolonSuffix)} found");
		}

		// ── Step 13: Content quality checks ──────────────────────────────────

		private static void Step_RunContentQualityChecks(CrawlerRunContext ctx, Config config)
		{
			Logger.LogInfo("Running content quality checks.");
			// Pass 1 (raw checks: unwanted patterns, CMS template defects, malformed
			// HTML/anchors) reads the raw download tree — these must reflect what the
			// server emitted, before any stripping. Pass 2 (structural checks: BareText,
			// MisplacedAnchors, etc.) reads the shared PRUNED tree (each page minus its
			// declared boilerplate) so it isn't re-checking the same boilerplate on every
			// page — inefficient and noisy.
			ctx.ContentQualityIssues = ContentQuality.Analyse(
				ctx.FilePrunedDirectory,
				ctx.FileDownloadDirectory,
				ctx.ContentQualityLogPath,
				config.ContentQuality,
				config.ResolvedDegreeOfParallelism,
				config.FilePattern,
				config.ContentQualityExcludedUrls,
				config.ContentUnwantedPatterns,
				ctx.CmsTemplateDefectsCsvBasePath,
				// D049: the quote detector resolves the declared language SET via the
				// shared resolver. Overrides correct the mislabelled branches and declare
				// multi-language pages; defaultLanguage is left empty (the param default)
				// so an undeclared page yields the empty set — no system anchor, so only
				// the structural quote checks run.
				config.SpellCheckEngine.PageLanguageOverrides);
		}

		// ── Step 14: Content quality triage (interactive) ────────────────────

		private static void Step_RunContentQualityTriage(CrawlerRunContext ctx, Config config)
		{
			if (config.EnableContentQualityTriage && !CrawlerContext.Silent && ctx.IsLatestSnapshotPath)
			{
				using (Logger.QuietConsole())
				{
					Logger.LogInfo("Running content quality triage.");
				}
				var qualityBoilerResolver = new Crawler.Boilerplate.BoilerplateResolver(
					config.SpellCheckEngine.BoilerplateGroups);
				var qualityTriageResults = ContentQualityTriage.Run(
					ctx.ContentQualityLogPath,
					LogFileNames.ContentQualityIssues,
					ctx.IssueTrackingPath,
					config.ContentQuality,
					ctx.FileDownloadDirectory,
					config.TriageUrlHighlight,
					qualityBoilerResolver,
					// D049: triage highlighting resolves language the same way the detector
					// does, so marked glyphs match the flag. Overrides applied; defaultLanguage
					// left empty (param default) for agnostic-on-undeclared.
					config.SpellCheckEngine.PageLanguageOverrides);
				if (qualityTriageResults.Count > 0)
				{
					// Use ApplyTriageDecisions (touches only the
					// triaged records' status/comment, leaves all other existing
					// records unchanged). Previously this used Merge, which
					// incorrectly classified all non-triaged existing records as
					// 'fixed' because they weren't in the partial decisions list.
					var existingForQuality = IssueTracking.Load(ctx.IssueTrackingPath);
					var mergedForQuality = IssueTracking.ApplyTriageDecisions(
						existingForQuality, qualityTriageResults);
					IssueTracking.Save(ctx.IssueTrackingPath, mergedForQuality);
					Logger.LogInfo($"Content quality triage: {qualityTriageResults.Count} issue(s) promoted to IssueTracking.log.");
				}
			}
		}

		// ── Step 17: Load dictionaries ───────────────────────────────────────

		/// <summary>
		/// Returns false if dictionary loading fails (typically: CharacterValidator
		/// found suspicious characters in a dictionary file). Caller should abort
		/// the run before spell-check.
		/// </summary>
		private static bool Step_LoadDictionaries(string urlDirectory, Config config)
		{
			Logger.LogInfo("Load dictionaries.");
			try
			{
				PreloadDictionaries(urlDirectory, config.CustomDictionaryFile, config.DictionaryBundles);
				_spellCheckExcludedUrls = config.SpellCheckExcludedUrls;

				// Fail-fast: every language named in PageLanguageOverrides must have a loaded bundle.
				// Validated here — right after dictionaries are loaded, before any page is processed —
				// so a misconfiguration halts immediately rather than silently checking fewer languages.
				Crawler.SpellCheck.PageLanguageResolver.ValidateBundles(
					config.SpellCheckEngine.PageLanguageOverrides, _dictionaryBundles);

				return true;
			}
			catch (InvalidOperationException ex)
			{
				// CharacterValidator throws this when suspicious characters are found
				// in a dictionary file. The red console block was already printed by
				// ValidateDictionaryFileHalt. Just wait for keypress and exit.
				Logger.LogError(ex.Message);
				if (!CrawlerContext.Silent)
				{
					ConsoleUi.PressEnterToExit();
				}
				return false;
			}
		}

		// ── Step 19: Dictionary maintenance (end of run) ─────────────────────

		/// <summary>
		/// End-of-run maintenance of the user and site dictionaries. Orphans are read
		/// from the cross-off usage recorder populated during spell-check
		/// (<see cref="UsageTracker"/>): any non-pinned user/site entry never
		/// consulted on a crawled page is an orphan. Redundant entries are computed from
		/// the loaded bundles. Pinned ('!') entries are exempt.
		///
		/// Mode (config.DictionaryMaintenance.Mode):
		///   Off         — does nothing.
		///   Report      — writes log 15 only; mutates nothing; runs in silent too.
		///   Interactive — offers each flagged word (remove/pin/keep) then backup+clean+sort;
		///                 non-silent only (a silent run with Mode=Interactive skips).
		/// Runs dead last, so it never affects this run's findings — it tidies the
		/// dictionaries for the next run.
		/// </summary>
		private static void Step_MaintainDictionaries(
			CrawlerRunContext ctx,
			Config config,
			string urlDirectory)
		{
			var maint = config.DictionaryMaintenance;
			var mode = (maint.Mode ?? "Off").Trim();

			bool report = string.Equals(mode, "Report", StringComparison.OrdinalIgnoreCase);
			bool interactive = string.Equals(mode, "Interactive", StringComparison.OrdinalIgnoreCase);

			if (!report && !interactive)
			{
				// Off, empty, or unrecognized → no-op.
				return;
			}

			// Interactive needs an operator at the console; never mutate dictionaries
			// unattended. A silent run with Mode=Interactive stands down entirely.
			if (interactive && CrawlerContext.Silent)
			{
				Logger.LogInfo(
					"Dictionary maintenance: Mode=Interactive but run is silent — skipping (no unattended mutation).");
				return;
			}

			if (!maint.UpdateUserDictionary && !maint.UpdateSiteSpecificDictionary)
			{
				Logger.LogInfo("Dictionary maintenance: neither dictionary selected — nothing to do.");
				return;
			}

			// Dictionary paths — same expressions Step_LoadDictionaries / PreloadDictionaries use.
			var userDictionaryPath = maint.UpdateUserDictionary
				? $"dictionaries\\{config.CustomDictionaryFile}"
				: string.Empty;
			var siteDictionaryPath = maint.UpdateSiteSpecificDictionary
				? $"dictionaries\\{urlDirectory}.dic"
				: string.Empty;

			// Snapshot the cross-off usage recorded during spell-check. Taken here, after
			// spell + triage, so the redundancy check's own Check calls below cannot
			// pollute it.
			var usedUser = UsageTracker.SnapshotUser();
			var usedSite = UsageTracker.SnapshotSite();

			var prefixesToStrip = config.SpellCheckWordPrefixesToStrip;
			var bundles = _dictionaryBundles.Values;

			if (!CrawlerContext.Silent)
			{
				ConsoleUi.WriteHeader("DICTIONARIES");
			}

			Audit.Analysis userAnalysis;
			Audit.Analysis siteAnalysis;
			using (Logger.QuietConsole())
			{
				var sw = System.Diagnostics.Stopwatch.StartNew();

				userAnalysis = string.IsNullOrEmpty(userDictionaryPath)
					? new Audit.Analysis([], [])
					: Audit.AnalyseFromUsage(userDictionaryPath, usedUser, prefixesToStrip, bundles);

				siteAnalysis = string.IsNullOrEmpty(siteDictionaryPath)
					? new Audit.Analysis([], [])
					: Audit.AnalyseFromUsage(siteDictionaryPath, usedSite, prefixesToStrip, bundles);

				Audit.WriteAnalysisReport(
					ctx.UserDictionaryOrphanWordsFilePath,
					userDictionaryPath,
					userAnalysis,
					siteDictionaryPath,
					siteAnalysis);

				sw.Stop();
				Logger.LogInfo(
					$"Dictionary maintenance analysis: {sw.ElapsedMilliseconds} ms " +
					$"(user: {userAnalysis.Orphaned.Count} orphan / {userAnalysis.Redundant.Count} redundant, " +
					$"site: {siteAnalysis.Orphaned.Count} orphan / {siteAnalysis.Redundant.Count} redundant).");
			}

			if (!string.IsNullOrEmpty(userDictionaryPath))
			{
				ConsoleUi.WriteStepRow("User dictionary",
					$"{Audit.CountDictionaryWords(userDictionaryPath)} words · " +
					$"{userAnalysis.Orphaned.Count} orphan(s) · {userAnalysis.Redundant.Count} redundant");
			}
			if (!string.IsNullOrEmpty(siteDictionaryPath))
			{
				ConsoleUi.WriteStepRow("Site dictionary",
					$"{Audit.CountDictionaryWords(siteDictionaryPath)} words · " +
					$"{siteAnalysis.Orphaned.Count} orphan(s) · {siteAnalysis.Redundant.Count} redundant");
			}

			if (report)
			{
				// Read-only: log 15 written above, dictionaries untouched.
				return;
			}

			// Interactive: offer + apply per dictionary.
			if (!string.IsNullOrEmpty(userDictionaryPath) && File.Exists(userDictionaryPath))
			{
				ApplyDictionaryTriage(userDictionaryPath, userAnalysis, maint.SortCulture);
			}

			if (!string.IsNullOrEmpty(siteDictionaryPath) && File.Exists(siteDictionaryPath))
			{
				ApplyDictionaryTriage(siteDictionaryPath, siteAnalysis, maint.SortCulture);
			}
		}

		/// <summary>
		/// Interactive removal/pin triage for one dictionary, then apply. CleanDictionary,
		/// PinWords, and SortDictionary each create their own numbered backup before
		/// writing, so no explicit backup is taken here (avoids duplicate backups). Sort
		/// runs only when something actually changed.
		/// </summary>
		private static void ApplyDictionaryTriage(
			string dictionaryPath,
			Audit.Analysis analysis,
			string sortCulture)
		{
			var (toRemove, toPin) = Audit.RunRemovalTriage(dictionaryPath, analysis);

			if (toRemove.Count == 0 && toPin.Count == 0)
			{
				return;
			}

			// Apply is a wall of per-word backup/remove/pin lines — keep it in the log
			// file but off the console; the green "Triage complete" line above already
			// summarises what was actioned.
			using (Logger.QuietConsole())
			{
				if (toRemove.Count > 0)
				{
					Audit.CleanDictionary(dictionaryPath, toRemove);
				}

				if (toPin.Count > 0)
				{
					Audit.PinWords(dictionaryPath, toPin);
				}

				Audit.SortDictionary(dictionaryPath, sortCulture);
			}
		}

		// ── Step 18: Spell-check ─────────────────────────────────────────────

		private static async Task Step_RunSpellCheck(
			CrawlerRunContext ctx,
			Config config)
		{
			Logger.LogInfo("Running spell-check.");

			// Cross-off dictionary maintenance: clear the usage recorder so it captures
			// only this run's user/site consultations (and so per-site runs in one
			// process do not leak usage between sites). Bundle.Check records
			// hits as the spell scan runs; Step_MaintainDictionaries reads them at the end.
			UsageTracker.Reset();

			// The new engine owns spell findings: it reads raw bytes from the download dir and
			// emits the ParallelStore views. Its in-memory tickets are the source of truth for
			// triage (stashed on ctx.LastSpellTickets); operator decisions live in IssueTracking.
			try
			{
				// File list comes straight from the download dir — the new engine reads raw bytes
				// from there and no longer borrows filenames from the (now-skipped) normalize pass.
				// download/ and simplified/ hold the identical filename set, so GetFiles enumerates
				// the same order the normalize-fed list had — the emitted views stay byte-identical.
				var newEngineFiles = Directory
					.GetFiles(ctx.FileDownloadDirectory, config.FilePattern)
					.Select(f => new Crawler.SpellCheck.NewSpellEngineRunner.FileInput
					{
						Filename = Path.GetFileName(f),
					});

				// Cross-pass dedup input: when enabled, build a matcher from this run's CQ findings so
				// the spell pass can mute the merged twin of a WORD_COLLISION CQ already reports.
				// Null when switched off or when CQ produced nothing — Run then mutes nothing.
				var wordCollisions = config.SpellCheckEngine.SuppressSpellFindingsCoveredByWordCollision
					? new Crawler.SpellCheck.WordCollisionMatcher(ctx.ContentQualityIssues)
					: null;

				// Cross-pass dedup (always on): mute spelling's twin of an anchor-severed word that
				// content-quality already reports as SPLIT_WORD_ANCHOR. Unconditional because the
				// gate is intrinsically surgical — a one-letter severed tail that rejoins to a real
				// word; see Crawler.Suppressions.AnchorSplitSpellSuppression for the full rationale.
				var anchorSplit = new Crawler.Suppressions.AnchorSplitSpellSuppression(ctx.ContentQualityIssues);

				// Cross-pass dedup (always on): mute spelling's twin of a token sitting inside a
				// configured unwanted-pattern delimiter run (e.g. a leaked CMS placeholder). The
				// anchors are the configured pattern strings; the gate is intrinsically surgical (a
				// >=2-special-char delimiter and a whitespace-bounded run) — see
				// Crawler.Suppressions.UnwantedPatternSpellSuppression for the full rationale.
				var unwantedPattern = new Crawler.Suppressions.UnwantedPatternSpellSuppression(
					ctx.ContentQualityIssues,
					config.ContentUnwantedPatterns.SelectMany(p => p.Patterns));

				// Cross-pass dedup (always on): mute spelling's twin of a word fractured across ADJACENT
				// anchors — a CMS editor's consecutive-anchor defect that content-quality already reports
				// as ADJACENT_ANCHOR. The gate is intrinsically surgical (two anchor fragments whose
				// verbatim join is a real word); see Crawler.Suppressions.AdjacentAnchorSpellSuppression.
				var adjacentAnchor = new Crawler.Suppressions.AdjacentAnchorSpellSuppression(ctx.ContentQualityIssues);

				var newEngineTickets = Crawler.SpellCheck.NewSpellEngineRunner.Run(
					newEngineFiles,
					ctx.FileDownloadDirectory,
					ctx.UniqueSpellErrorLogPath,
					ctx.SpellErrorLogPath,
					ctx.SpellLocatedLogPath,
					ctx.WordTicketsDiagnosticLogPath,
					ctx.SpellSuppressionSuggestionsLogPath,
					config.SpellCheckEngine,
					_dictionaryBundles,
					(_, doc) => Language.FromMeta(doc, config.SpellCheckEngine.DefaultLanguage),
					config.SpellCheckWordPrefixesToStrip,
					config.GermanFugenelemente,
					CrawlIndex.LookUpUrlForFile,
					config.ResolvedDegreeOfParallelism,
					wordCollisions,
					anchorSplit,
					unwantedPattern,
					adjacentAnchor);

				// Hand this run's tickets to the spell triage step (in-process source of truth for
				// per-page occurrences). Set only on success — if the harvest threw, this stays
				// null and triage no-ops rather than acting on stale tracking entries.
				ctx.LastSpellTickets = newEngineTickets;
			}
			catch (Exception ex)
			{
				Logger.LogError($"New spell engine pass failed; spell tickets unavailable this run: {ex.Message}");
			}
		}

		// ── Step 18b: Bulk inline-<script> scan (diagnostic, opt-in) ─────────

		private static (int Blocks, int Findings) Step_BulkScanPageScript(
			CrawlerRunContext ctx,
			Config config)
		{
			Logger.LogInfo("Running bulk inline-<script> scan (BulkScanPageScript).");
			try
			{
				var result = Crawler.SpellCheck.BulkScriptScanner.Run(
					ctx.FileDownloadDirectory,
					config.FilePattern,
					ctx.BulkScriptBlobLogPath,
					ctx.BulkScriptFindingsLogPath,
					config.SpellCheckEngine.SpellCheckJavaScript.ScriptBulkScanDictionaries,
					_dictionaryBundles,
					config.SpellCheckWordPrefixesToStrip,
					config.GermanFugenelemente,
					CrawlIndex.LookUpUrlForFile);
				return (result.Blocks, result.Findings);
			}
			catch (Exception ex)
			{
				Logger.LogError($"Bulk page-script scan failed: {ex.Message}");
				return (0, 0);
			}
		}

		private static (int Files, int Findings) Step_ScanJsFiles(
			CrawlerRunContext ctx,
			Config config)
		{
			Logger.LogInfo("Running JS-file spell-check scan (ScanScriptFilesInDownload).");
			try
			{
				var result = Crawler.SpellCheck.JsFileScanner.Run(
					ctx.FileDownloadDirectory,
					ctx.JsFilesSpellCheckLogPath,
					ctx.JsFilesSpellCheckTrimmedLogPath,
					ctx.JsFilesSpellCheckUniqueLogPath,
					ctx.JsFilesSpellCheckRoutingLogPath,
					config.FilePattern,
					config.SpellCheckEngine.SpellCheckJavaScript.JsFindingPageReachThreshold,
					CrawlIndex.LookUpUrlForFile,
					config.SpellCheckEngine.SpellCheckJavaScript.ScriptFileScanDictionaries,
					_dictionaryBundles,
					config.SpellCheckWordPrefixesToStrip,
					config.GermanFugenelemente,
					config.SpellCheckEngine.SpellCheckJavaScript.TokensToFilter);

				// 661 — feed the file-scan findings into the SAME triage as page findings by converting
				// them to WordTickets and merging onto LastSpellTickets (which the triage step consumes).
				var scriptTickets = Crawler.SpellCheck.ScriptSpellingTickets.FromBundleFindings(result.BundleFindings);
				if (scriptTickets.Count > 0)
				{
					var merged = new List<Crawler.SpellCheck.WordTicket>();
					if (ctx.LastSpellTickets != null)
					{
						merged.AddRange(ctx.LastSpellTickets);
					}
					merged.AddRange(scriptTickets);
					ctx.LastSpellTickets = merged;
				}

				return (result.Files, result.Findings);
			}
			catch (Exception ex)
			{
				Logger.LogError($"JS-file spell-check scan failed: {ex.Message}");
				return (0, 0);
			}
		}

		// ── Step 24: Spell-check triage (interactive) ────────────────────────

		private static void Step_RunSpellCheckTriage(
			CrawlerRunContext ctx, Config config, string urlDirectory)
		{
			// Spell triage now writes operator decisions straight into the committed
			// IssueTracking ledger (pending/wontfix SPELLING rows) and runs gone-is-gone
			// against this run's tickets. That makes it a ledger-commit step, so it adopts
			// the same gate as content-quality triage and the end-of-run Merge: a supervised,
			// non-silent run on the latest snapshot. Triaging a pinned older snapshot must
			// never reconcile the live ledger against stale detections.
			if (!config.InteractiveSpellCheckTriage || CrawlerContext.Silent || !ctx.IsLatestSnapshotPath)
			{
				return;
			}

			var userDictionaryPath = $"dictionaries\\{config.CustomDictionaryFile}";
			var siteSpecificDictionaryPath = $"dictionaries\\{urlDirectory}.dic";

			SpellTriage.RunSpellCheckTriage(
				ctx.IssueTrackingPath,
				userDictionaryPath,
				siteSpecificDictionaryPath,
				ctx.ContentQualityLogPath,
				config.TriageLocalisationKnownTypes,
				config.TriageTicketKnownTypes,
				config.TriageWontfixKnownTypes,
				config.TriageUrlHighlight,
				ctx.LastSpellTickets,
				config.TriageLocalisationComment,
				ctx.FileDownloadDirectory,
				config.CrawlHistoryDiagnostic.HeaderExtractors);
		}

		// ── Step 25: Ticket draft / text generation ──────────────────────────

		private static void Step_GenerateTicketDrafts(
			CrawlerRunContext ctx, Config config)
		{
			if (config.TicketGeneration.IsConfigured)
			{
				// Operator-facing tickets are sourced from the IssueTracking ledger:
				// SPELLING and QUALITY rows the operator raised in triage. WriteTicketText
				// keeps only 'pending' rows of each and escalates them by the wall-clock
				// OverdueAfterDays window. The legacy pipe-delimited draft is retired.
				var ledger = IssueTracking.Load(ctx.IssueTrackingPath);
				var spellingRows = ledger
					.Where(r => r.Type.Equals("SPELLING", StringComparison.OrdinalIgnoreCase))
					.ToList();
				var qualityRows = ledger
					.Where(r => r.Type.Equals("QUALITY", StringComparison.OrdinalIgnoreCase))
					.ToList();
				var metadataLookup = SpellMetadataLookup.BuildMetadataLookup(config, CrawlerContext.Silent);

				// D048: per-page tickets must name every WORD_COLLISION on a page, not
				// just the first stored excerpt. Re-accumulate this run's groups from
				// log 10 and key the full occurrence list by IssueRecord.Key, so a
				// ticketed collision row expands to one "Context:" line per occurrence.
				// Same log-10 source review and triage use; no re-derivation.
				var collisionContextsByKey = ContentQualityTriage.WordCollisionContextsByKey(
					ctx.ContentQualityLogPath, LogFileNames.ContentQualityIssues,
					config.ContentQuality, ctx.FileDownloadDirectory);

				Logger.LogInfo("Generating ticket text.");
				TicketRenderer.WriteTicketText(ctx.TicketTextPath, spellingRows,
					config.TicketGeneration, config.CmsContentList, metadataLookup,
					ctx.ErrorSourcesCsvBasePath, config.UrlSubdomainsAllowed, qualityRows,
					collisionContextsByKey);
				ConsoleUi.WriteStepRow("Ticket text",
					$"{spellingRows.Count + qualityRows.Count} row(s)");
			}
		}

		// ── Step 26: Copy findings to site root ──────────────────────────────

		private static void Step_CopySitemapToSiteRoot(
			CrawlerRunContext ctx, string workingFolder, string urlDirectory)
		{
			// Findings no longer need copying to the site root: the operator-facing
			// output lives there already as IssueTracking.log + TicketText.log (written
			// directly to site root). The per-run finding logs stay in the timestamp
			// snapshot; only the sitemap is still consumed from the stable site-root
			// path, so it alone is copied up.
			Logger.LogInfo("Copy sitemap to site directory.");
			CopyFilesToDirectory(workingFolder, urlDirectory, ctx.SitemapPath);
			ConsoleUi.WriteStepRow("Sitemap copy", "→ site root");
		}

		private static void CopyFilesToDirectory(string workingFolder, string urlDirectory, params string[] sourceFiles)
		{
			if (string.IsNullOrWhiteSpace(workingFolder))
			{
				throw new ArgumentException(null, nameof(workingFolder));
			}

			if (string.IsNullOrWhiteSpace(urlDirectory))
			{
				throw new ArgumentException(null, nameof(urlDirectory));
			}

			if (sourceFiles == null || sourceFiles.Length == 0)
			{
				return;
			}

			var destDir = Path.Combine(workingFolder, urlDirectory);
			Directory.CreateDirectory(destDir);

			foreach (var src in sourceFiles)
			{
				if (string.IsNullOrWhiteSpace(src) || !File.Exists(src))
				{
					continue;
				}
				var destPath = Path.Combine(destDir, Path.GetFileName(src));
				File.Copy(src, destPath, overwrite: true);
			}
		}

		private static void PreloadDictionaries(
			string urlDirectory, string customDictionaryFile, List<DictionaryBundleConfig> bundles)
		{
			var siteSpecificDictionary = $"dictionaries\\{urlDirectory}.dic";
			var customDictionaryPath = $"dictionaries\\{customDictionaryFile}";

			if (bundles.Count == 0)
			{
				Logger.LogWarning("No DictionaryBundles configured — spell-checking will produce no results.");
				return;
			}

			foreach (var bundle in bundles)
			{
				if (string.IsNullOrWhiteSpace(bundle.LanguageCode))
				{
					Logger.LogWarning("DictionaryBundles: skipping entry with empty LanguageCode.");
					continue;
				}

				_dictionaryBundles[bundle.LanguageCode] = Loader.Load(
					bundle.DicFile,
					bundle.AffFile,
					customDictionaryPath,
					siteSpecificDictionary,
					CrawlerContext.Silent);
			}
		}

		// ── Row-count helpers (artifact-derived; safe on missing/unreadable files) ──

		/// <summary>Non-blank data lines in a log, minus the single header row.</summary>
		private static int CountDataLines(string path)
		{
			try
			{
				if (string.IsNullOrEmpty(path) || !File.Exists(path))
				{
					return 0;
				}
				int lines = File.ReadAllLines(path).Count(l => !string.IsNullOrWhiteSpace(l));
				return lines > 0 ? lines - 1 : 0; // first non-blank line is the header
			}
			catch
			{
				return 0;
			}
		}

		/// <summary>Number of &lt;loc&gt; entries in a sitemap.xml.</summary>
		private static int CountSitemapEntries(string path)
		{
			try
			{
				if (string.IsNullOrEmpty(path) || !File.Exists(path))
				{
					return 0;
				}
				var text = File.ReadAllText(path);
				int count = 0, idx = 0;
				while ((idx = text.IndexOf("<loc>", idx, StringComparison.Ordinal)) >= 0)
				{
					count++;
					idx += 5;
				}
				return count;
			}
			catch
			{
				return 0;
			}
		}

		/// <summary>Files matching the pattern under a directory (recursive).</summary>
		private static int CountFiles(string directory, string pattern)
		{
			try
			{
				if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
				{
					return 0;
				}
				return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).Count();
			}
			catch
			{
				return 0;
			}
		}
	}
}
