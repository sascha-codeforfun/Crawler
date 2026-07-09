namespace Crawler
{
	/// <summary>
	/// Single source of truth for the crawler's output log filenames. Centralised so a rename — in
	/// particular the planned ".log → .csv" migration for the delimited (Excel-bound) logs — is a
	/// one-line edit here instead of a scattered hunt across the run context, the writers, the triage
	/// readers, and the snapshot/cleanup code that reference these names independently.
	///
	/// Values are filenames only; callers <see cref="System.IO.Path.Combine(string, string)"/> them
	/// with the run's save directory. Numbering is historical and not contiguous by category (e.g. 15
	/// and 25–27 were assigned as features landed); the prefix is a stable display/sort key, not a
	/// promise of grouping.
	/// </summary>
	internal static class LogFileNames
	{
		public const string CrawlerRaw = "00-crawler.log";
		public const string Crawler = "01-crawler.log";
		public const string CrawlerIndex = "02-crawler-index.log";
		public const string RedirectAnalysis = "03-redirect-analysis.log";
		public const string FullContent = "04-full-content.log";
		public const string NotDirectlyCrawlable = "05-not-directly-crawlable.log";
		public const string Errors404 = "06-404.log";
		// Dual-locale CSV (base stem; WriteCsvPair appends the suffix+extension).
		public const string Error404SourcesCsvBase = "07-404-sources";
		public const string SeoData = "08-seo-data.csv";
		// Dual-locale CSV (base stem; WriteCsvPair appends the suffix+extension).
		public const string SelfLinkAnalysisCsvBase = "09-self-link-analysis";
		public const string ContentQualityIssues = "10-content-quality-issues.log";
		public const string SpellErrorsUnique = "11-spell-errors-unique.log";
		public const string SpellErrorsSources = "12-spell-errors-sources.log";
		public const string SpellErrorsSourceLocation = "13-spell-errors-source-location.log";
		public const string WordTickets = "14-word-tickets.log";
		public const string UserDictionaryOrphanWords = "15-user-dictionary-orphan-words.log";
		// Dual-locale CSV (base stem; WriteCsvPair appends the suffix+extension).
		public const string CanonicalIssuesCsvBase = "16-canonical-issues";
		// Dual-locale CSV (base stems; WriteCsvPair appends the suffix+extension).
		public const string PdfQualityCsvBase = "17-pdf-quality";
		public const string PdfRemediationCsvBase = "18-pdf-remediation";
		public const string Base64Assets = "19-base64assets.log";
		public const string ResourceBloat = "20-resource-bloat.log";
		public const string ResourceBloatAboveBaseline = "21-resource-bloat-above-baseline.log";
		// Dual-locale CSV (base stem; WriteCsvPair appends the suffix+extension).
		public const string CmsTemplateAuthoringDefectsCsvBase = "22-cms-template-authoring-defects";
		public const string ContentTypeExtensionMismatch = "23-contenttype-extension-mismatch.log";
		public const string PdfContentTypeExtensionMismatch = "24-pdf-contenttype-extension-mismatch.log";
		// Dual-locale CSV: base stem only — IssueLogWriter.WriteCsvPair appends
		// "_semicolon.csv" / "_comma.csv". Human-facing, no machine reader.
		public const string AssetQualityCsvBase = "25-asset-quality";
		public const string ImageContentTypeExtensionMismatch = "26-image-contenttype-extension-mismatch.log";
		public const string SpellcheckSuppressionSuggestions = "27-spellcheck-suppression-suggestions.log";
		public const string BulkScriptBlob = "28-bulk-script-blob.log";
		public const string BulkScriptFindings = "29-bulk-script-findings.log";
		public const string JsFilesSpellCheck = "30-js-files-spell-check.log";
		public const string JsFilesSpellCheckTrimmed = "31-js-files-spell-check-trimmed.log";
		public const string JsFilesSpellCheckUnique = "32-js-files-spell-check-unique.log";
		public const string JsFilesSpellCheckRouting = "33-js-files-spell-check-routing.log";
		public const string IssueTracking = "IssueTracking.log";
		public const string TicketText = "TicketText.log";
	}
}
