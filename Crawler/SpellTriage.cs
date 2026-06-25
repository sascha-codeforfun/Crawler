namespace Crawler
{
	using System.Collections.Generic;
	using System.Text;
	using System.Text.RegularExpressions;
	using Crawler.SpellCheck;
	using Crawler.Urls;

	/// <summary>
	/// Interactive console triage for spelling findings. The detected set is this run's in-memory
	/// <see cref="WordTicket"/>s (the substrate); the decided set is the SPELLING rows in
	/// IssueTracking. Spelling is never auto-promoted — only an operator decision lands in the
	/// ledger. Presents each detected-but-undecided word with every occurrence on the page and
	/// applies hotkey decisions ([T]icket / [L]ocale → pending SPELLING row; [W]ontfix → wontfix
	/// row or dictionary add; [S]kip; plus [M]ore raw HTML). Gone-is-gone retires SPELLING rows
	/// whose word is no longer detected. Writes through <see cref="IssueTracking"/>; reads tickets
	/// + the ledger; uses ConsoleUi/ConsoleTriage for I/O.
	/// </summary>
	public static class SpellTriage
	{
		/// <summary>
		/// Shows a numbered menu of known types (0=custom, 1-4 from list).
		/// Returns the selected/edited text, or null if user pressed Q/Escape.
		/// Supports {word} and {language} placeholder substitution.
		/// </summary>
		private static string? ShowKnownTypeMenu(
			string prompt, List<string> knownTypes, string word, string language)
		{
			var options = knownTypes.Take(4).ToList();
			ConsoleUi.WriteBlank();
			ConsoleUi.WriteWarning($"{prompt}");
			ConsoleUi.WriteLine("[0] Custom");
			for (int i = 0; i < options.Count; i++)
			{
				var text = options[i]
					.Replace("{word}", word)
					.Replace("{language}", language);
				ConsoleUi.WriteLine($"[{i + 1}] {text}");
			}

			// Invalid keys (Tab, Enter, letters, anything outside the
			// 0-N digit range and not [Q]) used to fall through with sel = -1
			// and silently return null, which the caller renders as "→ Cancelled".
			// That was hostile — a stray Tab would discard the operator's [T]
			// press without an actionable message. Now: only [Q] cancels;
			// invalid keys re-prompt with a warning, matching the unified
			// semantics for the surrounding triage prompts.
			int sel;
			while (true)
			{
				var key = ConsoleUi.ReadKey($"Select [0-{options.Count}] or [Q] to cancel > ");
				if (key == ConsoleKey.Q)
				{
					return null;
				}

				sel = key switch
				{
					ConsoleKey.D0 or ConsoleKey.NumPad0 => 0,
					ConsoleKey.D1 or ConsoleKey.NumPad1 => 1,
					ConsoleKey.D2 or ConsoleKey.NumPad2 => 2,
					ConsoleKey.D3 or ConsoleKey.NumPad3 => 3,
					ConsoleKey.D4 or ConsoleKey.NumPad4 => 4,
					_ => -1
				};
				if (sel >= 0 && sel <= options.Count)
				{
					break;
				}

				ConsoleUi.WriteWarning($"Invalid choice — press 0-{options.Count} or Q to cancel.");
			}

			// Pre-populate text for editing.
			var prePopulated = sel == 0
				? string.Empty
				: options[sel - 1].Replace("{word}", word).Replace("{language}", language);

			ConsoleUi.WriteInline($"{ConsoleUi.Indent}Text (Enter to accept): ");

			var result = string.IsNullOrEmpty(prePopulated)
				? ConsoleUi.ReadLine("")
				: ConsoleUi.ReadLineWithDefault(prePopulated);

			// Empty custom input is valid — caller uses default comment.
			return result;
		}

		/// <summary>
		/// Appends a word to a dictionary file if not already present.
		/// Creates the file if it doesn't exist.
		/// </summary>
		private static bool AppendToDictionary(string dictionaryPath, string word)
		{
			try
			{
				var existing = File.Exists(dictionaryPath)
					? File.ReadAllLines(dictionaryPath, Encoding.UTF8)
					: [];
				if (existing.Any(l => l.Trim().Equals(word, StringComparison.Ordinal)))
				{
					return false; // Already present.
				}

				File.AppendAllText(dictionaryPath, word + Environment.NewLine, Encoding.UTF8);
				return true;
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Could not append to dictionary {dictionaryPath}: {ex.Message}");
				return false;
			}
		}

		/// <summary>
		/// Renders a 300-char raw HTML context excerpt centred on the flagged word,
		/// for the [M] More handler. Idempotent — re-displays the same context on
		/// every press, no flag-flipping (the fragile earlier pattern). Word-boundary
		/// match so "Adress" does not match inside "Adressen". Used by BOTH
		/// the new/overdue triage and the pending/wontfix review walk so they share
		/// one rendering and any future fix lands in one place.
		/// </summary>
		private static void ShowRawHtmlContext(string word, string rawFilePath)
		{
			try
			{
				var rawHtml = File.ReadAllText(rawFilePath, Encoding.UTF8);
				var wordIdx = SpellTokenizer.IndexOfWholeWord(rawHtml, word, ignoreCase: true);
				if (wordIdx >= 0)
				{
					const int radius = 150;
					var start = Math.Max(0, wordIdx - radius);
					var end = Math.Min(rawHtml.Length, wordIdx + word.Length + radius);
					var before = rawHtml[start..wordIdx];
					var hit = rawHtml.Substring(wordIdx, word.Length);
					var after = rawHtml[(wordIdx + word.Length)..end];
					ConsoleUi.WriteBlank();
					ConsoleUi.WriteSkipped("Raw HTML context:");
					ConsoleUi.WriteLineWithMutedHighlight($"{ConsoleUi.Indent}...{before}", hit, $"{after}...");
					ConsoleUi.WriteBlank();
				}
				else
				{
					ConsoleUi.WriteLine("(Word not found in raw HTML)");
				}
			}
			catch { ConsoleUi.WriteLine("(Could not read raw HTML file)"); }
		}

		/// <summary>
		/// Renders the CONTENT_BEFORE_DOCTYPE bag as one rollup card and applies [T]/[S]. [T]
		/// fan-outs a pending SPELLING row per (url,word) tagged with the marker (gone-is-gone
		/// retires them when the pages heal), then reads this run's sidecars
		/// and writes the ticket text. [S]/[Q] leaves the bag undecided so it nags next run. No
		/// Wontfix: a structurally broken page must keep surfacing until fixed or ticketed. Returns
		/// the number of findings ticketed (0 on skip).
		/// </summary>
		private static int HandleContentBeforeDoctypeBag(
			List<WordTicket> bag,
			List<IssueTracking.IssueRecord> ledger,
			string issueTrackingPath,
			string downloadDirectory,
			IReadOnlyList<CrawlHistoryDiagnosticHeaderExtractor>? headerExtractors,
			HashSet<string> decidedKeys,
			string today)
		{
			if (bag.Count == 0)
			{
				return 0;
			}

			var pages = bag
				.Select(t => t.Url)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(u => u, StringComparer.Ordinal)
				.ToList();

			lock (Logger.ConsoleLock)
			{
				ConsoleUi.WriteDivider();
				ConsoleUi.WriteWarning($"⚠ {ContentBeforeDoctypeBag.Marker} — {bag.Count} finding(s) on {pages.Count} page(s)");
				ConsoleUi.WriteInfo("Content before <!doctype> corrupts the parse upstream of all content; the spelling");
				ConsoleUi.WriteInfo("findings here are parse artifacts, not typos. Fix the response emission, not the words.");
				foreach (var u in pages)
				{
					ConsoleUi.WriteLine($"  {u}");
				}

				ConsoleUi.WriteDivider();
			}

			var choices = new List<ChoiceOption>
			{
				new(ConsoleKey.T, "Ticket"),
				new(ConsoleKey.S, "Skip"),
				new(ConsoleKey.Q, "Quit"),
			};

			ConsoleKey input = ConsoleTriage.Ask(prompt: string.Empty, choices: choices);

			if (input != ConsoleKey.T)
			{
				ConsoleUi.WriteSkipped($"→ {ContentBeforeDoctypeBag.Marker} left for next run");
				ConsoleUi.WriteBlank();
				return 0;
			}

			var ticketRef = ConsoleTriage.AskFreeText("Ticket reference (optional, Enter to skip): ");

			foreach (var t in bag)
			{
				UpsertSpelling(ledger, BuildDecision(t, "pending", ticketRef, ContentBeforeDoctypeBag.Marker, today));
				decidedKeys.Add(SpellingKey(t.Url, t.Word));
			}

			IssueTracking.Save(issueTrackingPath, ledger);

			var ticketText = BuildContentBeforeDoctypeTicketText(bag, pages, downloadDirectory, headerExtractors);
			WriteTicketTextLog(issueTrackingPath, ticketText);

			lock (Logger.ConsoleLock)
			{
				var label = $"→ {ContentBeforeDoctypeBag.Marker} ticketed — {bag.Count} finding(s) on {pages.Count} page(s)";
				if (!string.IsNullOrEmpty(ticketRef))
				{
					label += $" [{ticketRef}]";
				}

				ConsoleUi.WriteActionRequired(label);
				ConsoleUi.WriteBlank();
				ConsoleUi.WriteInfo("Ticket text (also written to ticketText.log):");
				foreach (var line in ticketText.Split('\n'))
				{
					ConsoleUi.WriteLine(line);
				}

				ConsoleUi.WriteBlank();
			}

			return bag.Count;
		}

		/// <summary>
		/// Reads this run's .header sidecars for the bagged pages and assembles the ticket text. If
		/// diagnostic header extractors are configured, their patterns are compiled and used to group
		/// the pages (an invalid pattern is skipped). Missing/unreadable sidecar → page kept with a
		/// "(sidecar unavailable)" marker.
		/// </summary>
		private static string BuildContentBeforeDoctypeTicketText(
			List<WordTicket> bag,
			List<string> pages,
			string downloadDirectory,
			IReadOnlyList<CrawlHistoryDiagnosticHeaderExtractor>? headerExtractors)
		{
			var compiled = new List<(string Label, Regex Pattern)>();
			if (headerExtractors != null)
			{
				foreach (var ex in headerExtractors)
				{
					if (string.IsNullOrEmpty(ex.Label) || string.IsNullOrEmpty(ex.Pattern))
					{
						continue;
					}

					try
					{
						compiled.Add((ex.Label, new Regex(ex.Pattern)));
					}
					catch (ArgumentException)
					{
						// Skip an invalid pattern rather than failing the run.
					}
				}
			}

			var pageDiagnostics = new List<ContentBeforeDoctypeBag.PageDiagnostics>();
			foreach (var url in pages)
			{
				var sidecar = ResolveSidecarPath(url, downloadDirectory);
				string? text = null;
				if (!string.IsNullOrEmpty(sidecar) && File.Exists(sidecar))
				{
					try { text = File.ReadAllText(sidecar, Encoding.UTF8); }
					catch { text = null; }
				}

				pageDiagnostics.Add(text == null
					? new ContentBeforeDoctypeBag.PageDiagnostics(
						url, null, null, System.Array.Empty<string>(),
						System.Array.Empty<(string, string)>(), SidecarFound: false)
					: ContentBeforeDoctypeBag.DiagnosticsFromSidecarText(url, text, compiled));
			}

			return ContentBeforeDoctypeBag.BuildTicketText(pageDiagnostics, bag.Count);
		}

		/// <summary>The .header sidecar path for a page (foo.html → foo.header), or empty if unresolved.</summary>
		private static string ResolveSidecarPath(string url, string downloadDirectory)
		{
			if (string.IsNullOrEmpty(downloadDirectory))
			{
				return string.Empty;
			}

			var fn = Cache.FilenameFor(url);
			if (string.IsNullOrEmpty(fn))
			{
				return string.Empty;
			}

			var raw = Path.Combine(downloadDirectory, fn);
			return Path.ChangeExtension(raw, HeaderSidecar.HeaderSidecarExtension.TrimStart('.'));
		}

		/// <summary>Appends the ticket text to ticketText.log beside IssueTracking.log. Never throws.</summary>
		private static void WriteTicketTextLog(string issueTrackingPath, string ticketText)
		{
			try
			{
				var dir = Path.GetDirectoryName(issueTrackingPath);
				var path = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, "ticketText.log");
				var header = $"=== {ContentBeforeDoctypeBag.Marker} · {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ===\n";
				File.AppendAllText(path, header + ticketText + "\n", Encoding.UTF8);
			}
			catch
			{
				// The ticket text is also shown on the console; a log-write failure must not break triage.
			}
		}

		/// <summary>
		/// Resolves the raw HTML file path for an entry's URL via Cache. Returns
		/// empty string when downloadDirectory is unset or the file is not present —
		/// the absence sentinel that gates whether [M] More is offered.
		/// </summary>
		private static string ResolveRawHtmlPath(string url, string downloadDirectory)
		{
			if (string.IsNullOrEmpty(downloadDirectory))
			{
				return string.Empty;
			}

			var fn = Cache.FilenameFor(url);
			if (string.IsNullOrEmpty(fn))
			{
				return string.Empty;
			}

			var candidate = Path.Combine(downloadDirectory, fn);
			return File.Exists(candidate) ? candidate : string.Empty;
		}


		/// <summary>
		/// Interactive spelling triage, ticket-driven. The detected set is this run's in-memory
		/// <see cref="WordTicket"/>s (the substrate); the decided set is the SPELLING rows already in
		/// IssueTracking. Spelling is never auto-promoted — only an operator [T]/[L]/[W] decision
		/// writes a SPELLING row. Per run:
		///   1. Gone-is-gone: drop SPELLING rows whose word is no longer detected (ticket fixed /
		///      suppression no longer needed; if the word returns it re-enters as undecided).
		///   2. Show every detected word NOT already decided, with all its occurrences.
		///   3. [T]/[L] write a pending SPELLING row (a raised ticket); [W] either writes a wontfix
		///      row (accept-on-page) or adds the word to a dictionary (no row — gone next run).
		/// Each decision saves immediately (crash-safe). The caller gates this on a supervised
		/// latest-snapshot run, so the committed ledger is never reconciled against a stale snapshot.
		/// </summary>
		public static void RunSpellCheckTriage(
			string issueTrackingPath,
			string userDictionaryPath,
			string siteDictionaryPath,
			string contentQualityLogPath,
			List<string> localisationKnownTypes,
			List<string> ticketKnownTypes,
			List<string> wontfixKnownTypes,
			IReadOnlyList<UrlHighlightRule> urlHighlightRules,
			IReadOnlyList<WordTicket>? spellTickets = null,
			string localisationComment = "Translation missing — content not localised for page language",
			string downloadDirectory = "",
			IReadOnlyList<CrawlHistoryDiagnosticHeaderExtractor>? headerExtractors = null)
		{
			// A null ticket set means this process's harvest did not complete (threw or did not
			// run). There is nothing trustworthy to reconcile the ledger against, so bail without
			// touching IssueTracking — gone-is-gone must never fire on a failed harvest.
			if (spellTickets == null)
			{
				ConsoleUi.WriteBlank();
				ConsoleUi.WriteInfo("Spell triage skipped — no spell results from this run.");
				return;
			}

			var ledger = IssueTracking.Load(issueTrackingPath);

			// Detected this run, as SPELLING|url|word keys.
			var detectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var t in spellTickets)
			{
				detectedKeys.Add(SpellingKey(t.Url, t.Word));
			}

			// Gone-is-gone (SPELLING only): a decided SPELLING row whose word is no longer detected
			// is retired outright. Non-SPELLING rows are left entirely alone — their lifecycle is the
			// end-of-run Merge. Persist the retire before triage so a mid-triage exit still records it.
			var reconciled = ReconcileSpelling(ledger, detectedKeys);
			if (reconciled.Count != ledger.Count)
			{
				IssueTracking.Save(issueTrackingPath, reconciled);
			}

			ledger = reconciled;

			// Review pass (optional, Y/N-gated): walk every already-decided SPELLING row (pending or
			// wontfix) — all still detected, since gone-is-gone above already retired the rest — and let
			// the operator [S] leave it as-is or [D] discard it back to undecided. A discard removes the
			// row, so the word re-enters the live triage below as new. Runs before decidedKeys is taken
			// so a resurrected word reappears in toTriage this same session.
			ReviewTriagedItems(issueTrackingPath, ledger, urlHighlightRules);

			// Already-decided SPELLING keys — those detected words are hidden from triage (the operator
			// already ruled on them, unless they discarded the row in the review pass just above).
			var decidedKeys = ledger
				.Where(IsSpelling)
				.Select(r => r.Key)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			// URL → distinct quality issue types, for the page-level annotation shown during triage.
			var qualityIssuesByUrl = BuildQualityIssueLookup(contentQualityLogPath);

			// NEW = detected this run AND not already decided, ordered by Word.
			var toTriage = spellTickets
				.Where(t => !decidedKeys.Contains(SpellingKey(t.Url, t.Word)))
				.OrderBy(t => t.Word, StringComparer.OrdinalIgnoreCase)
				.ToList();

			// CONTENT_BEFORE_DOCTYPE bag: pull parse-shrapnel findings (page carries the universal
			// MALFORMED_HTML:CONTENT_BEFORE_DOCTYPE sub-code AND the word has a non-word char) out of
			// the per-word queue into one rollup card. Lossless — a clean split fragment
			// (all letters) fails the char test and stays an individual card below.
			var cbdUrls = ContentBeforeDoctypeBag.BuildContentBeforeDoctypeUrls(
				File.Exists(contentQualityLogPath)
					? File.ReadAllLines(contentQualityLogPath, Encoding.UTF8)
					: Array.Empty<string>(),
				CrawlIndex.LookUpUrlForFile);
			var (cbdBag, cbdRest) = ContentBeforeDoctypeBag.Partition(toTriage, cbdUrls);
			toTriage = cbdRest;

			if (toTriage.Count == 0 && cbdBag.Count == 0)
			{
				ConsoleUi.WriteBlank();
				ConsoleUi.WriteSuccess("No new spelling items to triage.");
				return;
			}

			var today = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");

			// The CONTENT_BEFORE_DOCTYPE bag is shown first, as one rollup card: it is a structural
			// defect (the page is broken upstream of content), not a set of typos. [T] fan-outs a
			// pending row per (url,word) tagged with the marker (so gone-is-gone retires them when
			// the pages heal) and writes the ticket text; [S] leaves it to nag next run.
			int bagTicketed = HandleContentBeforeDoctypeBag(
				cbdBag, ledger, issueTrackingPath, downloadDirectory, headerExtractors, decidedKeys, today);

			if (toTriage.Count == 0)
			{
				return;
			}

			ConsoleUi.WriteHeader($"SPELLING TRIAGE — {toTriage.Count} item(s) to review.");
			ConsoleUi.WriteFooter();
			ConsoleUi.WriteBlank();

			// Words added to a dictionary this session auto-resolve every later occurrence (the
			// word will be gone next run, so it is never a ticket or a row).
			var wordsAddedToDictionary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			int current = 0;
			int ticketed = bagTicketed, wontfixed = 0, dictionaried = 0, skipped = 0;

			foreach (var ticket in toTriage)
			{
				var key = SpellingKey(ticket.Url, ticket.Word);

				// Decided earlier in this same session (an [L] batch covered this URL, or the word
				// was dictionaried) — skip silently.
				if (decidedKeys.Contains(key) || wordsAddedToDictionary.Contains(ticket.Word))
				{
					continue;
				}

				current++;

				var occurrences = ticket.Occurrences;
				var rawFilePath = ResolveRawHtmlPath(ticket.Url, downloadDirectory);

				var choices = new List<ChoiceOption>
				{
					new(ConsoleKey.T, "Ticket"),
					new(ConsoleKey.L, "Locale"),
					new(ConsoleKey.W, "Wontfix/Dictionary"),
				};
				if (!string.IsNullOrEmpty(rawFilePath))
				{
					choices.Add(new ChoiceOption(ConsoleKey.M, "More"));
				}

				choices.Add(new ChoiceOption(ConsoleKey.S, "Skip"));
				choices.Add(new ChoiceOption(ConsoleKey.Q, "Quit"));

				lock (Logger.ConsoleLock)
				{
					ConsoleUi.WriteDivider();

					// The lorem-ipsum placeholder finding carries the synthetic sentinel Word; tint it amber
					// in the header so the operator reads it as a special-cased, non-typo finding. Every other
					// word keeps the default (plain) header.
					bool isPlaceholder = ticket.Word == ScriptSpellingTickets.PlaceholderWord;
					ConsoleUi.WriteCardHeader(
						current,
						toTriage.Count,
						"Word",
						ticket.Word,
						valueColor: isPlaceholder ? System.ConsoleColor.DarkYellow : (System.ConsoleColor?)null);
					ConsoleUi.WriteUrlField(ticket.Url, urlHighlightRules);

					// Every occurrence of this word on the page (from the engine ticket), each with
					// its location label and the word highlighted in its own excerpt.
					foreach (var occ in occurrences)
					{
						WriteOccurrence(ticket.Word, occ);
					}

					ConsoleUi.WriteField("Language", ticket.Languages);

					// Annotation — warn if this page has known quality issues.
					if (qualityIssuesByUrl.TryGetValue(ticket.Url, out var pageIssues) && pageIssues.Count > 0)
					{
						ConsoleUi.WriteWarning($"⚠ Quality issue(s) on this page: {string.Join(", ", pageIssues)}");
					}

					ConsoleUi.WriteDivider();
				}

				ConsoleKey input = ConsoleTriage.Ask(
					prompt: string.Empty,
					choices: choices,
					defaultKey: null,
					continueOnKey: pressed =>
					{
						if (pressed != ConsoleKey.M)
						{
							return false;
						}

						// [M] — raw HTML context centred on the word. Idempotent: every press
						// re-renders. Shared whole-word locator with the excerpt + highlight.
						ShowRawHtmlContext(ticket.Word, rawFilePath);
						return true; // stay in loop, re-prompt
					});

				if (input == ConsoleKey.Q)
				{
					ConsoleUi.WriteSkipped("Triage paused — progress saved.");
					break;
				}

				if (input == ConsoleKey.L)
				{
					string comment;
					if (localisationKnownTypes.Count > 0)
					{
						var selected = ShowKnownTypeMenu(
							"Localisation reason:", localisationKnownTypes, ticket.Word, ticket.Languages);
						if (selected == null) { ConsoleUi.WriteSkipped("→ Cancelled"); ConsoleUi.WriteBlank(); continue; }
						comment = string.IsNullOrEmpty(selected) ? localisationComment : selected;
					}
					else
					{
						var typed = ConsoleTriage.AskFreeText($"Comment [{localisationComment}]: ");
						comment = string.IsNullOrEmpty(typed) ? localisationComment : typed;
					}

					var ticketRef = ConsoleTriage.AskFreeText("Ticket reference (optional, Enter to skip): ");

					// Localisation is page-level: ticket every still-undecided detected word on this
					// URL at once.
					int batched = 0;
					foreach (var t2 in toTriage)
					{
						if (!t2.Url.Equals(ticket.Url, StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}

						var k2 = SpellingKey(t2.Url, t2.Word);
						if (decidedKeys.Contains(k2) || wordsAddedToDictionary.Contains(t2.Word))
						{
							continue;
						}

						UpsertSpelling(ledger, BuildDecision(t2, "pending", ticketRef, comment, today));
						decidedKeys.Add(k2);
						batched++;
					}

					IssueTracking.Save(issueTrackingPath, ledger);

					var lLabel = $"→ Localisation — {batched} word(s) on this URL ticketed";
					if (!string.IsNullOrEmpty(ticketRef))
					{
						lLabel += $" [{ticketRef}]";
					}

					ConsoleUi.WriteActionRequired(lLabel);
					ticketed += batched;
				}
				else if (input == ConsoleKey.T)
				{
					string comment;
					if (ticketKnownTypes.Count > 0)
					{
						var selected = ShowKnownTypeMenu(
							"Ticket reason:", ticketKnownTypes, ticket.Word, ticket.Languages);
						if (selected == null) { ConsoleUi.WriteSkipped("→ Cancelled"); ConsoleUi.WriteBlank(); continue; }
						comment = selected;
					}
					else
					{
						comment = ConsoleTriage.AskFreeText("Comment (optional, Enter to skip): ");
					}

					var ticketRef = ConsoleTriage.AskFreeText("Ticket reference (optional, Enter to skip): ");

					UpsertSpelling(ledger, BuildDecision(ticket, "pending", ticketRef, comment, today));
					decidedKeys.Add(key);
					IssueTracking.Save(issueTrackingPath, ledger);

					var tLabel = "→ Pending (ticket)";
					if (!string.IsNullOrEmpty(ticketRef))
					{
						tLabel += $" [{ticketRef}]";
					}

					ConsoleUi.WriteActionRequired(tLabel);
					ticketed++;
				}
				else if (input == ConsoleKey.W)
				{
					ConsoleUi.WriteBlank();
					ConsoleUi.WriteWarning($"Wontfix options for '{ticket.Word}':");
					var wKey = ConsoleTriage.Ask(string.Empty,
						[new ChoiceOption(ConsoleKey.W, "Wontfix this URL only"),
						 new ChoiceOption(ConsoleKey.U, "User dictionary"),
						 new ChoiceOption(ConsoleKey.S, "Site dictionary"),
						 new ChoiceOption(ConsoleKey.Q, "Cancel")]);

					if (wKey == ConsoleKey.Q)
					{
						ConsoleUi.WriteSkipped("→ Cancelled");
					}
					else if (wKey == ConsoleKey.W)
					{
						// Accept this word on this page — a wontfix SPELLING row that lives until
						// gone-is-gone drops it. No dictionary change.
						string wComment;
						if (wontfixKnownTypes.Count > 0)
						{
							var selected = ShowKnownTypeMenu(
								"Wontfix reason:", wontfixKnownTypes, ticket.Word, ticket.Languages);
							if (selected == null) { ConsoleUi.WriteSkipped("→ Cancelled"); ConsoleUi.WriteBlank(); continue; }
							wComment = string.IsNullOrEmpty(selected) ? "Intentional" : selected;
						}
						else
						{
							wComment = ConsoleTriage.AskFreeText("Comment (optional, Enter to skip): ");
						}

						UpsertSpelling(ledger, BuildDecision(ticket, "wontfix", string.Empty, wComment, today));
						decidedKeys.Add(key);
						IssueTracking.Save(issueTrackingPath, ledger);

						ConsoleUi.WriteSkipped($"→ Wontfix — {wComment}");
						wontfixed++;
					}
					else if (wKey == ConsoleKey.U)
					{
						// Dictionary add IS the fix — the word is gone next run, so no row is written.
						var added = AppendToDictionary(userDictionaryPath, ticket.Word);
						wordsAddedToDictionary.Add(ticket.Word);
						ConsoleUi.WriteSuccess(added
							? $"→ Added '{ticket.Word}' to user dictionary."
							: $"→ '{ticket.Word}' already in user dictionary.");
						dictionaried++;
					}
					else if (wKey == ConsoleKey.S)
					{
						var added = AppendToDictionary(siteDictionaryPath, ticket.Word);
						wordsAddedToDictionary.Add(ticket.Word);
						ConsoleUi.WriteSuccess(added
							? $"→ Added '{ticket.Word}' to site dictionary."
							: $"→ '{ticket.Word}' already in site dictionary.");
						dictionaried++;
					}
				}
				else if (input == ConsoleKey.S)
				{
					skipped++;
				}

				ConsoleUi.WriteBlank();
			}

			ConsoleUi.WriteBlank();
			ConsoleUi.WriteSuccess($"Spelling triage complete: {ticketed} ticketed, {wontfixed} wontfix, " +
				$"{dictionaried} dictionaried, {skipped} skipped.");
		}

		// ── Ledger helpers ───────────────────────────────────────────────────────────

		/// <summary>
		/// Optional read-only-ish review pass over already-triaged SPELLING rows (status pending or
		/// wontfix), gated behind a Y/N prompt — N (the default) skips it and preserves today's flow.
		/// Mirrors <see cref="ContentQualityTriage.ReviewTriagedQualityItems"/> for spelling, with one
		/// model difference: spelling has no "new"-status row (undecided = no row at all), so a discard
		/// here REMOVES the SPELLING row rather than re-stamping its status. The caller runs this on the
		/// already-reconciled ledger, so every reviewable row is still detected this run (gone-is-gone
		/// retired the rest) — no live-key gate is needed. On [D] the row is removed and saved immediately
		/// (crash-safe); the caller takes decidedKeys AFTER this returns, so a discarded word re-enters
		/// the live triage as undecided. The <paramref name="ledger"/> list is mutated in place.
		/// </summary>
		internal static void ReviewTriagedItems(
			string issueTrackingPath,
			List<IssueTracking.IssueRecord> ledger,
			IReadOnlyList<UrlHighlightRule> urlHighlightRules)
		{
			var reviewable = SelectReviewableSpelling(ledger);
			if (reviewable.Count == 0)
			{
				return;   // nothing triaged yet — no prompt
			}

			ConsoleUi.WriteHeader("SPELLING REVIEW");
			ConsoleUi.WriteFooter();
			if (!ConsoleTriage.AskYesNo(
				$"Review {reviewable.Count} already-triaged spelling item(s) (pending/wontfix) before triage?"))
			{
				return;   // N — preserve today's behavior
			}

			ConsoleUi.WriteBlank();
			ConsoleUi.WriteInfo($"Review — {reviewable.Count} triaged item(s). [S] Skip leaves as-is, [D] Discard resets to new.");

			int discarded = 0;
			int position = 0;
			bool quit = false;
			foreach (var record in reviewable)
			{
				position++;
				ConsoleUi.WriteBlank();
				ConsoleUi.WriteDivider();
				ConsoleUi.WriteInfo($"[{position}/{reviewable.Count}]");
				ConsoleUi.WriteField("Status", record.Status);

				// Word highlighted muted (amber) so it reads as review, not live triage.
				ConsoleUi.WriteLineWithMutedHighlight($"{ConsoleUi.Indent}{"Word",-9}: ", record.Word, "");

				ConsoleUi.WriteUrlField(record.Url, urlHighlightRules);

				if (!string.IsNullOrEmpty(record.SourceLabel))
				{
					ConsoleUi.WriteField("Source", record.SourceLabel);
				}

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

				// Context with the flagged word highlighted muted in situ; whole-word locator so a stem
				// inside a longer word is not marked (matching live triage's WriteOccurrence).
				if (!string.IsNullOrEmpty(record.Excerpt))
				{
					var ctxPrefix = $"{ConsoleUi.Indent}{"Context",-9}: ";
					var hit = SpellTokenizer.IndexOfWholeWord(record.Excerpt, record.Word, ignoreCase: true);
					if (hit >= 0 && !string.IsNullOrEmpty(record.Word))
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
					// Resurrect: remove the SPELLING row so the word re-enters live triage as undecided.
					RemoveSpellingRow(ledger, record.Key);
					IssueTracking.Save(issueTrackingPath, ledger);   // immediate — crash-safe
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
		/// The already-decided SPELLING rows eligible for review: status pending or wontfix, ordered
		/// pending-before-wontfix then by Word. Pure. No live-key gate — the caller reviews the
		/// already-reconciled ledger, where every SPELLING row is by construction still detected.
		/// </summary>
		internal static List<IssueTracking.IssueRecord> SelectReviewableSpelling(
			IReadOnlyList<IssueTracking.IssueRecord> ledger)
		{
			return ledger
				.Where(IsSpelling)
				.Where(r => r.Status == "pending" || r.Status == "wontfix")
				.OrderBy(r => r.Status, StringComparer.Ordinal)   // pending before wontfix
					.ThenBy(r => r.Word, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		/// <summary>
		/// Removes the first SPELLING row matching <paramref name="key"/> from the ledger (the resurrect
		/// for a discarded review item). Returns true if a row was removed. Mutates the list in place.
		/// </summary>
		internal static bool RemoveSpellingRow(List<IssueTracking.IssueRecord> ledger, string key)
		{
			for (int i = 0; i < ledger.Count; i++)
			{
				if (IsSpelling(ledger[i]) && ledger[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
				{
					ledger.RemoveAt(i);
					return true;
				}
			}

			return false;
		}

		/// <summary>IssueTracking identity key for a spelling finding: SPELLING|url|word.</summary>
		internal static string SpellingKey(string url, string word) => $"SPELLING|{url}|{word}";

		internal static bool IsSpelling(IssueTracking.IssueRecord r) =>
			r.Type.Equals("SPELLING", StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// Gone-is-gone for SPELLING: keep every non-SPELLING row untouched, and keep a SPELLING
		/// row only if its key is still in the detected set. Pure; returns a new list.
		/// </summary>
		internal static List<IssueTracking.IssueRecord> ReconcileSpelling(
			List<IssueTracking.IssueRecord> ledger, HashSet<string> detectedKeys)
		{
			return ledger
				.Where(r => !IsSpelling(r) || detectedKeys.Contains(r.Key))
				.ToList();
		}

		/// <summary>
		/// Builds a triage decision row for a detected ticket. DateReported is set equal to
		/// DateFound (today) for now — the separate reported-clock is a parked backlog item, so
		/// escalation is wall-clock from first-seen via TicketGeneration.OverdueAfterDays.
		/// </summary>
		internal static IssueTracking.IssueRecord BuildDecision(
			WordTicket ticket, string status, string ticketRef, string comment, string today)
		{
			var first = ticket.Occurrences.Count > 0 ? ticket.Occurrences[0] : null;
			return new IssueTracking.IssueRecord
			{
				Source = "triage",
				Type = "SPELLING",
				Url = ticket.Url,
				Word = ticket.Word,
				Status = status,
				Ticket = ticketRef ?? string.Empty,
				DateFound = today,
				DateReported = today,
				DateLastSeen = today,
				Comment = comment ?? string.Empty,
				Language = ticket.Languages,
				SourceLabel = first?.SourcePath ?? string.Empty,
				Excerpt = first?.Excerpt ?? string.Empty,
			};
		}

		/// <summary>
		/// Upserts a SPELLING decision into the in-memory ledger by Key (replace if present, else
		/// add). The caller persists with IssueTracking.Save after each decision (crash-safe).
		/// </summary>
		internal static void UpsertSpelling(
			List<IssueTracking.IssueRecord> ledger, IssueTracking.IssueRecord decision)
		{
			for (int i = 0; i < ledger.Count; i++)
			{
				if (ledger[i].Key.Equals(decision.Key, StringComparison.OrdinalIgnoreCase))
				{
					ledger[i] = decision;
					return;
				}
			}

			ledger.Add(decision);
		}

		/// <summary>
		/// URL → distinct quality issue types, parsed from the content-quality log, for the
		/// page-level annotation shown during triage. Reader-side filename defence mirrors
		/// PromoteFromQuality.
		/// </summary>
		private static Dictionary<string, List<string>> BuildQualityIssueLookup(string contentQualityLogPath)
		{
			var qualityIssuesByUrl = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			if (!File.Exists(contentQualityLogPath))
			{
				return qualityIssuesByUrl;
			}

			foreach (var line in File.ReadAllLines(contentQualityLogPath, Encoding.UTF8))
			{
				if (line.Length == 0 || line.StartsWith("Filename|", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var p = line.Split('|');
				if (p.Length < 2)
				{
					continue;
				}

				var f0 = p[0].Trim();
				if (!IssueTracking.LooksLikeFilename(f0))
				{
					continue;
				}

				var url = CrawlIndex.LookUpUrlForFile(f0);
				if (string.IsNullOrEmpty(url) || url == "error")
				{
					continue;
				}

				var issueType = p[1].Trim();
				if (!qualityIssuesByUrl.TryGetValue(url, out var types))
				{
					types = [];
					qualityIssuesByUrl[url] = types;
				}

				if (!types.Contains(issueType))
				{
					types.Add(issueType);
				}
			}

			return qualityIssuesByUrl;
		}

		/// <summary>
		/// Renders one occurrence: its location label (SourcePath) and the flagged word
		/// highlighted within its excerpt. Located by <see cref="SpellTokenizer.IndexOfWholeWord"/>
		/// so a stem inside a longer word ("Adress" in "Adressen") or a hyphenated compound
		/// ("Adress-daten") is not highlighted; if the word is not present as a whole token it falls
		/// back to plain text rather than highlighting the wrong span. Script occurrences instead use
		/// <see cref="ScriptExcerptLocator"/>, because their excerpt is raw source that may carry JS
		/// escapes the decoded word would not match directly. Caller holds the console lock.
		/// </summary>
		private static void WriteOccurrence(string word, TicketOccurrence occ)
		{
			if (!string.IsNullOrEmpty(occ.SourcePath))
			{
				// A script-body finding's path is "script[L:C]". Tint the leading "script" amber as
				// subtle guidance — this is a token lifted from inline JS, not a normal prose typo —
				// leaving the [L:C] location plain. Every other source renders unchanged.
				const string scriptKind = "script";
				if (occ.SourcePath.StartsWith("script[", System.StringComparison.Ordinal))
				{
					ConsoleUi.WriteFieldWithAmberToken("Source", scriptKind, occ.SourcePath.Substring(scriptKind.Length));
				}
				else
				{
					ConsoleUi.WriteField("Source", occ.SourcePath);
				}
			}

			var excerpt = occ.Excerpt ?? string.Empty;
			if (excerpt.Length == 0)
			{
				return;
			}

			// Placeholder finding: the Word is the synthetic sentinel ("Lorem ipsum (placeholder text)"),
			// not a substring of the excerpt, so the word-locator below cannot light it. Instead light the
			// whole lorem-ipsum block (first filler token .. last) in the WCAG blue scheme — flagged, but
			// filler, not a typo. Gated on the sentinel, so no other finding takes this path. Falls back to
			// a plain Context line if no filler run is present (a placeholder finding always carries one).
			if (word == ScriptSpellingTickets.PlaceholderWord)
			{
				var (fillerStart, fillerLength) = ScriptSpellingTickets.LocateFillerRun(excerpt);
				if (fillerStart >= 0)
				{
					var fillerBefore = excerpt[..fillerStart];
					var fillerHit = excerpt.Substring(fillerStart, fillerLength);
					var fillerAfter = excerpt[(fillerStart + fillerLength)..];
					ConsoleUi.WriteLineWithWcagHighlight(
						$"{ConsoleUi.Indent}{"Context",-9}: {fillerBefore}", fillerHit, fillerAfter);
				}
				else
				{
					ConsoleUi.WriteField("Context", excerpt);
				}

				return;
			}

			// The flagged word is the DECODED token; for script sources (script bodies and on* handlers)
			// the excerpt is RAW source per 623, which may carry JS escapes ("Auto\u002DScroll"), so a
			// literal whole-word search fails. Locate the raw span that decodes to the word and highlight
			// that; every other source uses the plain whole-word search on its own (escape-free) excerpt.
			int wordIdx;
			int hitLength;
			if (occ.Source == RunSource.Script)
			{
				(wordIdx, hitLength) = ScriptExcerptLocator.LocateRawSpan(excerpt, word);
			}
			else
			{
				wordIdx = SpellTokenizer.IndexOfWholeWord(excerpt, word, ignoreCase: true);
				hitLength = word.Length;
			}

			if (wordIdx >= 0)
			{
				var before = excerpt[..wordIdx];
				var hit = excerpt.Substring(wordIdx, hitLength);
				var after = excerpt[(wordIdx + hitLength)..];
				// Prefix matches WriteField's "{Indent}{label,-9}: " so the highlighted Context
				// line aligns with the other fields.
				ConsoleUi.WriteLineWithHighlight($"{ConsoleUi.Indent}{"Context",-9}: {before}", hit, after);
			}
			else
			{
				ConsoleUi.WriteField("Context", excerpt);

				// A flagged word that cannot be located as a whole word in its own block excerpt was
				// split from its surrounding text between the checker's view (the run) and the rendered
				// block — by inline markup the traverser treats as a segment boundary (a misplaced empty
				// <a>, a <br>, …) or by a stray control character. A genuine prose typo always appears as
				// a whole word in its block, so this non-locatability is itself the signal that the
				// finding is a content-defect artifact — the same defects the page's quality issues
				// already flag. Surface the isolated fragment the checker actually flagged, highlighted as
				// a located word would be, with that explanation, so the operator can see the token and
				// read it as a markup / control-character split rather than a hidden typo. TextNode only:
				// attribute and meta excerpts share their checked source so never reach this branch, and
				// script excerpts are raw source handled by the locator path above.
				if (occ.Source == RunSource.TextNode)
				{
					ConsoleUi.WriteLineWithHighlight(
						$"{ConsoleUi.Indent}{"Fragment",-9}: ",
						word,
						"  (not locatable in context — split by inline markup or a control character; see this page's quality issues)");
				}
			}
		}
	}
}
