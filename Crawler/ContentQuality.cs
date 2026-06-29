using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Crawler.Quality;

namespace Crawler
{
	using HtmlAgilityPack;
	using System.Text;

	/// <summary>
	/// Scans downloaded HTML pages for content quality issues that are distinct from
	/// spelling errors — ligatures copied from PDFs/InDesign, typographic quote
	/// problems, and structural anchor defects.
	///
	/// Output: ContentQualityIssues.log — one line per issue, pipe-delimited:
	///   Filename|IssueType|Detail|Context
	///
	/// Issue types (raw HTML — download pass):
	///   UNWANTED_PATTERN        — configured pattern found in raw HTML
	///
	/// Issue types (simplified HTML pass):
	///   MISPLACED_ANCHOR_EMPTY  — anchor tag with no visible text content
	///   ADJACENT_ANCHOR         — two consecutive anchors with no separating whitespace
	///                             (renamed from MISPLACED_ANCHOR_SPLIT;
	///                             gated on AnchorDetection.DetectAdjacent, default off)
	///   LIGATURE               — ligature character found in visible text
	///   QUOTE_UNMATCHED        — typographic opener with no closer before next opener
	///   QUOTE_WRONG_CLOSE      — opener and closer from different typographic systems
	///   QUOTE_SYSTEM_MIX       — page uses openers from more than one typographic system
	///   QUOTE_WRONG_OPEN       — typographic closer found with no matching opener
	///   QUOTE_MIXED_KIND       — straight and typographic quotes mixed in one pair
	///   BARE_TEXT_IN_CONTAINER — text directly inside container without block wrapper
	///   WORD_COLLISION         — inline element abuts bare text with no separator, merging
	///                            two words (lowercase→Uppercase seam, e.g. "BasismodulInhalte")
	///   SPLIT_WORD_ANCHOR      — anchor closes mid-word (stray letter after closing tag)
	///   CONTROL_CHARS_IN_CONTENT — invisible control / bidi / zero-width characters
	///                              found in &lt;title&gt; or &lt;meta&gt; content attribute
	/// </summary>
	public static partial class ContentQuality
	{

		// ── Public entry point ────────────────────────────────────────────────────────

		/// <summary>
		/// Scans all HTML files and writes ContentQualityIssues.log to
		/// <paramref name="outputPath"/>. Two parallel passes:
		///   1. Raw downloaded HTML — unwanted patterns and CMS template
		///      authoring defects (embedded BOM, invisible characters).
		///   2. Simplified HTML    — ligatures, language mismatch, control
		///      characters, quotes, bare text in containers, split-word
		///      anchors, misplaced anchors.
		/// Each file is independent so both passes parallelise safely.
		/// Degree of parallelism controlled by <paramref name="maxDegreeOfParallelism"/>
		/// (0 = auto-detect processor count).
		/// </summary>
		[ExcludeFromCodeCoverage(Justification =
			"Orchestration entry point: composes per-check helpers, parallel " +
			"file enumeration, and log writing. The individual Check* helpers " +
			"and IssueSuppressions.Apply have their own unit tests covering " +
			"detection and filtering logic. Testing Analyse itself would " +
			"require staging temp filesystems with HTML fixtures and asserting " +
			"on log output — integration territory, not unit testing.")]
		// [KEEP] Coverage-tool quirk: [ExcludeFromCodeCoverage] suppresses the
		// method body but NOT the LINQ closures it captures. The coverage
		// report for ContentQuality.cs counts each <>c__DisplayClass8_*
		// and <>c.<Analyse>b__8_* lambda as a separate 0%-covered function,
		// contributing ~190 uncovered blocks. The blocks are inside this
		// method (closures over local variables and Parallel.ForEach bodies)
		// and are therefore correctly excluded by intent — the tool simply
		// doesn't propagate the attribute to compiler-generated nested types.
		// File-level coverage % understates real test coverage as a result.
		internal static IReadOnlyList<QualityIssue> Analyse(
			string simplifiedDirectory,
			string downloadDirectory,
			string outputPath,
			ContentQualityConfig config,
			int maxDegreeOfParallelism,
			string filePattern,
			IReadOnlyList<string>? excludedUrls = null,
			IReadOnlyList<ContentUnwantedPattern>? unwantedPatterns = null,
			string? architectCsvBasePath = null,
			IReadOnlyDictionary<string, List<string>>? pageLanguageOverrides = null,
			string defaultLanguage = "")
		{
			var hasUnwantedPatterns = unwantedPatterns is { Count: > 0 };
			if (!config.IsEnabled && !hasUnwantedPatterns)
			{
				Logger.LogInfo("Content quality checks disabled — skipping.");
				ConsoleUi.WriteStepRow("Content quality", "disabled", dimmed: true);
				return System.Array.Empty<QualityIssue>();
			}

			var parallelOptions = new ParallelOptions
			{
				MaxDegreeOfParallelism = maxDegreeOfParallelism > 0
					? maxDegreeOfParallelism
					: Environment.ProcessorCount
			};

			var bag = new ConcurrentBag<QualityIssue>();
			// Separate bag for architect-class findings (CMS template authoring defects).
			// Written to a separate log so routing is explicit: editor-class issues go to
			// 10-content-quality-issues.log; architect-class issues go to the
			// 22-cms-template-authoring-defects dual-locale CSV pair.
			var architectBag = new ConcurrentBag<QualityIssue>();

			// ── Pass 1: raw downloaded HTML ────────────────────────────────────────────
			// Raw HTML preserves all attributes and meta tags that simplification strips.
			// Also: the architect-class CMS template defects check needs raw BYTES
			// (for embedded-BOM detection — UTF-8-aware string readers silently consume
			// a leading BOM, masking the bug we want to catch).
			// Add further raw-HTML checks here as new check methods are introduced.
			var hasArchitectCheck = config.CheckCmsTemplateAuthoringDefects
				&& !string.IsNullOrEmpty(architectCsvBasePath);
			var hasRawChecks = hasUnwantedPatterns || hasArchitectCheck
				|| config.MalformedHtml.DetectContentBeforeDoctype
				|| config.MalformedHtml.DetectHtmlParseErrors;
			if (hasRawChecks && Directory.Exists(downloadDirectory))
			{
				var downloadFiles = Directory.EnumerateFiles(downloadDirectory, filePattern).ToList();
				Parallel.ForEach(downloadFiles, parallelOptions, file =>
				{
					var filename = Path.GetFileName(file);

					if (excludedUrls is { Count: > 0 })
					{
						var url = CrawlIndex.LookUpUrlForFile(filename);
						if (excludedUrls.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase)))
						{
							return;
						}
					}

					byte[] rawBytes;
					try { rawBytes = File.ReadAllBytes(file); }
					catch (Exception ex)
					{
						Logger.LogWarning($"ContentQuality (raw HTML): could not read {filename}: {ex.Message}");
						return;
					}

					// Decode bytes to string for non-BOM-sensitive checks. We use
					// UTF8 here (matching prior behaviour); the BOM scan operates
					// on the raw bytes directly so it sees embedded BOMs that the
					// UTF-8 decoder would otherwise silently strip.
					string html;
					try { html = Encoding.UTF8.GetString(rawBytes); }
					catch (Exception ex)
					{
						Logger.LogWarning($"ContentQuality (raw HTML decode): could not decode {filename}: {ex.Message}");
						return;
					}

					if (hasUnwantedPatterns)
					{
						foreach (var issue in UnwantedPatterns.Check(filename, html, unwantedPatterns!, config))
						{
							bag.Add(issue);
						}
					}

					// MALFORMED_HTML — main log (10), not architect: server-side
					// structural defects, auto-promoted (not triaged).
					// Tier 1 (CONTENT_BEFORE_DOCTYPE) reads raw bytes so a leading
					// BOM and pre-doctype content are seen faithfully.
					if (config.MalformedHtml.DetectContentBeforeDoctype)
					{
						foreach (var issue in ContentBeforeDoctype.Check(filename, rawBytes))
						{
							bag.Add(issue);
						}
					}

					// Tier 2 (MALFORMED_HTML:<code>) bridges HtmlAgilityPack's
					// ParseErrors from a raw-HTML parse. One finding per (file,code)
					// with an occurrence count in the Detail.
					if (config.MalformedHtml.DetectHtmlParseErrors)
					{
						foreach (var issue in HtmlParseErrors.Check(filename, html,
							config.MalformedHtml.SuppressParseErrorCodes))
						{
							bag.Add(issue);
						}
					}

					if (hasArchitectCheck)
					{
						foreach (var issue in DefectTemplateAuthoring.CheckCmsTemplateAuthoringDefects(filename, rawBytes, html, config))
						{
							architectBag.Add(issue);
						}
					}
				});
			}

			// ── Pass 2: simplified HTML ────────────────────────────────────────────────
			var simplifiedFiles = Directory.EnumerateFiles(simplifiedDirectory, filePattern).ToList();
			Parallel.ForEach(simplifiedFiles, parallelOptions, file =>
			{
				var filename = Path.GetFileName(file);

				if (excludedUrls is { Count: > 0 })
				{
					var url = CrawlIndex.LookUpUrlForFile(filename);
					if (excludedUrls.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase)))
					{
						Logger.LogInfo($"Content quality skipped (excluded URL): {url}");
						return;
					}
				}

				string html;
				try { html = File.ReadAllText(file, Encoding.UTF8); }
				catch (Exception ex)
				{
					Logger.LogWarning($"ContentQuality: could not read {filename}: {ex.Message}");
					return;
				}

				var doc = new HtmlDocument();
				doc.LoadHtml(html);

				// Extract visible text nodes only — ignore attribute values.
				var textNodes = doc.DocumentNode
					.Descendants()
					.Where(n => n.NodeType == HtmlNodeType.Text
						&& n.ParentNode?.Name is not ("script" or "style" or "noscript"))
					.Select(n => n.InnerText)
					.Where(t => !string.IsNullOrWhiteSpace(t))
					.ToList();

				var pageText = string.Join(" ", textNodes);

				if (config.CheckLigatures)
				{
					foreach (var issue in Ligatures.Check(filename, pageText, config))
					{
						bag.Add(issue);
					}
				}

				if (config.CheckLanguageMismatch)
				{
					foreach (var issue in LanguageMismatch.Check(filename, doc))
					{
						bag.Add(issue);
					}
				}

				if (config.CheckControlCharsInContent)
				{
					foreach (var issue in ControlChars.Check(filename, doc, config))
					{
						bag.Add(issue);
					}
				}

				if (config.ResolvedCheckDecomposition != DecompositionMode.Off)
				{
					foreach (var issue in Decomposition.Check(filename, doc, config))
					{
						bag.Add(issue);
					}
				}

				if (config.CheckQuoteSystemMixing || config.CheckQuotePairing)
				{
					foreach (var issue in Quotes.Check(filename, doc, config, pageLanguageOverrides, defaultLanguage))
					{
						bag.Add(issue);
					}
				}

				if (config.CheckBareTextInContainers)
				{
					foreach (var issue in DefectBareText.CheckBareText(filename, doc, config))
					{
						bag.Add(issue);
					}
				}

				if (config.CheckWordCollisions)
				{
					foreach (var issue in DefectWordCollisions.CheckWordCollisions(filename, doc, config))
					{
						bag.Add(issue);
					}
				}

				if (config.CheckSplitWordAnchors)
				{
					foreach (var issue in WordSplits.Check(filename, html, config))
					{
						bag.Add(issue);
					}
				}

				if (config.CheckMisplacedAnchors)
				{
					foreach (var issue in MisplacedAnchors.Check(filename, doc, config))
					{
						bag.Add(issue);
					}
				}
			});

			var issues = bag.ToList();

			// Apply operator-configured suppression rules before
			// writing the log. Filtered findings are dropped entirely (no
			// parallel log — flip Enabled=false on a rule to audit what it
			// was hiding). Per-rule hit counts and zero-match warnings are
			// surfaced via the console summary below.
			var filtered = IssueSuppressions.Apply(issues, config.ContentQualityIssueSuppressions);
			WriteLog(outputPath, filtered.Emitted);

			LogContentQualitySummary(
				emittedCount: filtered.Emitted.Count,
				pageCount: simplifiedFiles.Count,
				outputPath: outputPath,
				rules: config.ContentQualityIssueSuppressions,
				ruleHits: filtered.RuleHits);

			// Architect log — separate file, separate audience (CMS template/architect team
			// vs. content editors). Routed independently so log handoffs stay clean.
			var architectIssues = architectBag.ToList();
			if (!string.IsNullOrEmpty(architectCsvBasePath))
			{
				WriteCsvLog(architectCsvBasePath, architectIssues);
				if (architectIssues.Count > 0)
				{
					Logger.LogWarning($"CMS template authoring defects: {architectIssues.Count} issue(s). " +
						$"See {Path.GetFileName(architectCsvBasePath)}{IssueLogWriter.CsvSemicolonSuffix} / " +
						$"{Path.GetFileName(architectCsvBasePath)}{IssueLogWriter.CsvCommaSuffix}.");
				}
				else if (config.CheckCmsTemplateAuthoringDefects)
				{
					Logger.LogInfo("CMS template authoring defects: no issues found.");
				}
				ConsoleUi.WriteStepRow(
					"CMS template defects",
					$"{architectIssues.Count} issue(s)",
					dimmed: architectIssues.Count == 0);
			}

			// Editor-class issues (what went to the main log, post-suppression) are returned so a
			// later pass can dedup against them — e.g. spell suppressing the twin of a WORD_COLLISION
			// it already reports. Returning the POST-suppression set is deliberate: a collision an
			// operator rule hid is NOT reported by CQ, so its spell twin must stay visible.
			return filtered.Emitted;
		}

		/// <summary>
		/// Appends translation issues detected during spell-checking to the content
		/// quality log. Called after spell-checking, not during HTML analysis.
		/// Applies the same suppression rules as the main Analyse path so that
		/// operator-configured rules cover POTENTIAL_TRANSLATION findings too.
		/// </summary>
		public static void WriteTranslationIssues(
			string outputPath,
			string filename,
			List<(string SourceLabel, string ContentExcerpt, string OtherLanguage)> issues,
			IReadOnlyList<IssueSuppressionRule>? suppressionRules = null)
		{
			if (issues.Count == 0)
			{
				return;
			}

			// Build QualityIssue records so suppression has the same shape it
			// uses in the main path (Type/Detail/Context substring matching).
			var qualityIssues = issues.Select(i => new QualityIssue(
				filename,
				"POTENTIAL_TRANSLATION",
				$"{i.SourceLabel} (passes {i.OtherLanguage} dictionary)",
				i.ContentExcerpt)).ToList();

			// Filter through operator suppression rules. Per-rule hits
			// are not surfaced here (this is called per-file from the spell-check
			// step and the per-file noise of a summary per file would be worse
			// than the lost detail) — the main Analyse run already established
			// the per-rule expectations via its own summary.
			var filtered = IssueSuppressions.Apply(qualityIssues, suppressionRules);
			if (filtered.Emitted.Count == 0)
			{
				return;
			}

			// [KEEP] Routed through IssueLogWriter so each field (especially
			// ContentExcerpt, which carries crawled meta-attribute content) is
			// sanitized — newlines / control chars / bidi controls in CMS-pasted
			// content used to corrupt the log into multi-physical-line records
			// that downstream readers misparsed. See IssueLogWriter for details.
			var records = filtered.Emitted.Select(i => new string?[]
			{
				i.Filename,
				i.IssueType,
				i.Detail,
				i.Context
			});
			IssueLogWriter.AppendMany(outputPath, IssueLogWriter.PipeDelimiter, records);
		}

		/// <summary>
		/// Emits the end-of-run summary for content-quality analysis: total
		/// emitted findings, total suppressed (if any), per-rule hit counts,
		/// and a warning for any enabled rule that matched zero findings
		/// (typo detection — operator added a rule expecting hits and got
		/// none, signal that the Value/Pages don't match the data).
		/// </summary>
		[ExcludeFromCodeCoverage(Justification =
			"Logger-output formatting. The static Logger is not injected, so " +
			"capturing output for unit assertion would require either an " +
			"architecture change or fragile file/console capture. The summary " +
			"text is operator-visible on every run; formatting regressions " +
			"surface immediately in the console.")]
		private static void LogContentQualitySummary(
			int emittedCount,
			int pageCount,
			string outputPath,
			IReadOnlyList<IssueSuppressionRule>? rules,
			IReadOnlyDictionary<int, int> ruleHits)
		{
			int suppressedTotal = 0;
			int activeRules = 0;
			if (rules is not null)
			{
				for (int i = 0; i < rules.Count; i++)
				{
					if (!rules[i].Enabled || string.IsNullOrEmpty(rules[i].Type))
					{
						continue;
					}

					activeRules++;
					if (ruleHits.TryGetValue(i, out var n))
					{
						suppressedTotal += n;
					}
				}
			}

			var logName = Path.GetFileName(outputPath);

			if (suppressedTotal > 0)
			{
				Logger.LogWarning($"Content quality: {emittedCount} issue(s) found across " +
					$"{pageCount} pages, {suppressedTotal} suppressed via {activeRules} rule(s). " +
					$"See {logName}.");
			}
			else if (emittedCount > 0)
			{
				Logger.LogWarning($"Content quality: {emittedCount} issue(s) found across " +
					$"{pageCount} pages. See {logName}.");
			}
			else
			{
				Logger.LogInfo($"Content quality: no issues found across {pageCount} pages.");
			}

			ConsoleUi.WriteStepRow(
				"Content quality",
				suppressedTotal > 0
					? $"{emittedCount} issue(s) · {suppressedTotal} suppressed"
					: $"{emittedCount} issue(s)");

			// Per-rule breakdown only if there were any active rules.
			if (rules is null || activeRules == 0)
			{
				return;
			}

			for (int i = 0; i < rules.Count; i++)
			{
				var r = rules[i];
				if (!r.Enabled || string.IsNullOrEmpty(r.Type))
				{
					continue;
				}

				var hits = ruleHits.TryGetValue(i, out var n) ? n : 0;
				var valuePart = string.IsNullOrEmpty(r.Value) ? "" : $" value='{r.Value}'";
				var pagesPart = r.Pages is { Count: > 0 } ? $" pages={r.Pages.Count}" : "";
				var commentPart = string.IsNullOrEmpty(r.Comment) ? "" : $" — {r.Comment}";
				Logger.LogInfo($"   {hits,6} suppressed: {r.Type}{valuePart}{pagesPart}{commentPart}");
			}

			// Zero-match warning: an enabled rule that suppressed nothing is
			// either a typo (operator misspelled Value or Pages glob) or stale
			// (site changed and the rule no longer applies). Surface both so
			// the operator can act before the rule rots in config.
			for (int i = 0; i < rules.Count; i++)
			{
				var r = rules[i];
				if (!r.Enabled || string.IsNullOrEmpty(r.Type))
				{
					continue;
				}

				var hits = ruleHits.TryGetValue(i, out var n) ? n : 0;
				if (hits != 0)
				{
					continue;
				}

				var commentPart = string.IsNullOrEmpty(r.Comment) ? "" : $" — {r.Comment}";
				Logger.LogWarning($"Suppression rule matched 0 findings — possibly stale or wrong: " +
					$"{r.Type} value='{r.Value}'{commentPart}");
			}
		}

		// Pre-allocated header row reused across calls
		// instead of constructing a fresh string?[] on every WriteLog invocation.
		private static readonly string?[] WriteLogHeader =
			["Filename", "IssueType", "Detail", "Context"];

		[ExcludeFromCodeCoverage(Justification =
			"Thin wrapper over IssueLogWriter.AppendMany with a header row and " +
			"deterministic sort. The interesting logic (field sanitization) " +
			"lives in IssueLogWriter and is tested there.")]
		private static void WriteLog(string outputPath, List<QualityIssue> issues)
		{
			// [KEEP] Routed through IssueLogWriter — Context can contain arbitrary
			// crawled block text including newlines and control characters that
			// would otherwise corrupt the pipe-delimited log format.
			// Header row is the first record; its fields contain no delimiters
			// or control characters, so it passes through sanitization unchanged.
			var records = new List<string?[]> { WriteLogHeader };
			records.AddRange(issues
				.OrderBy(i => i.Filename)
				.ThenBy(i => i.IssueType)
				.Select(i => new string?[] { i.Filename, i.IssueType, i.Detail, i.Context }));
			IssueLogWriter.Write(outputPath, IssueLogWriter.PipeDelimiter, records);
		}

		// 22-cms-template-authoring-defects: the architect-class bag, emitted as the
		// dual-locale CSV pair for operator review in Excel. Unlike WriteLog (log 10),
		// this enriches each row with the page Url (resolved from the downloaded
		// filename via CrawlIndex) between Filename and IssueType, so the architect
		// team can jump straight to the offending page. Sanitization/quoting is handled
		// by WriteCsvPair (same routing rationale as WriteLog — Context can carry
		// newlines/control chars).
		[ExcludeFromCodeCoverage(Justification =
			"Thin wrapper over IssueLogWriter.WriteCsvPair with a header row, URL " +
			"resolution and deterministic sort, mirroring WriteLog. The interesting " +
			"logic (field sanitization, RFC-4180 quoting, dual-locale emission) lives " +
			"in IssueLogWriter and is tested there.")]
		private static void WriteCsvLog(string csvBasePath, List<QualityIssue> issues)
		{
			var records = new List<string?[]>
			{
				new string?[] { "Filename", "Url", "IssueType", "Detail", "Context" }
			};
			records.AddRange(issues
				.OrderBy(i => i.Filename)
				.ThenBy(i => i.IssueType)
				.Select(i =>
				{
					// Mirror AssetQuality: blank the "error"/empty miss sentinel rather
					// than surface it to the operator.
					var url = CrawlIndex.LookUpUrlForFile(i.Filename);
					var urlForLog = string.IsNullOrEmpty(url) || url == "error" ? string.Empty : url;
					return new string?[] { i.Filename, urlForLog, i.IssueType, i.Detail, i.Context };
				}));
			IssueLogWriter.WriteCsvPair(csvBasePath, records);
		}
	}
}
