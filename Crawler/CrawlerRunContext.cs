namespace Crawler
{
	// ── CrawlerRunContext ─────────────────────────────────────────────────────
	//
	// Per-run mutable state held by Program.RunAsync and threaded into the
	// extracted interactive/orchestration/analysis classes that need to read
	// or mutate it (snapshot choice [L] flips IsDebugSession; empty-download
	// recovery [N] flips it back and recomputes the timestamp and all paths).
	//
	// Distinct from CrawlerContext, which holds *process-level* flags set
	// once at startup (e.g. Silent). CrawlerRunContext lives for one run only
	// and is re-created on the next invocation of RunAsync.
	//
	// Why a class (not a record): several fields are mutated during a run
	// (IsDebugSession, TimeStamp, and all the path strings recomputed by the
	// [L] and empty-download [N] branches). A `with`-expression on a record
	// per mutation would be noisy and obscure intent. A mutable class makes
	// the mutation honest.
	// ─────────────────────────────────────────────────────────────────────────

	internal sealed class CrawlerRunContext
	{
		// ── Run mode flags ────────────────────────────────────────────────────

		/// <summary>
		/// True when the analysis pipeline runs on an existing snapshot without
		/// re-downloading. Set from Config.DebugDisableCrawl at startup; flipped
		/// to true by the snapshot [L] choice and back to false by the empty-
		/// download [N] recovery.
		/// </summary>
		internal bool IsDebugSession { get; set; }

		// True when this run analyses the LATEST snapshot — a fresh crawl (N) or a
		// replay of the most-recent snapshot (L / DebugTimeStamp="latest"). False for
		// a replay of a pinned OLDER snapshot (inspection-only). Gates site-level
		// commits together with !Silent: the persistent ledger is only written by a
		// supervised run against the latest snapshot, never by a stale-snapshot replay.
		internal bool IsLatestSnapshotPath { get; set; }

		/// <summary>
		/// Set by Step_CheckCmsContentListFreshness when silent mode + stale
		/// CmsContentList: tells Step_PerformPostCrawlPass to skip the post-crawl
		/// download pass for this run. Interactive mode never sets this — the
		/// operator's [A] choice aborts the whole run before the main crawl
		/// runs, so the pass is never reached.
		/// </summary>
		internal bool SuppressPostCrawlPass { get; set; }

		// ── Network identity (resolved proxy credentials) ─────────────────────

		// [KEEP-COMMENT] These hold the proxy credentials this run will actually
		// use, RESOLVED ONCE at startup in Program.RunAsync (see
		// ProxyCredentialResolution) and read thereafter by the connectivity
		// preflight, the main crawl, and the post-crawl pass. config.ProxyUser /
		// config.ProxyPassword are startup INPUT; these are the resolved TRUTH.
		// Do not read config.ProxyUser/Password downstream of resolution — that
		// reintroduces the split-brain where the preflight and the crawl
		// could authenticate with different identities. Deliberately NOT rebuilt
		// by RebuildTimestampPaths: they are not timestamp-derived, and a snapshot
		// [L] / empty-download [N] re-point must not wipe resolved credentials.
		internal string ProxyUser { get; set; } = string.Empty;
		internal string ProxyPassword { get; set; } = string.Empty;

		// ── Timestamp and root directories ────────────────────────────────────

		internal string TimeStamp { get; set; } = string.Empty;
		internal string SaveDirectory { get; set; } = string.Empty;

		internal string FileDownloadDirectory { get; set; } = string.Empty;
		internal string FilePrunedDirectory { get; set; } = string.Empty;

		// ── Log paths ─────────────────────────────────────────────────────────

		internal string CrawlerLogPath { get; set; } = string.Empty;
		internal string CrawlerRawLogPath { get; set; } = string.Empty;
		internal string CrawlerLogIndexPath { get; set; } = string.Empty;
		internal string ErrorLogPath { get; set; } = string.Empty;
		internal string ErrorSourcesCsvBasePath { get; set; } = string.Empty;
		internal string SpellErrorLogPath { get; set; } = string.Empty;
		internal string UniqueSpellErrorLogPath { get; set; } = string.Empty;
		internal string SpellLocatedLogPath { get; set; } = string.Empty;
		internal string WordTicketsDiagnosticLogPath { get; set; } = string.Empty;
		internal string SpellSuppressionSuggestionsLogPath { get; set; } = string.Empty;
		internal string BulkScriptBlobLogPath { get; set; } = string.Empty;
		internal string BulkScriptFindingsLogPath { get; set; } = string.Empty;
		internal string JsFilesSpellCheckLogPath { get; set; } = string.Empty;
		// 643 — trimmed/triage companion to log 30: only findings from literals that passed the
		// prose-ratio gate. Log 30 stays the full debug source; this is the list a human triages.
		internal string JsFilesSpellCheckTrimmedLogPath { get; set; } = string.Empty;
		// 646 — flat, sorted, de-duplicated list of the distinct kept-finding words (no context, no
		// dictionaries). The "what do I go after" view, mirroring page-spelling log 11's unique view.
		internal string JsFilesSpellCheckUniqueLogPath { get; set; } = string.Empty;
		// 647 — routing PREVIEW: per bundle, its reach (pages that load it) and the CLEAR/BULK decision.
		// Verification artifact; nothing here is written to the ticket ledger.
		internal string JsFilesSpellCheckRoutingLogPath { get; set; } = string.Empty;

		// In-memory spell-engine tickets from this run's harvest, handed to the spell triage step
		// for per-page occurrence display. Null when the harvest did not run or threw.
		internal System.Collections.Generic.IReadOnlyList<Crawler.SpellCheck.WordTicket>? LastSpellTickets { get; set; }

		// Editor-class content-quality issues from this run (post-suppression), handed to the spell
		// step so it can dedup a spell finding against a WORD_COLLISION CQ already reports. Null when
		// content-quality did not run.
		internal System.Collections.Generic.IReadOnlyList<Crawler.Quality.QualityIssue>? ContentQualityIssues { get; set; }
		internal string FullContentLogPath { get; set; } = string.Empty;
		internal string ContentCrawlCompareLogFile { get; set; } = string.Empty;
		internal string SitemapPath { get; set; } = string.Empty;
		internal string SeoDataPath { get; set; } = string.Empty;
		internal string UserDictionaryOrphanWordsFilePath { get; set; } = string.Empty;
		internal string RedirectAnalysisPath { get; set; } = string.Empty;
		internal string SelfLinkAnalysisCsvBasePath { get; set; } = string.Empty;
		internal string CanonicalCsvBasePath { get; set; } = string.Empty;
		internal string PdfQualityCsvBasePath { get; set; } = string.Empty;
		internal string PdfRemediationCsvBasePath { get; set; } = string.Empty;
		internal string AssetQualityCsvBasePath { get; set; } = string.Empty;
		internal string Base64AssetsLogPath { get; set; } = string.Empty;
		internal string Base64AssetsDirectory { get; set; } = string.Empty;
		internal string ResourceBloatLogPath { get; set; } = string.Empty;
		internal string ResourceBloatBaselineLogPath { get; set; } = string.Empty;
		internal string ContentQualityLogPath { get; set; } = string.Empty;
		internal string CmsTemplateDefectsCsvBasePath { get; set; } = string.Empty;
		internal string ContentTypeMismatchLogPath { get; set; } = string.Empty;
		internal string PdfContentTypeMismatchLogPath { get; set; } = string.Empty;
		internal string ImageContentTypeMismatchLogPath { get; set; } = string.Empty;

		// ── Site-root tracking files (no timestamp, fixed Power Query paths) ──

		internal string IssueTrackingPath { get; set; } = string.Empty;
		internal string TicketTextPath { get; set; } = string.Empty;

		/// <summary>
		/// Rebuilds every timestamp-dependent path from a new saveDirectory.
		/// Called by the snapshot [L] branch (replay an existing snapshot) and
		/// the empty-download [N] recovery branch (fresh crawl with new
		/// timestamp). Centralised here so all path-recompute sites stay in
		/// sync — adding a new log path means one edit, not three.
		/// </summary>
		internal void RebuildTimestampPaths(string saveDirectory)
		{
			SaveDirectory = saveDirectory;
			FileDownloadDirectory = Path.Combine(saveDirectory, "download");
			FilePrunedDirectory = Path.Combine(saveDirectory, "pruned");
			CrawlerRawLogPath = Path.Combine(saveDirectory, LogFileNames.CrawlerRaw);
			CrawlerLogPath = Path.Combine(saveDirectory, LogFileNames.Crawler);
			CrawlerLogIndexPath = Path.Combine(saveDirectory, LogFileNames.CrawlerIndex);
			RedirectAnalysisPath = Path.Combine(saveDirectory, LogFileNames.RedirectAnalysis);
			FullContentLogPath = Path.Combine(saveDirectory, LogFileNames.FullContent);
			ContentCrawlCompareLogFile = Path.Combine(saveDirectory, LogFileNames.NotDirectlyCrawlable);
			ErrorLogPath = Path.Combine(saveDirectory, LogFileNames.Errors404);
			ErrorSourcesCsvBasePath = Path.Combine(saveDirectory, LogFileNames.Error404SourcesCsvBase);
			SeoDataPath = Path.Combine(saveDirectory, LogFileNames.SeoData);
			SelfLinkAnalysisCsvBasePath = Path.Combine(saveDirectory, LogFileNames.SelfLinkAnalysisCsvBase);
			ContentQualityLogPath = Path.Combine(saveDirectory, LogFileNames.ContentQualityIssues);
			UniqueSpellErrorLogPath = Path.Combine(saveDirectory, LogFileNames.SpellErrorsUnique);
			SpellErrorLogPath = Path.Combine(saveDirectory, LogFileNames.SpellErrorsSources);
			SpellLocatedLogPath = Path.Combine(saveDirectory, LogFileNames.SpellErrorsSourceLocation);
			// Spell-engine verbatim diagnostic — what was flagged and where, every harvest run.
			WordTicketsDiagnosticLogPath = Path.Combine(saveDirectory, LogFileNames.WordTickets);
			// Script-source suppression suggestions for SpellCheckJavaScript.TokensToFilter (supplements
			// the JS spell-check feature). 15/16+ are taken; this takes the next free number.
			SpellSuppressionSuggestionsLogPath = Path.Combine(saveDirectory, LogFileNames.SpellcheckSuppressionSuggestions);
			BulkScriptBlobLogPath = Path.Combine(saveDirectory, LogFileNames.BulkScriptBlob);
			BulkScriptFindingsLogPath = Path.Combine(saveDirectory, LogFileNames.BulkScriptFindings);
			JsFilesSpellCheckLogPath = Path.Combine(saveDirectory, LogFileNames.JsFilesSpellCheck);
			JsFilesSpellCheckTrimmedLogPath = Path.Combine(saveDirectory, LogFileNames.JsFilesSpellCheckTrimmed);
			JsFilesSpellCheckUniqueLogPath = Path.Combine(saveDirectory, LogFileNames.JsFilesSpellCheckUnique);
			JsFilesSpellCheckRoutingLogPath = Path.Combine(saveDirectory, LogFileNames.JsFilesSpellCheckRouting);
			UserDictionaryOrphanWordsFilePath = Path.Combine(saveDirectory, LogFileNames.UserDictionaryOrphanWords);
			CanonicalCsvBasePath = Path.Combine(saveDirectory, LogFileNames.CanonicalIssuesCsvBase);
			PdfQualityCsvBasePath = Path.Combine(saveDirectory, LogFileNames.PdfQualityCsvBase);
			PdfRemediationCsvBasePath = Path.Combine(saveDirectory, LogFileNames.PdfRemediationCsvBase);
			AssetQualityCsvBasePath = Path.Combine(saveDirectory, LogFileNames.AssetQualityCsvBase);
			Base64AssetsLogPath = Path.Combine(saveDirectory, LogFileNames.Base64Assets);
			Base64AssetsDirectory = Path.Combine(saveDirectory, "base64assets");
			ResourceBloatLogPath = Path.Combine(saveDirectory, LogFileNames.ResourceBloat);
			ResourceBloatBaselineLogPath = Path.Combine(saveDirectory, LogFileNames.ResourceBloatAboveBaseline);
			CmsTemplateDefectsCsvBasePath = Path.Combine(saveDirectory, LogFileNames.CmsTemplateAuthoringDefectsCsvBase);
			ContentTypeMismatchLogPath = Path.Combine(saveDirectory, LogFileNames.ContentTypeExtensionMismatch);
			PdfContentTypeMismatchLogPath = Path.Combine(saveDirectory, LogFileNames.PdfContentTypeExtensionMismatch);
			ImageContentTypeMismatchLogPath = Path.Combine(saveDirectory, LogFileNames.ImageContentTypeExtensionMismatch);
			SitemapPath = Path.Combine(saveDirectory, "sitemap.xml");
		}
	}
}
