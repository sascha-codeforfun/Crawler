using System.Net;
using HtmlAgilityPack;

namespace Crawler.Quality
{
	/// <summary>
	/// Quote-system and pairing analysis over a parsed document.
	///
	/// [KEEP] Coverage note — expected, permanent partial coverage in the
	/// document-filtering paths. This class only ever consumes documents parsed
	/// by HtmlAgilityPack. HAP treats &lt;script&gt; and &lt;style&gt; as CDATA:
	/// their inner content is captured as raw text, never as child elements. So
	/// a block-level element (p, h1–h6, li, td, th) can never appear with a
	/// &lt;script&gt; or &lt;style&gt; parent in any document this class receives.
	/// Wherever such parents are excluded from checking, the script/style arms of
	/// that exclusion are therefore unreachable by construction — only the
	/// &lt;noscript&gt; arm (which HAP does parse as markup) is exercisable, and is
	/// covered by test. Likewise a descendant node always has a parent, so any
	/// null-parent branch in such a filter is unreachable. These gaps are not
	/// oversights and cannot be closed by a realistic fixture; greening them would
	/// require hand-built node trees the application never produces. Premise to
	/// re-check if it ever changes: that input is exclusively HAP-parsed. If this
	/// class is ever fed non-HAP or hand-constructed node trees, revisit whether
	/// those arms become reachable.
	/// </summary>
	internal static class Quotes
	{
		// Each system has a set of openers and closers. A page mixes systems when it
		// uses openers from more than one system. A wrong closer is one from a different
		// system than the opener that started the current quote.

		// D052: Languages tags the language(s) for which this system is the correct
		// typography. The SYSTEM_MIX offender selection uses it: on a single-language
		// page, an opener whose system is NOT valid for that language is the offender,
		// regardless of textual order. Empty = sanctioned by no language (ornamental).
		private record QuoteSystem(string Name, HashSet<char> Openers, HashSet<char> Closers, HashSet<string> Languages);

		private static readonly QuoteSystem[] QuoteSystems =
		[
			new("German-double",
				Openers:  ['\u201E'],          // „  U+201E DOUBLE LOW-9 QUOTATION MARK (99-Zeichen unten)
				Closers:  ['\u201C'],          // "  U+201C LEFT DOUBLE QUOTATION MARK (66-Zeichen oben) — correct German closer
				                               //    Reference: https://anfuehrungszeichen.de/
				Languages: ["de"]),

			// [KEEP] German-guillemet and French-guillemet use identical characters «»
			// — merging into one system prevents false QUOTE_SYSTEM_MIX detection.
			// D052: tagged {fr}. Standard German (de) does NOT use guillemets, so a de
			// page using «» is flagged. Swiss German (de-CH) does — handled separately
			// once region is preserved through PageLanguageSet (parked); until then the
			// base code "de" intentionally excludes guillemets.
			new("Guillemet",
				Openers:  ['\u00AB'],          // «  LEFT-POINTING DOUBLE ANGLE QUOTATION MARK
				Closers:  ['\u00BB'],          // »  RIGHT-POINTING DOUBLE ANGLE QUOTATION MARK
				Languages: ["fr"]),

			// [KEEP] " is BOTH the German-double closer AND the English-double opener.
			// German-double:  „....“  (U+201E opens, U+201C closes — 99/66 Zeichen)
			// English-double: “...”  (U+201C opens, U+201D closes — 66/99 Zeichen)
			// Mix detection checks openers only so the shared character causes no false
			// positives when per-block scoping is used.
			// Reference: https://anfuehrungszeichen.de/
			new("English-double",
				Openers:  ['“'],          // “  U+201C LEFT DOUBLE QUOTATION MARK (66-Zeichen oben)
				Closers:  ['”'],          // ”  U+201D RIGHT DOUBLE QUOTATION MARK (99-Zeichen oben)
				Languages: ["en"]),

			// Shares the U+201E opener with German-double but closes with U+201D
			// instead of U+201C. The two are distinguished by page language: the
			// pairing walk and the mix-offender selection resolve the shared opener
			// via SystemForOpener, so „…“ stays correct for de while „…” is correct
			// for the languages tagged here. Listed AFTER German-double (so the
			// declaration-order fallback for U+201E remains German-double) and AFTER
			// English-double (so the U+201D close-system label is unchanged).
			new("Slavic-double",
				Openers:  ['\u201E'],          // „  U+201E DOUBLE LOW-9 QUOTATION MARK
				Closers:  ['\u201D'],          // ”  U+201D RIGHT DOUBLE QUOTATION MARK
				Languages: ["pl", "ro", "bg", "cs"]),

			new("Heavy",
				Openers:  ['\u275D'],          // ❝
				Closers:  ['\u275E'],          // ❞
				Languages: []),                // ornamental — sanctioned by no language

			new("German-single",
				Openers:  ['\u201A'],          // ‚
				Closers:  ['\u2019'],          // '
				Languages: ["de"]),

			new("English-single",
				Openers:  ['\u2018'],          // '
				Closers:  ['\u2019'],          // '
				Languages: ["en"]),

			new("Angle-single",
				Openers:  ['\u2039'],          // ‹
				Closers:  ['\u203A'],          // ›
				Languages: ["fr"]),
		];

		// Flat sets for fast membership tests.
		private static readonly HashSet<char> AllOpeners =
			[.. QuoteSystems.SelectMany(s => s.Openers)];

		private static readonly HashSet<char> AllClosers =
			[.. QuoteSystems.SelectMany(s => s.Closers)];

		// Straight (ASCII) double quote U+0022. Belongs to NO QuoteSystem on purpose:
		// putting it in QuoteSystems would pull it into AllOpeners/AllClosers, feed it
		// into SYSTEM_MIX, and start pairing consistent straight quotes as a system.
		// Instead it is handled as a special case in the pairing walk for KIND
		// consistency only (D053): a typographic pair closed/opened by a straight quote
		// is QUOTE_MIXED_KIND; a CONSISTENT straight pair ("…") is left alone, since the
		// tool can't know the surrounding text isn't a context where ASCII quotes are
		// correct (code, quoted source) — only the inconsistency is unambiguously wrong.
		private const char StraightDouble = '\u0022';

		// Marker pushed onto the pairing stack for a straight opener. NOT a member of
		// QuoteSystems, so it never appears in AllOpeners/AllClosers or SYSTEM_MIX.
		private static readonly QuoteSystem StraightSystem =
			new("Straight", [StraightDouble], [StraightDouble], []);

		// A typographic double system (German-double, English-double, Guillemet, Heavy)
		// — the kinds whose pairing with a straight quote is a mixed-kind defect.
		private static bool IsTypographicDouble(QuoteSystem sys) =>
			sys.Name != StraightSystem.Name
			&& !sys.Name.Contains("single", StringComparison.OrdinalIgnoreCase)
			&& !sys.Name.Contains("angle", StringComparison.OrdinalIgnoreCase);
		private const char StraightSingle = '\u0027';

		// Resolve the system for an opener glyph. When a glyph opens more than one
		// system (U+201E opens both German-double and Slavic-double), the correct
		// one depends on the page language — „…“ closes with U+201C for de, „…”
		// with U+201D for the Slavic-double languages. Prefer a system that both
		// opens the glyph AND is valid for the language; with no page language, or
		// none whose system claims the glyph, fall back to declaration order. For a
		// glyph opening a single system this always returns that system, so every
		// non-shared opener behaves exactly as the bare First() lookup did.
		private static QuoteSystem SystemForOpener(char ch, string? pageLanguage)
		{
			if (pageLanguage is { } lang)
			{
				foreach (var s in QuoteSystems)
				{
					if (s.Openers.Contains(ch) && s.Languages.Contains(lang))
					{
						return s;
					}
				}
			}

			return QuoteSystems.First(s => s.Openers.Contains(ch));
		}

		internal static IEnumerable<QualityIssue> Check(
			string filename, HtmlDocument doc, ContentQualityConfig config,
			IReadOnlyDictionary<string, List<string>>? pageLanguageOverrides = null,
			string defaultLanguage = "")
		{
			// [KEEP] Quote checks run per block element, not on concatenated page text.
			// Cross-block quote pairs are not detected — a quote must open and close
			// within the same block element to be considered a pair.
			// This prevents false positives where openers from one element and closers
			// from another (completely unrelated) element create phantom mismatches.
			var blockElements = config.ContentQualityBlockElements
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			// Resolve the page's declared language SET via the shared resolver
			// (override → <html lang> → <meta language> → default). The quote SYSTEM
			// check needs a single anchor: it can only assert "this must be German
			// „…"" when exactly one language is declared. Zero declared (empty set —
			// undeclared, since defaultLanguage is empty here) or more than one (a
			// multi-language page, e.g. a de+en override) means no anchor, so
			// pageLanguage is null and only the language-agnostic structural checks
			// (unbalanced; curly↔straight kind mismatch) run. The html↔meta
			// disagreement is a separate finding owned by LanguageMismatch.
			// The URL is only needed to match a PageLanguageOverrides path prefix, so
			// resolve it only when overrides exist. This also keeps Check free of the
			// CrawlIndex/Logger initialisation that LookUpUrlForFile requires — relevant
			// for unit tests, and a small saving for runs without override config.
			var url = pageLanguageOverrides is { Count: > 0 }
				? CrawlIndex.LookUpUrlForFile(filename)
				: string.Empty;
			var languageSet = Crawler.Html.PageLanguageSet.Resolve(
				url, doc, pageLanguageOverrides, defaultLanguage);
			var pageLanguage = languageSet.Count == 1 ? languageSet[0] : null;

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
				// [KEEP] Determine which quote SYSTEMS act in this block, accounting for
				// ambiguous characters like \u201C which is both a German-double closer
				// and an English-double opener. We simulate pairing to determine role.
				// Reference: https://anfuehrungszeichen.de/
				// Each opener resolves to exactly one system (language-aware: the
				// U+201E shared opener is German-double for de, Slavic-double for the
				// Slavic-double languages). systemsPresent is built from the resolved
				// names actually pushed — NOT from every system whose opener set
				// contains the glyph, which would count a single „ as two systems and
				// trip a spurious SYSTEM_MIX once a second system shares that opener.
				var resolvedSystemNames = new HashSet<string>();
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
							var sys = SystemForOpener(ch, pageLanguage);
							simStack.Push((ch, sys));
							resolvedSystemNames.Add(sys.Name);
						}
					}
					else if (AllClosers.Contains(ch) && simStack.Count > 0)
					{
						simStack.Pop();
					}
				}

				var systemsPresent = resolvedSystemNames.ToList();

				if (systemsPresent.Count > 1)
				{
					var doubleSystemsPresent = systemsPresent
						.Where(s => !s.Contains("single", StringComparison.OrdinalIgnoreCase)
							&& !s.Contains("angle", StringComparison.OrdinalIgnoreCase))
						.ToList();

					if (doubleSystemsPresent.Count > 1)
					{
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
				foreach (var issue in CheckPairing(filename, text, config, pageLanguage))
				{
					yield return issue;
				}
			}
		}

		internal static IEnumerable<QualityIssue> CheckPairing(
			string filename, string text, ContentQualityConfig config,
			string? pageLanguage = null)
			=> BuildQuotePairingFlags(filename, text, config, pageLanguage).Select(f => f.issue);

		// Position-bearing core of the pairing check: identical matching +
		// verification, but returns each flag WITH its trigger position so triage
		// can highlight the exact offending glyph instead of every quote in the
		// block. CheckPairing is the position-stripping projection over this
		// (behaviour unchanged — the existing pairing tests exercise it via that
		// wrapper). Kept as a separate method because LocateFlags needs the
		// positions; do not inline back.
		private static List<(QualityIssue issue, int triggerPos)> BuildQuotePairingFlags(
			string filename, string text, ContentQualityConfig config,
			string? pageLanguage = null)
		{
			// [KEEP] Internal flag tracking carries the trigger position alongside
			// each QualityIssue. The position is needed by LocateFlags for triage
			// highlighting (painting the exact offending glyph) and by the card's
			// "Offenders" hex line. Position is internal plumbing — never exposed
			// on QualityIssue itself.
			List<(QualityIssue issue, int triggerPos)> flagged = [];
			var stack = new Stack<(char opener, int position, QuoteSystem system)>();

			// Resolve the per-language elision profile. Lookup uses lowercase
			// invariant keys because the dictionary's case-insensitive comparer
			// is lost during JSON deserialization (System.Text.Json replaces the
			// property's dictionary instance with a fresh ordinal-comparer one).
			// pageLanguage is already lowercased in Check; we lowercase
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
					// Rule 1a — Suffix elision: the letters AFTER the apostrophe
					// match a configured suffix entry that ENDS at a word boundary
					// (next char is a non-letter or end-of-text). The boundary check
					// separates a real elision (it's, 'ner Kerl — "ner" then space)
					// from an opening single quote whose quoted word merely STARTS
					// with a suffix letter ('show → 's'+how, 'Request → 're'+quest):
					// there the suffix matches but more letters follow, so it is not
					// a boundary and the quote is left to pair. Evaluated across all
					// entries (longest-match-by-survival): German 'ner reaches the
					// space via the "ner" entry even though "n"/"ne" match shorter.
					// No letter-BEFORE requirement, so front-elisions ('ner, 'mal —
					// apostrophe starts the word) survive, which a letter-before
					// guard would wrongly reject. Match is case-insensitive.
					var suffixMatched = suffixElisions.Any(e =>
						e.Length > 0 &&
						text.Length - (i + 1) >= e.Length &&
						text.AsSpan(i + 1, e.Length).Equals(e, StringComparison.OrdinalIgnoreCase) &&
						(i + 1 + e.Length >= text.Length
							|| !char.IsLetter(text[i + 1 + e.Length])));
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

				// ── Straight double-quote kind consistency (D053) ───────────────────
				// U+0022 is in no QuoteSystem, so without this it falls through the whole
				// loop and a typographic opener is reported as "unclosed". Here it is
				// resolved for KIND: a typographic opener closed by a straight quote is
				// QUOTE_MIXED_KIND; a straight opener is pushed (the reverse mix — straight
				// open, typographic close — is caught in the closer branch below); a
				// straight opener closed by a straight quote is a consistent pair, popped
				// silently (never flagged — see the StraightDouble note above).
				if (ch == StraightDouble)
				{
					if (stack.Count > 0 && IsTypographicDouble(stack.Peek().system))
					{
						var (opener, _, openSystem) = stack.Pop();
						flagged.Add((new QualityIssue(
							filename,
							"QUOTE_MIXED_KIND",
							$"Typographic opener '{opener}' ({openSystem.Name}) closed by straight quote \" (U+0022)",
							text), i));
					}
					else if (stack.Count > 0 && stack.Peek().system.Name == StraightSystem.Name)
					{
						stack.Pop();   // consistent straight pair "…" → deliberately not flagged
					}
					else
					{
						stack.Push((ch, i, StraightSystem));   // straight opener
					}

					continue;
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
						var system = SystemForOpener(ch, pageLanguage);
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
							if (openSystem.Name == StraightSystem.Name)
							{
								// Straight opener closed by a typographic quote → mixed kind.
								flagged.Add((new QualityIssue(
									filename,
									"QUOTE_MIXED_KIND",
									$"Straight opener \" (U+0022) closed by typographic '{ch}' (U+{(int)ch:X4})",
									text), i));
							}
							else
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

			// The detector emits findings directly — no second proximity pass.
			// (Removed: the verification pass downgraded a flag to QUOTE_AMBIGUOUS
			// when a stricter re-walk could pair it. Its only real job was masking
			// the cheap pass's elision over-eating; the boundary-after suffix rule
			// (Rule 1a) fixes that at the source, so a flag that survives is a
			// genuine finding to present, not something to soften.)
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
		internal static IReadOnlyList<(int Pos, string Type)> LocateFlags(
			string text, ContentQualityConfig config, string? pageLanguage = null)
		{
			var result = new List<(int, string)>();
			if (string.IsNullOrEmpty(text))
			{
				return result;
			}

			var mix = LocateSystemMixMismatch(text, pageLanguage);
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
		/// Character position of the opener that makes a block trip QUOTE_SYSTEM_MIX —
		/// the glyph the highlighter marks red. Single and angle systems are excluded;
		/// returns -1 when there is no double-system mix. Pure.
		///
		/// Context-wins stack walk: a glyph that closes the system on top of the stack
		/// (e.g. U+201C closing a German „) is a CLOSER, popped and never nominated —
		/// without this a correctly-paired German closer (which shares its codepoint
		/// with the English-double opener) would be falsely marked as the mixer.
		///
		/// D052 — offender selection:
		///   • Anchored (pageLanguage set, a single declared language): the offender is
		///     the first opener whose system is NOT valid for that language, regardless
		///     of textual order — provided a valid-for-the-language system is also
		///     present (otherwise it isn't a "this vs the page language" mix; the
		///     consistently-wrong-system case is a separate, parked check, so we fall
		///     through to order-based selection rather than suppress).
		///   • Unanchored (multi-language or undeclared): the first opener that diverges
		///     from the first double system seen — the pre-D052 behaviour, unchanged.
		/// </summary>
		internal static int LocateSystemMixMismatch(string text, string? pageLanguage = null)
		{
			if (string.IsNullOrEmpty(text))
			{
				return -1;
			}

			// One context-aware pass collecting each double-system opener actually
			// pushed (closers and single/angle systems excluded), with its position.
			var simStack = new Stack<QuoteSystem>();
			var doubleOpeners = new List<(int Pos, QuoteSystem Sys)>();
			for (int i = 0; i < text.Length; i++)
			{
				var ch = text[i];
				if (AllOpeners.Contains(ch))
				{
					if (AllClosers.Contains(ch)
						&& simStack.Count > 0
						&& simStack.Peek().Closers.Contains(ch))
					{
						simStack.Pop();
						continue;
					}

					var sys = SystemForOpener(ch, pageLanguage);
					simStack.Push(sys);

					if (sys.Name.Contains("single", StringComparison.OrdinalIgnoreCase)
						|| sys.Name.Contains("angle", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					doubleOpeners.Add((i, sys));
				}
				else if (AllClosers.Contains(ch) && simStack.Count > 0)
				{
					simStack.Pop();
				}
			}

			// A mix requires at least two distinct double systems.
			if (doubleOpeners.Select(o => o.Sys.Name).Distinct().Count() < 2)
			{
				return -1;
			}

			// Anchored: blame the system that doesn't belong to the page language —
			// but only when a system that DOES belong is also present (a genuine
			// page-language-vs-other mix). If none of the present systems is valid for
			// the language, this is a consistently-wrong-system block (parked); fall
			// through to order-based selection so the mix is still surfaced, never
			// suppressed.
			if (pageLanguage is { } lang
				&& doubleOpeners.Any(o => o.Sys.Languages.Contains(lang)))
			{
				foreach (var o in doubleOpeners)
				{
					if (!o.Sys.Languages.Contains(lang))
					{
						return o.Pos;
					}
				}

				return -1;   // every present system is valid for the language → no mismatch
			}

			// Unanchored fallback: first opener diverging from the first double system.
			var dominant = doubleOpeners[0].Sys.Name;
			foreach (var o in doubleOpeners)
			{
				if (!string.Equals(o.Sys.Name, dominant, StringComparison.OrdinalIgnoreCase))
				{
					return o.Pos;
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
				return Excerpt.Around(text, pos, maxLength);
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

		// [INVESTIGATE] No source caller as of D041 — only exercised by tests. Possible
		// migration orphan (handoff "orphan sweep"). Retained with the cluster pending a
		// decision; do not assume live.
		internal static string FindFirstContext(string text,
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
	}
}
