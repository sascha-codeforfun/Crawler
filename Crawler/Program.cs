namespace Crawler
{
	using System.Collections.Generic;
	using Crawler.Snapshots;
	using Crawler.Lexicon;
	using System.Diagnostics.CodeAnalysis;
	using System.Text;
	using System.Text.Json;
	using WeCantSpell.Hunspell;

	public class Result
	{
		public required string Filename { get; set; }
		public List<SpellingError> SpellingErrors { get; set; } = [];

		// Word → (sourceLabel, contentExcerpt) built during text extraction.
		public Dictionary<string, (string SourceLabel, string ContentExcerpt)> WordSourceMap { get; set; }
			= new(StringComparer.OrdinalIgnoreCase);

		// Elements flagged as potential translation issues — populated by AnalysisPipeline.CheckSpellingForNormalizedFile.
		// Each entry: (sourceLabel, contentExcerpt, otherLanguage)
		public List<(string SourceLabel, string ContentExcerpt, string OtherLanguage)> TranslationIssues { get; set; } = [];

		public class SpellingError(string word, string language)
		{
			public string Word { get; } = word;
			public string Language { get; } = language;
		}
	}

	public class DictionariesStore
	{
		public Dictionary<string, Bundle> LanguageBundles { get; } = [];
		public HashSet<string> SharedSite { get; } = new(StringComparer.OrdinalIgnoreCase);
		public HashSet<string> SharedUser { get; } = new(StringComparer.OrdinalIgnoreCase);
	}

	public class Program
	{
		private Config? config;

		/// <summary>
		/// When true all console output is suppressed — everything goes to the log file
		/// only. Set via --silent or -s command line argument for automated/scheduled runs.
		/// Kept for backwards compatibility — reads from CrawlerContext.Silent.
		/// </summary>
		private static bool _silent => CrawlerContext.Silent;

		[RequiresUnreferencedCode("requires unreferenced code")]
		public static async Task Main(string[] args)
		{
			// .NET 5+ omits legacy code pages from the default Encoding set. Register
			// the code-pages provider once at process start so callers can resolve
			// Windows-1252 (the fallback in DetectEncoding.FromBytes when a page
			// has no BOM and no recognizable <meta charset> in the first 4KB) and
			// other legacy code pages. Without this, GetEncoding(1252) throws
			// NotSupportedException at runtime — silently swallowed by upstream
			// catch blocks until this registration was added.
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

			CrawlerContext.Silent = args.Contains("--silent", StringComparer.OrdinalIgnoreCase)
							  || args.Contains("-s", StringComparer.OrdinalIgnoreCase);

			var program = new Program();
			await program.RunAsync();
		}

		[RequiresUnreferencedCode("Requires unreferenced code.")]
		public async Task RunAsync()
		{
			// Ensure UTF-8 output for correct rendering of Unicode characters
			// (arrows, box-drawing, umlauts) on Windows terminals.
			Console.OutputEncoding = System.Text.Encoding.UTF8;

			Logger.Initialize("application.log", _silent);
			LicenseAssets.EmitIfMissing();

			try
			{
				string configFile = File.Exists("config.private.json") ? "config.private.json" : "config.json";
				config = LoadConfig(Path.Combine(AppContext.BaseDirectory, configFile));
			}
			catch (Exception ex)
			{
				if (ex is not InvalidOperationException)
				{
					Logger.LogError($"An error occurred: {ex.Message}");
				}

				if (!_silent)
				{
					ConsoleUi.PressEnterToExit();
				}
				return;
			}

			if (config == null)
			{
				Logger.LogError("Configuration is null after loading.");
				if (!_silent)
				{
					ConsoleUi.PressEnterToExit();
				}
				return;
			}

			// What's running — a one-glance summary of the environment before any work.
			if (!_silent)
			{
				ConsoleUi.WriteHeader("CRAWLER");
				ConsoleUi.WriteStepRow("Log file", Logger.LogFilePath ?? "(not set)");
				ConsoleUi.WriteStepRow("Program folder", AppContext.BaseDirectory);
				ConsoleUi.WriteStepRow("Download folder",
					string.IsNullOrWhiteSpace(config.BaseDirectory) ? "(not set)" : config.BaseDirectory);
				ConsoleUi.WriteStepRow("Crawl policy",
					$"HTML: {config.ResolvedUnverifiedHtmlPolicy} · PDF: {config.ResolvedUnverifiedPdfPolicy}");
				ConsoleUi.WriteStepRow("Proxy",
					config.UseProxy
						? (string.IsNullOrWhiteSpace(config.ProxyUrl) ? "on" : $"on ({config.ProxyUrl})")
						: "off",
					dimmed: !config.UseProxy);

				ConfigSummary.WriteConfigurationBlock(config);
				ConfigSummary.WriteLanguagesBlock(config);
			}

			// Working-folder reachability check. BaseDirectory is foundational: every
			// crawl session folder is created beneath it. The app can create a missing
			// folder, but it cannot invent a missing drive — so an absolute BaseDirectory
			// whose root is absent (e.g. a drive letter not present on this machine) would
			// otherwise crash later at Directory.CreateDirectory. This is an environment
			// check (machine-dependent), kept separate from Config validation; it runs
			// first, before the dictionary check and any directory creation, and halts
			// calmly on an unreachable root.
			if (!BaseDirectoryCheck.CheckOrHalt(config))
			{
				if (!_silent)
				{
					ConsoleUi.PressEnterToExit();
				}
				return;
			}

			// Fresh-rig dictionary signpost: when no bundles are configured, ensure
			// the dictionaries\ folder + readme exist so the operator knows where to
			// start. Complements the integrity check below, which owns the configured
			// cases; this fills the gap where nothing is configured at all.
			Signpost.EmitIfUnconfigured(config);

			// Dictionary integrity check. Verifies that every
			// configured Bundle's .dic/.aff file matches the SHA-256
			// checksum recorded in config. On the first run after a config
			// upgrade, the operator pastes the actual checksums (written to
			// application.log) into config and re-runs. On subsequent runs,
			// any drift triggers a halt before Hunspell is loaded.
			//
			// Runs as early as possible — after config load, before any
			// network I/O or pipeline setup. By the time we get here we have
			// a valid Config object; nothing downstream can subvert it.
			if (!Integrity.CheckOrHalt(config))
			{
				if (!_silent)
				{
					ConsoleUi.PressEnterToExit();
				}
				return;
			}

			// Validate the crawl-history-diagnostic config (extractor regexes). The
			// diagnostic itself is DEBUG-only; the config validation is not, because
			// a broken regex in config.private.json shouldn't ship in any build
			// flavour. Same halt pattern as Integrity.CheckOrHalt — one
			// framed message naming every offending entry so the operator fixes
			// all in one pass.
			if (!CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config))
			{
				if (!_silent)
				{
					ConsoleUi.PressEnterToExit();
				}
				return;
			}

			// Validate the DownloadExclusions config: enabled entries with empty
			// Value would match every link (Contains("") is always true) and
			// silently reject the entire crawl. Halt at startup so the operator
			// hears about the problem at edit time, not via an empty result set.
			if (!DownloadExclusionsConfigValidator.CheckOrHalt(config))
			{
				if (!_silent)
				{
					ConsoleUi.PressEnterToExit();
				}
				return;
			}

			ParallelOptions options = new() { MaxDegreeOfParallelism = config.ResolvedDegreeOfParallelism };

			// ── Crawl-history diagnostic (runtime-gated forensic auditing) ────────
			// Walks every configured site's timestamp folders, classifies downloaded
			// HTML via operator-curated marker-substring scan, joins with header
			// sidecars for forensic detail (timestamps + extractor values). Writes
			// one per-site diagnostic log to each site's working folder. Runs BEFORE
			// site selection so the operator can peek any site's report before
			// choosing which to crawl — e.g., a recent anomalous crawl visible in
			// the log can inform whether to re-crawl now or skip to a different site.
			//
			// Gated by Config.CrawlHistoryDiagnostic.Enabled (default false in the
			// shipped config — feature is opt-in via config.private.json). Also
			// requires interactive mode: silent mode always skips the prompt.
			if (!_silent)
			{
				await CrawlHistoryDiagnostic.PromptAndRunAsync(config);
			}

			// ── Site selection + resolution (multi-site) ──────────────────────────
			// One run processes one site. Silent → the single IsPrimary site.
			// Interactive → numbered menu (Enter = primary). Config validation has
			// already guaranteed exactly one primary, so selection is total. The
			// selected site is then PROJECTED onto config (Url, UrlSubdomainsAllowed,
			// PostCrawlPass) and {tenant} is resolved, after which every downstream
			// consumer sees a fully-resolved single-site config. Per-site-dependent
			// validation (effective Url, CmsContentList cascade against the resolved
			// path) runs AFTER projection.
			SiteConfig selectedSite;
			if (_silent)
			{
				selectedSite = SiteSelection.SelectSilent(config.Sites);
				Logger.LogInfo($"Silent mode: selected primary site '{selectedSite.Name}' ({selectedSite.Url}).");
			}
			else
			{
				var picked = PromptForSite(config.Sites);
				if (picked == null)
				{
					return;                 // operator cancelled at the menu
				}

				selectedSite = picked;
			}

			try
			{
				config.ResolveForSite(selectedSite);
				Config.ValidateResolvedSite(config);
			}
			catch (InvalidOperationException ex)
			{
				// 650 — an operator SETUP halt renders as a calm, actionable yellow action screen
				// (teaching, not scolding). Genuine resolution failures keep the red error block below.
				if (ex is ConfigHaltException halt)
				{
					Logger.LogError($"Halted at setup: {halt.Heading}");
					if (!_silent)
					{
						ConsoleUi.WriteActionBlock(halt.Heading, halt.Lines);
						ConsoleUi.PressEnterToExit();
					}
					return;
				}

				Logger.LogError(ex.Message);
				if (!_silent)
				{
					ConsoleUi.WriteErrorBlock("SITE RESOLUTION FAILED", ex.Message.Split('\n'));
					ConsoleUi.PressEnterToExit();
				}
				return;
			}

			string workingFolder = config.BaseDirectory;
			string url = config.Url;
			string urlDirectory = url.Replace(":", "_").Replace("/", "_").Replace(".", "_");

			// Per-run mutable state. The snapshot [L] and empty-download [N]
			// interactive paths may mutate it mid-flow (flipping IsDebugSession
			// and recomputing all paths via RebuildTimestampPaths).
			var ctx = new CrawlerRunContext
			{
				IsDebugSession = config.DebugDisableCrawl,
			};

			// ── Resolve proxy credentials (once, up-front) ────────────────────────
			// Credentials are resolved here, BEFORE the connectivity preflight, so
			// the preflight authenticates with the exact identity the crawl will use.
			// The resolved values live on ctx and are the single source of truth for
			// every downstream HTTP path (preflight, main crawl, post-crawl pass).
			// The pure decision (what to do, given mode + config) is in
			// ProxyCredentialResolution.Decide; the console I/O for the interactive
			// cases is composed here from ConsoleUi primitives.
			ResolveProxyCredentials(ctx, config, _silent);

			// Connectivity check — before any crawl or processing work.
			var connectivityLogPath = Path.Combine(
				workingFolder,
				urlDirectory,
				"connectivity.log");
			var proxyUrlForCheck = config.UseProxy ? config.ProxyUrl : null;
			// Feed the preflight the SAME credentials the crawl will use (resolved
			// onto ctx just above). When they are blank, ProxyConfig.Build falls back
			// to UseDefaultCredentials (current OS identity) — covering silent runs
			// and the Windows-integrated-auth case.
			if (!await ConnectivityCheck.RunAsync(
					url, proxyUrlForCheck, connectivityLogPath, _silent,
					config.UseProxy ? ctx.ProxyUser : null,
					config.UseProxy ? ctx.ProxyPassword : null))
			{
				return;
			}

			ctx.TimeStamp = SnapshotFolder.NewName(
				ctx.IsDebugSession,
				config.DebugTimeStamp,
				Path.Combine(workingFolder, urlDirectory));
			ctx.RebuildTimestampPaths(Path.Combine(workingFolder, urlDirectory, ctx.TimeStamp));

			// Site-root tracking files — live one level up from the timestamp
			// folder for fixed Power Query paths, so they are NOT rebuilt by
			// RebuildTimestampPaths.
			ctx.IssueTrackingPath = Path.Combine(workingFolder, urlDirectory, LogFileNames.IssueTracking);
			ctx.TicketTextPath = Path.Combine(workingFolder, urlDirectory, LogFileNames.TicketText);

			// Commit eligibility (path half): the run analyses the LATEST snapshot
			// when it is a fresh crawl (N) or a replay of the most-recent snapshot
			// (L / DebugTimeStamp="latest"). A pinned older DebugTimeStamp is a
			// specific-folder replay → inspection-only → must not mutate the ledger.
			// Combined with !Silent at each commit site (silent runs never commit).
			ctx.IsLatestSnapshotPath = !config.DebugDisableCrawl
				|| config.DebugTimeStamp.Equals("latest", StringComparison.OrdinalIgnoreCase);

			// ── Crawl orchestration ───────────────────────────────────────────────
			// Snapshot/empty-download prompts, debug log clearing, directory creation,
			// main crawl, redirect analysis, lookup-file passes, integrity check,
			// 04/05 content logs, and the post-crawl pass all live in CrawlOrchestrator.
			// The orchestrator mutates ctx via the prompts; after this returns, ctx is
			// read-only for AnalysisPipeline and the end-of-run merge.
			var crawlResult = await CrawlOrchestrator.RunAsync(ctx, config, url, urlDirectory, workingFolder);

			// Null result = user picked abort at a prompt.
			if (crawlResult == null)
			{
				return;
			}


			// ── Analysis pipeline ──────────────────────────────────────────────────
			// All post-crawl analysis steps now live in AnalysisPipeline. The pipeline
			// operates on the indexed snapshot in ctx and returns three issue
			// collections (pdfIssues, base64Issues, canonicalIssues) that must
			// survive to the end-of-run IssueTracking merge below. canonicalIssues
			// are added here — previously merged mid-pipeline, which
			// triggered spurious 'fixed → reopened' transitions on the next run.
			var analysisResult = await AnalysisPipeline.RunAsync(
				ctx, config, url, urlDirectory, workingFolder, options);

			// Null result = pipeline aborted (e.g. dictionary validation failed).
			// PressEnterToExit was already called inside the pipeline.
			if (analysisResult == null)
			{
				return;
			}

			var pdfIssues = analysisResult.PdfIssues;
			var base64Issues = analysisResult.Base64Issues;
			var canonicalIssues = analysisResult.CanonicalIssues;
			var assetIssues = analysisResult.AssetIssues;


			// Promote findings into the persistent IssueTracking.log — but ONLY on a
			// supervised commit run: a fresh crawl (N) or a replay of the LATEST
			// snapshot (L), and never silent. A silent (unsupervised) run or a replay
			// of a pinned older snapshot is inspection-only: it produces the numbered
			// analysis logs but leaves the ledger untouched, so a half-broken or stale
			// snapshot can never rewrite it (gone-is-gone would otherwise drop rows).
			if (!_silent && ctx.IsLatestSnapshotPath)
			{
				ConsoleUi.WriteHeader("LEDGER");
				int totalIssues;
				string stateBreakdown;
				using (Logger.QuietConsole())
				{
					Logger.LogInfo("Updating IssueTracking.log.");
				var existingIssues = IssueTracking.Load(ctx.IssueTrackingPath);
				List<IssueTracking.IssueRecord> detectedIssues = [];
				detectedIssues.AddRange(IssueTracking.PromoteFrom404(ctx.ErrorSourcesCsvBasePath));
				detectedIssues.AddRange(IssueTracking.PromoteFromSelfLink(ctx.SelfLinkAnalysisCsvBasePath));
				detectedIssues.AddRange(IssueTracking.PromoteFromRedirect(ctx.RedirectAnalysisPath));
				detectedIssues.AddRange(IssueTracking.PromoteFromQuality(ctx.ContentQualityLogPath));
				detectedIssues.AddRange(IssueTracking.PromoteFromSeo(ctx.SeoDataPath, config.Seo));
				// Spelling is intentionally NOT promoted here. PromoteFromSpelling has been dormant
				// since the engine migration (it read an empty sources log), and relocating the
				// sources view to 12-spell-errors-sources.log would otherwise silently reactivate it.
				// Spelling enters IssueTracking via spell triage (T/L/W) writing pending/wontfix
				// SPELLING rows, and is reconciled (gone-is-gone) inside that triage against this
				// run's tickets. So SPELLING rows must be carved OUT of this Merge: they are absent
				// from detectedIssues and Merge would otherwise drop every one of them. Split the
				// ledger, Merge only the non-spelling rows against the detected set, then re-append
				// the already-reconciled SPELLING rows verbatim.
				detectedIssues.AddRange(pdfIssues);       // PDFQUALITY — included here to survive Merge
				detectedIssues.AddRange(base64Issues);    // BASE64_LARGE_ASSET — same pattern
				detectedIssues.AddRange(canonicalIssues); // CANONICAL_* — same pattern
				detectedIssues.AddRange(assetIssues);     // ASSET_* — same pattern
				// SPELLING is carved out: triage already wrote and reconciled those rows against
				// this run's tickets, and they are absent from detectedIssues, so a plain Merge
				// would drop every one. MergeExempt passes them through verbatim.
				var mergedIssues = IssueTracking.MergeExempt(existingIssues, detectedIssues, "SPELLING");
				IssueTracking.Save(ctx.IssueTrackingPath, mergedIssues);
				Logger.LogInfo($"IssueTracking.log: {mergedIssues.Count} total issue(s).");
					totalIssues = mergedIssues.Count;
					stateBreakdown = string.Join(" · ",
						mergedIssues.GroupBy(r => string.IsNullOrEmpty(r.Status) ? "(none)" : r.Status)
							.OrderByDescending(g => g.Count())
							.Select(g => $"{g.Count()} {g.Key}"));
				}
				ConsoleUi.WriteStepRow("IssueTracking", $"{totalIssues} issue(s)");
				if (stateBreakdown.Length > 0)
				{
					ConsoleUi.WriteStepRow("By state", stateBreakdown, dimmed: true);
				}
			}
			else
			{
				Logger.LogInfo(
					"IssueTracking.log left untouched — this run is silent or not the latest "
					+ "snapshot (inspection-only; commit happens on a supervised N/L-latest run).");
			}

			using (Logger.QuietConsole())
			{
				Logger.LogInfo("Program complete.");
			}

			if (!_silent)
			{
				ConsoleUi.WriteHeader("COMPLETE");
				ConsoleUi.WriteFooter();
				ConsoleUi.PressEnterToExit();
			}
		}


		/// <summary>
		/// Interactive numbered site picker. Renders all configured sites and reads
		/// the operator's choice; the pure mapping (choice → site) is delegated to
		/// SiteSelection, this method only does the console I/O. Per the keypress-
		/// prompt contract: an out-of-range or non-numeric entry warns
		/// and re-prompts (never silently falls through); Enter selects the primary
		/// (the same default silent mode uses); an explicit [Q] cancels the run.
		/// Returns the selected site, or null if the operator cancelled.
		/// </summary>
		private static SiteConfig? PromptForSite(IReadOnlyList<SiteConfig> sites)
		{
			ConsoleUi.WriteHeader("SITE SELECT");
			for (int i = 0; i < sites.Count; i++)
			{
				var s = sites[i];
				var marker = s.IsPrimary ? "  (primary, default)" : string.Empty;
				ConsoleUi.WriteLine($"  [{i + 1}] {s.Name} — {s.Url}{marker}");
			}
			ConsoleUi.WriteBlank();
			ConsoleUi.WriteLine("  [Q] Cancel");
			ConsoleUi.WriteFooter();

			while (true)
			{
				var entry = ConsoleUi.ReadLine("Enter a number, or press Enter for the default: ").Trim();

				if (entry.Length == 0)
				{
					return SiteSelection.SelectInteractive(sites, null);   // Enter → primary
				}

				if (entry.Equals("Q", StringComparison.OrdinalIgnoreCase))
				{
					return null;                                           // explicit cancel
				}

				if (int.TryParse(entry, out int choice))
				{
					var picked = SiteSelection.SelectInteractive(sites, choice);
					if (picked != null)
					{
						return picked;
					}

					ConsoleUi.WriteWarning($"'{choice}' is not one of 1..{sites.Count}. Please try again.");
					continue;
				}

				ConsoleUi.WriteWarning($"'{entry}' is not a valid choice. Enter a number 1..{sites.Count}, Enter for default, or Q to cancel.");
			}
		}


		/// <summary>
		/// Resolves the proxy credentials this run will use and writes them onto
		/// ctx (the single source of truth for every downstream HTTP path). The
		/// pure decision is delegated to ProxyCredentialResolution.Decide; the
		/// interactive cases do their console I/O here via ConsoleUi primitives.
		///
		/// Silent / no-proxy: config values pass straight through to ctx, no
		/// prompt. Interactive + proxy: the operator may always supply credentials
		/// by hand — config is a default to accept ([U]) or override ([O]), never a
		/// lock (config-held credentials are bridging tech; see
		/// ProxyCredentialResolution). The password is never displayed; only its
		/// presence is surfaced ("(set in config)" / "(none)").
		/// </summary>
		private static void ResolveProxyCredentials(
			CrawlerRunContext ctx, Config config, bool silent)
		{
			var decision = ProxyCredentialResolution.Decide(
				silent, config.UseProxy, config.ProxyUrl,
				config.ProxyUser, config.ProxyPassword);

			switch (decision.Outcome)
			{
				case ProxyCredentialResolution.Outcome.UseAsConfigured:
					ctx.ProxyUser = decision.User;
					ctx.ProxyPassword = decision.Password;
					return;

				case ProxyCredentialResolution.Outcome.OfferUseOrOverride:
					{
						ConsoleUi.WriteBlank();
						ConsoleUi.WriteLine("--- Proxy authentication ---");
						ConsoleUi.WriteLine("Configured user    : "
							+ (string.IsNullOrWhiteSpace(decision.User) ? "(none)" : decision.User));
						ConsoleUi.WriteLine("Configured password: "
							+ (string.IsNullOrWhiteSpace(decision.Password) ? "(none)" : "(set in config)"));
						ConsoleUi.WriteBlank();
						var key = ConsoleUi.ReadKey("[U] Use configured   [O] Override > ");
						if (key == ConsoleKey.O)
						{
							(ctx.ProxyUser, ctx.ProxyPassword) = PromptForProxyCredentials();
						}
						else
						{
							// [U] or any non-[O] key: keep the configured credentials.
							ctx.ProxyUser = decision.User;
							ctx.ProxyPassword = decision.Password;
						}
						ConsoleUi.WriteLine("----------------------------");
						ConsoleUi.WriteBlank();
						return;
					}

				case ProxyCredentialResolution.Outcome.PromptFresh:
					{
						ConsoleUi.WriteBlank();
						ConsoleUi.WriteLine("--- Proxy authentication required ---");
						(ctx.ProxyUser, ctx.ProxyPassword) = PromptForProxyCredentials();
						ConsoleUi.WriteLine("-------------------------------------");
						ConsoleUi.WriteBlank();
						return;
					}
			}
		}

		/// <summary>
		/// Reads a proxy username (defaulting to the current OS user) and a masked
		/// password from the console. Username is trimmed (via ConsoleUi.ReadLine);
		/// the password is taken verbatim — passwords may contain leading/trailing
		/// whitespace, so trimming would silently corrupt them.
		/// </summary>
		private static (string user, string password) PromptForProxyCredentials()
		{
			string defaultUser = Environment.UserName;
			string input = ConsoleUi.ReadLine($"Username [{defaultUser}]: ");
			string user = string.IsNullOrWhiteSpace(input) ? defaultUser : input;
			string password = ConsoleUi.ReadMaskedSecret("Password: ");
			return (user, password);
		}

		[RequiresUnreferencedCode("Calls Crawler.Config.LoadFromJson(String)")]
		private static Config LoadConfig(string configFilePath)
		{
			try
			{
				return Config.LoadFromJson(configFilePath);
			}
			catch (FileNotFoundException ex)
			{
				Logger.LogError($"Configuration file not found: {ex.Message}");
				throw;
			}
			catch (JsonException ex)
			{
				// 658 — calm "CONFIG CHECK · Configuration file" screen instead of the raw red wall.
				// Full technical detail is logged inside Render; the rethrown ConfigHaltException is an
				// InvalidOperationException, so the outer catch skips its LogError and exits cleanly.
				ConfigJsonErrorScreen.Render(configFilePath, ex);
				throw new ConfigHaltException(
					"Configuration file could not be parsed",
					new[] { "The configuration file is not valid JSON. See the CONFIG CHECK screen and the log for detail." });
			}
			catch (InvalidOperationException ex)
			{
				// ValidateConfig throws this with a full list of failing properties.
				// Write directly to Console as well so it's visible even if the log
				// path itself is broken — unless running silent.
				if (!_silent)
				{
					ConsoleUi.WriteBlank();
					ConsoleUi.WriteErrorBlock("=== CONFIG VALIDATION FAILED ===", [ex.Message, "================================="]);

				}
				throw;
			}
			catch (Exception ex)
			{
				Logger.LogError($"An unexpected error occurred: {ex.Message}");
				throw;
			}
		}

	}
}
