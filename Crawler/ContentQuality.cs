using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;

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
	///   QUOTE_AMBIGUOUS        — pairing flag the verification pass could resolve by
	///                            proximity matching; lower-confidence tier for review
	///   BARE_TEXT_IN_CONTAINER — text directly inside container without block wrapper
	///   WORD_COLLISION         — inline element abuts bare text with no separator, merging
	///                            two words (lowercase→Uppercase seam, e.g. "BasismodulInhalte")
	///   SPLIT_WORD_ANCHOR      — anchor closes mid-word (stray letter after closing tag)
	///   CONTROL_CHARS_IN_CONTENT — invisible control / bidi / zero-width characters
	///                              found in &lt;title&gt; or &lt;meta&gt; content attribute
	/// </summary>
	public static partial class ContentQuality
	{
		// ── Ligatures ────────────────────────────────────────────────────────────────

		private static readonly Dictionary<char, string> Ligatures = new()
		{
			{ '\uFB00', "ff  (U+FB00)" },
			{ '\uFB01', "fi  (U+FB01)" },
			{ '\uFB02', "fl  (U+FB02)" },
			{ '\uFB03', "ffi (U+FB03)" },
			{ '\uFB04', "ffl (U+FB04)" },
			{ '\uFB05', "ſt  (U+FB05)" },
			{ '\uFB06', "st  (U+FB06)" },
		};

		// ── Quote systems ─────────────────────────────────────────────────────────────
		// Each system has a set of openers and closers. A page mixes systems when it
		// uses openers from more than one system. A wrong closer is one from a different
		// system than the opener that started the current quote.

		private record QuoteSystem(string Name, HashSet<char> Openers, HashSet<char> Closers);

		private static readonly QuoteSystem[] QuoteSystems =
		[
			new("German-double",
				Openers:  ['\u201E'],          // „  U+201E DOUBLE LOW-9 QUOTATION MARK (99-Zeichen unten)
				Closers:  ['\u201C']),          // "  U+201C LEFT DOUBLE QUOTATION MARK (66-Zeichen oben) — correct German closer
				                               //    Reference: https://anfuehrungszeichen.de/

			// [KEEP] German-guillemet and French-guillemet use identical characters «»
			// — merging into one system prevents false QUOTE_SYSTEM_MIX detection.
			new("Guillemet",
				Openers:  ['\u00AB'],          // «  LEFT-POINTING DOUBLE ANGLE QUOTATION MARK
				Closers:  ['\u00BB']),          // »  RIGHT-POINTING DOUBLE ANGLE QUOTATION MARK

			// [KEEP] “ is BOTH the German-double closer AND the English-double opener.
			// German-double:  „....“  (U+201E opens, U+201C closes — 99/66 Zeichen)
			// English-double: “...”  (U+201C opens, U+201D closes — 66/99 Zeichen)
			// Mix detection checks openers only so the shared character causes no false
			// positives when per-block scoping is used.
			// Reference: https://anfuehrungszeichen.de/
			new("English-double",
				Openers:  ['“'],          // “  U+201C LEFT DOUBLE QUOTATION MARK (66-Zeichen oben)
				Closers:  ['”']),          // ”  U+201D RIGHT DOUBLE QUOTATION MARK (99-Zeichen oben)

			new("Heavy",
				Openers:  ['\u275D'],          // ❝
				Closers:  ['\u275E']),          // ❞

			new("German-single",
				Openers:  ['\u201A'],          // ‚
				Closers:  ['\u2019']),          // '

			new("English-single",
				Openers:  ['\u2018'],          // '
				Closers:  ['\u2019']),          // '

			new("Angle-single",
				Openers:  ['\u2039'],          // ‹
				Closers:  ['\u203A']),          // ›
		];

		// Flat sets for fast membership tests.
		private static readonly HashSet<char> AllOpeners =
			[.. QuoteSystems.SelectMany(s => s.Openers)];

		private static readonly HashSet<char> AllClosers =
			[.. QuoteSystems.SelectMany(s => s.Closers)];

		// Straight quotes — ambiguous, used as proxy for wrong opener detection.
		private const char StraightDouble = '\u0022';
		private const char StraightSingle = '\u0027';

		// ── Issue record ──────────────────────────────────────────────────────────────

		public record QualityIssue(
			string Filename,
			string IssueType,
			string Detail,
			string Context);

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
		public static IReadOnlyList<QualityIssue> Analyse(
			string simplifiedDirectory,
			string downloadDirectory,
			string outputPath,
			ContentQualityConfig config,
			int maxDegreeOfParallelism,
			string filePattern,
			IReadOnlyList<string>? excludedUrls = null,
			IReadOnlyList<ContentUnwantedPattern>? unwantedPatterns = null,
			string? architectOutputPath = null)
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
			// 10-content-quality-issues.log; architect-class issues go to
			// 22-cms-template-authoring-defects.log.
			var architectBag = new ConcurrentBag<QualityIssue>();

			// ── Pass 1: raw downloaded HTML ────────────────────────────────────────────
			// Raw HTML preserves all attributes and meta tags that simplification strips.
			// Also: the architect-class CMS template defects check needs raw BYTES
			// (for embedded-BOM detection — UTF-8-aware string readers silently consume
			// a leading BOM, masking the bug we want to catch).
			// Add further raw-HTML checks here as new check methods are introduced.
			var hasArchitectCheck = config.CheckCmsTemplateAuthoringDefects
				&& !string.IsNullOrEmpty(architectOutputPath);
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
						foreach (var issue in CheckUnwantedPatterns(filename, html, unwantedPatterns!, config))
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
						foreach (var issue in CheckContentBeforeDoctype(filename, rawBytes))
						{
							bag.Add(issue);
						}
					}

					// Tier 2 (MALFORMED_HTML:<code>) bridges HtmlAgilityPack's
					// ParseErrors from a raw-HTML parse. One finding per (file,code)
					// with an occurrence count in the Detail.
					if (config.MalformedHtml.DetectHtmlParseErrors)
					{
						foreach (var issue in CheckHtmlParseErrors(filename, html,
							config.MalformedHtml.SuppressParseErrorCodes))
						{
							bag.Add(issue);
						}
					}

					if (hasArchitectCheck)
					{
						foreach (var issue in CheckCmsTemplateAuthoringDefects(filename, rawBytes, html, config))
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
					foreach (var issue in CheckLigatures(filename, pageText, config))
					{
						bag.Add(issue);
					}
				}

				if (config.CheckLanguageMismatch)
				{
					foreach (var issue in CheckLanguageMismatch(filename, doc))
					{
						bag.Add(issue);
					}
				}

				if (config.CheckControlCharsInContent)
				{
					foreach (var issue in CheckControlCharsInContent(filename, doc))
					{
						bag.Add(issue);
					}
				}

				if (config.CheckQuoteSystemMixing || config.CheckQuotePairing)
				{
					foreach (var issue in CheckQuotes(filename, doc, config))
					{
						bag.Add(issue);
					}
				}

				if (config.CheckBareTextInContainers)
				{
					foreach (var issue in CheckBareText(filename, doc, config))
					{
						bag.Add(issue);
					}
				}

				if (config.CheckWordCollisions)
				{
					foreach (var issue in CheckWordCollisions(filename, doc, config))
					{
						bag.Add(issue);
					}
				}

				if (config.CheckSplitWordAnchors)
				{
					foreach (var issue in CheckSplitWordAnchors(filename, html, config))
					{
						bag.Add(issue);
					}
				}

				if (config.CheckMisplacedAnchors)
				{
					foreach (var issue in CheckMisplacedAnchors(filename, doc, config))
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
			if (!string.IsNullOrEmpty(architectOutputPath))
			{
				WriteLog(architectOutputPath, architectIssues);
				if (architectIssues.Count > 0)
				{
					Logger.LogWarning($"CMS template authoring defects: {architectIssues.Count} issue(s). " +
						$"See {Path.GetFileName(architectOutputPath)}.");
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

		// ── Bare text in container check ──────────────────────────────────────────

		/// <summary>
		/// Flags text nodes that are direct children of container elements
		/// (div, section, article etc.) without being wrapped in a block element.
		/// This is an HTML authoring defect — bare text in containers causes
		/// rendering inconsistencies and accessibility issues.
		/// </summary>
		internal static IEnumerable<QualityIssue> CheckBareText(
			string filename, HtmlDocument doc, ContentQualityConfig config)
		{
			var containers = config.ContentQualityContainerElements
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			foreach (var node in doc.DocumentNode.Descendants()
				.Where(n => n.NodeType == HtmlNodeType.Element
					&& containers.Contains(n.Name)))
			{
				foreach (var child in node.ChildNodes)
				{
					if (child.NodeType != HtmlNodeType.Text)
					{
						continue;
					}

					var text = WebUtility.HtmlDecode(child.InnerText).Trim();
					if (string.IsNullOrEmpty(text))
					{
						continue;
					}

					// Suppress BARE_TEXT when the post-trim text contains
					// no visible content — i.e. every remaining character is
					// either whitespace or an architect-class invisible (ZWSPs,
					// ZWNJs, BOMs, C0/C1 controls, bidi marks, etc.). Trim()
					// removes leading/trailing whitespace but not interior
					// whitespace between invisibles (e.g. "   \u200B  \u200C"
					// trims to "\u200B  \u200C" with interior spaces intact),
					// and Trim() never removes the invisibles themselves. So a
					// text node containing only invisibles plus filler
					// whitespace previously fired both BARE_TEXT_IN_CONTAINER
					// and INVISIBLE_CHAR_IN_BODY on the same content.
					// INVISIBLE_CHAR names the actual problem (the embedded
					// codepoint) and is the more useful of the two findings;
					// BARE_TEXT here adds noise without new information. Pure
					// whitespace was already excluded by the IsNullOrEmpty
					// check above (Trim of all-whitespace = empty).
					bool noVisibleContent = true;
					for (int i = 0; i < text.Length; i++)
					{
						var ch = text[i];
						if (char.IsWhiteSpace(ch) || IsArchitectClassInvisible(ch))
						{
							continue;
						}

						noVisibleContent = false;
						break;
					}
					if (noVisibleContent)
					{
						continue;
					}

					var textExcerpt = text.Length > config.ContentQualityExcerptRadius
						? text[..config.ContentQualityExcerptRadius] + "…"
						: text;

					// Prepend the container's start tag so the operator can identify
					// which XPath strip to write without having to open the source HTML
					// and grep for the text. Format: [<div class="...">] excerpt-text
					// Without this context, repeating chrome (e.g. caps-lock warnings,
					// related-link headers) that fires on hundreds of pages requires
					// per-text manual lookup.
					var excerpt = $"[{FormatContainerStartTag(node)}] {textExcerpt}";

					yield return new QualityIssue(
						filename,
						"BARE_TEXT_IN_CONTAINER",
						$"Text directly inside <{node.Name}> without block wrapper",
						excerpt);
				}
			}
		}

		// ── Word collision at inline-element seam ──────────────────────────────────

		/// <summary>
		/// Inline phrasing elements that add no implicit whitespace at their boundary —
		/// the same set the spell harvester glues across (see DomTraverser.InlinePhrasingGlue).
		/// Text on either side of such a boundary touches with no separator, exactly as the
		/// browser renders it.
		/// </summary>
		private static readonly HashSet<string> InlinePhrasingElements =
			new(StringComparer.OrdinalIgnoreCase)
			{
				"b", "i", "em", "strong", "mark", "small", "u", "s", "span", "wbr",
			};

		/// <summary>
		/// Flags word collisions where an inline phrasing element (e.g. a CMS editor's
		/// <c>&lt;span class="h2"&gt;</c> used to fake a heading size) abuts bare sibling
		/// text with no whitespace at the seam, so two words merge into one
		/// (e.g. <c>&lt;span&gt;Basismodul&lt;/span&gt;Inhalte</c> → "BasismodulInhalte").
		/// CSS (display:block) often hides the visual mash, so this is invisible to visual
		/// QA — but the DOM text, the accessibility tree, and the spell harvester all see
		/// the merged token. The high-precision signal is a lowercase→Uppercase transition
		/// straddling the seam: natural words carry no internal capital, whereas a true
		/// mid-word emphasis (<c>&lt;b&gt;bezah&lt;/b&gt;len</c> → "bezahlen") is
		/// lowercase→lowercase and is correctly left alone.
		/// </summary>
		internal static IEnumerable<QualityIssue> CheckWordCollisions(
			string filename, HtmlDocument doc, ContentQualityConfig config)
		{
			foreach (var node in doc.DocumentNode.Descendants()
				.Where(n => n.NodeType == HtmlNodeType.Element
					&& InlinePhrasingElements.Contains(n.Name)))
			{
				var inner = WebUtility.HtmlDecode(node.InnerText);
				if (string.IsNullOrEmpty(inner))
				{
					continue;
				}

				// Trailing seam: <span>…Basismodul</span>Inhalte…
				// left char = inner's last char; right char = next text sibling's first char.
				if (node.NextSibling is { NodeType: HtmlNodeType.Text } nextText)
				{
					var right = WebUtility.HtmlDecode(nextText.InnerText);
					if (right.Length > 0
						&& IsLowerUpperSeam(inner[^1], right[0]))
					{
						// Context is the RAW html around the seam (e.g.
						// "<span class=\"h2\">Basismodul</span>Inhalte des Moduls…"),
						// so triage can show actual markup and highlight WORD1/</tag>/WORD2.
						yield return new QualityIssue(
							filename,
							"WORD_COLLISION",
							$"Inline <{node.Name}> abuts bare text without separator — words merge",
							node.OuterHtml + CapExcerpt(nextText.InnerText, config));
						continue;   // one finding per element; don't also test the leading seam
					}
				}

				// Leading seam: …Inhalte<span>Basismodul</span>
				// left char = previous text sibling's last char; right char = inner's first char.
				if (node.PreviousSibling is { NodeType: HtmlNodeType.Text } prevText)
				{
					var left = WebUtility.HtmlDecode(prevText.InnerText);
					if (left.Length > 0
						&& IsLowerUpperSeam(left[^1], inner[0]))
					{
						yield return new QualityIssue(
							filename,
							"WORD_COLLISION",
							$"Bare text abuts inline <{node.Name}> without separator — words merge",
							CapExcerptEnd(prevText.InnerText, config) + node.OuterHtml);
					}
				}
			}
		}

		/// <summary>
		/// True when the seam straddles a lowercase letter immediately followed by an
		/// uppercase letter — the high-precision word-collision signal. Whitespace on
		/// either side breaks the seam (the characters compared are the literal adjacent
		/// ones, so a leading/trailing space yields a non-letter and returns false).
		/// </summary>
		private static bool IsLowerUpperSeam(char left, char right)
			=> char.IsLower(left) && char.IsUpper(right);

		/// <summary>Caps to the leading <c>ContentQualityExcerptRadius</c> chars (head kept).</summary>
		private static string CapExcerpt(string text, ContentQualityConfig config)
			=> text.Length > config.ContentQualityExcerptRadius
				? text[..config.ContentQualityExcerptRadius] + "…"
				: text;

		/// <summary>Caps to the trailing <c>ContentQualityExcerptRadius</c> chars (tail kept) — for the leading-seam fragment.</summary>
		private static string CapExcerptEnd(string text, ContentQualityConfig config)
			=> text.Length > config.ContentQualityExcerptRadius
				? "…" + text[^config.ContentQualityExcerptRadius..]
				: text;

		/// <summary>
		/// Builds an inspection-friendly representation of an element's start tag.
		/// Includes the tag name and a curated subset of attributes most useful for
		/// writing an XPath strip — class, id, role, data-component — in that order
		/// of preference. Empty attributes are skipped. Output is capped at
		/// <see cref="ContainerTagMaxLength"/> characters to keep log lines readable
		/// even when class lists are pathological.
		///
		/// Examples:
		///   <c>&lt;div class="caps-lock-warning"&gt;</c>
		///   <c>&lt;section id="related"&gt;</c>
		///   <c>&lt;div&gt;</c>  (no informative attributes)
		/// </summary>
		internal static string FormatContainerStartTag(HtmlNode node)
		{
			var sb = new StringBuilder();
			sb.Append('<').Append(node.Name);

			foreach (var attrName in ContainerTagAttributesOfInterest)
			{
				var value = node.GetAttributeValue(attrName, string.Empty);
				if (string.IsNullOrEmpty(value))
				{
					continue;
				}

				sb.Append(' ').Append(attrName).Append("=\"").Append(value).Append('"');
			}

			sb.Append('>');

			var result = sb.ToString();
			if (result.Length > ContainerTagMaxLength)
			{
				result = result[..(ContainerTagMaxLength - 2)] + "…>";
			}

			return result;
		}

		private static readonly string[] ContainerTagAttributesOfInterest =
			["class", "id", "role", "data-component"];

		private const int ContainerTagMaxLength = 200;

		// ── Malformed-HTML checks (structural well-formedness, raw HTML) ──────────

		/// <summary>
		/// Tier-1 MALFORMED_HTML check (<c>CONTENT_BEFORE_DOCTYPE</c>): flags
		/// non-whitespace content before the document's opening
		/// &lt;!doctype&gt;/&lt;html&gt;/&lt;?xml&gt; token (after an optional leading
		/// UTF-8 BOM), on raw bytes. Composite Word
		/// <c>MALFORMED_HTML:CONTENT_BEFORE_DOCTYPE</c>, log 10, auto-promoted.
		///
		/// Why raw bytes, not the parsed DOM: HtmlAgilityPack is lenient and folds
		/// leading stray content into the tree, so <c>HtmlDocument.ParseErrors</c>
		/// does not surface it. A direct check on the leading bytes is the only
		/// reliable detector. When this fires it is a backend templating /
		/// error-injection bug — always a server-side fix, never editorial, which
		/// is why it is not interactively triaged.
		///
		/// One finding per file: a whole-document property, not per-occurrence.
		/// </summary>
		internal static IEnumerable<QualityIssue> CheckContentBeforeDoctype(
			string filename, byte[] rawBytes)
		{
			if (rawBytes is null || rawBytes.Length == 0)
			{
				yield break;
			}

			// Skip a single legitimate leading UTF-8 BOM (EF BB BF at offset 0).
			int i = 0;
			if (rawBytes.Length >= 3
				&& rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF)
			{
				i = 3;
			}

			// Skip ASCII whitespace (space, tab, CR, LF) — leading whitespace
			// before the doctype is benign and common.
			while (i < rawBytes.Length)
			{
				byte b = rawBytes[i];
				if (b == 0x20 || b == 0x09 || b == 0x0D || b == 0x0A) { i++; continue; }
				break;
			}

			// All-whitespace (or empty after BOM) file: nothing to flag here.
			// An empty/near-empty body is a different defect class (download
			// robustness — see gotchas) and is not this check's concern.
			if (i >= rawBytes.Length)
			{
				yield break;
			}

			// Decode a bounded window from the first non-whitespace byte so the
			// prefix test sees text, not bytes. The longest token we test for is
			// "<!doctype" (9 chars); a 64-byte window is ample and keeps the
			// excerpt cheap even on a multi-megabyte page.
			const int probeBytes = 64;
			int probeLen = Math.Min(probeBytes, rawBytes.Length - i);

			// NB: a yield-return method may not contain `yield` inside a
			// try-with-catch (CS1626), so decode into a nullable local here and
			// branch after the try rather than yielding from the catch.
			string? lead = null;
			try { lead = Encoding.UTF8.GetString(rawBytes, i, probeLen); }
			catch { /* undecodable lead — not our defect to classify */ }
			if (lead is null)
			{
				yield break;
			}

			// The byte-level skip above already consumed leading ASCII
			// whitespace, so `lead` begins at the first significant byte. Do NOT
			// TrimStart here: a non-ASCII "whitespace" lookalike (e.g. NBSP
			// U+00A0, U+2028) before the doctype is genuine pre-doctype content
			// and must stay flaggable, and trimming would also desync the
			// reported byte offset from the evidence.
			bool startsWell =
				lead.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
				|| lead.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
				|| lead.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);

			if (startsWell)
			{
				yield break;
			}

			// Offending lead: render with operator-friendly invisible markers and
			// cap at the 250-char excerpt limit used for invisible-marker output.
			var evidence = TruncateForLog(lead);
			const int maxExcerpt = 250;
			if (evidence.Length > maxExcerpt)
			{
				evidence = evidence[..(maxExcerpt - 1)] + "…";
			}

			yield return new QualityIssue(
				filename,
				"MALFORMED_HTML",
				"CONTENT_BEFORE_DOCTYPE",
				$"offset {i}: {evidence}");
		}

		/// <summary>
		/// Tier-2 MALFORMED_HTML check (<c>MALFORMED_HTML:&lt;code&gt;</c>): bridges
		/// HtmlAgilityPack's <c>HtmlDocument.ParseErrors</c> from a raw-HTML parse
		/// into findings. One finding per (file, error code): the code goes in
		/// Detail (so the promoted Word is MALFORMED_HTML:&lt;code&gt;, a stable Key)
		/// and the occurrence count goes in the Context only (e.g.
		/// <c>TagNotClosed (3 occurrence(s))</c>) so a run-to-run-varying count
		/// never churns the Key. Composite Word <c>MALFORMED_HTML:&lt;code&gt;</c>,
		/// log 10, auto-promoted.
		///
		/// Deliberately conservative on what it emits: only the parser-error
		/// <c>Code</c> and a count. HAP's free-text <c>Reason</c> and
		/// <c>SourceText</c> fields are NOT used — they are unbounded and can carry
		/// control characters / newlines that would need sanitising, and they add
		/// no triage value here (the code names the defect; the page is the fix
		/// target). No code whitelist at current corpus scale (only a handful of
		/// pages trip any parse error); gate the whole check off via
		/// <see cref="MalformedHtmlConfig.DetectHtmlParseErrors"/> if a noisier
		/// site floods the log.
		///
		/// Parses raw HTML (not simplified) so the errors reflect what the server
		/// actually emitted, before any stripping/rewriting.
		/// </summary>
		internal static IEnumerable<QualityIssue> CheckHtmlParseErrors(
			string filename, string html, IReadOnlyCollection<string>? suppressCodes = null)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);

			var parseErrors = doc.ParseErrors;
			if (parseErrors is null)
			{
				yield break;
			}

			var suppressed = suppressCodes is { Count: > 0 }
				? new HashSet<string>(suppressCodes, StringComparer.OrdinalIgnoreCase)
				: null;

			// Aggregate by error code → occurrence count. Preserve first-seen
			// order so output is deterministic across runs (byte-stable logs).
			var counts = new Dictionary<string, int>(StringComparer.Ordinal);
			var order = new List<string>();
			foreach (var err in parseErrors)
			{
				var code = err.Code.ToString();
				if (suppressed is not null && suppressed.Contains(code))
				{
					continue;
				}

				if (!counts.TryGetValue(code, out var n))
				{
					counts[code] = 1;
					order.Add(code);
				}
				else
				{
					counts[code] = n + 1;
				}
			}

			foreach (var code in order)
			{
				var n = counts[code];
				yield return new QualityIssue(
					filename,
					"MALFORMED_HTML",
					code,
					$"{code} ({n} occurrence(s))");
			}
		}

		/// <summary>
		/// Runs the architect-class CMS template authoring defect checks against the
		/// raw downloaded HTML. Emits three distinct IssueTypes routed to the architect
		/// log (22-cms-template-authoring-defects.log):
		///
		///   EMBEDDED_BOM_IN_BODY
		///     UTF-8 BOM (U+FEFF, byte sequence EF BB BF) appearing at any position
		///     other than offset 0. A leading BOM at offset 0 is the legitimate
		///     UTF-8 signature and is NOT flagged. Each subsequent BOM is an
		///     embedded BOM — almost always the residue of concatenating multiple
		///     UTF-8-with-signature template fragments without stripping the
		///     residual signature bytes. One finding per file with occurrence count.
		///
		///   INVISIBLE_CHAR_IN_BODY
		///     Zero-width characters, bidi control marks, line/paragraph separators,
		///     and C0/C1 control codes in body text whose parent element is NOT in
		///     <see cref="ContentQualityConfig.ContentQualityBlockElements"/>. The
		///     parent-element scope filter routes findings: invisibles inside
		///     p/h*/li/td/th are editor-paste-class (caught elsewhere or untreated);
		///     invisibles outside those are template-emitted (architect-class).
		///     One finding per (file, codepoint) — never per occurrence — with
		///     occurrence count and first container surfaced in the Detail/Excerpt.
		///
		///   WORD_SPLIT_BY_FORMATTING
		///     A word fractured for looks: several consecutive words each have
		///     their first letter wrapped in its own phrasing element (e.g.
		///     &lt;b&gt;I&lt;/b&gt;nternational &lt;b&gt;B&lt;/b&gt;ank
		///     &lt;b&gt;A&lt;/b&gt;ccount), which splits the word apart for screen
		///     readers and search engines. Flagged when THREE OR MORE such
		///     single-letter phrasing elements, each glued (no whitespace) to a
		///     lowercase word continuation, occur within ONE block. The >=3
		///     threshold passes over a lone drop-cap; the glued-lowercase condition
		///     passes over math (&lt;i&gt;x&lt;/i&gt; + &lt;i&gt;y&lt;/i&gt;) and
		///     single-letter emphasis. Deliberately distinct in NAME and (architect)
		///     LOG from SPLIT_WORD_ANCHOR: an anchor closing mid-word is a
		///     FUNCTIONAL link defect (main log), per-letter formatting is an
		///     AESTHETIC markup defect — different fix, different reader workflow,
		///     so they are never bucketed together. One finding per block.
		///
		/// Why raw bytes for BOM: the UTF-8 string decoder silently consumes a
		/// leading BOM and may also normalise embedded ones, masking the very bug
		/// we want to detect. Byte-level scan sees every occurrence faithfully.
		/// </summary>
		internal static IEnumerable<QualityIssue> CheckCmsTemplateAuthoringDefects(
			string filename, byte[] rawBytes, string html, ContentQualityConfig config)
		{
			// ── EMBEDDED_BOM_IN_BODY ──────────────────────────────────────────
			// UTF-8 BOM byte sequence: EF BB BF.
			var bomOffsets = FindUtf8BomOffsets(rawBytes);
			if (bomOffsets.Count > 0)
			{
				bool leadingBomPresent = bomOffsets[0] == 0;
				var embeddedOffsets = leadingBomPresent
					? bomOffsets.GetRange(1, bomOffsets.Count - 1)
					: bomOffsets;

				if (embeddedOffsets.Count > 0)
				{
					var detail = leadingBomPresent
						? $"BOM (U+FEFF) found at {embeddedOffsets.Count} embedded position(s); file also has the legitimate leading BOM at offset 0."
						: $"BOM (U+FEFF) found at {embeddedOffsets.Count} embedded position(s); no leading BOM.";

					// Excerpt = first embedded offset with surrounding bytes rendered as text.
					var firstOffset = embeddedOffsets[0];
					yield return new QualityIssue(
						filename,
						"EMBEDDED_BOM_IN_BODY",
						detail,
						BuildBomExcerpt(rawBytes, firstOffset));
				}
			}

			// ── INVISIBLE_CHAR_IN_BODY ────────────────────────────────────────
			// Walk DOM text nodes; restrict to text whose parent element is NOT
			// in ContentQualityBlockElements (editor-authored prose) and NOT in
			// the head/script/style/noscript skip set.
			var blockElements = config.ContentQualityBlockElements
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			// Aggregate findings: one per (codepoint) for this file. The value is
			// (count, first container start tag, first excerpt) — used to build the
			// single finding emitted per codepoint with occurrence count surfaced.
			var perCodepoint = new Dictionary<int, (int Count, string FirstContainerTag, string FirstExcerpt, string Name)>();

			var doc = new HtmlDocument();
			doc.LoadHtml(html);

			foreach (var textNode in doc.DocumentNode.Descendants()
				.Where(n => n.NodeType == HtmlNodeType.Text))
			{
				var parent = textNode.ParentNode;
				if (parent == null)
				{
					continue;
				}

				// Skip head/script/style/noscript subtrees.
				if (IsInsideArchitectScopeSkipAncestor(parent))
				{
					continue;
				}

				// Skip editor-class prose containers.
				if (blockElements.Contains(parent.Name))
				{
					continue;
				}

				var text = textNode.InnerText;
				if (string.IsNullOrEmpty(text))
				{
					continue;
				}

				foreach (var ch in text)
				{
					if (!IsArchitectClassInvisible(ch))
					{
						continue;
					}

					var name = NameArchitectInvisible(ch);

					if (perCodepoint.TryGetValue(ch, out var existing))
					{
						perCodepoint[ch] = (existing.Count + 1, existing.FirstContainerTag, existing.FirstExcerpt, existing.Name);
					}
					else
					{
						var containerTag = FormatContainerStartTag(parent);
						var excerpt = $"[{containerTag}] {RenderTextWithInvisibleMarkers(text)}";
						perCodepoint[ch] = (1, containerTag, excerpt, name);
					}
				}
			}

			foreach (var (codepoint, agg) in perCodepoint)
			{
				yield return new QualityIssue(
					filename,
					"INVISIBLE_CHAR_IN_BODY",
					$"{agg.Name} found at {agg.Count} position(s) in non-editorial container content (first inside {agg.FirstContainerTag}).",
					agg.FirstExcerpt);
			}

			// ── WORD_SPLIT_BY_FORMATTING ──────────────────────────────────────
			// A word fractured for looks: each of several consecutive words has its
			// first letter wrapped in its own phrasing element (<b>I</b>nternational
			// <b>B</b>ank <b>A</b>ccount …), splitting the word for screen readers and
			// search engines. Counted per block; emitted once per block at >=3. The
			// same DOM is reused (no second parse). Blocks recorded in first-seen
			// document order so the log is deterministic.
			var splitBlocks = new List<HtmlNode>();
			var splitCounts = new Dictionary<HtmlNode, int>();

			foreach (var el in doc.DocumentNode.Descendants()
				.Where(n => n.NodeType == HtmlNodeType.Element && WordSplitPhrasingTags.Contains(n.Name)))
			{
				if (IsInsideArchitectScopeSkipAncestor(el))
				{
					continue;
				}

				// The element's own text must be exactly one letter (a bolded initial),
				// ignoring any incidental whitespace inside the tag (e.g. "<b> A</b>").
				var letter = (el.InnerText ?? string.Empty).Trim();
				if (letter.Length != 1 || !char.IsLetter(letter[0]))
				{
					continue;
				}

				// The continuation must be glued: the immediately following text node
				// begins, with NO whitespace, with a lowercase letter (the rest of the
				// word). This excludes math ("<i>x</i> + …") and lone emphasis.
				var next = el.NextSibling;
				if (next == null || next.NodeType != HtmlNodeType.Text)
				{
					continue;
				}

				var tail = next.InnerText;
				if (string.IsNullOrEmpty(tail) || char.IsWhiteSpace(tail[0]) || !char.IsLower(tail[0]))
				{
					continue;
				}

				var parent = el.ParentNode;
				if (parent == null)
				{
					continue;
				}

				if (!splitCounts.ContainsKey(parent))
				{
					splitBlocks.Add(parent);
					splitCounts[parent] = 0;
				}

				splitCounts[parent]++;
			}

			foreach (var block in splitBlocks)
			{
				if (splitCounts[block] < WordSplitByFormattingThreshold)
				{
					continue;
				}

				// Context shows the REASSEMBLED block text (InnerText glues the phrasing
				// children back into the words a reader sees), windowed to the excerpt radius.
				var blockText = System.Net.WebUtility.HtmlDecode(block.InnerText).Trim();
				var excerptText = blockText.Length > config.ContentQualityExcerptRadius
					? blockText[..config.ContentQualityExcerptRadius] + "…"
					: blockText;

				yield return new QualityIssue(
					filename,
					"WORD_SPLIT_BY_FORMATTING",
					"The first letters of several words in a row are each formatted on their own (for example, "
						+ "each letter bolded), which splits the words apart for screen readers and search engines. "
						+ "To highlight an abbreviation, wrap the full term in an <abbr> tag instead.",
					$"[{FormatContainerStartTag(block)}] {excerptText}");
			}
		}

		/// <summary>
		/// Phrasing/formatting elements counted by WORD_SPLIT_BY_FORMATTING — the "make it
		/// pretty" wrappers an editor uses to style a single letter. Mirrors the safe-core
		/// glue set used by the spell traverser (minus the void &lt;wbr&gt;, which holds no
		/// letter). Case-insensitive.
		/// </summary>
		private static readonly HashSet<string> WordSplitPhrasingTags =
			new(StringComparer.OrdinalIgnoreCase)
			{
				"b", "i", "em", "strong", "mark", "small", "u", "s", "span",
			};

		/// <summary>
		/// Minimum single-letter phrasing elements (each glued to a lowercase continuation)
		/// within one block before WORD_SPLIT_BY_FORMATTING fires. Three separates a systemic
		/// per-letter authoring pattern from a lone drop-cap or one-off emphasis.
		/// </summary>
		private const int WordSplitByFormattingThreshold = 3;

		/// <summary>
		/// Returns byte offsets of UTF-8 BOM occurrences (EF BB BF) in <paramref name="bytes"/>.
		/// </summary>
		internal static List<int> FindUtf8BomOffsets(byte[] bytes)
		{
			var offsets = new List<int>();
			if (bytes == null || bytes.Length < 3)
			{
				return offsets;
			}

			for (int i = 0; i <= bytes.Length - 3; i++)
			{
				if (bytes[i] == 0xEF && bytes[i + 1] == 0xBB && bytes[i + 2] == 0xBF)
				{
					offsets.Add(i);
					i += 2; // skip past this BOM; next iteration's i++ moves to next byte after
				}
			}
			return offsets;
		}

		/// <summary>
		/// True for character codepoints classed as architect-emitted invisibles:
		/// zero-widths, bidi controls, line/paragraph separators, C0/C1 controls,
		/// and BOM/ZWNBSP when it appears outside its leading-byte role. Excludes
		/// CR/LF/TAB which are normal whitespace in HTML source.
		/// </summary>
		internal static bool IsArchitectClassInvisible(char ch)
		{
			if (ch == '\r' || ch == '\n' || ch == '\t')
			{
				return false;
			}

			if (ch < 0x20)
			{
				return true;                              // C0 controls
			}

			if (ch >= 0x80 && ch <= 0x9F)
			{
				return true;               // C1 controls
			}

			if (ch == '\u200B' || ch == '\u200C' || ch == '\u200D')
			{
				return true; // ZWSP/ZWNJ/ZWJ
			}

			if (ch == '\u2060')
			{
				return true;                         // WJ
			}

			if (ch == '\uFEFF')
			{
				return true;                         // ZWNBSP / embedded BOM
			}

			if (ch >= '\u202A' && ch <= '\u202E')
			{
				return true;       // bidi controls
			}

			if (ch >= '\u2066' && ch <= '\u2069')
			{
				return true;       // bidi isolates
			}

			if (ch == '\u2028' || ch == '\u2029')
			{
				return true;       // line/paragraph separators
			}

			return false;
		}

		/// <summary>Human-readable name for an architect-class invisible codepoint.</summary>
		internal static string NameArchitectInvisible(char ch)
		{
			if (ch == '\u200B')
			{
				return "ZWSP (U+200B)";
			}

			if (ch == '\u200C')
			{
				return "ZWNJ (U+200C)";
			}

			if (ch == '\u200D')
			{
				return "ZWJ (U+200D)";
			}

			if (ch == '\u2060')
			{
				return "WJ (U+2060)";
			}

			if (ch == '\uFEFF')
			{
				return "ZWNBSP/BOM (U+FEFF)";
			}

			if (ch == '\u2028')
			{
				return "LINE SEPARATOR (U+2028)";
			}

			if (ch == '\u2029')
			{
				return "PARAGRAPH SEPARATOR (U+2029)";
			}

			if (ch >= '\u202A' && ch <= '\u202E')
			{
				return $"bidi control (U+{(int)ch:X4})";
			}

			if (ch >= '\u2066' && ch <= '\u2069')
			{
				return $"bidi isolate (U+{(int)ch:X4})";
			}

			if (ch < 0x20)
			{
				return $"C0 control (U+{(int)ch:X4})";
			}

			if (ch >= 0x80 && ch <= 0x9F)
			{
				return $"C1 control (U+{(int)ch:X4})";
			}

			return $"invisible (U+{(int)ch:X4})";
		}

		private static readonly HashSet<string> ArchitectScopeSkipAncestors =
			new(StringComparer.OrdinalIgnoreCase) { "head", "title", "meta", "script", "style", "noscript" };

		/// <summary>
		/// True if <paramref name="node"/> or any ancestor is in head / title / meta /
		/// script / style / noscript. Used to suppress architect-scope invisible-char
		/// findings inside head metadata (covered by CONTROL_CHARS_IN_CONTENT, editor-
		/// targeted) and inside script/style/noscript (raw content, not body prose).
		/// </summary>
		internal static bool IsInsideArchitectScopeSkipAncestor(HtmlNode node)
		{
			for (var cur = node; cur != null; cur = cur.ParentNode)
			{
				if (ArchitectScopeSkipAncestors.Contains(cur.Name))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Renders a text string with architect-class invisible characters replaced by
		/// readable markers (e.g. [BOM U+FEFF], [ZWSP U+200B]), so the architect can
		/// see exactly what's there. Caps the output length to keep log lines readable.
		/// </summary>
		internal static string RenderTextWithInvisibleMarkers(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return s;
			}

			var sb = new StringBuilder(s.Length + 16);
			foreach (var ch in s)
			{
				if (IsArchitectClassInvisible(ch))
				{
					sb.Append('[').Append(NameArchitectInvisible(ch)).Append(']');
				}
				else
				{
					sb.Append(ch);
				}
			}
			var result = sb.ToString().Trim();
			const int max = 200;
			if (result.Length > max)
			{
				result = result[..(max - 1)] + "…";
			}

			return result;
		}

		/// <summary>
		/// Builds an excerpt for an EMBEDDED_BOM_IN_BODY finding centred on the first
		/// embedded BOM offset, with surrounding bytes decoded as UTF-8 and the BOM
		/// itself rendered as [BOM U+FEFF] so it's visible in the log.
		/// </summary>
		internal static string BuildBomExcerpt(byte[] bytes, int bomOffset)
		{
			const int radius = 40;
			int start = Math.Max(0, bomOffset - radius);
			int end = Math.Min(bytes.Length, bomOffset + 3 + radius);

			// Decode segments either side of the BOM separately to avoid the
			// decoder consuming the BOM byte sequence as a marker.
			string before = start < bomOffset
				? SafeDecodeUtf8(bytes, start, bomOffset - start)
				: string.Empty;
			string after = bomOffset + 3 < end
				? SafeDecodeUtf8(bytes, bomOffset + 3, end - (bomOffset + 3))
				: string.Empty;

			return $"offset {bomOffset}: …{before}[BOM U+FEFF]{after}…";
		}

		private static string SafeDecodeUtf8(byte[] bytes, int index, int count)
		{
			try
			{
				var raw = Encoding.UTF8.GetString(bytes, index, count);
				// Render any architect-class invisibles inside the excerpt too.
				return RenderTextWithInvisibleMarkers(raw);
			}
			catch { return "<decode-error>"; }
		}

		// ── Split-word anchor check ───────────────────────────────────────────────────

		/// <summary>
		/// Detects anchor tags that close mid-word — a common CMS authoring mistake.
		/// Pattern: &lt;/a&gt; immediately followed by a RUN of letters/digits, then
		/// whitespace. The run is the orphaned tail of a token that should have been
		/// inside the link.
		/// Examples: "Hello Wor&lt;/a&gt;ld " — "ld" belongs to the preceding word;
		/// "08&lt;/a&gt;15 Uhr" — "15" belongs to the preceding number.
		///
		/// The quantifier is "+", not a single char: a one-character orphan is the
		/// EDGE case, not the norm — real splits almost always strand a multi-char
		/// fragment ("Wor|ld", "08|15"). The previous single-char pattern
		/// (</a>(X)\s) only fired when EXACTLY one stray char sat before the space,
		/// so it silently missed every multi-char split — the common case. "+"
		/// strictly supersedes it (length-1 runs still match), so the recall change
		/// is purely additive: nothing that fired before stops; multi-char splits
		/// now fire too.
		///
		/// The class is \p{L}\p{N} (any Unicode letter or digit) so the check fires
		/// across scripts — Latin, German/Turkish extended, Cyrillic — not only
		/// ASCII+Latin-Extended. The trailing \s is deliberately left unchanged:
		/// tightening it to ASCII-space-only is a separate concern (it would change
		/// which matches fire) and is not part of this change.
		///
		/// Tails that LEAD with punctuation/connectors (".com", "/home.html",
		/// "-Event") are NOT caught here — the run must START with a letter/digit.
		/// Distinguishing a leading-punctuation split from normal trailing
		/// punctuation ("&lt;/a&gt;." at sentence end) needs its own precise rule and
		/// log-diff validation, deferred to a later change.
		/// </summary>
		[System.Text.RegularExpressions.GeneratedRegex(@"</a>([\p{L}\p{N}]+)\s")]
		private static partial System.Text.RegularExpressions.Regex SplitAnchorPattern();

		internal static IEnumerable<QualityIssue> CheckSplitWordAnchors(
			string filename, string html, ContentQualityConfig config)
		{
			foreach (System.Text.RegularExpressions.Match m in SplitAnchorPattern().Matches(html))
			{
				// Decode HTML entities for the excerpt so it matches the user-visible text,
				// consistent with how all other checks produce their excerpts.
				var decoded = System.Net.WebUtility.HtmlDecode(html);
				yield return new QualityIssue(
					filename,
					"SPLIT_WORD_ANCHOR",
					$"Anchor closes mid-word — stray text after </a>: '{m.Groups[1].Value}'",
					Excerpt(decoded, m.Index, config.ContentQualityExcerptRadius));
			}
		}

		// ── Misplaced anchor check ──────────────────────────────────────────────────────

		/// <summary>
		/// Detects structurally malformed anchor tags in raw downloaded HTML.
		/// Two defect types are reported independently:
		///
		///   MISPLACED_ANCHOR_EMPTY — an anchor whose visible text is absent.
		///     Anchors containing only whitespace or only child elements that
		///     themselves carry no text are treated as empty — InnerText of all
		///     descendants is checked, not just the immediate text node.
		///
		///   ADJACENT_ANCHOR (renamed from MISPLACED_ANCHOR_SPLIT) —
		///     two consecutive sibling anchor tags with no whitespace-text node
		///     between them, AND the rendered excerpt contains the literal
		///     "&lt;/a&gt;&lt;a" (a string-evidence post-filter on HAP's DOM
		///     verdict). Adjacency alone is a structural fact, not a verdict;
		///     OFF by default (AnchorDetection.DetectAdjacent), opted in per
		///     site. The previous name's "SPLIT" overlapped misleadingly with
		///     SPLIT_WORD_ANCHOR — the new name names what is actually checked.
		///
		/// Runs on the already-parsed simplified HTML document — navigation chrome,
		/// scripts, and framework noise are stripped before this check runs, eliminating
		/// false positives from icon anchors, hidden UI controls, and CMS scaffolding.
		/// The caller passes the already-parsed <paramref name="doc"/> to avoid a second
		/// parse of the same file.
		/// </summary>
		internal static IEnumerable<QualityIssue> CheckMisplacedAnchors(
			string filename, HtmlDocument doc, ContentQualityConfig config)
		{
			foreach (var anchor in doc.DocumentNode.Descendants("a").ToList())
			{
				// ── MISPLACED_ANCHOR_EMPTY ────────────────────────────────────────────
				// Treat as empty when InnerText (all descendant text collapsed) is blank.
				// Covers: no children, whitespace-only text, empty inline elements.
				// Anchors wrapping an <img> are intentional image links — not flagged.
				var visibleText = WebUtility.HtmlDecode(anchor.InnerText).Trim();
				if (string.IsNullOrEmpty(visibleText)
					&& !anchor.Descendants("img").Any())
				{
					var hrefAttr = anchor.GetAttributeValue("href", string.Empty);
					var detail = string.IsNullOrEmpty(hrefAttr)
						? "No href"
						: $"href: {hrefAttr}";
					yield return new QualityIssue(
						filename,
						"MISPLACED_ANCHOR_EMPTY",
						detail,
						CentredExcerpt(
							anchor.ParentNode?.OuterHtml ?? anchor.OuterHtml,
							anchor.OuterHtml,
							config.ContentQualityQuoteMaxExcerpt));
				}

				// ── ADJACENT_ANCHOR (was MISPLACED_ANCHOR_SPLIT) ────────────────
				// Flag when the immediately following sibling is also an anchor with no
				// whitespace-text node between them. Check current anchor against its next
				// sibling only — avoids double-reporting the same adjacent pair.
				//
				// Gated on AnchorDetection.DetectAdjacent (default false) because adjacency
				// alone is a structural fact, not a defect verdict — many sites use
				// adjacent <a><a> intentionally (CSS-spaced button rows, JS-trigger
				// widgets, dense nav). Operator opts in per site.
				if (!config.AnchorDetection.DetectAdjacent)
				{
					continue;
				}

				var next = anchor.NextSibling;

				// Skip comment nodes — invisible and not a real separator.
				while (next is { NodeType: HtmlNodeType.Comment })
				{
					next = next.NextSibling;
				}

				if (next is not { NodeType: HtmlNodeType.Element } || next.Name != "a")
				{
					continue;
				}

				// Walk siblings between anchor and next to find any whitespace text node.
				var between = anchor.NextSibling;
				var hasSeparator = false;
				while (between != null && between != next)
				{
					if (between.NodeType == HtmlNodeType.Text
						&& between.InnerText.Any(char.IsWhiteSpace))
					{
						hasSeparator = true;
						break;
					}
					between = between.NextSibling;
				}

				if (!hasSeparator)
				{
					var textA = WebUtility.HtmlDecode(anchor.InnerText).Trim();
					var textB = WebUtility.HtmlDecode(next.InnerText).Trim();

					// Centre the excerpt on the </a><a split BOUNDARY, not on the first
					// anchor's OuterHtml start (which sits before a potentially long SVG
					// body, pushing the actual split out of the window). The
					// boundary is where anchor.OuterHtml ends within the source. Fall back
					// to centring on the source midpoint if the anchor markup can't be
					// located (defensive — OuterHtml should always be present in source).
					var source = anchor.ParentNode?.OuterHtml ?? $"{anchor.OuterHtml}{next.OuterHtml}";
					var anchorAt = source.IndexOf(anchor.OuterHtml, StringComparison.Ordinal);
					var boundaryAt = anchorAt >= 0 ? anchorAt + anchor.OuterHtml.Length : source.Length / 2;

					var excerpt = CentredExcerpt(source, boundaryAt, config.ContentQualityQuoteMaxExcerpt);

					// [KEEP] String-evidence post-filter: require the literal
					// "</a><a" to appear in the rendered excerpt before firing.
					// HAP's DOM-level adjacency verdict is what flagged this pair, but
					// HAP may normalize source whitespace during parsing/serialization,
					// so a DOM "adjacent" pair occasionally lacks literal source-level
					// adjacency. Operator decision: drop those edge cases rather
					// than ship a finding the operator cannot confirm by looking at the
					// shown excerpt. Case-insensitive to match the existing anchor-tag
					// highlighter's stance. Do NOT remove this gate — it is the honest
					// bridge between HAP's verdict and the source-bytes claim.
					if (excerpt.IndexOf("</a><a", StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}

					yield return new QualityIssue(
						filename,
						"ADJACENT_ANCHOR",
						// Prepend [boundaryAt] — the source-byte position
						// of this collision's "</a><a" boundary in the parent's
						// OuterHtml. Persists structural position in the log
						// (4-column shape unchanged) so BuildGroups can sort
						// multiple findings within one page-cluster reliably,
						// independent of ConcurrentBag emission order (which is
						// undefined and observably LIFO under current .NET).
						// Display layer renders this as sequential [01]/[02]
						// for clusters of 2+; bracket stays raw in the log so
						// the position info is intact across crawls. Stripping
						// is a trivial regex if the raw value is unwanted in
						// downstream tools (Excel: replace `\[\d+\] ` with "").
						$"[{boundaryAt}] \u201e{textA}\u201c + \u201e{textB}\u201c",
						excerpt);
				}
			}
		}

		// ── Unwanted pattern check ────────────────────────────────────────────────────

		// Max characters an OPEN envelope's region may span when no closing delimiter
		// bounds it — stops a missing-closer placeholder from fusing with distant text
		// on the same line. The whitespace/'<' edge below almost always fires first.
		private const int EnvelopeRegionMaxChars = 120;

		/// <summary>
		/// Right edge of an OPEN envelope's region — used when the closing delimiter is
		/// absent, so there is nothing to bound "inside the placeholder" with. Returns the
		/// first whitespace or '&lt;' at or after <paramref name="start"/>, capped at
		/// <see cref="EnvelopeRegionMaxChars"/>. The token-run boundary is what makes the
		/// rule generic: a leaked CMS variable, URL slug, or any unbroken run reads as one
		/// region, while a space or a tag boundary (e.g. <c>%(institut.name)&lt;/title&gt;</c>)
		/// ends it so following content is never swallowed.
		/// </summary>
		private static int FindEnvelopeRegionEnd(string html, int start)
		{
			int limit = Math.Min(html.Length, start + EnvelopeRegionMaxChars);
			for (int i = start; i < limit; i++)
			{
				char c = html[i];
				if (char.IsWhiteSpace(c) || c == '<')
				{
					return i;
				}
			}

			return limit;
		}

		internal static IEnumerable<QualityIssue> CheckUnwantedPatterns(
			string filename,
			string html,
			IReadOnlyList<ContentUnwantedPattern> patterns,
			ContentQualityConfig config)
		{
			// Pass 1 — collect every atomic match (the rows we WOULD emit pre-coalescing),
			// each tagged with its source set and char position(s). A grouped set yields one
			// atom (first occurrence per pattern); an ungrouped set yields one atom per
			// occurrence. Nothing is emitted yet — coalescing needs the whole picture first.
			var atoms = new List<(ContentUnwantedPattern Set, bool Grouped, List<(string Pattern, int Pos)> Matched)>();
			foreach (var group in patterns)
			{
				if (!group.IsConfigured)
				{
					continue;
				}

				var comparison = group.CaseSensitive
					? StringComparison.Ordinal
					: StringComparison.OrdinalIgnoreCase;

				if (group.GroupPatterns)
				{
					var matched = new List<(string Pattern, int Pos)>();
					foreach (var pattern in group.Patterns)
					{
						if (string.IsNullOrEmpty(pattern))
						{
							continue;
						}

						var pos = html.IndexOf(pattern, comparison);
						if (pos >= 0)
						{
							matched.Add((pattern, pos));
						}
					}

					if (matched.Count > 0)
					{
						atoms.Add((group, true, matched));
					}
				}
				else
				{
					foreach (var pattern in group.Patterns)
					{
						if (string.IsNullOrEmpty(pattern))
						{
							continue;
						}

						int pos = 0;
						while ((pos = html.IndexOf(pattern, pos, comparison)) >= 0)
						{
							atoms.Add((group, false, [(pattern, pos)]));
							pos += pattern.Length;
						}
					}
				}
			}

			// Pass 2 — coalesce the clear case: a BROKEN (open) envelope plus the hint
			// patterns it References, sitting inside the envelope's region, collapse into
			// ONE finding. Generic by design — the trigger is the structural fact "open
			// delimiter pair whose Reference set has hits in range", never a literal string.
			// Purely SUBTRACTIVE: folding happens only on that corroborated case; every other
			// atom emits exactly as before, so the worst case equals the pre-feature output.
			var consumed = new HashSet<int>();
			var merges = new List<(int EnvIdx, int OpenerPos, string Opener, string Closer, List<int> Folded)>();
			for (int i = 0; i < atoms.Count; i++)
			{
				if (consumed.Contains(i))
				{
					continue;
				}

				var (set, grouped, matched) = atoms[i];
				// Envelope = a grouped opener/closer pair that names (via Reference) the set
				// expected inside it. Only an OPEN envelope — opener present, closer absent —
				// is the broken case we coalesce; a balanced pair is left to emit as today.
				if (!grouped || set.Patterns.Count != 2 || string.IsNullOrEmpty(set.Reference))
				{
					continue;
				}

				var opener = set.Patterns[0];
				var closer = set.Patterns[1];
				var openerEntry = matched.FirstOrDefault(m => m.Pattern == opener);
				bool openerMatched = openerEntry.Pattern != null;
				bool closerMatched = matched.Any(m => m.Pattern == closer);
				if (!openerMatched || closerMatched)
				{
					continue;
				}

				int openerPos = openerEntry.Pos;
				int regionEnd = FindEnvelopeRegionEnd(html, openerPos + opener.Length);

				var folded = new List<int>();
				for (int j = 0; j < atoms.Count; j++)
				{
					if (j == i || consumed.Contains(j))
					{
						continue;
					}

					var other = atoms[j];
					if (!other.Grouped
						&& string.Equals(other.Set.Name, set.Reference, StringComparison.Ordinal)
						&& other.Matched[0].Pos > openerPos
						&& other.Matched[0].Pos < regionEnd)
					{
						folded.Add(j);
					}
				}

				if (folded.Count == 0)
				{
					// Booster, not gate: an uncorroborated open envelope still fires — as today.
					continue;
				}

				consumed.Add(i);
				foreach (var j in folded)
				{
					consumed.Add(j);
				}

				merges.Add((i, openerPos, opener, closer, folded));
			}

			// Pass 3 — emit. Merged findings first, then every non-consumed atom unchanged.
			var issues = new List<QualityIssue>();

			foreach (var (envIdx, openerPos, opener, closer, folded) in merges)
			{
				var env = atoms[envIdx];
				// Highlight list = opener plus each folded hint pattern in document order,
				// de-duplicated. Kept as the trailing "— patterns: …" segment so the existing
				// ExtractHighlightPatterns marks all of them on the card AND in the ticket,
				// and so suppression round-trips on one stable composite key.
				var byPos = new List<(string Pattern, int Pos)> { (opener, openerPos) };
				foreach (var j in folded)
				{
					byPos.Add(atoms[j].Matched[0]);
				}

				var ordered = byPos
					.OrderBy(x => x.Pos)
					.Select(x => x.Pattern)
					.Distinct(StringComparer.Ordinal)
					.ToList();

				var detail =
					$"{env.Set.Category}: {env.Set.Name} — open placeholder, missing closing '{closer}'" +
					$" — patterns: {string.Join(", ", ordered)}";
				issues.Add(new QualityIssue(
					filename, "UNWANTED_PATTERN", detail,
					Excerpt(html, openerPos, config.ContentQualityExcerptRadius)));
			}

			for (int i = 0; i < atoms.Count; i++)
			{
				if (consumed.Contains(i))
				{
					continue;
				}

				var (set, grouped, matched) = atoms[i];
				if (grouped)
				{
					// [KEEP] Grouped mode — at most ONE issue per page per named group. All
					// patterns together indicate a single defect (e.g. a full CMS variable
					// — fix is CMS-side, not per pattern). Word = group Name for
					// a stable IssueTracking identity key across runs.
					var matchedPatterns = matched.Select(m => m.Pattern).ToList();
					var detail = $"{set.Category}: {set.Name}" +
						(matchedPatterns.Count > 1
							? $" — patterns: {string.Join(", ", matchedPatterns)}"
							: $" — pattern: {matchedPatterns[0]}");
					issues.Add(new QualityIssue(
						filename, "UNWANTED_PATTERN", detail,
						Excerpt(html, matched[0].Pos, config.ContentQualityExcerptRadius)));
				}
				else
				{
					// Ungrouped mode — one issue per pattern occurrence per page (default).
					var (pattern, pos) = matched[0];
					issues.Add(new QualityIssue(
						filename,
						"UNWANTED_PATTERN",
						$"{set.Category}: {set.Name} — pattern: {pattern}",
						Excerpt(html, pos, config.ContentQualityExcerptRadius)));
				}
			}

			return issues;
		}

		// ── Language mismatch check ───────────────────────────────────────────────────

		internal static IEnumerable<QualityIssue> CheckLanguageMismatch(
			string filename, HtmlDocument doc)
		{
			var htmlNode = doc.DocumentNode.SelectSingleNode("//html");
			var htmlLang = htmlNode?.GetAttributeValue("lang", string.Empty)?.Trim();
			var metaNode = doc.DocumentNode.SelectSingleNode("//meta[@name='language']");
			var metaLang = metaNode?.GetAttributeValue("content", string.Empty)?.Trim();

			if (string.IsNullOrEmpty(htmlLang) || string.IsNullOrEmpty(metaLang))
			{
				yield break;
			}

			// Normalise to base language code (de-DE → de).
			var htmlCode = htmlLang.Split('-')[0].ToLowerInvariant();
			var metaCode = metaLang.Split('-')[0].ToLowerInvariant();

			if (!htmlCode.Equals(metaCode, StringComparison.OrdinalIgnoreCase))
			{
				yield return new QualityIssue(
					filename,
					"LANGUAGE_MISMATCH",
					$"<html lang=\"{htmlLang}\"> conflicts with <meta name=\"language\" content=\"{metaLang}\">",
					$"html lang wins for spell-checking — meta tag should be corrected to \"{htmlCode}\"");
			}
		}

		// ── Control-chars-in-content check ────────────────────────────────────────────
		// Detect control characters / bidi controls / zero-widths
		// in title and meta attribute values. These often originate from
		// CMS editors copy-pasting from other sources (Word, PDFs, web pages).
		// Invisible to the author but they break downstream parsing and can,
		// in the worst case, be used as an injection vector by a malicious
		// page. Distinct from LIGATURE — that's about visible-but-wrong
		// characters; this is about invisible-but-harmful characters.

		/// <summary>
		/// First codepoint of <paramref name="s"/> that is a control / bidi /
		/// zero-width character, or null if none. Returns the codepoint and
		/// a short human-readable name. Used by CheckControlCharsInContent.
		/// </summary>
		internal static (int Codepoint, string Name)? FindFirstControlChar(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return null;
			}

			foreach (var ch in s)
			{
				if (ch == '\r')
				{
					return (ch, "CR (U+000D)");
				}

				if (ch == '\n')
				{
					return (ch, "LF (U+000A)");
				}

				if (ch == '\t')
				{
					return (ch, "TAB (U+0009)");
				}

				if (ch < 0x20)
				{
					return (ch, $"C0 control (U+{(int)ch:X4})");
				}

				if (ch >= 0x80 && ch <= 0x9F)
				{
					return (ch, $"C1 control (U+{(int)ch:X4})");
				}

				if (ch == '\u200B')
				{
					return (ch, "ZWSP (U+200B)");
				}

				if (ch == '\u200C')
				{
					return (ch, "ZWNJ (U+200C)");
				}

				if (ch == '\u200D')
				{
					return (ch, "ZWJ (U+200D)");
				}

				if (ch == '\uFEFF')
				{
					return (ch, "BOM/ZWNBSP (U+FEFF)");
				}

				if (ch >= '\u202A' && ch <= '\u202E')
				{
					return (ch, $"bidi control (U+{(int)ch:X4})");
				}

				if (ch >= '\u2066' && ch <= '\u2069')
				{
					return (ch, $"bidi isolate (U+{(int)ch:X4})");
				}
				// Unicode line-break characters that .NET ReadLine doesn't split
				// on but text editors and other tooling may render as breaks.
				if (ch == '\u2028')
				{
					return (ch, "LINE SEPARATOR (U+2028)");
				}

				if (ch == '\u2029')
				{
					return (ch, "PARAGRAPH SEPARATOR (U+2029)");
				}
			}
			return null;
		}

		/// <summary>
		/// Scans &lt;title&gt; text and the content attribute of &lt;meta&gt; tags
		/// for control characters / bidi controls / zero-widths. CMS-pasted
		/// content frequently contains these — invisible to the author but
		/// breaking downstream tooling. One issue per offending element
		/// (not per character) — Detail names the first bad codepoint.
		/// </summary>
		internal static IEnumerable<QualityIssue> CheckControlCharsInContent(
			string filename, HtmlDocument doc)
		{
			// Title text content.
			var titleNode = doc.DocumentNode.SelectSingleNode("//title");
			if (titleNode != null)
			{
				var rawTitle = WebUtility.HtmlDecode(titleNode.InnerText);
				var hit = FindFirstControlChar(rawTitle);
				if (hit.HasValue)
				{
					yield return new QualityIssue(
						filename,
						"CONTROL_CHARS_IN_CONTENT",
						$"Found {hit.Value.Name} in <title> text",
						TruncateForLog(rawTitle));
				}
			}

			// Meta content attributes.
			foreach (var meta in doc.DocumentNode.SelectNodes("//meta[@content]") ?? Enumerable.Empty<HtmlNode>())
			{
				var name = meta.GetAttributeValue("name", "").Trim();
				var content = meta.GetAttributeValue("content", "");
				var decoded = WebUtility.HtmlDecode(content);
				var hit = FindFirstControlChar(decoded);
				if (hit.HasValue)
				{
					yield return new QualityIssue(
						filename,
						"CONTROL_CHARS_IN_CONTENT",
						$"Found {hit.Value.Name} in meta[@name=\"{name}\"] content",
						TruncateForLog(decoded));
				}
			}
		}

		/// <summary>
		/// Renders content text with invisible characters replaced by operator-
		/// readable markers, then truncates. Designed for non-technical CMS
		/// authors reviewing CONTROL_CHARS_IN_CONTENT flags: each marker says
		/// what KIND of invisible character is present (so an editor can
		/// understand the issue) AND its codepoint (for search / self-help).
		///
		/// Markers use plain ASCII only — the tool detects exotic non-ASCII
		/// characters, so using exotic non-ASCII in its own diagnostic output
		/// would be confusing. Short forms ([CR], [LF], [TAB]) for the three
		/// universally-known control chars; the prefix [INVISIBLE &lt;kind&gt;
		/// U+XXXX] for everything else.
		/// </summary>
		internal static string TruncateForLog(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return string.Empty;
			}

			var sb = new System.Text.StringBuilder(s.Length);
			foreach (var ch in s)
			{
				// Universally-known short forms.
				if (ch == '\r') { sb.Append("[CR]"); continue; }
				if (ch == '\n') { sb.Append("[LF]"); continue; }
				if (ch == '\t') { sb.Append("[TAB]"); continue; }

				// Obscure codepoints — operator-friendly long form.
				if (ch == '\u2028') { sb.Append("[INVISIBLE LINE SEPARATOR U+2028]"); continue; }
				if (ch == '\u2029') { sb.Append("[INVISIBLE PARAGRAPH SEPARATOR U+2029]"); continue; }
				if (ch == '\u200B') { sb.Append("[INVISIBLE ZERO-WIDTH SPACE U+200B]"); continue; }
				if (ch == '\u200C') { sb.Append("[INVISIBLE ZERO-WIDTH NON-JOINER U+200C]"); continue; }
				if (ch == '\u200D') { sb.Append("[INVISIBLE ZERO-WIDTH JOINER U+200D]"); continue; }
				if (ch == '\uFEFF') { sb.Append("[INVISIBLE BOM U+FEFF]"); continue; }
				if (ch >= '\u202A' && ch <= '\u202E')
				{ sb.Append($"[INVISIBLE BIDI CONTROL U+{(int)ch:X4}]"); continue; }
				if (ch >= '\u2066' && ch <= '\u2069')
				{ sb.Append($"[INVISIBLE BIDI ISOLATE U+{(int)ch:X4}]"); continue; }

				// Other C0 and C1 controls fall here — generic INVISIBLE
				// CONTROL marker. C0 = U+0000..001F (minus CR/LF/TAB above),
				// C1 = U+0080..009F.
				if (ch < 0x20 || (ch >= 0x80 && ch <= 0x9F))
				{ sb.Append($"[INVISIBLE CONTROL U+{(int)ch:X4}]"); continue; }

				// Everything else — legitimate content character, keep as-is.
				sb.Append(ch);
			}

			var visible = sb.ToString();
			// Bumped from 200 → 250 to accommodate longer markers without
			// losing surrounding context. A 250-char excerpt with two
			// [INVISIBLE LINE SEPARATOR U+2028] markers still leaves ~180
			// chars of actual content visible to the operator.
			return visible.Length > 250 ? visible[..250] + "…" : visible;
		}

		// ── Ligature check ────────────────────────────────────────────────────────────

		internal static IEnumerable<QualityIssue> CheckLigatures(
			string filename, string text, ContentQualityConfig config)
		{
			foreach (var (ch, name) in Ligatures)
			{
				int pos = 0;
				while ((pos = text.IndexOf(ch, pos)) >= 0)
				{
					var context = Excerpt(text, pos, config.ContentQualityExcerptRadius);
					yield return new QualityIssue(
						filename,
						"LIGATURE",
						$"Ligature {name}",
						context);
					pos++;
				}
			}
		}

		// ── Quote checks ──────────────────────────────────────────────────────────────

		internal static IEnumerable<QualityIssue> CheckQuotes(
			string filename, HtmlDocument doc, ContentQualityConfig config)
		{
			// [KEEP] Quote checks run per block element, not on concatenated page text.
			// Cross-block quote pairs are not detected — a quote must open and close
			// within the same block element to be considered a pair.
			// This prevents false positives where openers from one element and closers
			// from another (completely unrelated) element create phantom mismatches.
			var blockElements = config.ContentQualityBlockElements
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			// Resolve page language once. Mirrors CheckLanguageMismatch's source of
			// truth: <html lang="…">, normalised to lowercase 2-letter base code
			// (de-DE → de). Falls back to null when no lang attribute is present —
			// downstream uses "_default" in that case.
			var htmlNode = doc.DocumentNode.SelectSingleNode("//html");
			var htmlLang = htmlNode?.GetAttributeValue("lang", string.Empty)?.Trim();
			var pageLanguage = string.IsNullOrEmpty(htmlLang)
				? null
				: htmlLang.Split('-')[0].ToLowerInvariant();

			var blocks = doc.DocumentNode
				.Descendants()
				.Where(n => n.NodeType == HtmlNodeType.Element
					&& blockElements.Contains(n.Name)
					&& n.ParentNode?.Name is not ("script" or "style" or "noscript"))
				.ToList();

			foreach (var block in blocks)
			{
				var text = WebUtility.HtmlDecode(block.InnerText)
					.Replace("\u00AD", "")
					.Replace("\n", " ").Replace("\r", " ").Replace("\t", " ").Trim();
				while (text.Contains("  "))
				{
					text = text.Replace("  ", " ");
				}

				if (string.IsNullOrWhiteSpace(text))
				{
					continue;
				}

				foreach (var issue in CheckQuotesInBlock(filename, text, config, pageLanguage))
				{
					yield return issue;
				}
			}
		}

		/// <summary>
		/// Runs quote system mixing and pairing checks on a single block of text.
		/// Excerpt is centred on the first mismatching opener within this block.
		/// </summary>
		private static IEnumerable<QualityIssue> CheckQuotesInBlock(
			string filename, string text, ContentQualityConfig config, string? pageLanguage)
		{
			// ── Level 1: System mixing ────────────────────────────────────────────────
			if (config.CheckQuoteSystemMixing)
			{
				// [KEEP] Build the set of characters that act as OPENERS in this block,
				// accounting for ambiguous characters like \u201C which is both a German-double
				// closer and an English-double opener. We simulate pairing to determine role.
				// Reference: https://anfuehrungszeichen.de/
				var openerCharsFound = new HashSet<char>();
				var simStack = new Stack<(char Ch, QuoteSystem Sys)>();
				foreach (var ch in text)
				{
					if (AllOpeners.Contains(ch))
					{
						if (AllClosers.Contains(ch)
							&& simStack.Count > 0
							&& simStack.Peek().Sys.Closers.Contains(ch))
						{
							simStack.Pop(); // acting as closer — don't count as opener
						}
						else
						{
							var sys = QuoteSystems.First(s => s.Openers.Contains(ch));
							simStack.Push((ch, sys));
							openerCharsFound.Add(ch);
						}
					}
					else if (AllClosers.Contains(ch) && simStack.Count > 0)
					{
						simStack.Pop();
					}
				}

				var systemsPresent = QuoteSystems
					.Where(s => s.Openers.Any(openerCharsFound.Contains))
					.Select(s => s.Name)
					.Distinct()
					.ToList();

				if (systemsPresent.Count > 1)
				{
					var doubleSystemsPresent = systemsPresent
						.Where(s => !s.Contains("single", StringComparison.OrdinalIgnoreCase)
							&& !s.Contains("angle", StringComparison.OrdinalIgnoreCase))
						.ToList();

					if (doubleSystemsPresent.Count > 1)
					{
						// Find the dominant system — first opener found in the block.
						// The mismatch is the first opener from any OTHER system.
						// Centre the excerpt there so the operator sees exactly what is wrong.
						char? dominantOpener = null;
						int mismatchPos = -1;
						for (int i = 0; i < text.Length && mismatchPos < 0; i++)
						{
							var ch = text[i];
							if (!AllOpeners.Contains(ch))
							{
								continue;
							}

							if (dominantOpener == null)
							{
								dominantOpener = ch;
								continue;
							}
							// Different opener system = mismatch.
							var dominantSystem = QuoteSystems.FirstOrDefault(
								s => s.Openers.Contains(dominantOpener.Value));
							var thisSystem = QuoteSystems.FirstOrDefault(
								s => s.Openers.Contains(ch));
							if (dominantSystem?.Name != thisSystem?.Name)
							{
								mismatchPos = i;
							}
						}

						// [KEEP] Store full block text as context so triage can show
						// 300-char preview with [M] to reveal the complete block.
						// The mismatch position is implicit — triage uses the QUOTE_SYSTEM_MIX
						// entry context which contains the full block for human inspection.
						yield return new QualityIssue(
							filename,
							"QUOTE_SYSTEM_MIX",
							$"Multiple quote systems: {string.Join(", ", doubleSystemsPresent)}",
							text);
					}
				}
			}

			// ── Level 2: Pairing analysis ─────────────────────────────────────────────
			if (config.CheckQuotePairing)
			{
				foreach (var issue in CheckQuotePairing(filename, text, config, pageLanguage))
				{
					yield return issue;
				}
			}
		}

		internal static IEnumerable<QualityIssue> CheckQuotePairing(
			string filename, string text, ContentQualityConfig config,
			string? pageLanguage = null)
			=> BuildQuotePairingFlags(filename, text, config, pageLanguage).Select(f => f.issue);

		// Position-bearing core of the pairing check: identical matching +
		// verification, but returns each flag WITH its trigger position so triage
		// can highlight the exact offending glyph instead of every quote in the
		// block. CheckQuotePairing is the position-stripping projection over this
		// (behaviour unchanged — the existing pairing tests exercise it via that
		// wrapper). Kept as a separate method because LocateQuoteFlags needs the
		// positions; do not inline back.
		private static List<(QualityIssue issue, int triggerPos)> BuildQuotePairingFlags(
			string filename, string text, ContentQualityConfig config,
			string? pageLanguage = null)
		{
			// [KEEP] Internal flag tracking carries the trigger position alongside
			// each QualityIssue. The position is needed by the verification pass
			// (CheckQuotePairingVerification) to look up whether the offending
			// character pairs cleanly under proximity matching. Position is
			// internal plumbing — never exposed on QualityIssue itself.
			List<(QualityIssue issue, int triggerPos)> flagged = [];
			var stack = new Stack<(char opener, int position, QuoteSystem system)>();

			// Resolve the per-language elision profile. Lookup uses lowercase
			// invariant keys because the dictionary's case-insensitive comparer
			// is lost during JSON deserialization (System.Text.Json replaces the
			// property's dictionary instance with a fresh ordinal-comparer one).
			// pageLanguage is already lowercased in CheckQuotes; we lowercase
			// "_default" usage too for consistency. Config authors should write
			// keys in lowercase; this defensive normalisation keeps "FR" working.
			// [KEEP] Profiles must be resolved per-call, not cached at module level —
			// config can change between runs in debug-replay mode.
			var profiles = config.ContentQualityApostropheElisions;
			ApostropheElisionProfile profile;
			if (profiles != null && pageLanguage != null
				&& TryGetProfileCaseInsensitive(profiles, pageLanguage, out var langProfile))
			{
				profile = langProfile;
			}
			else if (profiles != null
				&& TryGetProfileCaseInsensitive(profiles, "_default", out var defProfile))
			{
				profile = defProfile;
			}
			else
			{
				profile = new ApostropheElisionProfile();
			}

			var apostropheChars = profile.ApostropheChars.ToHashSet();
			var suffixElisions = profile.SuffixElisions ?? [];
			var prefixElisions = profile.PrefixElisions ?? [];

			for (int i = 0; i < text.Length; i++)
			{
				var ch = text[i];

				// ── Apostrophe / elision guard ─────────────────────────────────────
				// Triggered only for characters declared as apostrophes for the
				// active language. A character outside this set falls through to
				// standard opener / closer matching with no disambiguation.
				if (apostropheChars.Contains(ch))
				{
					// Rule 1a — Suffix elision: text starting at the apostrophe
					// matches a configured suffix entry (e.g. "s" for it's,
					// "ner" for 'ner Kerl). Match is forward from i; case-insensitive.
					var suffixMatched = suffixElisions.Any(e =>
						e.Length > 0 &&
						text.Length - (i + 1) >= e.Length &&
						text.AsSpan(i + 1, e.Length).Equals(e, StringComparison.OrdinalIgnoreCase));
					if (suffixMatched)
					{
						continue;
					}

					// Rule 1b — Prefix elision: text immediately BEFORE the
					// apostrophe matches a configured prefix entry (e.g. "l" for
					// l'accès, "qu" for qu'il, "lorsqu" for lorsqu'il). Match is
					// backward from i; case-insensitive. The character immediately
					// AFTER the apostrophe must be a letter — without this anchor,
					// "qu'" at end-of-word would falsely match.
					// Additionally, the prefix must be word-anchored: the character
					// BEFORE the prefix (if any) must be a non-letter, otherwise
					// "tabl'art" would match prefix "l" against "tabl'…".
					var prefixMatched = i + 1 < text.Length
						&& char.IsLetter(text[i + 1])
						&& prefixElisions.Any(p =>
							p.Length > 0 && i >= p.Length &&
							text.AsSpan(i - p.Length, p.Length).Equals(p, StringComparison.OrdinalIgnoreCase) &&
							(i - p.Length == 0 || !char.IsLetter(text[i - p.Length - 1])));
					if (prefixMatched)
					{
						continue;
					}

					// Rule 2 — Between word characters: letter BEFORE and letter
					// AFTER. Unambiguous apostrophe in any language. Applies even
					// when a quote stack is active — there is no Western-language
					// quoting convention where a closer immediately abuts letters
					// on both sides without whitespace or punctuation.
					// (Prior versions excepted this when stack.Count > 0; that
					// exception produced QUOTE_WRONG_CLOSE false positives on
					// French text inside guillemets and has been lifted.)
					if (i > 0 && i < text.Length - 1
						&& char.IsLetter(text[i - 1])
						&& char.IsLetter(text[i + 1]))
					{
						continue;
					}

					// Rule 3 — Word-final possessive (opt-in per profile): an
					// apostrophe right after 's'/'S' and right before a non-letter
					// (or end of text) is a possessive — "visitors'", "Users'",
					// "boss'". English plural possessives are always word-final 's.
					// Fires ONLY when this glyph would otherwise be an orphan closer:
					// if the stack has an opener expecting it as a closer, a genuine
					// single-quoted s-ending word (…'words') still closes normally,
					// so the rule never manufactures a QUOTE_UNMATCHED. The trade-off
					// is a deliberate false-negative — a real orphan closer sitting
					// after 's' is read as possessive — accepted on languages that
					// set the flag.
					if (profile.WordFinalSPossessive
						&& i > 0 && (text[i - 1] == 's' || text[i - 1] == 'S')
						&& (i + 1 >= text.Length || !char.IsLetter(text[i + 1]))
						&& !(stack.Count > 0 && stack.Peek().system.Closers.Contains(ch)))
					{
						continue;
					}
				}

				if (AllOpeners.Contains(ch))
				{
					// [KEEP] \u201C is both a German-double closer AND English-double opener.
					// If the top of the stack expects this char as a closer, treat it as
					// a closer first — context wins over the opener interpretation.
					// Reference: https://anfuehrungszeichen.de/
					if (AllClosers.Contains(ch)
						&& stack.Count > 0
						&& stack.Peek().system.Closers.Contains(ch))
					{
						var (opener, openPos, openSystem) = stack.Pop();
						// Correctly closed — no issue.
						_ = (opener, openPos, openSystem);
					}
					else
					{
						var system = QuoteSystems.First(s => s.Openers.Contains(ch));
						stack.Push((ch, i, system));
					}
				}
				else if (AllClosers.Contains(ch))
				{
					if (stack.Count == 0)
					{
						flagged.Add((new QualityIssue(
							filename,
							"QUOTE_WRONG_OPEN",
							$"Closer '{ch}' (U+{(int)ch:X4}) found with no matching typographic opener",
						// [KEEP] Full block text stored — triage handles 300-char truncation with [M] for more.
						text), i));
					}
					else
					{
						var (opener, openPos, openSystem) = stack.Pop();

						if (!openSystem.Closers.Contains(ch))
						{
							var closeSystem = QuoteSystems.FirstOrDefault(s => s.Closers.Contains(ch));
							flagged.Add((new QualityIssue(
								filename,
								"QUOTE_WRONG_CLOSE",
								$"Opener '{opener}' ({openSystem.Name}) closed by '{ch}' " +
								$"({closeSystem?.Name ?? "unknown"})",
								text), i));
						}
					}
				}
			}

			// [KEEP] With per-block scoping, the block boundary is the natural limit —
			// any unmatched opener within a block is a real issue, no distance cutoff needed.
			foreach (var (opener, pos, system) in stack)
			{
				flagged.Add((new QualityIssue(
					filename,
					"QUOTE_UNMATCHED",
					$"Opener '{opener}' ({system.Name}) at position {pos} has no closer",
					text), pos));
			}

			// ── Verification pass ─────────────────────────────────────────────
			// When CheckQuotePairingVerification is enabled and the cheap pass
			// produced flags, run a proximity-based pairing pass to identify
			// flags that can be explained away. Resolved flags are downgraded
			// to QUOTE_AMBIGUOUS; unresolved flags pass through unchanged.
			// Per-flag granularity: a block can produce a mix of tiers.
			if (config.CheckQuotePairingVerification && flagged.Count > 0)
			{
				var pairedPositions = ProximityPair(text, apostropheChars,
					suffixElisions, prefixElisions);

				for (int idx = 0; idx < flagged.Count; idx++)
				{
					var (issue, triggerPos) = flagged[idx];
					if (pairedPositions.Contains(triggerPos))
					{
						flagged[idx] = (new QualityIssue(
							issue.Filename,
							"QUOTE_AMBIGUOUS",
							$"{issue.IssueType} (resolved by proximity): {issue.Detail}",
							issue.Context), triggerPos);
					}
				}
			}

			return flagged;
		}

		// ── Trigger locators (for triage highlighting) ─────────────────────────────────

		/// <summary>
		/// Returns the trigger position and finding type for every quote flag the
		/// detector would raise on <paramref name="text"/>. Triage uses this to
		/// highlight the exact offending glyph rather than every quote in the block.
		/// Reuses the same matcher (<see cref="BuildQuotePairingFlags"/>) and the
		/// SYSTEM_MIX mismatch scan, so a located glyph always matches what analysis
		/// flagged — provided <paramref name="pageLanguage"/> matches the language
		/// analysis resolved from &lt;html lang&gt;. A language mismatch can only cause
		/// a miss (the caller then highlights the block as context-only), never a
		/// wrong mark. Pure aside from no I/O; safe to call from triage.
		/// </summary>
		internal static IReadOnlyList<(int Pos, string Type)> LocateQuoteFlags(
			string text, ContentQualityConfig config, string? pageLanguage = null)
		{
			var result = new List<(int, string)>();
			if (string.IsNullOrEmpty(text))
			{
				return result;
			}

			var mix = LocateSystemMixMismatch(text);
			if (mix >= 0)
			{
				result.Add((mix, "QUOTE_SYSTEM_MIX"));
			}

			if (config.CheckQuotePairing)
			{
				foreach (var (issue, pos) in BuildQuotePairingFlags(string.Empty, text, config, pageLanguage))
				{
					result.Add((pos, issue.IssueType));
				}
			}

			return result;
		}

		/// <summary>
		/// Character position of the first quote opener whose DOUBLE system differs
		/// from the block's dominant (first) double-system opener — the glyph that
		/// makes a block trip QUOTE_SYSTEM_MIX. Single and angle systems are excluded.
		/// Returns -1 when no divergent double opener exists. Pure.
		///
		/// Mirrors the SYSTEM_MIX detection's context-wins stack walk: a glyph that
		/// closes the system on top of the stack (e.g. U+201C closing a German „) is
		/// a CLOSER here, not an opener, so it is popped and never nominated as the
		/// mismatch. Without this, a correctly-paired German closer — which shares
		/// its codepoint with the English-double opener — would be falsely marked as
		/// the mixer. The real divergent opener is the one actually pushed.
		/// </summary>
		internal static int LocateSystemMixMismatch(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return -1;
			}

			var simStack = new Stack<QuoteSystem>();
			string? dominantDoubleSystem = null;

			for (int i = 0; i < text.Length; i++)
			{
				var ch = text[i];

				if (AllOpeners.Contains(ch))
				{
					// Context wins: if this glyph can close the system on top, it is
					// acting as a closer — pop it, do not treat it as an opener.
					if (AllClosers.Contains(ch)
						&& simStack.Count > 0
						&& simStack.Peek().Closers.Contains(ch))
					{
						simStack.Pop();
						continue;
					}

					var sys = QuoteSystems.First(s => s.Openers.Contains(ch));
					simStack.Push(sys);

					// Only double systems participate in SYSTEM_MIX.
					if (sys.Name.Contains("single", StringComparison.OrdinalIgnoreCase)
						|| sys.Name.Contains("angle", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					if (dominantDoubleSystem is null)
					{
						dominantDoubleSystem = sys.Name;
					}
					else if (!string.Equals(sys.Name, dominantDoubleSystem, StringComparison.OrdinalIgnoreCase))
					{
						return i;   // first divergent double-system opener actually pushed
					}
				}
				else if (AllClosers.Contains(ch) && simStack.Count > 0)
				{
					simStack.Pop();
				}
			}

			return -1;
		}

		// ── Helpers ───────────────────────────────────────────────────────────────────

		/// <summary>
		/// Case-insensitive profile lookup for ContentQualityApostropheElisions.
		/// The dictionary's StringComparer.OrdinalIgnoreCase is preserved when
		/// the default initializer is used (no config file), but is lost when
		/// System.Text.Json replaces the dictionary during deserialization.
		/// This helper provides defensive case-insensitive lookup regardless.
		/// </summary>
		private static bool TryGetProfileCaseInsensitive(
			Dictionary<string, ApostropheElisionProfile> profiles,
			string key,
			out ApostropheElisionProfile profile)
		{
			if (profiles.TryGetValue(key, out var direct))
			{
				profile = direct;
				return true;
			}
			foreach (var kvp in profiles)
			{
				if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
				{
					profile = kvp.Value;
					return true;
				}
			}
			profile = null!;
			return false;
		}

		/// <summary>
		/// Verification pass for CheckQuotePairing. Re-examines a block using
		/// per-system proximity pairing — each QuoteSystem has its own stack,
		/// so misclassification of one character does not corrupt other systems.
		///
		/// Uses STRICTER apostrophe classification than the cheap pass: Rule 1a
		/// (suffix elision) additionally requires a letter immediately BEFORE
		/// the apostrophe. This catches cases where the cheap pass eats a true
		/// opener as an apostrophe — e.g. 'show password' where the opening
		/// U+2018 is followed by 's' which English suffix list would match.
		/// Without the letter-before guard, '<letter>... would always classify
		/// as apostrophe even at the start of a quoted phrase.
		///
		/// Returns the set of character positions that paired cleanly (both
		/// opener positions and closer positions). The caller compares each
		/// flag's trigger position against this set: hits get downgraded to
		/// QUOTE_AMBIGUOUS; misses keep their original high-confidence type.
		/// </summary>
		internal static HashSet<int> ProximityPair(
			string text,
			HashSet<char> apostropheChars,
			List<string> suffixElisions,
			List<string> prefixElisions)
		{
			// Per-system stacks. When a closer of system S is encountered and
			// the top of S's stack is a matching opener, pop and record the pair.
			var systemStacks = new Dictionary<string, Stack<int>>();
			foreach (var sys in QuoteSystems)
			{
				systemStacks[sys.Name] = new Stack<int>();
			}

			var paired = new HashSet<int>();

			for (int i = 0; i < text.Length; i++)
			{
				var ch = text[i];

				// ── Apostrophe classification (stricter than cheap pass) ──────
				if (apostropheChars.Contains(ch))
				{
					// Rule 1a-strict — Suffix elision with letter-before guard.
					// Differs from cheap pass: requires text[i-1] to be a letter,
					// preventing '<letter> at the start of a quoted phrase from
					// being eaten as a contraction.
					bool suffixMatched = i > 0 && char.IsLetter(text[i - 1]) &&
						suffixElisions.Any(e =>
							e.Length > 0 &&
							text.Length - (i + 1) >= e.Length &&
							text.AsSpan(i + 1, e.Length).Equals(e, StringComparison.OrdinalIgnoreCase));
					if (suffixMatched)
					{
						continue;
					}

					// Rule 1b — Prefix elision: unchanged from cheap pass; already
					// has the symmetric word-anchor guards.
					bool prefixMatched = i + 1 < text.Length
						&& char.IsLetter(text[i + 1])
						&& prefixElisions.Any(p =>
							p.Length > 0 && i >= p.Length &&
							text.AsSpan(i - p.Length, p.Length).Equals(p, StringComparison.OrdinalIgnoreCase) &&
							(i - p.Length == 0 || !char.IsLetter(text[i - p.Length - 1])));
					if (prefixMatched)
					{
						continue;
					}

					// Rule 2 — Between letters. Same as cheap pass.
					if (i > 0 && i < text.Length - 1
						&& char.IsLetter(text[i - 1])
						&& char.IsLetter(text[i + 1]))
					{
						continue;
					}
				}

				// ── Per-system opener/closer handling ─────────────────────────

				// Shared-character handling first: if the character is both opener
				// and closer (e.g. U+201C closes German-double / opens English-
				// double), and the top of the closing-system's stack has a matching
				// opener, treat as closer. Same context-wins rule as cheap pass,
				// applied per-system.
				bool handledAsCloser = false;
				if (AllOpeners.Contains(ch) && AllClosers.Contains(ch))
				{
					foreach (var sys in QuoteSystems)
					{
						if (sys.Closers.Contains(ch) &&
							systemStacks[sys.Name].Count > 0)
						{
							int openPos = systemStacks[sys.Name].Pop();
							paired.Add(openPos);
							paired.Add(i);
							handledAsCloser = true;
							break;
						}
					}
				}
				if (handledAsCloser)
				{
					continue;
				}

				if (AllOpeners.Contains(ch))
				{
					var sys = QuoteSystems.First(s => s.Openers.Contains(ch));
					systemStacks[sys.Name].Push(i);
				}
				else if (AllClosers.Contains(ch))
				{
					// Find the system this character closes for and pop if there's
					// a matching opener.
					foreach (var sys in QuoteSystems)
					{
						if (sys.Closers.Contains(ch) &&
							systemStacks[sys.Name].Count > 0)
						{
							int openPos = systemStacks[sys.Name].Pop();
							paired.Add(openPos);
							paired.Add(i);
							break;
						}
					}
					// If no matching opener in any system, this character stays
					// unpaired — its position is NOT added to `paired`, so the
					// caller's flag for this position will remain high-confidence.
				}
			}

			return paired;
		}

		/// <summary>
		/// Returns up to <paramref name="maxLength"/> characters from
		/// <paramref name="source"/> centred on the first occurrence of
		/// <paramref name="needle"/>. When the needle is not found, returns
		/// the start of source truncated to maxLength.
		/// Whitespace is collapsed to single spaces for console readability.
		/// </summary>
		private static string CentredExcerpt(string source, string needle, int maxLength)
		{
			var half = maxLength / 2;
			var hitIdx = source.IndexOf(needle, StringComparison.Ordinal);
			var centre = hitIdx >= 0 ? hitIdx + needle.Length / 2 : 0;
			var start = Math.Max(0, centre - half);
			var end = Math.Min(source.Length, start + maxLength);
			// Adjust start if end was clamped so we still show maxLength chars where possible.
			start = Math.Max(0, end - maxLength);
			return source[start..end].Replace('\n', ' ').Replace('\r', ' ');
		}

		// Position-centred variant. Windows maxLength chars centred on an
		// explicit character offset rather than on the location of a needle string.
		// Used by ADJACENT_ANCHOR (was MISPLACED_ANCHOR_SPLIT) to centre on
		// the </a><a boundary: the needle-based overload centred on the first
		// anchor's OuterHtml, whose start sits before the (often SVG-laden) anchor
		// body, so the window filled with SVG path data and the actual split fell
		// outside it — the operator saw a wall of coordinates with no visible
		// split. Centring on the boundary offset keeps the split in frame and
		// clips the SVG to the leading edge instead.
		// Adds conditional horizontal-ellipsis (…) markers: a leading … iff the
		// window was clipped on the left, a trailing … iff clipped on the right, so
		// truncation is honest (mirrors Excerpt's markers, but only where actually
		// clipped rather than unconditionally). The needle-based overload above is
		// left untouched so the MISPLACED_ANCHOR_EMPTY caller is unaffected.
		internal static string CentredExcerpt(string source, int centrePos, int maxLength)
		{
			if (string.IsNullOrEmpty(source))
			{
				return string.Empty;
			}

			if (centrePos < 0)
			{
				centrePos = 0;
			}

			if (centrePos > source.Length)
			{
				centrePos = source.Length;
			}

			var half = maxLength / 2;
			var start = Math.Max(0, centrePos - half);
			var end = Math.Min(source.Length, start + maxLength);
			// Adjust start if end was clamped so we still show maxLength chars where possible.
			start = Math.Max(0, end - maxLength);

			var body = source[start..end].Replace('\n', ' ').Replace('\r', ' ');
			var lead = start > 0 ? "\u2026" : string.Empty;          // … iff clipped on the left
			var tail = end < source.Length ? "\u2026" : string.Empty; // … iff clipped on the right
			return $"{lead}{body}{tail}";
		}

		private static string Excerpt(string text, int pos, int radius)
		{
			int start = Math.Max(0, pos - radius / 2);
			int end = Math.Min(text.Length, pos + radius / 2);
			var excerpt = text[start..end].Replace('\n', ' ').Replace('\r', ' ');
			return $"...{excerpt}...";
		}

		/// <summary>
		/// Builds a quote excerpt. When fullSentence is true, expands outward from
		/// pos to the nearest sentence boundary (. ! ?) in each direction, capped at
		/// maxLength characters total. Falls back to a fixed-radius excerpt if no
		/// sentence boundary is found within the cap.
		/// </summary>
		internal static string QuoteExcerpt(string text, int pos,
			bool fullSentence, int maxLength)
		{
			if (!fullSentence)
			{
				return Excerpt(text, pos, maxLength);
			}

			int half = maxLength / 2;

			// Expand left to sentence boundary.
			int start = pos;
			for (int i = pos - 1; i >= Math.Max(0, pos - half); i--)
			{
				if (text[i] == '.' || text[i] == '!' || text[i] == '?')
				{
					start = i + 1;
					break;
				}
				if (i == Math.Max(0, pos - half))
				{
					start = i;
				}
			}

			// Expand right to sentence boundary.
			int end = pos;
			for (int i = pos + 1; i < Math.Min(text.Length, pos + half); i++)
			{
				if (text[i] == '.' || text[i] == '!' || text[i] == '?')
				{
					end = Math.Min(text.Length, i + 1);
					break;
				}
				if (i == Math.Min(text.Length, pos + half) - 1)
				{
					end = i + 1;
				}
			}

			// Cap total length.
			if (end - start > maxLength)
			{
				int centre = (start + end) / 2;
				start = Math.Max(0, centre - maxLength / 2);
				end = Math.Min(text.Length, start + maxLength);
			}

			var excerpt = text[start..end].Trim().Replace('\n', ' ').Replace('\r', ' ');
			var prefix = start > 0 ? "..." : string.Empty;
			var suffix = end < text.Length ? "..." : string.Empty;
			return $"{prefix}{excerpt}{suffix}";
		}

		internal static string FindFirstQuoteContext(string text,
			bool fullSentence, int maxLength)
		{
			for (int i = 0; i < text.Length; i++)
			{
				if (AllOpeners.Contains(text[i]))
				{
					return QuoteExcerpt(text, i, fullSentence, maxLength);
				}
			}
			return "";
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
	}
}
