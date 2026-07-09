using System.Text;
using Crawler.Urls;
using Crawler.Security;

namespace Crawler
{
	// ── IssueTracking ─────────────────────────────────────────────────────────
	//
	// Maintains a persistent IssueTracking.log at site level (no timestamp folder).
	// Every run: promoted issues from the five source logs are merged with the
	// existing log, statuses are updated, and the file is rewritten.
	//
	// Column order (Source first — easy to filter in Excel/Power Query):
	//   Source|Ticket|DateReported|Type|Url|Status|DateFound|DateLastSeen|DateExpiry|
	//   Word|Comment|Language|SourceLabel|Excerpt|CrawlSource
	//
	// Issue types:  REDIRECT | 404 | SELFLINK | QUALITY | SPELLING | SEO
	// Status values: new | pending | overdue | fixed | reopened | wontfix | config
	//
	// Identity key (prevents duplicate rows):
	//   SPELLING / QUALITY : Type + Url + Word
	//   404 / SELFLINK / REDIRECT : Type + Url
	// ─────────────────────────────────────────────────────────────────────────

	public static class IssueTracking
	{
		// ── Record ────────────────────────────────────────────────────────────

		public record IssueRecord
		{
			/// <summary>
			/// How this record was created. First column — easy to filter in Excel/Power Query.
			/// "auto"   = promoted at end of run by IssueTracking.Merge (no human review)
			/// "triage" = human pressed [T] or [W] in triage (deliberate decision)
			/// "manual" = row edited directly in IssueTracking.log
			/// </summary>
			public string Source { get; init; } = "auto";
			public string Ticket { get; init; } = string.Empty;
			public string DateReported { get; init; } = string.Empty;
			public string Type { get; init; } = string.Empty;
			public string Url { get; init; } = string.Empty;
			public string Status { get; set; } = "new";
			public string DateFound { get; init; } = Today();
			public string DateLastSeen { get; set; } = Today();
			public string DateExpiry { get; init; } = string.Empty;
			public string Word { get; init; } = string.Empty;
			public string Comment { get; init; } = string.Empty;
			public string Language { get; init; } = string.Empty;
			public string SourceLabel { get; init; } = string.Empty;
			public string Excerpt { get; init; } = string.Empty;
			/// <summary>
			/// Crawl source of the page: "discovery" (found via link following) or
			/// "list" (post-crawl pass from CMS content list). Empty for issue types
			/// where a file mapping is not available (e.g. REDIRECT). Appended as the
			/// last column for backward compatibility — old logs without this column
			/// load cleanly with an empty value.
			/// </summary>
			public string CrawlSource { get; init; } = string.Empty;

			/// <summary>
			/// Identity key — used to match an existing record against a newly
			/// detected issue. Word distinguishes SPELLING/QUALITY sub-issues.
			/// </summary>
			public string Key => string.IsNullOrEmpty(Word)
				? $"{Type}|{Url}"
				: $"{Type}|{Url}|{Word}";

			public static IssueRecord Parse(string line)
			{
				var p = line.Split('|');
				string F(int i) => i < p.Length ? p[i].Trim() : string.Empty;
				return new IssueRecord
				{
					Source = F(0),
					Ticket = F(1),
					DateReported = F(2),
					Type = F(3),
					Url = F(4),
					Status = F(5),
					DateFound = F(6),
					DateLastSeen = F(7),
					DateExpiry = F(8),
					Word = F(9),
					Comment = F(10),
					Language = F(11),
					SourceLabel = F(12),
					Excerpt = F(13),
					CrawlSource = F(14),
				};
			}

			public string Serialize() =>
				IssueLogWriter.ComposeLine(IssueLogWriter.PipeDelimiter,
					Source, Ticket, DateReported, Type, Url, Status, DateFound,
					DateLastSeen, DateExpiry, Word, Comment, Language, SourceLabel,
					Excerpt, CrawlSource);
		}

		private static readonly string?[] HeaderFields =
		[
			"Source", "Ticket", "DateReported", "Type", "Url", "Status",
			"DateFound", "DateLastSeen", "DateExpiry", "Word", "Comment",
			"Language", "SourceLabel", "Excerpt", "CrawlSource"
		];

		private static string Today() =>
			DateTime.Now.ToString("yyyy-MM-dd");

		// ── Load / Save ───────────────────────────────────────────────────────

		/// <summary>
		/// Loads IssueTracking.log. Returns empty list if file does not exist.
		/// Skips the header line and blank lines. Also drops any record whose Url is
		/// the unresolved-lookup sentinel "error" (see Cache.UrlFor), since it
		/// can't be tied to a real page.
		/// </summary>
		public static List<IssueRecord> Load(string filePath)
		{
			if (!File.Exists(filePath))
			{
				return [];
			}

			return File.ReadAllLines(filePath, Encoding.UTF8)
				.Where(l => l.Length > 0
					&& !l.StartsWith("Source|", StringComparison.OrdinalIgnoreCase)
					&& l.Contains('|'))
				.Select(IssueRecord.Parse)
				.Where(r => !string.IsNullOrEmpty(r.Type)
					&& !string.IsNullOrEmpty(r.Url)
					&& !string.Equals(r.Url, "error", StringComparison.Ordinal))
				.ToList();
		}

		/// <summary>
		/// Saves the issue list to IssueTracking.log with header.
		/// Sorts by Type then Url for stable output.
		/// [KEEP] Routed through IssueLogWriter — each field is sanitized so
		/// crawled content (Word, SourceLabel, Excerpt, Comment) can never
		/// inject newlines or delimiters into the persistent tracking log.
		/// </summary>
		public static void Save(string filePath, List<IssueRecord> records)
		{
			var rows = new List<string?[]> { HeaderFields };
			rows.AddRange(records
				.OrderBy(r => r.Type)
				.ThenBy(r => r.Url)
				.ThenBy(r => r.Word)
				.Select(r => new string?[]
				{
					r.Source, r.Ticket, r.DateReported, r.Type, r.Url, r.Status,
					r.DateFound, r.DateLastSeen, r.DateExpiry, r.Word, r.Comment,
					r.Language, r.SourceLabel, r.Excerpt, r.CrawlSource
				}));
			IssueLogWriter.Write(filePath, IssueLogWriter.PipeDelimiter, rows);
		}

		// ── Merge ─────────────────────────────────────────────────────────────

		/// <summary>
		/// Merges newly detected issues into the existing issue list.
		/// Rules:
		///   - New issue not in existing → add with status "new"
		///   - Issue still detected → update DateLastSeen only
		///   - Issue was fixed, now re-detected → status "reopened", update DateLastSeen
		///   - Issue not detected this run, status new/pending/overdue → status "fixed"
		///   - Issue not detected, status fixed/wontfix/config/reopened → leave as-is
		///   - DateExpiry passed and status is pending → auto-set "overdue"
		/// </summary>
		public static List<IssueRecord> Merge(
			List<IssueRecord> existing,
			List<IssueRecord> detected)
		{
			// Gone-is-gone: the ledger holds ONLY issues present in the current
			// detected set. A still-detected existing record is kept VERBATIM —
			// preserving its disposition (Status/Ticket/Comment), the Excerpt frozen
			// at promotion, and DateFound. A newly-detected issue is added. An
			// existing record NOT in the detected set is DROPPED outright — no
			// "fixed"/"reopened" status, no retention. If it reappears in a later run
			// it re-enters as new and is re-triaged. This holds for EVERY disposition,
			// including wontfix/config: once the finding is gone the row goes with it
			// (a disposition is cross-run state only while the finding is still present).
			var detectedMap = detected
				.Where(r => !string.IsNullOrEmpty(r.Type) && !string.IsNullOrEmpty(r.Url))
				.GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
			var existingMap = existing
				.Where(r => !string.IsNullOrEmpty(r.Type) && !string.IsNullOrEmpty(r.Url))
				.GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

			List<IssueRecord> result = [];

			// Keep existing records that are still detected (verbatim); drop the rest.
			foreach (var record in existing)
			{
				if (detectedMap.ContainsKey(record.Key))
				{
					result.Add(record);
				}
			}

			// Add newly-detected issues not already present.
			foreach (var issue in detected)
			{
				if (!existingMap.ContainsKey(issue.Key))
				{
					result.Add(issue);
				}
			}

			return result;
		}

		/// <summary>
		/// Gone-is-gone Merge that EXEMPTS one record type. Existing rows of
		/// <paramref name="exemptType"/> are passed through verbatim — never dropped,
		/// never reconciled against <paramref name="detected"/> — while every other
		/// row follows the normal <see cref="Merge"/> rules against the detected set.
		/// Used for SPELLING: those rows are operator decisions written and reconciled
		/// by spell triage against the in-memory tickets, and they are deliberately
		/// absent from the end-of-run detected set, so a plain Merge would drop them all.
		/// </summary>
		public static List<IssueRecord> MergeExempt(
			List<IssueRecord> existing,
			List<IssueRecord> detected,
			string exemptType)
		{
			var exempt = existing
				.Where(r => r.Type.Equals(exemptType, StringComparison.OrdinalIgnoreCase))
				.ToList();
			var rest = existing
				.Where(r => !r.Type.Equals(exemptType, StringComparison.OrdinalIgnoreCase))
				.ToList();

			var merged = Merge(rest, detected);
			merged.AddRange(exempt);
			return merged;
		}

		/// <summary>
		/// Removes <c>fixed</c> records past their retention window. Other
		/// statuses are never purged — wontfix/config encode deliberate
		/// operator decisions ("do not flag again") and must persist; new/
		/// pending/overdue/reopened are still-actionable.
		///
		/// Retention semantics (<paramref name="retentionDays"/>):
		///   0   → disabled, keep all fixed forever (default).
		///   &gt;0 → purge fixed whose DateLastSeen is older than N days. A
		///          fixed record's DateLastSeen is frozen at the last run the
		///          issue was actually detected (Merge stops updating it once
		///          the issue disappears), so it approximates "fixed since".
		///   -1  → purge ALL fixed unconditionally (dev/reset). Any negative
		///          value is treated this way.
		///
		/// A fixed record with an unparseable/empty DateLastSeen is kept under
		/// a positive window (cannot age it) but purged under -1 (purge-all).
		/// Pure: no I/O, returns a new list.
		/// </summary>
		public static List<IssueRecord> PurgeExpiredFixed(
			List<IssueRecord> records, int retentionDays)
		{
			if (retentionDays == 0)
			{
				return records;   // disabled — keep all
			}

			bool purgeAll = retentionDays < 0;
			var cutoff = purgeAll
				? default
				: DateTime.Now.Date.AddDays(-retentionDays);

			var kept = new List<IssueRecord>(records.Count);
			foreach (var r in records)
			{
				bool isFixed = r.Status.Equals("fixed", StringComparison.OrdinalIgnoreCase);
				if (!isFixed)
				{
					kept.Add(r);
					continue;
				}

				if (purgeAll)
				{
					continue;   // -1 → drop every fixed
				}

				// Positive window: drop only if DateLastSeen is parseable AND
				// older than the cutoff. Unparseable/empty → keep (can't age).
				//
				// Parse with the exact format Today() writes ("yyyy-MM-dd") under
				// the invariant culture, rather than a loose TryParse. This makes
				// the producer/consumer date contract explicit and immune to a
				// future locale surprise — anything not in the canonical written
				// form falls into the same safe "unparseable → kept" bucket.
				if (DateTime.TryParseExact(
						r.DateLastSeen,
						"yyyy-MM-dd",
						System.Globalization.CultureInfo.InvariantCulture,
						System.Globalization.DateTimeStyles.None,
						out var seen)
					&& seen.Date < cutoff)
				{
					continue;   // expired — drop
				}

				kept.Add(r);
			}

			return kept;
		}

		/// <summary>
		/// Applies operator triage decisions to an existing issue list,
		/// touching ONLY the records the operator decided on. All other
		/// existing records are passed through unchanged.
		///
		/// This is the correct semantics for triage Save.
		/// Previously the triage step used Merge(existing, partialDecisions),
		/// which incorrectly classified all non-triaged existing records as
		/// 'fixed' (because they weren't in the partial detection set),
		/// triggering spurious 'fixed → reopened' transitions when end-of-run
		/// Merge re-detected them with the full set.
		///
		/// Rules:
		///   - decision with Key matching existing record → overwrite
		///     Status, Comment, DateLastSeen, DateReported; preserve DateFound,
		///     DateExpiry, Source. Other fields take the decision's value.
		///   - decision with no matching Key → add as a new record
		///   - existing records with no matching decision → passed through
		///     completely untouched
		/// </summary>
		public static List<IssueRecord> ApplyTriageDecisions(
			List<IssueRecord> existing,
			List<IssueRecord> decisions)
		{
			var today = Today();
			var decisionsByKey = decisions
				.Where(d => !string.IsNullOrEmpty(d.Type) && !string.IsNullOrEmpty(d.Url))
				.GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

			List<IssueRecord> result = [];

			// Walk existing records. Overwrite when a triage decision targets
			// the same Key; otherwise pass through unchanged.
			var keysUpdated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var record in existing)
			{
				if (decisionsByKey.TryGetValue(record.Key, out var decision))
				{
					keysUpdated.Add(record.Key);
					result.Add(record with
					{
						Status = decision.Status,
						Comment = decision.Comment,
						DateLastSeen = today,
						DateReported = string.IsNullOrEmpty(record.DateReported)
							? today
							: record.DateReported,
						// Triage often supplies a richer Excerpt and SourceLabel
						// from the current run — prefer the decision's value
						// when present, fall back to existing.
						Excerpt = string.IsNullOrEmpty(decision.Excerpt)
							? record.Excerpt
							: decision.Excerpt,
						SourceLabel = string.IsNullOrEmpty(decision.SourceLabel)
							? record.SourceLabel
							: decision.SourceLabel,
					});
				}
				else
				{
					// Untouched — explicitly NOT marked 'fixed' just because
					// it isn't in the triage decisions list.
					result.Add(record);
				}
			}

			// Add new triage decisions that didn't match an existing record.
			foreach (var decision in decisions)
			{
				if (string.IsNullOrEmpty(decision.Type) || string.IsNullOrEmpty(decision.Url))
				{
					continue;
				}

				if (!keysUpdated.Contains(decision.Key))
				{
					result.Add(decision);
				}
			}

			return result;
		}

		// ── Parsers ───────────────────────────────────────────────────────────

		/// <summary>
		/// Parses the 07-404-sources dual-locale CSV pair (the _semicolon.csv is the
		/// canonical machine-read side; quote-aware via ParseCsvLine).
		/// Columns: 404Url, SourceUrl (header row skipped).
		/// Url = 404Url, SourceLabel = SourceUrl.
		/// </summary>
		public static List<IssueRecord> PromoteFrom404(string csvBasePath)
		{
			var semicolonPath = csvBasePath + IssueLogWriter.CsvSemicolonSuffix;
			if (!File.Exists(semicolonPath))
			{
				return [];
			}

			return File.ReadAllLines(semicolonPath, Encoding.UTF8)
				.Where(l => l.Length > 0)
				.Select(l => IssueLogWriter.ParseCsvLine(l, ';'))
				.Where(p => p.Length > 0
					&& !string.Equals(p[0], "404Url", StringComparison.OrdinalIgnoreCase)) // header row
				.Select(p =>
				{
					string F(int i) => i < p.Length ? p[i].Trim() : string.Empty;
					return new IssueRecord
					{
						Type = "404",
						Url = F(0),
						SourceLabel = F(1),
					};
				})
				.Where(r => !string.IsNullOrEmpty(r.Url))
				.DistinctBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		/// <summary>
		/// Parses the 09-self-link-analysis dual-locale CSV pair (the _semicolon.csv
		/// is the canonical machine-read side; quote-aware via ParseCsvLine).
		/// Columns: File, FileUrl, LinkFound, ContextSnippet (header row skipped).
		/// Url = FileUrl, Excerpt = ContextSnippet.
		/// Every row is an issue — a page linking to itself.
		/// </summary>
		public static List<IssueRecord> PromoteFromSelfLink(string csvBasePath)
		{
			var semicolonPath = csvBasePath + IssueLogWriter.CsvSemicolonSuffix;
			if (!File.Exists(semicolonPath))
			{
				return [];
			}

			return File.ReadAllLines(semicolonPath, Encoding.UTF8)
				.Where(l => l.Length > 0)
				.Select(l => IssueLogWriter.ParseCsvLine(l, ';'))
				.Where(p => p.Length > 0
					&& !string.Equals(p[0], "File", StringComparison.OrdinalIgnoreCase)) // header row
				.Select(p =>
				{
					string F(int i) => i < p.Length ? p[i].Trim() : string.Empty;
					var filename = F(0);
					return new IssueRecord
					{
						Type = "SELFLINK",
						Url = F(1),
						// F(3) ContextSnippet is the one read-back content field that can
						// carry a write-side formula-injection neutralizer; strip it so the
						// excerpt round-trips to its original text (see CsvInjectionGuard).
						Excerpt = CsvInjectionGuard.Denormalize(F(3)),
						CrawlSource = CrawlIndex.LookUpSourceForFile(filename),
					};
				})
				.Where(r => !string.IsNullOrEmpty(r.Url))
				.DistinctBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		/// <summary>
		/// Parses 03-redirect-analysis.log.
		/// Format: {url} | {status} | {redirectTarget} > {chain}
		/// Issue rule: line contains "Found" more than once (double redirect).
		/// NotFound at end = out-of-scope external URL, ignored.
		/// Url = first URL in the line, Excerpt = full chain.
		/// </summary>
		public static List<IssueRecord> PromoteFromRedirect(string logPath)
		{
			if (!File.Exists(logPath))
			{
				return [];
			}

			List<IssueRecord> issues = [];

			foreach (var line in File.ReadAllLines(logPath, Encoding.UTF8))
			{
				if (line.Length == 0)
				{
					continue;
				}

				// Count "Found" occurrences excluding "NotFound" — double redirect = issue.
				int foundCount = CountOccurrences(line, "Found", StringComparison.OrdinalIgnoreCase);
				int notFoundCount = CountOccurrences(line, "NotFound", StringComparison.OrdinalIgnoreCase);
				int netFound = foundCount - notFoundCount;
				if (netFound < 2)
				{
					continue;
				}

				// First segment before " | " is the originating URL.
				var firstPipe = line.IndexOf(" | ", StringComparison.Ordinal);
				var url = firstPipe >= 0 ? line[..firstPipe].Trim() : line.Trim();

				issues.Add(new IssueRecord
				{
					Type = "REDIRECT",
					Url = url,
					Excerpt = line.Trim(),
				});
			}

			return issues
				.DistinctBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		/// <summary>
		/// Parses 10-content-quality-issues.log.
		/// Format: Filename|IssueType|Detail|Context (header line skipped)
		/// Type = QUALITY, Url resolved from filename via Cache,
		/// Word = IssueType (LIGATURE, QUOTE_UNMATCHED etc.), Excerpt = Context.
		///
		/// [KEEP] Reader-side defence: lines whose F(0) doesn't look like a
		/// filename are skipped with a warning. Writers route through
		/// IssueLogWriter which sanitises content, but this defence protects
		/// against historical / out-of-tree log files that may have been
		/// written before sanitization existed.
		/// </summary>
		public static List<IssueRecord> PromoteFromQuality(string logPath)
		{
			if (!File.Exists(logPath))
			{
				return [];
			}

			return File.ReadAllLines(logPath, Encoding.UTF8)
				.Where(l => l.Length > 0
					&& !l.StartsWith("Filename|", StringComparison.OrdinalIgnoreCase))
				.Select(line =>
				{
					var p = line.Split('|');
					string F(int i) => i < p.Length ? p[i].Trim() : string.Empty;
					var filename = F(0);

					if (!LooksLikeFilename(filename))
					{
						Logger.LogWarning(
							$"PromoteFromQuality: skipping line with non-filename F(0): " +
							$"'{Truncate(filename, 80)}'. " +
							$"This usually indicates a malformed log line (e.g. embedded newline " +
							$"in content); review the writer for the relevant log.");
						return (IssueRecord?)null;
					}

					var url = CrawlIndex.LookUpUrlForFile(filename);
					var issueType = F(1);
					// Reject records where the IssueType field
					// is empty (malformed-line residue). Also reject the
					// pathological combination of a failed URL lookup AND empty
					// IssueType — that's the exact shape persisted by earlier
					// runs as anonymous "auto|||QUALITY|error|..." records.
					// A legitimate quality issue always has a non-empty
					// IssueType (LIGATURE, QUOTE_*, etc.). A non-empty
					// IssueType with a failed URL lookup is preserved as-is
					// (Url falls back to filename per the existing logic).
					if (string.IsNullOrEmpty(issueType))
					{
						return (IssueRecord?)null;
					}

					var detail = F(2);

					// For IssueTypes whose Detail carries a
					// meaningful sub-category, build a composite Word that includes
					// the Detail field (e.g. "UNWANTED_PATTERN:Category: Name —
					// pattern: example", or "MALFORMED_HTML:CONTENT_BEFORE_DOCTYPE").
					// This matches the composite Word shape produced on the triage /
					// detection side, so auto-promoted and triage-promoted records
					// share the same Key for the same underlying issue. Without this,
					// triage decisions create parallel records instead of updating the
					// auto-promoted ones.
					//
					// Composite shape is used where the IssueType has meaningful
					// sub-categories in Detail:
					//   UNWANTED_PATTERN  — Detail is "Category: Name — pattern(s): …"
					//   MALFORMED_HTML    — Detail is the sub-defect (CONTENT_BEFORE_DOCTYPE);
					//                       a server-side defect, auto-promoted only.
					//   CONTROL_CHARS_IN_CONTENT — Detail carries a stable identity payload
					//                       after the ControlChars identity separator: a
					//                       location token plus the full marker-encoded
					//                       element text. The triage path keys on that
					//                       payload (so each affected element on a page gets
					//                       its own Key); this side MUST key on the identical
					//                       payload or the auto-promoted (detected) record and
					//                       the ticketed record get different Keys — the
					//                       ticketed pending row would then fail Merge's
					//                       gone-is-gone match and be dropped, and the finding
					//                       would re-present every run. The split mirrors the
					//                       triage builder exactly; a line with no separator
					//                       (pre-upgrade log) falls back to the whole Detail,
					//                       matching the triage fallback.
					// Other QUALITY IssueTypes (LIGATURE, BARE_TEXT_*, QUOTE_*, etc.)
					// use the bare IssueType Word because their triage paths do too,
					// or because composing with Detail would over-discriminate the
					// Key space.
					string word;
					if (issueType == "UNWANTED_PATTERN")
					{
						// A url-tagged occurrence (Detail carries the ExcludeUrl marker) keys
						// on the pattern prose LEFT of the marker as a UNWANTED_PATTERN_URL
						// summary, so every url occurrence on a page collapses to one Key (the
						// trailing DistinctBy(Key) folds them) — matching the triage summary
						// Word byte-for-byte, or the ticketed summary and the detected set get
						// different Keys and Merge drops it (the same failure the control-chars
						// round-trip had). An untagged UNWANTED_PATTERN keeps the composite
						// Word (per-occurrence), as before.
						var marker = Quality.UnwantedPatterns.UrlMarker;
						var markerIndex = detail.IndexOf(marker, StringComparison.Ordinal);
						if (markerIndex >= 0)
						{
							var prose = detail[..markerIndex];
							word = $"UNWANTED_PATTERN_URL:{prose}";
						}
						else
						{
							word = string.IsNullOrEmpty(detail) ? issueType : $"{issueType}:{detail}";
						}
					}
					else if (issueType == "MALFORMED_HTML")
					{
						word = string.IsNullOrEmpty(detail) ? issueType : $"{issueType}:{detail}";
					}
					else if (issueType == "CONTROL_CHARS_IN_CONTENT")
					{
						var sep = Quality.ControlChars.IdentitySeparator;
						var sepIndex = detail.IndexOf(sep, StringComparison.Ordinal);
						var identityPayload = sepIndex >= 0
							? detail[(sepIndex + sep.Length)..]
							: detail;
						word = $"{issueType}:{identityPayload}";
					}
					else
					{
						word = issueType;
					}

					return new IssueRecord
					{
						Type = "QUALITY",
						Url = string.IsNullOrEmpty(url) || url == "error" ? filename : url,
						Word = word,
						SourceLabel = detail,
						Excerpt = F(3),         // Context
						CrawlSource = CrawlIndex.LookUpSourceForFile(filename),
					};
				})
				.Where(r => r != null && !string.IsNullOrEmpty(r!.Url))
				.Cast<IssueRecord>()
				.DistinctBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		/// <summary>
		/// Parses 08-seo-data.csv and promotes pages that are indexable (no noindex
		/// robots directive) but were only reachable via the CMS list pass (source = "list"),
		/// meaning they have no inbound links from crawled pages.
		/// Type = SEO, Word = "robots:index+source:list", Comment = "IndexableButNotCrawlable".
		/// Auto-promoted — no human triage required.
		/// </summary>
		public static List<IssueRecord> PromoteFromSeo(string seoLogPath, SeoConfig seo)
		{
			if (!File.Exists(seoLogPath))
			{
				return [];
			}

			var issues = new List<IssueRecord>();

			// Normalised allow-list of robots values that qualify a page for SEO
			// content findings. Pages whose robots value is not in this set are
			// suppressed (noise reduction — noindex/tech pages are not SEO targets).
			// Normalised once here; each page's robots value is normalised the same
			// way before lookup, so spelling/case/order variance matches.
			var indexableRobots = seo.IndexableRobotsValues
				.Select(NormalizeRobots)
				.ToHashSet(StringComparer.Ordinal);

			foreach (var line in File.ReadLines(seoLogPath, Encoding.UTF8))
			{
				if (string.IsNullOrWhiteSpace(line))
				{
					continue;
				}
				// Skip the column header, and defensively a legacy "sep=;" directive
				// (08-seo-data.csv files written before D085 carried one; the writer no
				// longer emits it because it breaks Excel's BOM-based UTF-8 detection).
				if (line.StartsWith("sep=", StringComparison.OrdinalIgnoreCase)
					|| line.StartsWith("url;", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var parts = IssueLogWriter.ParseCsvLine(line, ';');
				string F(int i) => i < parts.Length ? parts[i].Trim() : string.Empty;

				// Column indices (08-seo-data.csv):
				// url=0 robots=1 title=2 titleLen=3 desc=4 descLen=5 keywords=6 source=7 h1Count=8
				var url = F(0);
				var robots = F(1).ToLowerInvariant();
				var title = F(2);
				var desc = F(4);
				var keywords = F(6);
				var source = F(7);

				void Flag(string word, string comment, string label = "", string excerpt = "") =>
					issues.Add(new IssueRecord
					{
						Type = "SEO",
						Url = url,
						Word = word,
						Comment = comment,
						SourceLabel = string.IsNullOrEmpty(label) ? source : label,
						Excerpt = excerpt,
						CrawlSource = source,
					});

				// ── Per-page content checks ──────────────────────────────────────
				// Only for pages that are SEO targets per the configured robots
				// allow-list. Non-indexable pages (noindex tech/print/staging pages)
				// are skipped — findings against them are moot and pure noise. The
				// IndexableButNotCrawlable check below is intentionally OUTSIDE this
				// gate: it is a crawlability finding with its own indexability logic.
				if (indexableRobots.Contains(NormalizeRobots(robots)))
				{
					// Title: extract the {title} portion per the first matching template
					// (if any), then length-check that portion; flag a framing mismatch
					// when the title matches no template.
					var (titleCore, titleFramingOk) = ExtractTitleCore(title, seo.TitleTemplates);
					if (!titleFramingOk)
					{
						Flag("InconsistentTitleFormat", "InconsistentTitleFormat",
							$"expected framing: {string.Join(" | ", seo.TitleTemplates)}", title);
					}

					// Title length is measured ASYMMETRICALLY against the template framing:
					//   • Too-LONG uses the stripped {title} core — the brand suffix is
					//     truncated by search engines anyway, so it must not count toward
					//     the maximum (a long brand should not flag every page).
					//   • Too-SHORT uses the WHOLE title — the brand framing fills out the
					//     title the user/SERP sees, so it legitimately counts toward the
					//     minimum. "Our Cars | Brand Name" is a fine title even though the
					//     core "Our Cars" is short.
					// With no template, core == whole title and the two coincide.
					// Excerpt carries the offending title for triage; ComposeLine sanitizes
					// the pipe delimiter (→ '/') automatically on write.
					if (string.IsNullOrWhiteSpace(titleCore))
					{
						Flag("MissingTitle", "MissingTitle");
					}
					else
					{
						if (title.Length < seo.TitleMinLength)
						{
							Flag("TitleTooShort", "TitleTooShort", $"{title.Length} < {seo.TitleMinLength}", title);
						}

						if (titleCore.Length > seo.TitleMaxLength)
						{
							Flag("TitleTooLong", "TitleTooLong", $"{titleCore.Length} > {seo.TitleMaxLength}", title);
						}
					}

					// Description. Excerpt carries the offending description text.
					if (string.IsNullOrWhiteSpace(desc))
					{
						Flag("MissingDescription", "MissingDescription");
					}
					else if (desc.Length < seo.DescriptionMinLength)
					{
						Flag("DescriptionTooShort", "DescriptionTooShort", $"{desc.Length} < {seo.DescriptionMinLength}", desc);
					}
					else if (desc.Length > seo.DescriptionMaxLength)
					{
						Flag("DescriptionTooLong", "DescriptionTooLong", $"{desc.Length} > {seo.DescriptionMaxLength}", desc);
					}

					// Meta keywords — obsolete tag; flag its presence when configured.
					if (seo.MetaKeywordsFlagAsError && !string.IsNullOrWhiteSpace(keywords))
					{
						Flag("MetaKeywordsPresent", "MetaKeywordsPresent");
					}

					// H1 presence / uniqueness (column 8). Parse defensively.
					if (int.TryParse(F(8), out var h1Count))
					{
						if (seo.MissingH1FlagAsError && h1Count == 0)
						{
							Flag("MissingH1", "MissingH1");
						}
						else if (seo.MultipleH1FlagAsError && h1Count > 1)
						{
							Flag("MultipleH1", "MultipleH1", $"{h1Count} h1 elements");
						}
					}
				}

				// ── Existing list-gated check: indexable but only reachable via list ─
				if (source.Equals("list", StringComparison.OrdinalIgnoreCase))
				{
					var directives = robots.Split(',').Select(d => d.Trim()).ToArray();
					if (!directives.Contains("noindex"))
					{
						Flag("robots:index+source:list", "IndexableButNotCrawlable");
					}
				}
			}

			return issues
				.DistinctBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		/// <summary>
		/// Normalises a robots meta value for order/case/space-insensitive matching:
		/// lower-cases, splits on commas, trims each directive, drops empties, sorts,
		/// and rejoins with a single comma. So "index, follow", "INDEX,FOLLOW", and
		/// "follow,index" all normalise to "follow,index"; an empty or whitespace
		/// value normalises to the empty string (matching a configured "" entry).
		/// </summary>
		private static string NormalizeRobots(string? robots)
		{
			if (string.IsNullOrWhiteSpace(robots))
			{
				return string.Empty;
			}

			var directives = robots
				.ToLowerInvariant()
				.Split(',')
				.Select(d => d.Trim())
				.Where(d => d.Length > 0)
				.OrderBy(d => d, StringComparer.Ordinal);

			return string.Join(",", directives);
		}

		/// <summary>
		/// Extracts the variable <c>{title}</c> portion of a page title given an
		/// optional template, and reports whether the title's fixed framing matches.
		/// Empty template → the whole title is the core and framing is trivially OK
		/// (no conformance check). With a template like <c>"{title} | Brand"</c> or
		/// <c>"Brand | {title}"</c>, the literal text before/after <c>{title}</c> is
		/// the expected framing: it is stripped to yield the core for length
		/// measurement, and if the actual title does not carry that exact framing
		/// (prefix and suffix), <paramref name="framingOk"/> is false. Matching is
		/// strict (ordinal, no case/whitespace tolerance) by design.
		/// </summary>
		/// <summary>
		/// Tries each template in order and returns the {title} core of the FIRST whose
		/// framing the title fully matches, with <c>framingOk=true</c>. If the list is
		/// empty (no template configured) the whole title is the core and framing is OK.
		/// If non-empty but no entry matches, returns the whole title with
		/// <c>framingOk=false</c> (→ InconsistentTitleFormat, whole-title length).
		/// First-match-wins makes list order operator-controlled when several could match.
		/// </summary>
		private static (string core, bool framingOk) ExtractTitleCore(string title, List<string> templates)
		{
			if (templates is null || templates.Count == 0)
			{
				return (title, true);
			}

			foreach (var template in templates)
			{
				if (string.IsNullOrEmpty(template))
				{
					continue;
				}

				var (core, ok) = TryExtractWithTemplate(title, template);
				if (ok)
				{
					return (core, true);
				}
			}

			// Matched no template's framing: measure length against the whole title
			// and report the mismatch.
			return (title, false);
		}

		/// <summary>
		/// Single-template framing strip. Returns the {title} core and <c>ok=true</c> when
		/// the title carries this template's prefix and suffix literals exactly; otherwise
		/// returns the whole title with <c>ok=false</c>. Strict, ordinal comparison.
		/// </summary>
		private static (string core, bool ok) TryExtractWithTemplate(string title, string template)
		{
			int ph = template.IndexOf("{title}", StringComparison.Ordinal);
			if (ph < 0)
			{
				return (title, true);  // defensive; config validation guarantees one
			}

			var prefix = template[..ph];
			var suffix = template[(ph + "{title}".Length)..];

			bool prefixOk = title.StartsWith(prefix, StringComparison.Ordinal);
			bool suffixOk = title.EndsWith(suffix, StringComparison.Ordinal);

			// The core must be long enough to contain both literals without overlap.
			bool longEnough = title.Length >= prefix.Length + suffix.Length;

			if (prefixOk && suffixOk && longEnough)
			{
				var core = title.Substring(prefix.Length, title.Length - prefix.Length - suffix.Length);
				return (core, true);
			}

			return (title, false);
		}

		/// <summary>
		/// Parses 11-spell-error-sources.log.
		/// Format: {pageUrl}|{filename}|{word (lang)}|{word (lang)}...
		/// One record per word per page.
		/// Type = SPELLING, Url = pageUrl, Word = word, Language = lang code.
		/// </summary>
		public static List<IssueRecord> PromoteFromSpelling(string logPath)
		{
			if (!File.Exists(logPath))
			{
				return [];
			}

			List<IssueRecord> issues = [];

			foreach (var line in File.ReadAllLines(logPath, Encoding.UTF8))
			{
				if (line.Length == 0)
				{
					continue;
				}

				var parts = line.Split('|');
				if (parts.Length < 3)
				{
					continue;
				}

				var pageUrl = parts[0].Trim();

				// parts[1] = filename (optional when showLocalSourceInLog is false)
				// parts[2..] = word entries formatted as "word (lang)" or "word (lang) (meta[...])"
				int wordStart = parts.Length > 2 ? 2 : 1;
				for (int i = wordStart; i < parts.Length; i++)
				{
					var entry = parts[i].Trim();
					if (entry.Length == 0)
					{
						continue;
					}

					// Parse "word (lang)" — word is everything before the first " ("
					var parenIdx = entry.IndexOf(" (", StringComparison.Ordinal);
					var word = parenIdx >= 0 ? entry[..parenIdx].Trim() : entry;
					var lang = string.Empty;

					if (parenIdx >= 0)
					{
						// Extract lang code from first parenthetical "(lang)"
						var closeIdx = entry.IndexOf(')', parenIdx);
						if (closeIdx > parenIdx + 2)
						{
							lang = entry[(parenIdx + 2)..closeIdx].Trim();
						}
					}

					if (string.IsNullOrEmpty(word))
					{
						continue;
					}

					var spellFilename = Cache.FilenameFor(pageUrl);
					issues.Add(new IssueRecord
					{
						Type = "SPELLING",
						Url = pageUrl,
						Word = word,
						Language = lang,
						CrawlSource = spellFilename is not null
							? CrawlIndex.LookUpSourceForFile(spellFilename)
							: string.Empty,
					});
				}
			}

			return issues
				.DistinctBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		// ── Helper ────────────────────────────────────────────────────────────

		/// <summary>
		/// Cheap sanity check: does this look like a filename rather than
		/// crawled content text? Used as a reader-side defence in PromoteFrom*
		/// methods — if a log line's F(0) doesn't look like a filename, the
		/// line is most likely a corrupted entry (e.g. embedded newline in
		/// content split one record across multiple lines).
		///
		/// Rejection criteria (any one is enough): contains whitespace,
		/// starts with a dash, longer than 260 chars (Windows MAX_PATH),
		/// contains a control character, or contains zero non-letter / non-
		/// digit characters across its entire length.
		/// </summary>
		internal static bool LooksLikeFilename(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return false;
			}

			if (s.Length > 260)
			{
				return false;
			}

			if (s[0] == '-' || s[0] == ' ')
			{
				return false;
			}

			foreach (var ch in s)
			{
				if (ch == ' ' || ch == '\t')
				{
					return false;
				}

				if (ch < 0x20)
				{
					return false;
				}
			}

			return true;
		}

		private static string Truncate(string s, int max) =>
			s.Length <= max ? s : s[..max] + "…";

		private static int CountOccurrences(string text, string pattern,
			StringComparison comparison)
		{
			int count = 0;
			int idx = 0;
			while ((idx = text.IndexOf(pattern, idx, comparison)) >= 0)
			{
				count++;
				idx += pattern.Length;
			}
			return count;
		}

		/// <summary>
		/// Parses 10-content-quality-issues.log for SPLIT_WORD_ANCHOR entries and
		/// extracts the artifact word (fragment inside the anchor tag) per filename.
		/// These words are spelling artifacts caused by the CMS authoring error —
		/// they should be suppressed from spell-check results.
		///
		/// Extraction: the context column contains "...text>Fragment</a>char..."
		/// The fragment is the text between the last '>' and '</a>'.
		/// </summary>
		public static Dictionary<string, HashSet<string>> BuildSplitAnchorArtifacts(
			string qualityLogPath)
		{
			var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

			if (!File.Exists(qualityLogPath))
			{
				return result;
			}

			var anchorPattern = new System.Text.RegularExpressions.Regex(
				@">([^<>]+)</a>",
				System.Text.RegularExpressions.RegexOptions.Compiled);

			foreach (var line in File.ReadAllLines(qualityLogPath, Encoding.UTF8))
			{
				if (line.Length == 0)
				{
					continue;
				}

				var p = line.Split('|');
				if (p.Length < 2)
				{
					continue;
				}

				var issueType = p.Length > 1 ? p[1].Trim() : string.Empty;
				if (!issueType.Equals("SPLIT_WORD_ANCHOR", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var filename = p[0].Trim();
				var context = p.Length > 3 ? p[3].Trim() : string.Empty;

				var match = anchorPattern.Match(context);
				if (!match.Success)
				{
					continue;
				}

				var artifact = match.Groups[1].Value.Trim();
				if (string.IsNullOrEmpty(artifact))
				{
					continue;
				}

				if (!result.TryGetValue(filename, out var words))
				{
					words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					result[filename] = words;
				}
				words.Add(artifact);
			}

			return result;
		}
	}
}
