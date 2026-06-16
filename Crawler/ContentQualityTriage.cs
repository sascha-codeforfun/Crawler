using System.Text;

namespace Crawler
{
	// ── ContentQualityTriage ──────────────────────────────────────────────────
	//
	// Reads 10-content-quality-issues.log, groups issues by type and page,
	// presents them for interactive triage, and returns a list of IssueRecords
	// to feed into IssueTracking.Merge().
	//
	// Grouping strategy:
	//   UNWANTED_PATTERN  — grouped by pattern string (one entry covers all pages,
	//                       comment references the log + page count)
	//   QUOTE_SYSTEM_MIX
	//   QUOTE_UNMATCHED   — grouped by page URL (all quote issues shown together)
	//   QUOTE_WRONG_CLOSE
	//   QUOTE_WRONG_OPEN
	//   SPLIT_WORD_ANCHOR      — grouped by page URL
	//   MISPLACED_ANCHOR_EMPTY — grouped by page URL
	//   ADJACENT_ANCHOR        — grouped by page URL (was MISPLACED_ANCHOR_SPLIT)
	//   POTENTIAL_TRANSLATION  — grouped by page URL
	//   LIGATURE               — grouped by page URL
	//
	// Options per group:
	//   [T] Ticket  — promote as status "new"
	//   [L] Locale  — promote as status "config" (intentional language decision)
	//   [W] Wontfix — promote as status "wontfix" with comment
	//   [S] Skip    — do not promote this run
	//   [Q] Quit    — stop triage, return decisions made so far
	//
	// [L] is only offered for POTENTIAL_TRANSLATION groups.
	// ─────────────────────────────────────────────────────────────────────────

	public static class ContentQualityTriage
	{
		// The WCAG-violation marker injected into an essentially-empty anchor's
		// (absent) text slot so the defect renders as a coloured presence rather
		// than an absence the eye must infer. Defined once here and reused by the
		// injector and both render sites (live triage + review) so the literal
		// never drifts between them. Rendered in ConsoleUi's reserved WCAG scheme
		// (white-on-DarkBlue) via WriteWithWcagMarkerHighlight.
		internal const string EmptyLinkMarker = "[WCAG-VIOLATION-EMPTY-LINK]";

		// ── Public entry point ────────────────────────────────────────────────

		/// <summary>
		/// Runs interactive content quality triage. Returns IssueRecords for
		/// all decisions made. Only call in non-silent mode.
		/// </summary>
		public static List<IssueTracking.IssueRecord> Run(
			string qualityLogPath,
			string qualityLogFilename,   // e.g. "10-content-quality-issues.log"
			string issueTrackingPath,
			ContentQualityConfig config,
			string fileDownloadDirectory,
			IReadOnlyList<UrlHighlightRule> urlHighlightRules,
			Crawler.Boilerplate.BoilerplateResolver? boilerplateResolver = null)
		{
			List<IssueTracking.IssueRecord> promoted = [];

			if (!File.Exists(qualityLogPath))
			{
				Logger.LogInfo("ContentQualityTriage: log not found, skipping.");
				return promoted;
			}

			var groups = BuildGroups(qualityLogPath, qualityLogFilename, config, fileDownloadDirectory);

			// The set of tracking keys present in THIS crawl. A review item whose
			// key is absent here is no longer detected — it was fixed, the page
			// left scope (e.g. an escaped host now blocked), or the page
			// is simply gone. Such stale items must not be presented for review:
			// the end-of-run IssueTracking.Merge promotes "not detected this run"
			// records to "fixed", but that runs LATER (Program.RunAsync), so at
			// triage time the tracking file still shows the stale status. Gating
			// review by the live key set is the in-pipeline equivalent — it needs
			// no Merge to have run, and (correctly) cannot move Merge earlier
			// because triage itself produces status changes that Merge must see.
			// Key shape matches: TrackingKeys yields IssueRecord.Key values, the
			// same .Key the review's loaded records carry. Status arg is
			// irrelevant to the key value (Key = Type+Url+Word).
			var liveKeys = groups
				.SelectMany(g => g.TrackingKeys("new"))
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			// Offer a review pass over already-triaged items (pending/wontfix)
			// before suppression — mirrors spelling triage's ReviewTriagedItems.
			// A discard here resets the record to 'new', so it survives the
			// suppression filter below and flows back into the live walk.
			ReviewTriagedQualityItems(issueTrackingPath, liveKeys, urlHighlightRules);

			// [KEEP] Suppress groups already decided in IssueTracking.
			// wontfix/config/fixed = operator decided, do not re-present.
			// pending = ticketed via [T] — also decided, do not
			//   re-present (mirrors spelling triage, where [T]→pending drops
			//   the item from the walk).
			// new/overdue/reopened = still open and not yet ticketed —
			//   re-present if re-detected.
			// A group occupies one tracking key PER per-type finding
			//   (TrackingKeys). A group is suppressed only when EVERY one of
			//   its keys is already decided — if any key is still open, the
			//   group is re-presented so the undecided findings can be triaged.
			if (File.Exists(issueTrackingPath))
			{
				var existing = IssueTracking.Load(issueTrackingPath);
				var suppressed = existing
					.Where(r => r.Status is "wontfix" or "config" or "fixed" or "pending")
					.Select(r => r.Key)
					.ToHashSet(StringComparer.OrdinalIgnoreCase);
				groups = groups
					.Where(g => !g.TrackingKeys("new").All(suppressed.Contains))
					.ToList();
			}

			if (groups.Count == 0)
			{
				ConsoleUi.WriteBlank();
				ConsoleUi.WriteSuccess("Content Quality Triage: nothing to review.");
				return promoted;
			}

			ConsoleUi.WriteBlank();
			ConsoleUi.WriteHeader($"CONTENT QUALITY TRIAGE — {groups.Count} group(s) to review.");
			ConsoleUi.WriteInfo("[T] Ticket  [W] Wontfix  [S] Skip  [Q] Quit");
			ConsoleUi.WriteInfo("[L] Locale  — shown only for POTENTIAL_TRANSLATION groups");
			ConsoleUi.WriteFooter();
			ConsoleUi.WriteBlank();

			int current = 0;
			int groupsActioned = 0;   // groups ticketed/wontfixed/localed (NOT skipped)
			foreach (var group in groups)
			{
				current++;

				ConsoleUi.WriteDivider();
				// On a boilerplate check page the whole page is checked (chrome included),
				// so a finding here CAN be site-wide chrome surfaced once — the operator
				// judges which from the excerpt. Tag the page; don't claim the verdict.
				var boilerTag = (boilerplateResolver?.Resolve(group.Url).IsCheckPage ?? false)
					? "boilerplate page (can contain site-wide chrome)"
					: null;
				ConsoleUi.WriteCardHeader(current, groups.Count, "Type", group.DisplayType, boilerTag);
				ConsoleUi.WriteUrlField(group.Url, urlHighlightRules);

				foreach (var line in group.DisplayLines)
				{
					if (group.DisplayType == "UNWANTED_PATTERN"
						&& line.StartsWith("Example : ", StringComparison.Ordinal)
						&& !string.IsNullOrEmpty(group.Word))
					{
						// Highlight the pattern string(s) in the Example line.
						// Word = "UNWANTED_PATTERN:Category: Name — pattern: X"  (single)
						//      = "UNWANTED_PATTERN:Category: Name — patterns: X, Y" (grouped)
						var highlightPatterns = ExtractHighlightPatterns(group.Word);

						var example = line["Example : ".Length..];
						ConsoleUi.WriteFieldInline("Example");
						ConsoleUi.WriteWithPatternHighlight(example, highlightPatterns);
						ConsoleUi.WriteBlank();
					}
					else if (group.DisplayType == "QUOTE ISSUES"
						&& line.StartsWith("Excerpt : ", StringComparison.Ordinal))
					{
						var body = line["Excerpt : ".Length..];
						ConsoleUi.WriteFieldInline("Excerpt");
						ConsoleUi.WriteWithQuoteSpans(
							body, ComputeQuoteSpans(body, group.QuoteTriggerPositions ?? []));
						ConsoleUi.WriteBlank();
					}
					else if ((group.DisplayType is "MISPLACED_ANCHOR_EMPTY")
						&& line.StartsWith("HTML    : ", StringComparison.Ordinal))
					{
						// Materialise the empty anchor's absent text as the WCAG marker,
						// then colour the anchor itself — structure (red), href (gold),
						// other attributes + surrounding markup dimmed — so the eye lands
						// on what matters. Source is the DisplayLines "HTML    : "
						// substring. Shared map + painter with the review branch.
						var markedHtml = InjectEmptyAnchorMarker(line["HTML    : ".Length..]);
						ConsoleUi.WriteFieldInline("HTML");
						ConsoleUi.WriteWithEmptyAnchorSpans(markedHtml, ComputeEmptyAnchorSpans(markedHtml));
						ConsoleUi.WriteBlank();
					}
					else if ((group.DisplayType is "SPLIT_WORD_ANCHOR")
						&& line.StartsWith("HTML    : ", StringComparison.Ordinal))
					{
						// Split-word: a token continues past </a>. Render three spans
						// (tags red, inside DarkCyan, tail DarkGreen) so the escaped
						// fragment is visible without reading the language. Spans are
						// computed by the pure helper; the primitive only paints them.
						// Shared with review. Source is the "HTML    : " substring.
						var html = line["HTML    : ".Length..];
						ConsoleUi.WriteFieldInline("HTML");
						ConsoleUi.WriteWithSplitWordHighlight(html, ComputeSplitWordSpans(html));
						ConsoleUi.WriteBlank();
					}
					else if ((group.DisplayType is "ADJACENT_ANCHOR")
						&& line.StartsWith("HTML    : ", StringComparison.Ordinal))
					{
						ConsoleUi.WriteFieldInline("HTML");
						var html = line["HTML    : ".Length..];
						ConsoleUi.WriteWithAdjacentAnchorHintHighlight(html, ComputeAdjacentHintSpans(html));
						ConsoleUi.WriteBlank();
					}
					else if ((group.DisplayType is "WORD_COLLISION")
						&& line.StartsWith("HTML    : ", StringComparison.Ordinal))
					{
						// Word collision: WORD1</tag>WORD2 with no separator. Three spans —
						// the inline text WORD1 (Inside/DarkCyan), the closing </tag> (Tag/red),
						// and the colliding tail WORD2 (Tail/DarkBlue = the WCAG-blue scheme).
						// Reuses the split-word painter; only the span computation differs.
						var html = line["HTML    : ".Length..];
						ConsoleUi.WriteFieldInline("HTML");
						ConsoleUi.WriteWithSplitWordHighlight(html, ComputeWordCollisionSpans(html));
						ConsoleUi.WriteBlank();
					}
					else if (group.DisplayType == "LIGATURE"
						&& line.StartsWith("Ligature : ", StringComparison.Ordinal))
					{
						// Highlight the ligature glyph(s) in the display line so the
						// offending character — easily lost mid-word, since it renders
						// as ordinary "fi"/"fl" — is visible at a glance. The body
						// after the "Ligature : " prefix carries the excerpt; the pure
						// helper locates the U+FB0x glyphs, the primitive paints them.
						// Shared with review (muted variant).
						var body = line["Ligature : ".Length..];
						ConsoleUi.WriteFieldInline("Ligature");
						ConsoleUi.WriteWithLigatureSpans(body, ComputeLigatureSpans(body));
						ConsoleUi.WriteBlank();
					}
					else
					{
						// Parse the pre-formatted "label : value" or "LABEL: value"
						// string from BuildGroups and re-emit through WriteField so
						// all property lines align to the canonical column
						// convention (Indent + 9-wide label + ": "). The upstream
						// uses two shapes:
						//   "Stray   : value"  / "Excerpt : value"  → " : " separator (field shape)
						//   "QUOTE_UNMATCHED: value"               → ": "  separator (diagnostic shape)
						// Try field shape first; fall back to diagnostic shape.
						// Routing through WriteField normalises the indent and
						// column treatment across both. Labels longer than 9 chars
						// (e.g. QUOTE_WRONG_CLOSE) spill their colon to the right
						// of the standard column — accepted: within one finding
						// the long diagnostic labels and short field labels (e.g.
						// Excerpt) don't share a colon column, but the indent
						// (2-space) and the label-then-colon shape are uniform.
						var sep = line.IndexOf(" : ", StringComparison.Ordinal);
						int valueOffset;
						if (sep > 0)
						{
							valueOffset = sep + 3;
						}
						else
						{
							sep = line.IndexOf(": ", StringComparison.Ordinal);
							valueOffset = sep + 2;
						}
						if (sep > 0)
						{
							var label = line[..sep].TrimEnd();
							var value = line[valueOffset..];
							ConsoleUi.WriteField(label, value);
						}
						else
						{
							// No colon separator at all — write as-is with
							// standard indent (defensive; should not happen with
							// current BuildGroups shapes).
							ConsoleUi.WriteLine($"{ConsoleUi.Indent}{line}");
						}
					}
				}

				ConsoleUi.WriteDivider();

				// Prompt loop and key handling route through
				// ConsoleTriage.Ask, standardising keypress semantics with the
				// other triages migrated in this series. Specifically:
				//   - Invalid keypresses re-prompt (previously: silently treated
				//     as "skip").
				//   - The [W] Wontfix follow-up comment uses ConsoleTriage.AskFreeText
				//     so all free-text inputs go through one path.
				// Ask takes verbose-labelled ChoiceOption instead of a
				// bare key list — promptline renders as
				// "[T] Ticket  [W] Wontfix  [S] Skip  [Q] Quit > ".
				// QUOTE ISSUES now render the full block inline (no [M] More), so
				// there is no continue-key behaviour — every key resolves the item.
				var choices = new List<ChoiceOption>
				{
					new(ConsoleKey.T, "Ticket"),
					new(ConsoleKey.W, "Wontfix"),
					new(ConsoleKey.S, "Skip"),
					new(ConsoleKey.Q, "Quit"),
				};
				if (group.IsTranslation)
				{
					choices.Insert(1, new ChoiceOption(ConsoleKey.L, "Locale"));
				}

				ConsoleKey key = ConsoleTriage.Ask(
					prompt: string.Empty,
					choices: choices,
					defaultKey: null,
					continueOnKey: _ => false);

				ConsoleUi.WriteBlank();

				if (key == ConsoleKey.Q)
				{
					ConsoleUi.WriteSkipped("Triage stopped.");
					ConsoleUi.WriteBlank();
					break;
				}

				if (key == ConsoleKey.T)
				{
					// Promote as "pending" (not "new") so a ticketed
					// content-quality issue drops out of the triage walk on
					// subsequent runs — consistent with spelling triage.
					// Emit one record PER per-type key (ToIssueRecords),
					// so grouped quote findings round-trip against the detector's
					// per-type keys instead of orphaning under a composite Word.
					promoted.AddRange(group.ToIssueRecords("pending", string.Empty));
					groupsActioned++;
					ConsoleUi.WriteActionRequired($"→ Ticket: {group.Url}");
				}
				else if (key == ConsoleKey.L && group.IsTranslation)
				{
					promoted.AddRange(group.ToIssueRecords("config", "Intentional — page language decision"));
					groupsActioned++;
					ConsoleUi.WriteSkipped($"→ Locale: {group.Url}");
				}
				else if (key == ConsoleKey.W)
				{
					var comment = ConsoleTriage.AskFreeText("Comment (optional, Enter to skip): ");
					promoted.AddRange(group.ToIssueRecords("wontfix", comment));
					groupsActioned++;
					ConsoleUi.WriteSkipped($"→ Wontfix: {group.Url}");
					ConsoleUi.WriteBlank();
				}
				else
				{
					ConsoleUi.WriteSkipped("→ Skipped");
				}
			}

			// Report in terms of GROUPS, consistent on both sides.
			// Previously each group promoted exactly one record so
			// promoted.Count == groups actioned; the 1:N quote change broke
			// that (4 quote groups → 8 records → "−4 skipped"). Count groups
			// acted on vs groups presented instead. The record count is shown
			// separately since 1:N expansion makes "records written" useful.
			ConsoleUi.WriteInfo(
				$"Quality triage complete: {groupsActioned} group(s) actioned " +
				$"({promoted.Count} record(s) written), " +
				$"{current - groupsActioned} skipped.");
			ConsoleUi.WriteBlank();

			return promoted;
		}

		// ── Group model ───────────────────────────────────────────────────────

		public record TriageGroup(
			string DisplayType,
			string Url,
			string Word,            // issue type or pattern — used as IssueRecord.Word
			string Comment,         // pre-built comment (e.g. "also affects N pages")
			string Excerpt,
			bool IsTranslation,
			List<string> DisplayLines,  // lines shown in the triage UI
			List<string>? TrackingWords = null,  // per-finding Words for 1:N promotion
			IReadOnlyList<int>? QuoteTriggerPositions = null)  // glyph offsets in Excerpt to mark as the offender (QUOTE ISSUES)
		{
			/// <summary>
			/// The Word(s) this group must promote as tracking records.
			/// Most groups map 1:1 — a single <see cref="Word"/>. QUOTE groups,
			/// however, bundle several distinct IssueTypes on one page-block for
			/// display, and the detector tracks each type separately (key
			/// QUALITY|url|IssueType). A single composite Word like
			/// "QUOTE_SYSTEM_MIX+QUOTE_UNMATCHED" matches NONE of the detector's
			/// per-type keys, so a ticketed quote group never round-trips: its
			/// pending record orphans while the per-type findings reappear as new.
			/// When <see cref="TrackingWords"/> is set, the group promotes one
			/// record per Word so every per-type key matches and is suppressed.
			/// Falls back to the single <see cref="Word"/> when null/empty.
			/// </summary>
			public IEnumerable<string> EffectiveWords =>
				TrackingWords is { Count: > 0 } ? TrackingWords : [Word];

			/// <summary>
			/// Expands the group into one IssueRecord per effective Word
			/// (see <see cref="EffectiveWords"/>). Replaces the former single-
			/// record ToIssueRecord so grouped multi-type findings (quotes) round-
			/// trip against the detector's per-type keys.
			/// </summary>
			public IEnumerable<IssueTracking.IssueRecord> ToIssueRecords(string status, string userComment) =>
				EffectiveWords.Select(w => new IssueTracking.IssueRecord
				{
					Source = "triage",
					Type = "QUALITY",
					Url = Url,
					Status = status,
					Word = w,
					Comment = string.IsNullOrEmpty(userComment) ? Comment : userComment,
					SourceLabel = DisplayType,
					Excerpt = Excerpt,
				});

			/// <summary>
			/// The tracking keys this group occupies — one per effective
			/// Word. Used by the suppression filter to decide whether a group is
			/// already fully decided.
			/// </summary>
			public IEnumerable<string> TrackingKeys(string status) =>
				ToIssueRecords(status, string.Empty).Select(r => r.Key);
		}

		// ── Group builder ─────────────────────────────────────────────────────

		/// <summary>
		/// Resolves a filename to its crawled URL using the URL cache.
		/// Falls back to the filename itself when the cache is not loaded (e.g. in tests).
		/// </summary>
		/// <summary>
		/// Writes text to the console with all typographic quote characters
		/// highlighted in white-on-red so they stand out immediately in long excerpts.
		/// </summary>

		private static string ResolveUrl(string filename)
		{
			var url = CrawlIndex.LookUpUrlForFile(filename);
			return string.IsNullOrEmpty(url) || url == "error" ? filename : url;
		}

		/// <summary>
		/// Reads the quality log and produces triage groups.
		/// Internal so it can be unit-tested without Console.
		/// </summary>
		/// <summary>
		/// One human-readable summary of a page-block's quote findings, replacing the
		/// raw "QUOTE_TYPE: detail" lines on the console card. Names the mixed systems
		/// (when a QUOTE_SYSTEM_MIX is present) and counts the unmatched / mismatched
		/// findings, e.g. "mixed German + English double quotes; 2 unclosed openers".
		/// The raw per-finding detail (positions, exact phrasing) stays in the log.
		/// </summary>
		internal static string SynthesizeQuoteNote(
			IReadOnlyList<(string Filename, string IssueType, string Detail, string Context)> entries)
		{
			var parts = new List<string>();

			var mix = entries.FirstOrDefault(e =>
				e.IssueType.Equals("QUOTE_SYSTEM_MIX", StringComparison.OrdinalIgnoreCase));
			if (mix.IssueType != null)
			{
				parts.Add(DescribeQuoteSystemMix(mix.Detail));
			}

			int Count(string type) =>
				entries.Count(e => e.IssueType.Equals(type, StringComparison.OrdinalIgnoreCase));
			void AddCount(string type, string singular, string plural)
			{
				var n = Count(type);
				if (n > 0)
				{
					parts.Add($"{n} {(n == 1 ? singular : plural)}");
				}
			}

			AddCount("QUOTE_UNMATCHED", "unclosed opener", "unclosed openers");
			AddCount("QUOTE_WRONG_CLOSE", "mismatched closer", "mismatched closers");
			AddCount("QUOTE_WRONG_OPEN", "mismatched opener", "mismatched openers");

			return parts.Count > 0 ? string.Join("; ", parts) : "quote issues";
		}

		/// <summary>
		/// Turns the QUOTE_SYSTEM_MIX detail ("Multiple quote systems: German-double,
		/// English-double") into "mixed German + English double quotes" when the listed
		/// systems share a shape suffix, else "mixed quote systems: &lt;list&gt;".
		/// </summary>
		private static string DescribeQuoteSystemMix(string detail)
		{
			const string prefix = "Multiple quote systems: ";
			var systems = detail.StartsWith(prefix, StringComparison.Ordinal)
				? detail[prefix.Length..]
				: detail;

			var items = systems.Split(',',
				StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
			if (items.Length >= 2)
			{
				var shapes = items
					.Select(s => s.Contains('-') ? s[(s.LastIndexOf('-') + 1)..] : string.Empty)
					.ToList();
				if (shapes.All(s => s.Length > 0 && string.Equals(s, shapes[0], StringComparison.Ordinal)))
				{
					var langs = items.Select(s => s[..s.LastIndexOf('-')]);
					return $"mixed {string.Join(" + ", langs)} {shapes[0]} quotes";
				}
			}

			return $"mixed quote systems: {systems}";
		}

		internal static List<TriageGroup> BuildGroups(
			string qualityLogPath,
			string qualityLogFilename,
			ContentQualityConfig config,
			string fileDownloadDirectory)
		{
			var lines = File.ReadAllLines(qualityLogPath, Encoding.UTF8)
				.Where(l => l.Length > 0
					&& !l.StartsWith("Filename|", StringComparison.OrdinalIgnoreCase))
				.Select(l =>
				{
					var p = l.Split('|');
					string F(int i) => i < p.Length ? p[i].Trim() : string.Empty;
					return (Filename: F(0), IssueType: F(1), Detail: F(2), Context: F(3));
				})
				.ToList();

			List<TriageGroup> groups = [];

			// ── UNWANTED_PATTERN — one entry per affected page ────────────────
			// [KEEP] No grouping — each page gets its own IssueTracking row so the
			// full list serves as a QA checklist when fixing the pattern site-wide.
			var unwanted = lines.Where(x => x.IssueType == "UNWANTED_PATTERN").ToList();
			foreach (var entry in unwanted)
			{
				var url = ResolveUrl(entry.Filename);
				if (string.IsNullOrEmpty(url))
				{
					url = entry.Filename;
				}

				groups.Add(new TriageGroup(
					DisplayType: "UNWANTED_PATTERN",
					Url: url,
					Word: $"UNWANTED_PATTERN:{entry.Detail}",
					Comment: $"See {qualityLogFilename} — pattern '{entry.Detail}'",
					Excerpt: entry.Context,
					IsTranslation: false,
					DisplayLines:
					[
						$"Pattern : {entry.Detail}",
						$"Example : {entry.Context}",
					]));
			}

			// ── Quote issues — group by page URL and block context ────────────
			// [KEEP] Grouped by both URL and Context (block text) so each triage entry
			// represents one block's issues. Grouping by URL alone merged issues from
			// different blocks — excerpt showed the wrong block, context was misleading.
			var quoteTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				"QUOTE_SYSTEM_MIX", "QUOTE_UNMATCHED", "QUOTE_WRONG_CLOSE", "QUOTE_WRONG_OPEN"
			};

			var quotesByBlock = lines
				.Where(x => quoteTypes.Contains(x.IssueType))
				.GroupBy(x => (Url: ResolveUrl(x.Filename), Context: x.Context))
				.OrderBy(g => g.Key.Url);

			foreach (var blockGroup in quotesByBlock)
			{
				var url = blockGroup.Key.Url;
				var entries = blockGroup.ToList();
				// One human Note instead of the raw per-finding "QUOTE_TYPE: detail"
				// lines (positions, CONSTANT_CASE, parser phrasing). The raw detail
				// stays in the content-quality log; the console card gets the summary.
				var display = new List<string> { $"Note : {SynthesizeQuoteNote(entries)}" };
				// [KEEP] Use the longest context available — different quote findings
				// store contexts of different lengths; the longest is the full block.
				var excerpt = entries.OrderByDescending(e => e.Context.Length).First().Context;
				var distinctTypes = entries.Select(e => e.IssueType).Distinct().ToList();
				var word = string.Join("+", distinctTypes);

				// Full block is shown inline (no truncation, no [M]) so the operator
				// always sees the offending glyph in its real context for the decision.
				if (!string.IsNullOrEmpty(excerpt))
				{
					display.Add($"Excerpt : {excerpt}");
				}

				// Locate the exact offending glyph(s) so the renderer can mark them as
				// the trigger (red) and every other quote as context (blue). Reuses the
				// detector on the stored block under the SAME page language analysis
				// used (resolved from <html lang>), so the marked glyph matches the
				// flagging decision. Any failure (missing file, language mismatch,
				// pipe-sanitised block) degrades to context-only highlighting — never a
				// wrong mark. Filtered to the types actually present in this block.
				IReadOnlyList<int> triggerPositions = [];
				if (!string.IsNullOrEmpty(excerpt))
				{
					try
					{
						string? lang = null;
						if (!string.IsNullOrEmpty(fileDownloadDirectory))
						{
							var resolved = HtmlLanguage.GetLanguageFromHtmlFile(
								entries[0].Filename, fileDownloadDirectory, string.Empty);
							lang = string.IsNullOrWhiteSpace(resolved) ? null : resolved;
						}
						var typesPresent = distinctTypes.ToHashSet(StringComparer.Ordinal);
						triggerPositions = ContentQuality.LocateQuoteFlags(excerpt, config, lang)
							.Where(f => typesPresent.Contains(f.Type))
							.Select(f => f.Pos)
							.ToList();
					}
					catch
					{
						// Graceful: context-only highlight (all quotes blue, none red).
						triggerPositions = [];
					}
				}

				groups.Add(new TriageGroup(
					DisplayType: "QUOTE ISSUES",
					Url: url,
					Word: word,
					Comment: string.Empty,
					Excerpt: excerpt,
					IsTranslation: false,
					DisplayLines: display,
					TrackingWords: distinctTypes,   // 1:N per-type promotion
					QuoteTriggerPositions: triggerPositions));
			}

			// ── SPLIT_WORD_ANCHOR — group by page URL ─────────────────────────
			var splitByPage = lines
				.Where(x => x.IssueType == "SPLIT_WORD_ANCHOR")
				.GroupBy(x => ResolveUrl(x.Filename),
					StringComparer.OrdinalIgnoreCase)
				.OrderBy(g => g.Key);

			foreach (var pageGroup in splitByPage)
			{
				var url = pageGroup.Key;
				var entries = pageGroup.ToList();
				// Two-line display per entry so the HTML context can
				// be picked up by the "HTML    : "-prefixed anchor-tag highlight
				// branch in the rendering loop (parallel to MISPLACED_ANCHOR_*).
				// Previously the display was a single line which fell through to
				// the plain WriteLine path — operator saw the broken anchor in
				// dim text rather than white-on-red.
				var display = entries.SelectMany(e => new[]
				{
					$"Stray   : {e.Detail}",
					$"HTML    : {e.Context}"
				}).ToList();

				groups.Add(new TriageGroup(
					DisplayType: "SPLIT_WORD_ANCHOR",
					Url: url,
					Word: "SPLIT_WORD_ANCHOR",
					Comment: string.Empty,
					Excerpt: entries[0].Context,
					IsTranslation: false,
					DisplayLines: display));
			}

			// ── MISPLACED_ANCHOR_EMPTY — group by page URL ──────────────────────
			var emptyAnchorsByPage = lines
				.Where(x => x.IssueType == "MISPLACED_ANCHOR_EMPTY")
				.GroupBy(x => ResolveUrl(x.Filename),
					StringComparer.OrdinalIgnoreCase)
				.OrderBy(g => g.Key);

			foreach (var pageGroup in emptyAnchorsByPage)
			{
				var url = pageGroup.Key;
				var entries = pageGroup.ToList();
				var display = entries.SelectMany(e => new[]
				{
					$"Anchor  : {e.Detail}",
					$"HTML    : {e.Context}"
				}).ToList();

				groups.Add(new TriageGroup(
					DisplayType: "MISPLACED_ANCHOR_EMPTY",
					Url: url,
					Word: "MISPLACED_ANCHOR_EMPTY",
					Comment: string.Empty,
					Excerpt: entries[0].Context,
					IsTranslation: false,
					DisplayLines: display));
			}

			// ── ADJACENT_ANCHOR (was MISPLACED_ANCHOR_SPLIT) — group by page URL
			var splitAnchorsByPage = lines
				.Where(x => x.IssueType == "ADJACENT_ANCHOR")
				.GroupBy(x => ResolveUrl(x.Filename),
					StringComparer.OrdinalIgnoreCase)
				.OrderBy(g => g.Key);

			foreach (var pageGroup in splitAnchorsByPage)
			{
				var url = pageGroup.Key;
				var entries = pageGroup.ToList();
				// Renumber the cluster: sort by source position (extracted
				// from each Detail's "[N]" prefix written by the detector), then
				// re-prefix with "[01]"/"[02]"/... for display. Single-entry
				// clusters get the prefix stripped (visual noise without
				// siblings). The detector-written raw "[boundaryAt]" stays in
				// the log file for stable across-crawl ordering.
				var renumbered = RenumberCluster(
					entries,
					getDetail: e => e.Detail,
					extractPosition: ExtractLeadingBracketPosition);
				var display = renumbered.SelectMany(r => new[]
				{
					$"Anchors : {r.Detail}",
					$"HTML    : {r.Source.Context}"
				}).ToList();

				groups.Add(new TriageGroup(
					DisplayType: "ADJACENT_ANCHOR",
					Url: url,
					Word: "ADJACENT_ANCHOR",
					Comment: string.Empty,
					Excerpt: renumbered[0].Source.Context,
					IsTranslation: false,
					DisplayLines: display));
			}

			// ── POTENTIAL_TRANSLATION — group by page URL ─────────────────────
			var transByPage = lines
				.Where(x => x.IssueType == "POTENTIAL_TRANSLATION")
				.GroupBy(x => ResolveUrl(x.Filename),
					StringComparer.OrdinalIgnoreCase)
				.OrderBy(g => g.Key);

			foreach (var pageGroup in transByPage)
			{
				var url = pageGroup.Key;
				var entries = pageGroup.ToList();
				var display = new List<string>
				{
					$"Elements: {entries.Count} element(s) passing alternate dictionary",
					$"Example : {entries[0].Detail}",
					$"Excerpt : {entries[0].Context}",
				};

				groups.Add(new TriageGroup(
					DisplayType: "POTENTIAL_TRANSLATION",
					Url: url,
					Word: "POTENTIAL_TRANSLATION",
					Comment: string.Empty,
					Excerpt: entries[0].Context,
					IsTranslation: true,
					DisplayLines: display));
			}

			// ── LIGATURE — group by page URL ──────────────────────────────────
			var ligByPage = lines
				.Where(x => x.IssueType == "LIGATURE")
				.GroupBy(x => ResolveUrl(x.Filename),
					StringComparer.OrdinalIgnoreCase)
				.OrderBy(g => g.Key);

			foreach (var pageGroup in ligByPage)
			{
				var url = pageGroup.Key;
				var entries = pageGroup.ToList();
				var display = entries.Select(e => $"Ligature : {e.Detail} — {e.Context}").ToList();

				groups.Add(new TriageGroup(
					DisplayType: "LIGATURE",
					Url: url,
					Word: "LIGATURE",
					Comment: string.Empty,
					Excerpt: entries[0].Context,
					IsTranslation: false,
					DisplayLines: display));
			}

			// ── WORD_COLLISION — group by page URL ────────────────────────────
			var collisionByPage = lines
				.Where(x => x.IssueType == "WORD_COLLISION")
				.GroupBy(x => ResolveUrl(x.Filename),
					StringComparer.OrdinalIgnoreCase)
				.OrderBy(g => g.Key);

			foreach (var pageGroup in collisionByPage)
			{
				var url = pageGroup.Key;
				var entries = pageGroup.ToList();
				// "HTML    : " prefix so the live/review loops route this through the
				// word-collision highlighter (WORD1 inside-blue, </tag> red, WORD2 WCAG-blue).
				var display = entries.Select(e => $"HTML    : {e.Context}").ToList();

				groups.Add(new TriageGroup(
					DisplayType: "WORD_COLLISION",
					Url: url,
					Word: "WORD_COLLISION",
					Comment: string.Empty,
					Excerpt: entries[0].Context,
					IsTranslation: false,
					DisplayLines: display));
			}

			return groups;
		}

		/// <summary>
		/// Review pass over already-triaged content-quality items (pending/wontfix)
		/// before suppression — the operator can leave each as-is [S] or discard [D]
		/// it back to 'new', which makes it survive suppression and reappear in the
		/// live triage walk. Mirrors SpellTriage.ReviewTriagedItems. Review uses the
		/// amber (muted) highlight so the operator reads it as review, not as live
		/// triage (which uses the red-scheme highlights).
		/// </summary>
		internal static void ReviewTriagedQualityItems(
			string issueTrackingPath,
			IReadOnlySet<string> liveKeys,
			IReadOnlyList<UrlHighlightRule> urlHighlightRules)
		{
			if (!File.Exists(issueTrackingPath))
			{
				return;   // no tracking file yet — nothing triaged
			}

			var records = IssueTracking.Load(issueTrackingPath);

			// Index-tagged so a discard can target the exact list slot.
			// Gated by liveKeys: only present already-triaged items still detected
			// in this crawl. Items absent from liveKeys are stale (fixed / out of
			// scope / page gone) — presenting them would ask the operator to
			// re-decide findings that no longer exist. See the call site for why
			// this gate (not an earlier Merge) is the correct fix.
			var reviewable = records
				.Select((r, i) => (record: r, index: i))
				.Where(x => x.record.Status == "pending" || x.record.Status == "wontfix")
				.Where(x => liveKeys.Contains(x.record.Key))
				.OrderBy(x => x.record.Status, StringComparer.Ordinal)   // pending before wontfix
					.ThenBy(x => x.record.Word, StringComparer.OrdinalIgnoreCase)
				.ToList();

			if (reviewable.Count == 0)
			{
				return;   // nothing triaged yet — no prompt
			}

			ConsoleUi.WriteHeader("CONTENT QUALITY REVIEW");
			ConsoleUi.WriteFooter();
			if (!ConsoleTriage.AskYesNo(
				$"Review {reviewable.Count} already-triaged content quality item(s) (pending/wontfix) before triage?"))
			{
				return;   // N — preserve today's behavior
			}

			ConsoleUi.WriteBlank();
			ConsoleUi.WriteInfo($"Review — {reviewable.Count} triaged item(s). [S] Skip leaves as-is, [D] Discard resets to new.");

			int discarded = 0;
			int position = 0;
			bool quit = false;
			foreach (var (record, index) in reviewable)
			{
				position++;
				ConsoleUi.WriteBlank();
				ConsoleUi.WriteDivider();
				ConsoleUi.WriteInfo($"[{position}/{reviewable.Count}]");
				ConsoleUi.WriteField("Status", record.Status);

				// Word highlighted (muted/amber) so the flagged term is unmistakable
				// while reading as review, not live triage.
				ConsoleUi.WriteLineWithMutedHighlight(
					$"{ConsoleUi.Indent}{"Word",-9}: ", record.Word, "");

				ConsoleUi.WriteField("Type", record.Type);

				if (!string.IsNullOrEmpty(record.SourceLabel))
				{
					ConsoleUi.WriteField("Source", record.SourceLabel);
				}

				ConsoleUi.WriteUrlField(record.Url, urlHighlightRules);

				if (!string.IsNullOrEmpty(record.Ticket))
				{
					ConsoleUi.WriteField("Ticket", record.Ticket);
				}

				if (!string.IsNullOrEmpty(record.DateReported))
				{
					ConsoleUi.WriteField("Reported", record.DateReported);
				}

				if (!string.IsNullOrEmpty(record.Comment))
				{
					ConsoleUi.WriteField("Decision", record.Comment);
				}

				// Context (Excerpt) highlighted in the review (amber) scheme,
				// dispatched by issue type so the operator sees the same visual
				// diagnosis as live triage — just muted to read as review. The raw
				// excerpt is the same text triage highlights; only the colour and
				// the call site differ. Anchor/quote/pattern types use structural
				// highlighters; simple-word types mark the literal Word in situ;
				// everything else falls back to a plain field.
				if (!string.IsNullOrEmpty(record.Excerpt))
				{
					var ctxPrefix = $"{ConsoleUi.Indent}{"Context",-9}: ";
					// Dispatch on SourceLabel — persisted records carry the issue
					// classification there (= the group's DisplayType); Type is
					// always "QUALITY". This matches how live triage keys its
					// highlighter choice off DisplayType.
					var displayType = record.SourceLabel ?? string.Empty;

					if (displayType == "MISPLACED_ANCHOR_EMPTY")
					{
						// Materialise the absent link text as a coloured marker so the
						// defect — an anchor with no visible text — reads as a present
						// thing rather than an absence the eye must infer between tag
						// boundaries. The injector covers every essentially-empty
						// shape (literal, whitespace-only, empty inline children); the
						// colour map then lights structure (red), href (gold) and dims
						// the rest. Shared with live triage so both render the same way.
						// No WriteBlank-then-newline doubling: the painter does not
						// self-terminate, so WriteBlank() closes the line.
						ConsoleUi.WriteInline(ctxPrefix);
						var markedHtml = InjectEmptyAnchorMarker(record.Excerpt);
						ConsoleUi.WriteWithEmptyAnchorSpans(markedHtml, ComputeEmptyAnchorSpans(markedHtml));
						ConsoleUi.WriteBlank();
					}
					else if (displayType == "SPLIT_WORD_ANCHOR")
					{
						// Split-word in review: same three-span highlight as live
						// triage (tags red, inside DarkCyan, tail DarkGreen). The
						// split is a defect whichever pass surfaces it, so it uses the
						// one split-word scheme rather than a muted review variant —
						// the escaped fragment must read the same to a non-fluent
						// operator routing it to a linguist. Source is record.Excerpt.
						ConsoleUi.WriteInline(ctxPrefix);
						ConsoleUi.WriteWithSplitWordHighlight(record.Excerpt, ComputeSplitWordSpans(record.Excerpt));
						ConsoleUi.WriteBlank();
					}
					else if (displayType == "ADJACENT_ANCHOR")
					{
						ConsoleUi.WriteInline(ctxPrefix);
						ConsoleUi.WriteWithAdjacentAnchorHintHighlightMuted(record.Excerpt, ComputeAdjacentHintSpans(record.Excerpt));
						ConsoleUi.WriteBlank();
					}
					else if (displayType == "WORD_COLLISION")
					{
						// Review twin of the live WORD_COLLISION branch. Like split-word,
						// a collision is a defect whichever pass surfaces it, so it uses the
						// one collision scheme (WORD1 inside-blue, </tag> red, WORD2 WCAG-blue)
						// rather than a muted variant. Source is record.Excerpt (the raw html).
						ConsoleUi.WriteInline(ctxPrefix);
						ConsoleUi.WriteWithSplitWordHighlight(record.Excerpt, ComputeWordCollisionSpans(record.Excerpt));
						ConsoleUi.WriteBlank();
					}
					else if (displayType == "LIGATURE")
					{
						// Review twin of the live LIGATURE branch: mark the U+FB0x
						// glyph(s) in the muted scheme so the operator reads the same
						// diagnosis as live triage. Source is record.Excerpt.
						ConsoleUi.WriteInline(ctxPrefix);
						ConsoleUi.WriteWithLigatureSpansMuted(
							record.Excerpt, ComputeLigatureSpans(record.Excerpt));
						ConsoleUi.WriteBlank();
					}
					else if (displayType == "QUOTE ISSUES")
					{
						// Review shows the same blue-context palette as live triage via
						// the shared primitive. Trigger pinpointing in review needs the
						// page language (and thus the record's source file) threaded into
						// this pass — deferred; until then every quote renders as context,
						// which is palette-consistent and never mis-marks.
						ConsoleUi.WriteInline(ctxPrefix);
						ConsoleUi.WriteWithQuoteSpansMuted(
							record.Excerpt, ComputeQuoteSpans(record.Excerpt, []));
						ConsoleUi.WriteBlank();
					}
					else if (displayType == "UNWANTED_PATTERN")
					{
						ConsoleUi.WriteInline(ctxPrefix);
						ConsoleUi.WriteWithPatternHighlightMuted(
							record.Excerpt, ExtractHighlightPatterns(record.Word));
						ConsoleUi.WriteBlank();
					}
					else
					{
						// Simple-word types: mark the literal Word where it occurs.
						var hit = string.IsNullOrEmpty(record.Word)
							? -1
							: record.Excerpt.IndexOf(record.Word, StringComparison.OrdinalIgnoreCase);
						if (hit >= 0)
						{
							ConsoleUi.WriteLineWithMutedHighlight(
								ctxPrefix + record.Excerpt[..hit],
								record.Excerpt.Substring(hit, record.Word.Length),
								record.Excerpt[(hit + record.Word.Length)..]);
						}
						else
						{
							ConsoleUi.WriteField("Context", record.Excerpt);
						}
					}
				}
				ConsoleUi.WriteDivider();

				var key = ConsoleTriage.Ask(
					prompt: string.Empty,
					choices:
					[
						new ChoiceOption(ConsoleKey.S, "Skip (leave as-is)"),
						new ChoiceOption(ConsoleKey.D, "Discard (reset to new)"),
						new ChoiceOption(ConsoleKey.Q, "Quit (end review — progress will be saved)"),
					]);

				if (key == ConsoleKey.Q)
				{
					quit = true;
					break;   // end review early — discards already saved are kept
				}

				if (key == ConsoleKey.D)
				{
					// Reset to detection state — clear all triage decision fields.
					records[index] = DiscardToNew(record);
					IssueTracking.Save(issueTrackingPath, records);   // immediate — crash-safe
					discarded++;
					ConsoleUi.WriteActionRequired("→ Discarded — reset to new.");
				}
				else
				{
					ConsoleUi.WriteSkipped("→ Skipped — left untouched.");
				}
			}

			ConsoleUi.WriteBlank();
			ConsoleUi.WriteSuccess(
				ConsoleTriage.FormatReviewSummary(discarded, position, reviewable.Count, quit, "left as-is"));
		}

		/// <summary>
		/// Extracts the pattern string(s) to highlight from an UNWANTED_PATTERN
		/// composite Word. Shared by live triage (red highlight) and the review
		/// pass (amber highlight) so both mark identical spans on the same raw
		/// text. Word shapes:
		///   "UNWANTED_PATTERN:Category: Name — pattern: X"   → ["X"]
		///   "UNWANTED_PATTERN:Category: Name — patterns: X, Y" → ["X", "Y"]
		///   bare/other                                        → [whole-after-colon]
		/// Internal so it can be unit-tested without Console.
		/// </summary>
		internal static string[] ExtractHighlightPatterns(string word)
		{
			if (string.IsNullOrEmpty(word))
			{
				return [];
			}

			var patternKey = word.Contains(':')
				? word[(word.IndexOf(':') + 1)..]
				: word;

			// Try singular first, then plural marker.
			var singleMarker = "— pattern: ";
			var multiMarker = "— patterns: ";
			var singleIdx = patternKey.IndexOf(singleMarker, StringComparison.OrdinalIgnoreCase);
			var multiIdx = patternKey.IndexOf(multiMarker, StringComparison.OrdinalIgnoreCase);
			if (singleIdx >= 0)
			{
				return [patternKey[(singleIdx + singleMarker.Length)..]];
			}
			if (multiIdx >= 0)
			{
				// Multiple matched patterns — split on ", " and highlight each.
				return patternKey[(multiIdx + multiMarker.Length)..]
					.Split(", ", StringSplitOptions.RemoveEmptyEntries);
			}
			return [patternKey];
		}

		/// <summary>
		/// Returns <paramref name="excerpt"/> with <see cref="EmptyLinkMarker"/>
		/// injected into the text slot of every <c>essentially-empty</c> anchor —
		/// one whose visible text content is absent. The marker materialises the
		/// absence as a coloured presence so a non-HTML-fluent reader can see the
		/// defect; ConsoleUi.WriteWithWcagMarkerHighlight then lights the injected
		/// token. Pure (no Console), so the matcher is unit-tested directly.
		///
		/// "Essentially empty" deliberately mirrors the detector's verdict in
		/// ContentQuality.CheckMisplacedAnchors (which flags an anchor when
		/// InnerText.Trim() is blank and it wraps no &lt;img&gt;): the marker must
		/// appear on exactly the anchors the detector flagged, no more, no less.
		/// Because we work on the raw excerpt string (not a parsed DOM — re-parsing
		/// in the render path would be wasteful and could drift from the snapshot),
		/// emptiness is decided by stripping inner tags and trimming:
		///   - literal adjacency      &lt;a …&gt;&lt;/a&gt;            → marked
		///   - whitespace-only        &lt;a …&gt;   &lt;/a&gt;          → marked
		///   - empty inline children  &lt;a …&gt;&lt;i&gt;&lt;/i&gt;&lt;/a&gt;     → marked
		///   - text-bearing           &lt;a …&gt;Click&lt;/a&gt;        → NOT marked
		///   - text in a child        &lt;a …&gt;&lt;span&gt;Go&lt;/span&gt;&lt;/a&gt; → NOT marked
		///   - image link             &lt;a …&gt;&lt;img …&gt;&lt;/a&gt;     → NOT marked
		/// Nested anchors do not occur in valid HTML and the detector works on the
		/// parsed tree; the string scan pairs each &lt;a …&gt; with the next &lt;/a&gt;,
		/// which is correct for the flat anchor spans these excerpts contain.
		/// The marker is inserted immediately before the closing &lt;/a&gt; so the
		/// opening tag's attributes (href etc.) stay visible to the operator.
		/// Internal so it can be unit-tested without Console.
		/// </summary>
		internal static string InjectEmptyAnchorMarker(string excerpt)
		{
			if (string.IsNullOrEmpty(excerpt))
			{
				return excerpt;
			}

			var sb = new StringBuilder(excerpt.Length + EmptyLinkMarker.Length);
			var pos = 0;
			while (pos < excerpt.Length)
			{
				// Find the next opening "<a" that begins a tag — i.e. "<a" followed
				// by whitespace or ">" (so "<article"/"<aside" don't match).
				var openTag = FindAnchorOpenTag(excerpt, pos);
				if (openTag < 0)
				{
					sb.Append(excerpt, pos, excerpt.Length - pos);
					break;
				}

				// End of the opening tag (the '>' that closes "<a …>").
				var openEnd = excerpt.IndexOf('>', openTag);
				if (openEnd < 0)
				{
					// Malformed / truncated excerpt — no tag close. Emit the rest
					// verbatim rather than guessing.
					sb.Append(excerpt, pos, excerpt.Length - pos);
					break;
				}

				// Matching close for this anchor.
				var closeIdx = excerpt.IndexOf("</a>", openEnd + 1, StringComparison.OrdinalIgnoreCase);
				if (closeIdx < 0)
				{
					sb.Append(excerpt, pos, excerpt.Length - pos);
					break;
				}

				// Inner content between ">" and "</a>".
				var inner = excerpt[(openEnd + 1)..closeIdx];

				// Emit up to and including the opening tag's ">".
				sb.Append(excerpt, pos, (openEnd + 1) - pos);

				if (IsEssentiallyEmptyAnchorInner(inner))
				{
					// Inject marker, then the (whitespace/empty) inner verbatim so
					// the operator still sees exactly what was on the page, then the
					// close handled on the next loop pass.
					sb.Append(EmptyLinkMarker);
				}

				sb.Append(inner);
				sb.Append("</a>");
				pos = closeIdx + "</a>".Length;
			}

			return sb.ToString();
		}

		/// <summary>
		/// Foreground colour map for a MISPLACED_ANCHOR_EMPTY excerpt that has already had
		/// the marker injected by <see cref="InjectEmptyAnchorMarker"/>. Each marked empty
		/// anchor contributes Structure spans (&lt;a, &gt;, &lt;/a&gt;), an Href span for the
		/// href attribute, Attr spans for the remaining attributes, and a Marker span for the
		/// injected token; everything else (leading text, non-empty anchors, trailing markup)
		/// is Context. The spans are ordered and contiguous over the whole string, so
		/// ConsoleUi.WriteWithEmptyAnchorSpans paints by walking them once.
		/// </summary>
		internal static IReadOnlyList<ConsoleUi.EmptyAnchorSpan> ComputeEmptyAnchorSpans(string injected)
		{
			var spans = new List<ConsoleUi.EmptyAnchorSpan>();
			if (string.IsNullOrEmpty(injected))
			{
				return spans;
			}

			var pos = 0;
			while (pos < injected.Length)
			{
				var anchorAt = FindNextMarkedAnchor(injected, pos, out var openEnd, out var closeIdx);
				if (anchorAt < 0)
				{
					spans.Add(new(pos, injected.Length - pos, ConsoleUi.EmptyAnchorSpanKind.Context));
					break;
				}

				if (anchorAt > pos)
				{
					spans.Add(new(pos, anchorAt - pos, ConsoleUi.EmptyAnchorSpanKind.Context));
				}

				// "<a"
				spans.Add(new(anchorAt, 2, ConsoleUi.EmptyAnchorSpanKind.Structure));

				// attributes between "<a" and ">"
				EmitAnchorAttrSpans(injected, anchorAt + 2, openEnd, spans);

				// ">"
				spans.Add(new(openEnd, 1, ConsoleUi.EmptyAnchorSpanKind.Structure));

				// injected marker, right after ">"
				var markerStart = openEnd + 1;
				spans.Add(new(markerStart, EmptyLinkMarker.Length, ConsoleUi.EmptyAnchorSpanKind.Marker));

				// any (whitespace/empty) inner content between marker and "</a>"
				var innerStart = markerStart + EmptyLinkMarker.Length;
				if (closeIdx > innerStart)
				{
					spans.Add(new(innerStart, closeIdx - innerStart, ConsoleUi.EmptyAnchorSpanKind.Context));
				}

				// "</a>"
				spans.Add(new(closeIdx, 4, ConsoleUi.EmptyAnchorSpanKind.Structure));

				pos = closeIdx + 4;
			}

			return spans;
		}

		/// <summary>
		/// Index of the next anchor open tag at/after <paramref name="from"/> whose inner
		/// content begins with the injected <see cref="EmptyLinkMarker"/> — i.e. the empty
		/// anchor the marker was placed in. Non-empty anchors are skipped. Returns -1 (and
		/// leaves the out params at -1) when none remains.
		/// </summary>
		private static int FindNextMarkedAnchor(string s, int from, out int openEnd, out int closeIdx)
		{
			openEnd = -1;
			closeIdx = -1;
			var p = from;
			while (true)
			{
				var a = FindAnchorOpenTag(s, p);
				if (a < 0)
				{
					return -1;
				}

				var oe = s.IndexOf('>', a);
				if (oe < 0)
				{
					return -1;
				}

				var markerStart = oe + 1;
				if (markerStart + EmptyLinkMarker.Length <= s.Length
					&& string.CompareOrdinal(s, markerStart, EmptyLinkMarker, 0, EmptyLinkMarker.Length) == 0)
				{
					var ci = s.IndexOf("</a>", markerStart, StringComparison.OrdinalIgnoreCase);
					if (ci < 0)
					{
						return -1;
					}

					openEnd = oe;
					closeIdx = ci;
					return a;
				}

				p = oe + 1;
			}
		}

		/// <summary>
		/// Splits an anchor's attribute region [<paramref name="attrStart"/>,
		/// <paramref name="attrEnd"/>) into an Href span (the whole <c>href="…"</c>
		/// attribute) and Attr spans for everything else. With no href the whole region is
		/// one Attr span. The emitted spans are contiguous and cover the region exactly.
		/// </summary>
		private static void EmitAnchorAttrSpans(
			string s, int attrStart, int attrEnd, List<ConsoleUi.EmptyAnchorSpan> spans)
		{
			if (attrEnd <= attrStart)
			{
				return;
			}

			var hrefAt = IndexOfHrefAttr(s, attrStart, attrEnd);
			if (hrefAt < 0)
			{
				spans.Add(new(attrStart, attrEnd - attrStart, ConsoleUi.EmptyAnchorSpanKind.Attr));
				return;
			}

			if (hrefAt > attrStart)
			{
				spans.Add(new(attrStart, hrefAt - attrStart, ConsoleUi.EmptyAnchorSpanKind.Attr));
			}

			var hrefEnd = HrefAttrEnd(s, hrefAt, attrEnd);
			spans.Add(new(hrefAt, hrefEnd - hrefAt, ConsoleUi.EmptyAnchorSpanKind.Href));

			if (hrefEnd < attrEnd)
			{
				spans.Add(new(hrefEnd, attrEnd - hrefEnd, ConsoleUi.EmptyAnchorSpanKind.Attr));
			}
		}

		/// <summary>
		/// Start index of the <c>href</c> attribute within [start, end), or -1. Matches a
		/// whitespace-preceded (or region-start) "href" followed by '=' (so "hreflang" and
		/// substrings are rejected). Case-insensitive on the name.
		/// </summary>
		private static int IndexOfHrefAttr(string s, int start, int end)
		{
			for (var i = start; i + 4 <= end; i++)
			{
				if (!(i == start || char.IsWhiteSpace(s[i - 1])))
				{
					continue;
				}

				if (string.Compare(s, i, "href", 0, 4, StringComparison.OrdinalIgnoreCase) != 0)
				{
					continue;
				}

				var j = i + 4;
				while (j < end && char.IsWhiteSpace(s[j]))
				{
					j++;
				}

				if (j < end && s[j] == '=')
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>
		/// End index (exclusive) of the href attribute starting at <paramref name="hrefAt"/>:
		/// past the name, '=', and the quoted or unquoted value. A bare "href" with no value
		/// returns the index just past the name.
		/// </summary>
		private static int HrefAttrEnd(string s, int hrefAt, int end)
		{
			var j = hrefAt + 4;
			while (j < end && char.IsWhiteSpace(s[j]))
			{
				j++;
			}

			if (j >= end || s[j] != '=')
			{
				return Math.Min(hrefAt + 4, end);
			}

			j++;   // past '='
			while (j < end && char.IsWhiteSpace(s[j]))
			{
				j++;
			}

			if (j < end && (s[j] == '"' || s[j] == '\''))
			{
				var quote = s[j];
				j++;
				while (j < end && s[j] != quote)
				{
					j++;
				}

				if (j < end)
				{
					j++;   // include the closing quote
				}

				return j;
			}

			while (j < end && !char.IsWhiteSpace(s[j]))
			{
				j++;
			}

			return j;
		}

		/// <summary>
		/// Index of the next "&lt;a" that opens an anchor tag at or after
		/// <paramref name="from"/> — "&lt;a" followed by ASCII whitespace or "&gt;".
		/// Rejects "&lt;article", "&lt;aside", etc. Case-insensitive on the tag name.
		/// Returns -1 if none.
		/// </summary>
		private static int FindAnchorOpenTag(string text, int from)
		{
			var i = from;
			while (true)
			{
				var hit = text.IndexOf("<a", i, StringComparison.OrdinalIgnoreCase);
				if (hit < 0)
				{
					return -1;
				}

				var after = hit + 2;
				if (after >= text.Length)
				{
					return -1;   // "<a" at very end — no tag body
				}
				var c = text[after];
				if (c == '>' || char.IsWhiteSpace(c))
				{
					return hit;
				}
				i = hit + 2;   // false positive (<article …) — keep scanning
			}
		}

		/// <summary>
		/// True when an anchor's inner HTML carries no visible text — empty,
		/// whitespace-only, or only-empty-child-elements — and is not an image
		/// link. Mirrors the detector's InnerText.Trim()-is-blank verdict on a raw
		/// string: strip all tags, decode nothing (entities are not whitespace),
		/// and trim. An inner &lt;img&gt; counts as content (intentional image link),
		/// matching CheckMisplacedAnchors' &lt;img&gt; exclusion.
		/// </summary>
		private static bool IsEssentiallyEmptyAnchorInner(string inner)
		{
			if (string.IsNullOrWhiteSpace(inner))
			{
				return true;   // "" or whitespace-only
			}

			// Image link — content-bearing by the detector's rule, never marked.
			if (inner.IndexOf("<img", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return false;
			}

			// Strip tags and see whether any non-whitespace text remains.
			var sb = new StringBuilder(inner.Length);
			var inTag = false;
			foreach (var c in inner)
			{
				if (c == '<') { inTag = true; continue; }
				if (c == '>') { inTag = false; continue; }
				if (!inTag)
				{
					sb.Append(c);
				}
			}
			return string.IsNullOrWhiteSpace(sb.ToString());
		}

		// Max characters of the orphaned tail to highlight after </a>. The tail is
		// otherwise bounded by the first literal space (U+0020). This cap is only a
		// runaway guard for a tail with no space within the visible window (a long
		// URL, say) — it is NOT a correctness boundary: a real orphan (even a long
		// German compound such as "-Versicherungsrechner", 21 chars) is meant to
		// colour fully, and a clipped highlight still flags "investigate here". 24
		// clears the realistic orphans seen on live sites while staying well under
		// the excerpt half-window (ContentQualityExcerptRadius, 120) so surrounding
		// context the operator needs to judge the finding is never swallowed.
		private const int SplitTailMaxHighlight = 24;

		/// <summary>
		/// Computes quote-highlight spans for a quote-issue excerpt: every
		/// typographic quote glyph (<see cref="ConsoleUi.HighlightQuoteChars"/>)
		/// becomes a one-char span; positions in <paramref name="triggerPositions"/>
		/// are marked as the trigger (offender), the rest as context. Pure and
		/// testable; returns spans ordered by Start, non-overlapping. An empty
		/// trigger set yields all-context spans — the graceful fallback when the
		/// offending glyph could not be located.
		/// </summary>
		internal static List<ConsoleUi.QuoteHighlightSpan> ComputeQuoteSpans(
			string text, IReadOnlyCollection<int> triggerPositions)
		{
			var spans = new List<ConsoleUi.QuoteHighlightSpan>();
			if (string.IsNullOrEmpty(text))
			{
				return spans;
			}

			var triggers = triggerPositions as HashSet<int>
				?? (triggerPositions is { Count: > 0 } ? new HashSet<int>(triggerPositions) : null);

			for (int i = 0; i < text.Length; i++)
			{
				if (ConsoleUi.HighlightQuoteChars.Contains(text[i]))
				{
					spans.Add(new ConsoleUi.QuoteHighlightSpan(
						i, 1, IsTrigger: triggers is not null && triggers.Contains(i)));
				}
			}

			return spans;
		}

		/// <summary>
		/// Computes 1-char highlight spans for every typographic ligature glyph in
		/// <paramref name="text"/> (U+FB00–U+FB06, per
		/// <see cref="ConsoleUi.HighlightLigatureChars"/>). Pure mirror of
		/// <see cref="ComputeQuoteSpans"/> without the trigger concept — every
		/// ligature is an offender, so all spans render in the emphasis scheme.
		/// Returns an empty list when the text has none (caller degrades to plain).
		/// </summary>
		internal static List<ConsoleUi.QuoteHighlightSpan> ComputeLigatureSpans(string text)
		{
			var spans = new List<ConsoleUi.QuoteHighlightSpan>();
			if (string.IsNullOrEmpty(text))
			{
				return spans;
			}

			for (int i = 0; i < text.Length; i++)
			{
				if (ConsoleUi.HighlightLigatureChars.Contains(text[i]))
				{
					spans.Add(new ConsoleUi.QuoteHighlightSpan(i, 1, IsTrigger: true));
				}
			}

			return spans;
		}

		/// <summary>
		/// Computes the coloured spans for a SPLIT_WORD_ANCHOR excerpt: for every
		/// closing &lt;/a&gt; that is immediately followed by a non-space character
		/// (the split signature — a token continues past the tag), three spans are
		/// emitted — the anchor's opening &lt;a…&gt; and closing &lt;/a&gt; TAGS,
		/// the INSIDE link text between them, and the orphaned TAIL after &lt;/a&gt;.
		///
		/// Pure (no Console), so the offset logic is unit-tested directly. The
		/// renderer (ConsoleUi.WriteWithSplitWordHighlight) only paints these spans.
		///
		/// Tail rule (deliberately mechanical — a visual indicator, not a verdict):
		/// the tail runs from just after &lt;/a&gt; up to the first literal space
		/// (U+0020) OR <see cref="SplitTailMaxHighlight"/> characters, whichever
		/// comes first. EVERY other character is consumed — tabs, all Unicode and
		/// zero-width spaces, line separators, punctuation, connectors, letters,
		/// digits. Only a plain space stops it. This is language-blind on purpose:
		/// the inside/tail colour straddle of the red close tag shows "linked part /
		/// escaped part" without the operator needing to read the script (German,
		/// Turkish, Cyrillic). Returns spans ordered by Start, non-overlapping.
		/// </summary>
		internal static IReadOnlyList<ConsoleUi.SplitSpan> ComputeSplitWordSpans(string excerpt)
		{
			var spans = new List<ConsoleUi.SplitSpan>();
			if (string.IsNullOrEmpty(excerpt))
			{
				return spans;
			}

			const string close = "</a>";
			var searchFrom = 0;
			while (true)
			{
				var closeAt = excerpt.IndexOf(close, searchFrom, StringComparison.OrdinalIgnoreCase);
				if (closeAt < 0)
				{
					break;
				}
				var afterClose = closeAt + close.Length;
				searchFrom = afterClose;

				// Split signature: </a> immediately followed by a non-space char.
				// A space (or end of excerpt) here means a clean link, not a split.
				if (afterClose >= excerpt.Length || excerpt[afterClose] == ' ')
				{
					continue;
				}

				// ── Inside + opening tag ──────────────────────────────────────
				// The inside (link text) is anchored to the ">" that closes the
				// opening tag — the last ">" before this "</a>". That ">" is in-window
				// even when the "<a" itself has scrolled off the left edge (the
				// excerpt is centred on the </a> boundary, so a long anchor's
				// open tag is frequently truncated away). Anchoring to the ">" — not
				// to "<a" — is what lets the inside colour in that common case; a raw
				// ">" never appears in link text (it would be &gt;), so the last ">"
				// before </a> reliably lands on the opening tag's close bracket and
				// the attribute markup before it stays uncoloured.
				//
				// The opening-tag SPAN ("<a…>") is emitted only when the "<a" start
				// is actually in-window; when it is truncated the inside still colours
				// (bounded by the ">"), just without a red open-tag span.
				var insideStart = -1;
				var openAt = LastIndexOfAnchorOpen(excerpt, closeAt);
				if (openAt >= 0)
				{
					var openTagEnd = excerpt.IndexOf('>', openAt);
					if (openTagEnd >= 0 && openTagEnd < closeAt)
					{
						// Opening tag span: "<a…>" inclusive of the ">".
						spans.Add(new ConsoleUi.SplitSpan(openAt, (openTagEnd + 1) - openAt, ConsoleUi.SplitSpanKind.Tag));
						insideStart = openTagEnd + 1;
					}
				}
				if (insideStart < 0)
				{
					// Open "<a" out of window — fall back to the last ">" before
					// "</a>" as the inside's left bound.
					var precedingGt = excerpt.LastIndexOf('>', closeAt - 1);
					if (precedingGt >= 0)
					{
						insideStart = precedingGt + 1;
					}
				}
				if (insideStart >= 0 && closeAt > insideStart)
				{
					spans.Add(new ConsoleUi.SplitSpan(insideStart, closeAt - insideStart, ConsoleUi.SplitSpanKind.Inside));
				}

				// ── Closing tag ───────────────────────────────────────────────
				spans.Add(new ConsoleUi.SplitSpan(closeAt, close.Length, ConsoleUi.SplitSpanKind.Tag));

				// ── Tail ──────────────────────────────────────────────────────
				// From just after </a> to first literal space or the cap.
				var tailEnd = afterClose;
				var limit = Math.Min(excerpt.Length, afterClose + SplitTailMaxHighlight);
				while (tailEnd < limit && excerpt[tailEnd] != ' ')
				{
					tailEnd++;
				}
				if (tailEnd > afterClose)
				{
					spans.Add(new ConsoleUi.SplitSpan(afterClose, tailEnd - afterClose, ConsoleUi.SplitSpanKind.Tail));
				}

				// Continue scanning after this tail for any further splits.
				searchFrom = Math.Max(searchFrom, tailEnd);
			}

			return spans;
		}

		/// <summary>
		/// Inline closing tags whose boundary glues text (mirrors the spell harvester's
		/// InlinePhrasingGlue and ContentQuality.InlinePhrasingElements).
		/// </summary>
		private static readonly string[] InlineCloseTags =
		{
			"</span>", "</b>", "</i>", "</em>", "</strong>",
			"</mark>", "</small>", "</u>", "</s>", "</wbr>",
		};

		/// <summary>
		/// Spans for a WORD_COLLISION excerpt — raw html of the shape
		/// <c>WORD1&lt;/tag&gt;WORD2</c>. Three spans: WORD1 (the inline element's text,
		/// Inside/DarkCyan), the closing <c>&lt;/tag&gt;</c> (Tag/red), and WORD2 (the
		/// colliding bare tail, Tail/DarkBlue = the WCAG-blue scheme). The seam is the
		/// inline closing tag immediately followed by an uppercase letter — unique even
		/// when WORD1 contains nested inline markup (those inner close tags are followed
		/// by lowercase or further tags, not the uppercase collider). Returns no spans
		/// for the rarer leading-seam shape (text&lt;tag&gt;WORD); it renders as plain
		/// raw html. The renderer (WriteWithSplitWordHighlight) only paints these spans.
		/// </summary>
		internal static IReadOnlyList<ConsoleUi.SplitSpan> ComputeWordCollisionSpans(string excerpt)
		{
			var spans = new List<ConsoleUi.SplitSpan>();
			if (string.IsNullOrEmpty(excerpt))
			{
				return spans;
			}

			// Locate the seam: an inline closing tag immediately followed by an
			// uppercase letter (the lowercase→Uppercase collision).
			int closeAt = -1, closeLen = 0;
			string closeTag = string.Empty;
			for (int i = 0; i < excerpt.Length && closeAt < 0; i++)
			{
				if (excerpt[i] != '<')
				{
					continue;
				}

				foreach (var tag in InlineCloseTags)
				{
					if (i + tag.Length <= excerpt.Length
						&& string.Compare(excerpt, i, tag, 0, tag.Length, StringComparison.OrdinalIgnoreCase) == 0)
					{
						int after = i + tag.Length;
						if (after < excerpt.Length && char.IsUpper(excerpt[after]))
						{
							closeAt = i;
							closeLen = tag.Length;
							closeTag = tag;
						}

						break;
					}
				}
			}

			if (closeAt < 0)
			{
				// No trailing (</tag>WORD) seam — try the leading (WORD<tag>) shape.
				return ComputeLeadingSeamSpans(excerpt);
			}

			var afterClose = closeAt + closeLen;

			// ── WORD1: inline text, anchored to the ">" of the OPENING tag, so the
			// text is highlighted even when an inner tag (e.g. a <br> spacer) sits
			// between it and </tag>. Element name comes from the close tag ("</span>"
			// → "span"); we find the matching "<span…>" before the seam. Falls back to
			// the last ">" before the seam if the open tag scrolled out of the window.
			var element = closeTag.Substring(2, closeTag.Length - 3);
			var insideStart = OpenTagContentStart(excerpt, "<" + element, closeAt);
			if (insideStart < 0)
			{
				var precedingGt = excerpt.LastIndexOf('>', closeAt - 1);
				insideStart = precedingGt >= 0 ? precedingGt + 1 : 0;
			}

			// Walk the inside region: text → Inside (light blue), <br…> spacer runs →
			// BrSpacer (DarkMagenta). Only <br> is carved out — it is the spacing abuse
			// being surfaced; any other inner markup stays within the Inside run.
			var pos = insideStart;
			while (pos < closeAt)
			{
				var br = IndexOfBrTag(excerpt, pos, closeAt);
				if (br < 0)
				{
					if (closeAt > pos)
					{
						spans.Add(new ConsoleUi.SplitSpan(pos, closeAt - pos, ConsoleUi.SplitSpanKind.Inside));
					}

					break;
				}

				if (br > pos)
				{
					spans.Add(new ConsoleUi.SplitSpan(pos, br - pos, ConsoleUi.SplitSpanKind.Inside));
				}

				var gt = excerpt.IndexOf('>', br);
				var brEnd = (gt >= 0 && gt < closeAt) ? gt + 1 : closeAt;
				spans.Add(new ConsoleUi.SplitSpan(br, brEnd - br, ConsoleUi.SplitSpanKind.BrSpacer));
				pos = brEnd;
			}

			// ── </tag> ──
			spans.Add(new ConsoleUi.SplitSpan(closeAt, closeLen, ConsoleUi.SplitSpanKind.Tag));

			// ── WORD2: colliding tail, from just after </tag> to first space or cap ──
			var tailEnd = afterClose;
			var limit = Math.Min(excerpt.Length, afterClose + SplitTailMaxHighlight);
			while (tailEnd < limit && excerpt[tailEnd] != ' ')
			{
				tailEnd++;
			}
			if (tailEnd > afterClose)
			{
				spans.Add(new ConsoleUi.SplitSpan(afterClose, tailEnd - afterClose, ConsoleUi.SplitSpanKind.Tail));
			}

			return spans;
		}

		/// <summary>
		/// Spans for the leading-seam WORD_COLLISION shape — <c>WORD1&lt;tag…&gt;WORD2</c>,
		/// where a bare lowercase-ending word abuts an OPENING inline tag whose text
		/// starts uppercase (e.g. <c>Android&lt;span&gt;&lt;sup&gt;TM</c>). Mirrors the
		/// trailing-seam painting so both read identically: WORD1 (Inside/light blue),
		/// the opening tag(s) (Tag/red), WORD2 (Tail/blue). The trailing-seam path in
		/// <see cref="ComputeWordCollisionSpans"/> is tried first; this is its fallback.
		/// One seam per excerpt; returns no spans when none is present.
		/// </summary>
		internal static IReadOnlyList<ConsoleUi.SplitSpan> ComputeLeadingSeamSpans(string excerpt)
		{
			var spans = new List<ConsoleUi.SplitSpan>();
			if (string.IsNullOrEmpty(excerpt))
			{
				return spans;
			}

			for (int i = 1; i < excerpt.Length; i++)
			{
				// Seam candidate: a '<' directly after a lowercase letter (WORD1's tail)
				// that opens a tag (not a closing tag — that is the trailing shape).
				if (excerpt[i] != '<' || !char.IsLower(excerpt[i - 1]))
				{
					continue;
				}

				if (i + 1 < excerpt.Length && excerpt[i + 1] == '/')
				{
					continue;
				}

				// Skip consecutive opening inline tags to the first text char after them.
				int p = i;
				while (p < excerpt.Length && excerpt[p] == '<'
					&& (p + 1 >= excerpt.Length || excerpt[p + 1] != '/'))
				{
					int gt = excerpt.IndexOf('>', p);
					if (gt < 0)
					{
						p = excerpt.Length;
						break;
					}

					p = gt + 1;
				}

				// A real collision only when the post-tag text starts uppercase.
				if (p >= excerpt.Length || !char.IsUpper(excerpt[p]))
				{
					continue;
				}

				// WORD1: the lowercase-ending word immediately before the tag.
				int w1 = i;
				while (w1 > 0 && (char.IsLetter(excerpt[w1 - 1]) || excerpt[w1 - 1] == '-'))
				{
					w1--;
				}

				if (w1 < i)
				{
					spans.Add(new ConsoleUi.SplitSpan(w1, i - w1, ConsoleUi.SplitSpanKind.Inside));
				}

				// Opening tag(s): the markup between WORD1 and WORD2.
				spans.Add(new ConsoleUi.SplitSpan(i, p - i, ConsoleUi.SplitSpanKind.Tag));

				// WORD2: colliding tail, to the next tag, space, or cap.
				int tailEnd = p;
				int limit = Math.Min(excerpt.Length, p + SplitTailMaxHighlight);
				while (tailEnd < limit && excerpt[tailEnd] != ' ' && excerpt[tailEnd] != '<')
				{
					tailEnd++;
				}

				if (tailEnd > p)
				{
					spans.Add(new ConsoleUi.SplitSpan(p, tailEnd - p, ConsoleUi.SplitSpanKind.Tail));
				}

				break;   // one seam per excerpt, matching the trailing-seam path
			}

			return spans;
		}
		/// "&gt;" of the last <paramref name="openPrefix"/> ("&lt;span", "&lt;b", …)
		/// opening tag at or before <paramref name="closeAt"/>. Requires the char after
		/// the prefix to be whitespace or "&gt;" (so "&lt;span" matches but "&lt;spanner"
		/// does not). Returns -1 if no such open tag with a "&gt;" before the seam.
		/// </summary>
		private static int OpenTagContentStart(string text, string openPrefix, int closeAt)
		{
			var search = closeAt;
			while (search > 0)
			{
				var at = text.LastIndexOf(openPrefix, search - 1, StringComparison.OrdinalIgnoreCase);
				if (at < 0)
				{
					return -1;
				}

				var after = at + openPrefix.Length;
				if (after < text.Length
					&& (text[after] == '>' || char.IsWhiteSpace(text[after])))
				{
					var gt = text.IndexOf('>', at);
					if (gt >= 0 && gt < closeAt)
					{
						return gt + 1;
					}
				}

				search = at;   // false match (<spanner…) — keep walking back
			}

			return -1;
		}

		/// <summary>
		/// Index of the next "&lt;br" tag in [<paramref name="from"/>,
		/// <paramref name="end"/>) — matching &lt;br&gt;, &lt;br/&gt;, and
		/// &lt;br class="…"&gt;, but not other "&lt;br…"-prefixed names. Returns -1 if none.
		/// </summary>
		private static int IndexOfBrTag(string text, int from, int end)
		{
			var i = from;
			while (i < end)
			{
				var at = text.IndexOf("<br", i, StringComparison.OrdinalIgnoreCase);
				if (at < 0 || at >= end)
				{
					return -1;
				}

				var after = at + 3;
				if (after < text.Length
					&& (text[after] == '>' || text[after] == '/' || char.IsWhiteSpace(text[after])))
				{
					return at;
				}

				i = at + 3;   // "<break"/"<brxyz" — not a <br>, keep scanning
			}

			return -1;
		}

		/// <summary>
		/// Index of the opening "&lt;a" that begins the anchor closing at
		/// <paramref name="closeAt"/> — the last "&lt;a" (followed by whitespace or
		/// "&gt;") at or before <paramref name="closeAt"/>. Returns -1 if none is in
		/// window. Mirrors the anchor-open recognition in InjectEmptyAnchorMarker
		/// ("&lt;article" etc. are not anchors).
		/// </summary>
		private static int LastIndexOfAnchorOpen(string text, int closeAt)
		{
			var i = closeAt;
			while (i > 0)
			{
				var hit = text.LastIndexOf("<a", i - 1, StringComparison.OrdinalIgnoreCase);
				if (hit < 0)
				{
					return -1;
				}
				var after = hit + 2;
				if (after < text.Length)
				{
					var c = text[after];
					if (c == '>' || char.IsWhiteSpace(c))
					{
						return hit;
					}
				}
				i = hit;   // false positive (<article…) — keep walking back
			}
			return -1;
		}

		// ── ADJACENT_ANCHOR same-href hint computation ────────────────
		//
		// Pure span-computation for the ADJACENT_ANCHOR visualization layer. The
		// detector flags any literal </a><a collision honestly (broad on purpose);
		// this helper analyses the excerpt to surface structural facts (matching
		// hrefs, matching titles, placeholder hrefs) as coloured spans. The
		// renderer downstream is dumb — it just paints what this helper computes.
		// No interpretation: every span is a fact, the operator interprets.
		//
		// The shape of the computation:
		//   1. Find every literal "</a><a" (case-insensitive) → Collision span.
		//   2. For each collision, parse the LEFT anchor (ending at the </a>) and
		//      RIGHT anchor (starting at the <a) — extract href value, title value,
		//      and inner-text bounds for each side.
		//   3. Compare hrefs across the pair:
		//        match (real)        → HrefMatch on both sides + AnchorTextHint on inner text
		//        match (placeholder) → HrefPlaceholder on both sides
		//        differ              → HrefBaseline on each side (mild visibility, no claim)
		//        one or both missing → HrefBaseline only on present side(s)
		//   4. Compare titles: if both present and identical, emit TitleMatch on both.
		//   5. Sort all spans by Start, dedupe by Start (cluster cases like
		//      <a></a><a></a><a> produce overlapping pair analyses on the middle
		//      anchor — keep first occurrence of each Start).
		//
		// Edge cases handled:
		//   - Truncated left anchor (the <a is off the left excerpt edge): no
		//     opening-tag <a in window → fall back to last ">" before </a> as the
		//     inner-text left bound. Same approach as ComputeSplitWordSpans.
		//   - Truncated right anchor (the > of its open tag is off the right
		//     excerpt edge): no closing ">" in window → no inner-text span, but
		//     attribute parsing may still find href/title in the visible portion.
		//   - Both double-quoted (href="x") and single-quoted (href='x') values.
		//   - Attribute order variation.
		//   - Anchors with no href attribute at all (legacy <a name=…> shapes).
		//   - Empty href value ("") → treated as placeholder-class.

		/// <summary>
		/// Computes coloured spans for an ADJACENT_ANCHOR excerpt: every literal
		/// "&lt;/a&gt;&lt;a" collision plus structural-fact hints (matching
		/// hrefs/titles, placeholder hrefs, anchor-text scope when same-href).
		/// Pure and testable; returns spans ordered by Start, non-overlapping.
		/// Empty/null excerpt → empty list. Excerpts with no collision → empty list.
		/// </summary>
		internal static IReadOnlyList<ConsoleUi.AdjacentHintSpan> ComputeAdjacentHintSpans(string excerpt)
		{
			var spans = new List<ConsoleUi.AdjacentHintSpan>();
			if (string.IsNullOrEmpty(excerpt))
			{
				return spans;
			}

			const string needle = "</a><a";
			var searchFrom = 0;
			while (true)
			{
				var hit = excerpt.IndexOf(needle, searchFrom, StringComparison.OrdinalIgnoreCase);
				if (hit < 0)
				{
					break;
				}

				// Collision span (red): the literal "</a><a", 6 chars.
				spans.Add(new ConsoleUi.AdjacentHintSpan(hit, needle.Length, ConsoleUi.AdjacentHintSpanKind.Collision));

				// Parse the LEFT anchor (ends at hit + len("</a>") = hit + 4).
				var leftCloseAt = hit;                   // index of "</a>"
				var leftAnchor = ParseAnchorEndingAt(excerpt, leftCloseAt);

				// Parse the RIGHT anchor (starts at hit + len("</a>") = hit + 4).
				var rightOpenAt = hit + 4;               // index of "<a"
				var rightAnchor = ParseAnchorStartingAt(excerpt, rightOpenAt);

				EmitPairSpans(spans, leftAnchor, rightAnchor);

				// Advance: continue scanning after this collision. The "<a" we just
				// consumed may also be the left half of a NEXT collision (cluster
				// case: </a><a></a><a> — three anchors, two collisions). Restart
				// search just after the collision so the second is found too.
				searchFrom = hit + needle.Length;
			}

			// Sort by Start; dedupe overlapping/duplicate spans (cluster cases can
			// produce the same anchor's href/title under two pair analyses).
			spans.Sort((a, b) => a.Start.CompareTo(b.Start));
			return DedupeOrdered(spans);
		}

		// ── Anchor parsing helpers (excerpt-local, defensive about truncation) ──

		private readonly record struct ParsedAnchor(
			int InnerTextStart, int InnerTextEnd,    // bounds of the anchor's link text in the excerpt; -1 if not in window
			int HrefValueStart, int HrefValueLen,    // bounds of the href value (between quotes); -1 if absent
			string? HrefValue,                       // the href value itself (null if absent)
			bool HrefIsPlaceholder,                  // true for href="#", href="", javascript: scheme
			int TitleValueStart, int TitleValueLen,  // bounds of the title value; -1 if absent
			string? TitleValue);                     // the title value itself (null if absent)

		/// <summary>Parses the LEFT anchor of an adjacency — the one ending at "&lt;/a&gt;".</summary>
		private static ParsedAnchor ParseAnchorEndingAt(string excerpt, int closeAt)
		{
			// Walk back to find the opening "<a"; if not in window, fall back to
			// last ">" before closeAt to bound the inner text.
			var openAt = LastIndexOfAnchorOpen(excerpt, closeAt);
			var openTagEnd = -1;
			if (openAt >= 0)
			{
				openTagEnd = excerpt.IndexOf('>', openAt);
				if (openTagEnd >= closeAt)
				{
					openTagEnd = -1;
				}
			}
			int innerStart;
			if (openTagEnd >= 0)
			{
				innerStart = openTagEnd + 1;
			}
			else
			{
				// Fallback: last ">" before the </a>. A raw ">" never appears in
				// link text (it would be &gt;), so this reliably lands on the open
				// tag's ">" even when "<a" is truncated away.
				var gt = excerpt.LastIndexOf('>', closeAt - 1);
				innerStart = gt >= 0 ? gt + 1 : -1;
			}

			var (hrefStart, hrefLen, hrefVal, hrefPlaceholder) =
				openAt >= 0 ? ExtractAttribute(excerpt, openAt, closeAt, "href") : (-1, 0, (string?)null, false);
			var (titleStart, titleLen, titleVal, _) =
				openAt >= 0 ? ExtractAttribute(excerpt, openAt, closeAt, "title") : (-1, 0, (string?)null, false);

			return new ParsedAnchor(
				InnerTextStart: innerStart,
				InnerTextEnd: innerStart >= 0 ? closeAt : -1,
				HrefValueStart: hrefStart,
				HrefValueLen: hrefLen,
				HrefValue: hrefVal,
				HrefIsPlaceholder: hrefPlaceholder,
				TitleValueStart: titleStart,
				TitleValueLen: titleLen,
				TitleValue: titleVal);
		}

		/// <summary>Parses the RIGHT anchor of an adjacency — the one starting at "&lt;a".</summary>
		private static ParsedAnchor ParseAnchorStartingAt(string excerpt, int openAt)
		{
			// Find this open tag's ">". If off-window, attribute parsing scans the
			// visible portion only; inner-text span is omitted.
			var openTagEnd = excerpt.IndexOf('>', openAt);
			// Find this anchor's "</a>". If off-window, no inner-text span.
			var closeAt = openTagEnd >= 0
				? excerpt.IndexOf("</a>", openTagEnd, StringComparison.OrdinalIgnoreCase)
				: -1;

			var innerStart = openTagEnd >= 0 ? openTagEnd + 1 : -1;
			var innerEnd = closeAt >= 0 ? closeAt : -1;

			// Attribute parsing scans from <a to the END of its open tag, or to a
			// reasonable cap if the > is off-window (avoid running into the next
			// anchor's attributes). Cap = the next "<" character.
			var attrScanEnd = openTagEnd >= 0
				? openTagEnd
				: NextLessThan(excerpt, openAt + 2);
			if (attrScanEnd < 0)
			{
				attrScanEnd = excerpt.Length;
			}

			var (hrefStart, hrefLen, hrefVal, hrefPlaceholder) =
				ExtractAttribute(excerpt, openAt, attrScanEnd, "href");
			var (titleStart, titleLen, titleVal, _) =
				ExtractAttribute(excerpt, openAt, attrScanEnd, "title");

			return new ParsedAnchor(
				InnerTextStart: innerStart,
				InnerTextEnd: innerEnd,
				HrefValueStart: hrefStart,
				HrefValueLen: hrefLen,
				HrefValue: hrefVal,
				HrefIsPlaceholder: hrefPlaceholder,
				TitleValueStart: titleStart,
				TitleValueLen: titleLen,
				TitleValue: titleVal);
		}

		private static int NextLessThan(string s, int from)
		{
			for (var i = from; i < s.Length; i++)
			{
				if (s[i] == '<')
				{
					return i;
				}
			}
			return -1;
		}

		/// <summary>
		/// Extracts the value of <paramref name="attrName"/> from the substring
		/// [openAt, scanEnd). Returns (valueStart, valueLen, value, isPlaceholder)
		/// or (-1, 0, null, false) if absent. valueStart points at the FIRST char
		/// of the value (after the opening quote, if any). Handles double-quoted,
		/// single-quoted, and unquoted values defensively. Placeholder classification:
		/// "#", "", any value starting with "javascript:" (case-insensitive).
		/// </summary>
		private static (int Start, int Length, string? Value, bool IsPlaceholder)
			ExtractAttribute(string excerpt, int openAt, int scanEnd, string attrName)
		{
			if (scanEnd <= openAt || scanEnd > excerpt.Length)
			{
				return (-1, 0, null, false);
			}
			// Case-insensitive search for "attrName" within the tag scan range,
			// requiring a word-boundary before (whitespace or "<a" itself).
			var needle = attrName;
			var scanFrom = openAt;
			while (scanFrom < scanEnd)
			{
				var nameIdx = excerpt.IndexOf(needle, scanFrom, scanEnd - scanFrom, StringComparison.OrdinalIgnoreCase);
				if (nameIdx < 0)
				{
					return (-1, 0, null, false);
				}
				// Word-boundary check: char before must be whitespace (or this is
				// immediately after "<a"). Avoids matching "data-href" when looking
				// for "href".
				var before = nameIdx > 0 ? excerpt[nameIdx - 1] : '\0';
				if (!char.IsWhiteSpace(before))
				{
					scanFrom = nameIdx + needle.Length;
					continue;
				}
				// Must be followed by "=" (with optional whitespace between).
				var after = nameIdx + needle.Length;
				while (after < scanEnd && char.IsWhiteSpace(excerpt[after]))
				{
					after++;
				}

				if (after >= scanEnd || excerpt[after] != '=')
				{
					scanFrom = nameIdx + needle.Length;
					continue;
				}
				after++; // past '='
				while (after < scanEnd && char.IsWhiteSpace(excerpt[after]))
				{
					after++;
				}

				if (after >= scanEnd)
				{
					return (-1, 0, null, false);
				}
				// Value: quoted ("…", '…') or bare (up to whitespace or end).
				var quote = excerpt[after];
				int valStart, valEnd;
				if (quote == '"' || quote == '\'')
				{
					valStart = after + 1;
					valEnd = excerpt.IndexOf(quote, valStart, scanEnd - valStart);
					if (valEnd < 0)
					{
						valEnd = scanEnd;
					}
				}
				else
				{
					valStart = after;
					valEnd = valStart;
					while (valEnd < scanEnd && !char.IsWhiteSpace(excerpt[valEnd]) && excerpt[valEnd] != '>')
					{
						valEnd++;
					}
				}
				var value = excerpt[valStart..valEnd];
				var isPlaceholder = value.Length == 0
					|| value == "#"
					|| value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);
				return (valStart, valEnd - valStart, value, isPlaceholder);
			}
			return (-1, 0, null, false);
		}

		/// <summary>
		/// Emits hint spans for a parsed adjacent pair into <paramref name="spans"/>.
		/// Compares hrefs and titles across the pair and adds the appropriate
		/// HrefMatch/HrefPlaceholder/HrefBaseline + TitleMatch + AnchorTextHint
		/// spans. No-op for missing attributes or out-of-window spans.
		/// </summary>
		private static void EmitPairSpans(
			List<ConsoleUi.AdjacentHintSpan> spans, ParsedAnchor left, ParsedAnchor right)
		{
			var leftHref = left.HrefValue;
			var rightHref = right.HrefValue;
			var bothHaveHref = leftHref is not null && rightHref is not null;
			var hrefsMatch = bothHaveHref
				&& string.Equals(leftHref, rightHref, StringComparison.Ordinal);

			// HrefMatch: both real and identical → DarkMagenta on both, plus AnchorTextHint.
			// HrefPlaceholder: both placeholders (most commonly both #) → DarkYellow on both.
			// HrefBaseline: any present href that doesn't fall in the above → DarkGray (mild).
			//
			// Note: a placeholder on ONE side and a real href on the other is rare; we
			// classify each side independently (placeholder gets DarkYellow, real
			// gets DarkGray). The "match" colour requires both real + identical.
			if (hrefsMatch && !left.HrefIsPlaceholder && !right.HrefIsPlaceholder)
			{
				AddSpan(spans, left.HrefValueStart, left.HrefValueLen, ConsoleUi.AdjacentHintSpanKind.HrefMatch);
				AddSpan(spans, right.HrefValueStart, right.HrefValueLen, ConsoleUi.AdjacentHintSpanKind.HrefMatch);
				// Same-real-href: also paint anchor inner-text in DarkGray to scope the pair.
				if (left.InnerTextStart >= 0 && left.InnerTextEnd > left.InnerTextStart)
				{
					AddSpan(spans, left.InnerTextStart, left.InnerTextEnd - left.InnerTextStart, ConsoleUi.AdjacentHintSpanKind.AnchorTextHint);
				}
				if (right.InnerTextStart >= 0 && right.InnerTextEnd > right.InnerTextStart)
				{
					AddSpan(spans, right.InnerTextStart, right.InnerTextEnd - right.InnerTextStart, ConsoleUi.AdjacentHintSpanKind.AnchorTextHint);
				}
			}
			else
			{
				if (leftHref is not null)
				{
					var kind = left.HrefIsPlaceholder
						? ConsoleUi.AdjacentHintSpanKind.HrefPlaceholder
						: ConsoleUi.AdjacentHintSpanKind.HrefBaseline;
					AddSpan(spans, left.HrefValueStart, left.HrefValueLen, kind);
				}
				if (rightHref is not null)
				{
					var kind = right.HrefIsPlaceholder
						? ConsoleUi.AdjacentHintSpanKind.HrefPlaceholder
						: ConsoleUi.AdjacentHintSpanKind.HrefBaseline;
					AddSpan(spans, right.HrefValueStart, right.HrefValueLen, kind);
				}
			}

			// TitleMatch: both titles present and equal → DarkCyan on both.
			if (left.TitleValue is not null && right.TitleValue is not null
				&& string.Equals(left.TitleValue, right.TitleValue, StringComparison.Ordinal))
			{
				AddSpan(spans, left.TitleValueStart, left.TitleValueLen, ConsoleUi.AdjacentHintSpanKind.TitleMatch);
				AddSpan(spans, right.TitleValueStart, right.TitleValueLen, ConsoleUi.AdjacentHintSpanKind.TitleMatch);
			}
		}

		private static void AddSpan(
			List<ConsoleUi.AdjacentHintSpan> spans, int start, int length, ConsoleUi.AdjacentHintSpanKind kind)
		{
			if (start < 0 || length <= 0)
			{
				return;
			}

			spans.Add(new ConsoleUi.AdjacentHintSpan(start, length, kind));
		}

		/// <summary>
		/// Returns a deduplicated, non-overlapping copy of the given spans list
		/// (which MUST already be sorted by Start). Duplicates can arise in cluster
		/// cases where the middle anchor of "&lt;a&gt;A&lt;/a&gt;&lt;a&gt;&lt;/a&gt;&lt;a&gt;B&lt;/a&gt;" is
		/// analysed under two pair contexts. First occurrence at each Start wins;
		/// overlapping spans drop the later one (renderer's invariant: spans must
		/// not overlap).
		/// </summary>
		private static IReadOnlyList<ConsoleUi.AdjacentHintSpan> DedupeOrdered(
			IReadOnlyList<ConsoleUi.AdjacentHintSpan> sorted)
		{
			var result = new List<ConsoleUi.AdjacentHintSpan>(sorted.Count);
			var lastEnd = -1;
			foreach (var s in sorted)
			{
				if (s.Start < lastEnd)
				{
					continue;  // overlap with previous span — drop
				}

				result.Add(s);
				lastEnd = s.Start + s.Length;
			}
			return result;
		}

		/// <summary>
		/// Returns a copy of the record reset to detection state: Status 'new'
		/// with all triage decision fields cleared (Comment, Ticket, DateReported).
		/// Detection facts (Type, Url, Word, Excerpt, SourceLabel, dates found) are
		/// preserved. Internal so it can be unit-tested without Console. The
		/// content-quality analogue of SpellTriage.DiscardToDetectionState.
		/// </summary>
		internal static IssueTracking.IssueRecord DiscardToNew(IssueTracking.IssueRecord record) =>
			record with
			{
				Status = "new",
				Comment = string.Empty,
				Ticket = string.Empty,
				DateReported = string.Empty,
			};

		// ── Cluster renumbering for multi-finding clusters ─────────────
		//
		// Findings of the same Type on the same Url can appear multiple times
		// (e.g., ADJACENT_ANCHOR clusters on a page with multiple </a><a
		// boundaries). The detector emits in source order, but ConcurrentBag's
		// undefined-order ToList() reshuffles before WriteLog sorts only by
		// (Filename, IssueType) — so equal-key entries land in arbitrary order
		// (observed: LIFO under current .NET, but ConcurrentBag explicitly
		// guarantees no order).
		//
		// The detector therefore prepends a source-position prefix to Detail
		// (e.g., "[1247] ...") so BuildGroups can recover source order. This
		// helper takes per-page entries plus a position-extractor and:
		//   - For clusters of size 1: strips the prefix (visual noise without
		//     siblings to order against).
		//   - For clusters of size 2+: sorts by extracted position, then
		//     replaces each entry's prefix with a sequential "[01]"/"[02]"/...
		//     for readable display.
		//
		// Generic by design: the extractor is a Func<string, int?> so the same
		// helper handles ADJACENT_ANCHOR (extracts leading "[N]") and any
		// future type with embedded position info (e.g., QUOTE's "at position N"
		// already in its Detail strings).

		/// <summary>
		/// Position-extractor regex for "[N] ..." leading-bracket form. Used by
		/// ADJACENT_ANCHOR's Detail strings written by the detector.
		/// Returns null if no leading bracket is present (defensive).
		/// </summary>
		private static readonly System.Text.RegularExpressions.Regex LeadingBracketRegex =
			new(@"^\[(\d+)\]\s*", System.Text.RegularExpressions.RegexOptions.Compiled);

		/// <summary>
		/// Extracts the numeric value from a leading "[N]" bracket in a Detail
		/// string. Returns null if no bracket is found. Used as the position
		/// extractor for ADJACENT_ANCHOR cluster ordering.
		/// </summary>
		internal static int? ExtractLeadingBracketPosition(string detail)
		{
			if (string.IsNullOrEmpty(detail))
			{
				return null;
			}

			var m = LeadingBracketRegex.Match(detail);
			if (!m.Success)
			{
				return null;
			}

			return int.TryParse(m.Groups[1].Value, out var n) ? n : null;
		}

		/// <summary>
		/// Strips the leading "[N] " prefix from a Detail string. Returns the
		/// input unchanged if no leading bracket is present.
		/// </summary>
		internal static string StripLeadingBracket(string detail)
		{
			if (string.IsNullOrEmpty(detail))
			{
				return detail;
			}

			var m = LeadingBracketRegex.Match(detail);
			return m.Success ? detail[m.Length..] : detail;
		}

		/// <summary>
		/// Renumbers a cluster of findings for display. If <paramref name="entries"/>
		/// has fewer than 2 elements, the prefix is stripped from each Detail
		/// (no cluster-mates to order against). If 2 or more, entries are
		/// sorted by <paramref name="extractPosition"/> applied to each Detail,
		/// then re-prefixed with "[01]"/"[02]"/... in display order.
		///
		/// Pure function; returns a new list of details in the rendered order.
		/// Callers should also retain the entries' Context fields paired with
		/// the resulting details (zip on index of the returned list and the
		/// sorted entries; see usage in BuildGroups' ADJACENT_ANCHOR branch).
		/// </summary>
		internal static IReadOnlyList<(string Detail, T Source)> RenumberCluster<T>(
			IReadOnlyList<T> entries,
			Func<T, string> getDetail,
			Func<string, int?> extractPosition,
			int bracketDigits = 2)
		{
			if (entries == null || entries.Count == 0)
			{
				return [];
			}
			if (entries.Count == 1)
			{
				var only = entries[0];
				return [(StripLeadingBracket(getDetail(only)), only)];
			}
			// Sort by extracted position; nulls go last (defensive — should not
			// happen if detector and extractor are aligned).
			var ordered = entries
				.OrderBy(e => extractPosition(getDetail(e)) ?? int.MaxValue)
				.ToList();
			var width = bracketDigits;
			var result = new List<(string Detail, T Source)>(ordered.Count);
			for (var i = 0; i < ordered.Count; i++)
			{
				var stripped = StripLeadingBracket(getDetail(ordered[i]));
				var bracket = $"[{(i + 1).ToString().PadLeft(width, '0')}] ";
				result.Add((bracket + stripped, ordered[i]));
			}
			return result;
		}

	}
}
