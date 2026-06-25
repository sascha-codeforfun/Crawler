namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text.RegularExpressions;
	using HtmlAgilityPack;

	/// <summary>
	/// A self-describing spell-check result for ONE OCCURRENCE of a misspelled word. Unlike the
	/// old pipeline — which flagged a bare word and then tried to RE-ASSOCIATE it with a location
	/// through a word-keyed map, collapsing multiple occurrences to whichever won a first-wins
	/// race — a finding carries its origin from birth: the node it came from, its source kind,
	/// and the character span of the word within its run. Multiple occurrences of the same word
	/// on a page produce multiple findings, each separately located, so every fix-site is
	/// discoverable. The excerpt is intentionally left to a later stage (SourceKind-aware): a
	/// text-node finding's context is its surrounding block, an attribute finding's context is
	/// the attribute value itself.
	/// </summary>
	public sealed record SpellFinding(
		string Word,
		string Suggestions,
		string Language,
		RunSource Source,
		string SourcePath,
		HtmlNode Node,
		int Start,
		int Length)
	{
		/// <summary>
		/// Excerpt context carried ON the finding, used ONLY for <see cref="RunSource.Script"/>:
		/// a raw-source window around the literal (the surrounding assignment/call), built at
		/// extraction time so the excerpt shows the code an operator needs to judge a bare technical
		/// id — context that cannot be re-derived from the DOM node, which for a script literal holds
		/// the raw undecoded body, not the decoded string that was checked. Null for every other
		/// source (their excerpt is rebuilt from the node as before).
		/// </summary>
		public string? ExcerptText { get; init; }
	}

	/// <summary>
	/// One miss returned by a checker: the misspelled word plus any suggestions. This is the
	/// minimal shape the new module needs from whatever does the actual dictionary/compound
	/// work, so the checker can be injected (and faked in tests) rather than welded to a
	/// specific implementation.
	/// </summary>
	public readonly record struct CheckMiss(string Word, string Suggestions);

	/// <summary>
	/// Delegate for the actual spell decision over a run's canonical text. In production this
	/// wraps the existing Hunspell/compound/prefix/cross-language logic verbatim (see
	/// ToolsSpellChecker) — the new module does NOT reimplement that subtle, oracle-critical
	/// logic. It is a delegate so the finding-assembly spine can be tested in isolation with a
	/// fake, and so the new module is not coupled to where the checker currently lives (it
	/// moved cleanly when Tools.cs was dissolved).
	///
	/// Contract: given the run's canonical text and its language, return the set of words that
	/// fail the check. The text is whole-run (not per-token) so source-context-sensitive rules
	/// — e.g. the trailing-hyphen compound scan — keep working exactly as before.
	/// </summary>
	public delegate IEnumerable<CheckMiss> RunCheck(string canonicalRunText, string language);

	/// <summary>
	/// Turns a run into located <see cref="SpellFinding"/>s, ONE PER OCCURRENCE:
	///   1. canonicalize the run's text ONCE (shared rule feeding both check and span lookup);
	///   2. run the whole canonical text through the injected checker to get the set of
	///      misspelled WORDS (the checker is a verdict — it dedupes by word; that is fine);
	///   3. tokenize the same canonical text and emit a finding for EVERY token whose word is
	///      in the misspelled set — each with its own real span on the originating node.
	///
	/// Per-occurrence is deliberate and load-bearing: a word may appear in several fragments of
	/// one run/page, and each is a distinct, separately discoverable place a human must fix. The
	/// checker collapses to a word verdict; OUR tokenizer re-expands to located occurrences. The
	/// later aggregation stage groups occurrences into a (word, url) ticket while keeping every
	/// occurrence's location and excerpt — so the human sees all of them, and the best-context
	/// excerpt is chosen deterministically rather than by the old first-wins race.
	///
	/// A run is single-sourced (one node, one source kind), so every finding is node-bound: the
	/// run IS the node. There is no word-keyed map across runs and no attribute-vs-text race.
	/// </summary>
	public static class RunChecker
	{
		/// <summary>
		/// Out-of-band report from the 643 Script prose-ratio gate for ONE literal. The gate keys
		/// on the union-miss set the checker already produced, so it is nearly free: a literal whose
		/// word tokens are mostly non-words (a minified-bundle machine list, a bare id/event/glyph
		/// token, a kebab key) is demoted WHOLE and emits nothing. Passed in by the caller (the
		/// file-scan path) so it can write a one-line audit note for the demoted literal; left null
		/// by every path that does not run the gate. Only populated when the gate actually evaluates
		/// (a literal that the structural <see cref="ValueClassifier.ClassifyScriptLiteral"/> gate
		/// already dropped, or one with no union-miss, never reaches the ratio test, so Evaluated
		/// stays false and the caller writes no note).
		/// </summary>
		public sealed class ScriptGateInfo
		{
			public bool Evaluated { get; set; }
			public bool Gated { get; set; }
			public int TotalTokens { get; set; }
			public int MissTokens { get; set; }
			public double Ratio => TotalTokens == 0 ? 0.0 : (double)MissTokens / TotalTokens;
		}

		/// <summary>Single-language convenience overload — delegates to the union version.</summary>
		public static IEnumerable<SpellFinding> Check(TextRun run, string language, RunCheck check, KnownDefectMatcher? knownDefects = null, bool heuristicNonProseDataAttributeSuppression = false, IReadOnlySet<string>? collisionWords = null, IReadOnlySet<string>? scriptTokensToFilter = null, string? scriptFallbackLanguage = null, bool scriptProseRatioGate = false, double scriptProseRatioTau = 0.0, ScriptGateInfo? scriptGateInfo = null, IReadOnlyDictionary<string, IReadOnlySet<string>>? anchorSplitTails = null, IReadOnlySet<string>? unwantedPatternWords = null, IReadOnlyDictionary<string, IReadOnlySet<string>>? adjacentAnchorJoins = null)
		{
			return Check(run, new[] { language }, check, knownDefects, heuristicNonProseDataAttributeSuppression, collisionWords, scriptTokensToFilter, scriptFallbackLanguage, scriptProseRatioGate, scriptProseRatioTau, scriptGateInfo, anchorSplitTails, unwantedPatternWords, adjacentAnchorJoins);
		}

		public static IEnumerable<SpellFinding> Check(TextRun run, IReadOnlyList<string> languages, RunCheck check, KnownDefectMatcher? knownDefects = null, bool heuristicNonProseDataAttributeSuppression = false, IReadOnlySet<string>? collisionWords = null, IReadOnlySet<string>? scriptTokensToFilter = null, string? scriptFallbackLanguage = null, bool scriptProseRatioGate = false, double scriptProseRatioTau = 0.0, ScriptGateInfo? scriptGateInfo = null, IReadOnlyDictionary<string, IReadOnlySet<string>>? anchorSplitTails = null, IReadOnlySet<string>? unwantedPatternWords = null, IReadOnlyDictionary<string, IReadOnlySet<string>>? adjacentAnchorJoins = null)
		{
			// Whole-value gate for attribute/meta runs: a value must look like prose to be checked.
			// This is what keeps URLs, paths, JSON, digit/hex runs and high-entropy tokens (session
			// ids, hashes, base64) out of the checker — they are not words and would otherwise flood
			// the output with false findings. Text-node runs are prose by construction and always
			// pass. (The classifier judges the value's SHAPE only, never the attribute name.)
			//
			// data-* runs additionally get the heuristic shape gate (ClassifyDataAttribute) when the
			// switch is on. This is scoped strictly to data-* ATTRIBUTE runs via SourcePath: text
			// nodes never enter this branch (they short-circuit on the Source check above), and
			// script-derived content is NOT a data-* attribute run — so the slug/selector heuristics
			// can never silently pre-filter JS prose if/when script checking is enabled.
			if (run.Source != RunSource.TextNode)
			{
				// A decoded JS string literal: gated by the script-specific classifier (slug /
				// path / structured / underscore / acronym, etc.). Kept distinct from the data-*
				// heuristics on purpose — a script literal is never a data-* attribute run.
				if (run.Source == RunSource.Script)
				{
					if (!ValueClassifier.ClassifyScriptLiteral(run.RawText, scriptTokensToFilter).ShouldCheck)
					{
						yield break;
					}

					// Identifier-lookup guard (both script paths): if this literal is the quoted
					// argument to a DOM identifier lookup (getElementById, …), it is an element
					// id / name / class reference, never prose — drop the whole literal. Shape
					// cannot catch a lowercase wordish id; only the surrounding call signature can.
					// See IsIdentifierLookupArgument.
					if (IsIdentifierLookupArgument(run.RawText, run.ScriptContext))
					{
						yield break;
					}
				}
				else
				{
					bool dataAttr = heuristicNonProseDataAttributeSuppression && IsDataAttributeRun(run.SourcePath);
					if (dataAttr)
					{
						// Name-guarded positional rule: a data-* attribute whose NAME carries an "align"
						// tell, holding an EXACT correctly-spelled positional keyword, is suppressed. By
						// construction this can only ever decline to flag a correctly-spelled English
						// positional word (top/left/center/...) — a non-finding that surfaced only because
						// the value is English against a non-English dictionary. A misspelling or odd-case
						// value fails the exact match and stays checked.
						if (AttributeNameHasAlignTell(run.SourcePath)
							&& ValueClassifier.IsExactPositionalKeyword(run.RawText))
						{
							yield break;
						}
					}

					var verdict = dataAttr
						? ValueClassifier.ClassifyDataAttribute(run.RawText)
						: ValueClassifier.Classify(run.RawText);
					if (!verdict.ShouldCheck)
					{
						yield break;
					}
				}
			}

			string canonical = Canonicalizer.Canonicalize(run.RawText);

			// 643: class-2 placeholder strip. A merge tag (##WZ##, ##GKLfrom##) embedded in a real
			// sentence would otherwise flag its body (WZ) AND survive as noise; stripping it to a
			// space recovers the surrounding prose and removes the tag. Script + ratio-gate path ONLY
			// (the file scan), applied AFTER the structural classifier and BEFORE the union/tokenize,
			// so the live per-page path is byte-for-byte untouched. {{...}} / ${...} need no strip
			// here: a literal containing either is already dropped wholesale upstream by Classify
			// (SkipTemplate / SkipStructured) and never reaches this point.
			if (run.Source == RunSource.Script && scriptProseRatioGate)
			{
				canonical = StripScriptPlaceholders(canonical);
			}

			if (canonical.Length == 0)
			{
				yield break;
			}

			// UNION over the page's languages: a word is a real miss only if EVERY language's checker
			// misses it (it passes if ANY dictionary accepts it). Seed the candidate set from the first
			// language, then drop any word a later language accepts. For the common single-language
			// case this is exactly the old behaviour with one pass.
			//
			// Script-only fallback: when configured AND this is a script run, append a fallback
			// dictionary (e.g. English) to the union so a token it accepts is not a miss even though the
			// page's language(s) reject it — stripping JS/markup keywords and foreign UI strings that are
			// not typos in the page's language. Appended LAST so the page languages seed the candidates;
			// the displayed language tag (below) stays the page's languages, since the fallback is a
			// noise filter, not a language the word "failed". Skipped when already among the page set.
			IReadOnlyList<string> unionLanguages = languages;
			if (run.Source == RunSource.Script
				&& !string.IsNullOrWhiteSpace(scriptFallbackLanguage)
				&& !languages.Contains(scriptFallbackLanguage, StringComparer.OrdinalIgnoreCase))
			{
				unionLanguages = new List<string>(languages) { scriptFallbackLanguage };
			}

			// [KEEP][PERF] This loop is the file scan's hot path, and the cost lever here is NOT obvious.
			// It runs once per dictionary per shape-passing literal, short-circuiting the moment the
			// miss-set empties (see the `missByWord.Count == 0` break below). Two consequences a future
			// maintainer chasing throughput must not confuse:
			//   1. Dictionary ORDER only helps CLEAN literals. A literal whose words are all accepted by
			//      some dictionary drains to empty and exits early, skipping the dictionaries it never
			//      reached — so leading with the high-hit dictionaries (for the file scan: en, enUS, then
			//      DefaultLanguage/de — derivable from config, no file inspection) lets clean prose exit
			//      in a few passes and skip the heavy tail (e.g. Turkish, whose .aff ruleset dwarfs the
			//      rest). Worth doing because it is free; but it is a SINGLE-DIGIT win, because…
			//   2. …a literal carrying ANY genuine non-word never empties its miss-set, so it runs the
			//      FULL dictionary set regardless of order — and the sum of per-dictionary costs is
			//      order-invariant (addition commutes). In minified bundles these dominate (~40:1 vs
			//      survivors here), so they set the cost FLOOR that ordering cannot lower.
			// The lever with real leverage, if this scan is ever genuinely slow, is therefore NOT
			// reordering: it is that the machine-literal majority runs this full union ONLY to be demoted
			// by the prose-ratio gate afterward (the gate needs the complete miss-set, so the cost is
			// already paid before the gate decides). A shape-prefilter that demotes obvious machine
			// literals BEFORE this loop would skip the whole N-dictionary cost for that majority —
			// potentially an order-of-magnitude cut, vs single digits from reordering. The metric to
			// watch is "literals reaching this union", not the dictionary order each one walks.
			Dictionary<string, string>? missByWord = null;
			foreach (var language in unionLanguages)
			{
				// [KEEP] diag code to enable for quick dev diag
				//var __sw = System.Diagnostics.Stopwatch.StartNew();
				//var misses = check(canonical, language);
				//__sw.Stop();
				//if (__sw.ElapsedMilliseconds > 200)
				//{
				//	System.Console.WriteLine($"SLOW {__sw.ElapsedMilliseconds}ms lang={language} len={canonical.Length} src={run.SourcePath}");
				//	System.Console.WriteLine($"  >>> {canonical}");
				//}
				var misses = check(canonical, language);

				var wordsThisLang = new Dictionary<string, string>(StringComparer.Ordinal);
				if (misses != null)
				{
					foreach (var miss in misses)
					{
						wordsThisLang[miss.Word] = miss.Suggestions;
					}
				}

				if (missByWord == null)
				{
					missByWord = wordsThisLang; // first language seeds the candidates
				}
				else
				{
					// Keep only words ALSO missed here; a word this language accepts is no longer a
					// union-miss. Iterate a snapshot of keys since we mutate the dictionary.
					foreach (var word in missByWord.Keys.ToList())
					{
						if (!wordsThisLang.ContainsKey(word))
						{
							missByWord.Remove(word);
						}
					}
				}

				if (missByWord.Count == 0)
				{
					yield break; // nothing survives the union — done early
				}
			}

			if (missByWord == null || missByWord.Count == 0)
			{
				yield break;
			}

			// Findings are tagged with the FULL set checked, e.g. "de, en, fr", so a reviewer sees the
			// word failed every one of the page's languages (the strongest signal it is a real error).
			string languageTag = string.Join(", ", languages);

			// German Ergänzungsstrich (suspended-compound) rescue applies only when German is among the
			// page's languages — it is a German orthographic convention, irrelevant elsewhere.
			bool germanInSet = languages.Any(l => l != null && l.StartsWith("de", StringComparison.OrdinalIgnoreCase));

			// Re-expand to occurrences: tokenize ONCE (the list is shared by the 643 ratio gate below
			// and the emit loop, so the tokenizer runs a single time per literal — behaviour-identical
			// to the previous streaming form for every non-gated path).
			var canonicalRun = run with { RawText = canonical };
			var tokens = SpellTokenizer.Tokenize(canonicalRun).ToList();

			// 643: prose-ratio gate (Script + file-scan path only; live path leaves the flag off, so
			// this block never runs there). A literal whose WORD tokens are mostly union-misses reads
			// as a run of machine tokens, not prose, and is demoted WHOLE — the single, dictionary-
			// grounded lever that subsumes both leak points: a lone unknown token scores 1/1 = 1.0
			// (ids / events / glyphs / kebab keys), a minified machine list scores high (React dev
			// strings, lowercase event lists), while a real sentence carrying one typo scores low and
			// is KEPT so the typo still surfaces. Counts WORD tokens only (a token with a letter), so
			// punctuation never dilutes the ratio. Computed on the RAW union-miss set, before the
			// per-occurrence rescues below, so a rescue can never flip a literal's prose verdict.
			if (run.Source == RunSource.Script && scriptProseRatioGate)
			{
				int totalWordTokens = 0;
				int missWordTokens = 0;
				foreach (var token in tokens)
				{
					if (!HasLetter(token.Text))
					{
						continue;
					}

					totalWordTokens++;
					if (missByWord.ContainsKey(token.Text))
					{
						missWordTokens++;
					}
				}

				if (totalWordTokens > 0)
				{
					double ratio = (double)missWordTokens / totalWordTokens;
					bool gated = ratio >= scriptProseRatioTau;

					if (scriptGateInfo != null)
					{
						scriptGateInfo.Evaluated = true;
						scriptGateInfo.TotalTokens = totalWordTokens;
						scriptGateInfo.MissTokens = missWordTokens;
						scriptGateInfo.Gated = gated;
					}

					if (gated)
					{
						yield break; // whole literal demoted — emit nothing
					}
				}
			}

			foreach (var token in tokens)
			{
				if (missByWord.TryGetValue(token.Text, out var suggestions))
				{
					// 644: gate-path token veto. A literal can read as prose (low miss-ratio) yet carry a
					// lone non-word token — a code identifier, acronym, slug or universal web/JS term —
					// riding inside otherwise-valid words (e.g. "useEffect" inside an English error
					// string). Such a token leaked here only because it sat in a multi-word literal, so
					// the per-literal classifier never judged it alone. Re-judge the flagged TOKEN now and
					// drop it if it is structurally a non-prose token. The test is language-agnostic and
					// shape/vocabulary-based, so it can never suppress a real misspelling in ANY configured
					// dictionary (a misspelled word is not a slug/acronym/code term). Gate (file-scan)
					// path only; the live per-page path leaves the flag off and is untouched.
					if (scriptProseRatioGate && IsNonProseToken(token.Text, scriptTokensToFilter))
					{
						continue;
					}

					// Mute declared known chrome defects (same offending text from the same element
					// sitewide). Only the word within the declared literal is muted; varying tail
					// content from the same element still surfaces.
					if (knownDefects != null
						&& knownDefects.IsKnownDefect(run.SourcePath, canonical, token.Text))
					{
						continue;
					}

					// Cross-pass dedup: if this token is the merged twin of a WORD_COLLISION that
					// content-quality already reports for this file, mute it here so the defect is
					// reported once (by CQ). The set holds only seam-merged tokens CQ actually
					// emitted, so a genuine word is never in it.
					if (collisionWords != null && collisionWords.Contains(token.Text))
					{
						continue;
					}

					// Cross-pass dedup (content-quality → spelling): an anchor-severed word. If CQ
					// reports a SPLIT_WORD_ANCHOR whose HEAD is this token and whose single severed
					// tail letter rejoins to a real word in the page's language, the spelling "miss"
					// is only the markup wound CQ already owns — mute it so the defect is reported
					// once (by CQ). The tail map is per-file and pre-filtered to the one-letter gate;
					// the rejoin is checked here so it uses the same dictionary the word would
					// normally get. See Crawler.Suppressions.AnchorSplitSpellSuppression for the full
					// rationale, including why the tail is capped at one letter.
					if (anchorSplitTails != null
						&& anchorSplitTails.TryGetValue(token.Text, out var severedTails)
						&& DictionaryAcceptsAnyRejoin(token.Text, severedTails, languages, check))
					{
						continue;
					}

					// Cross-pass dedup (content-quality → spelling): a word fractured across ADJACENT
					// anchors — a CMS editor's consecutive-anchor defect, "<a>And</a><a></a><a>roid</a>".
					// CQ reports the page as ADJACENT_ANCHOR; the traverser ended a segment at each <a>,
					// so each fragment ("And", "roid") landed in its own run and surfaces as a stray miss.
					// The per-file map carries, for each fragment, the verbatim source-order join of its
					// fracture (textA+textB); mute the fragment when that join is a real word in the page's
					// language, so the defect is reported once (by CQ). A bad+bad join is never a word, so
					// a mispaired fracture cannot mute. See Crawler.Suppressions.AdjacentAnchorSpellSuppression.
					if (adjacentAnchorJoins != null
						&& adjacentAnchorJoins.TryGetValue(token.Text, out var anchorJoins)
						&& DictionaryAcceptsAny(anchorJoins, languages, check))
					{
						continue;
					}

					// Cross-pass dedup (content-quality → spelling): a token sitting inside a
					// configured unwanted-pattern delimiter run (e.g. the innards of a leaked CMS
					// placeholder) is junk CQ already reports as UNWANTED_PATTERN — mute it so the
					// defect is reported once (by CQ). The set holds only tokens lifted from inside a
					// qualified delimiter's whitespace-bounded run, so a genuine word is never in it.
					// No dictionary check: it is junk by construction, not prose. See
					// Crawler.Suppressions.UnwantedPatternSpellSuppression for the full rationale.
					if (unwantedPatternWords != null && unwantedPatternWords.Contains(token.Text))
					{
						continue;
					}

					// German Ergänzungsstrich (suspended compound): a lowercase tail like the "-lampe"
					// in "Straßenlaterne/-lampe" tokenizes to a bare "lampe" with the shared head
					// elided, so it fails the dictionary on case alone. When the page is German and the
					// tail sits in a valid suspension context (X/-tail, or X, -tail), the CAPITALIZED
					// tail is the real noun ("Lampe"); if German accepts that, the lowercase flag is a
					// false positive and the occurrence is dropped. Additive and per-occurrence: it can
					// only ever remove a finding, and only when BOTH the suspension marker is present
					// AND the capitalized form is a real German word — so a tail typo ("/-auflsung" →
					// "Auflsung") and a markerless lowercase noun (a genuine case error) still surface.
					if (germanInSet
						&& token.Text.Length > 0
						&& char.IsLower(token.Text[0])
						&& IsErgaenzungsstrichTail(canonical, token.Start)
						&& GermanAcceptsCapitalized(token.Text, languages, check))
					{
						continue;
					}

					// German Ergänzungsstrich (suspended HEAD): the mirror of the tail rescue above. A
					// truncated compound head like "Investitions-" or "Schul-" carries a trailing
					// Ergänzungsstrich and borrows its tail from a COORDINATED sibling compound —
					// "Investitions- oder Avalkredit" (= Investitionskredit), "Schul- / Studienreise"
					// (= Schulreise). The head is a real German word only once rejoined, so standing alone
					// it fails the dictionary. When run-splitting has fractured the head into its own run
					// (each conjunct in its own <a>, the slash/coordinator in the text node between them),
					// the sibling is not in THIS run's canonical text — but it IS in the block the run
					// belongs to. So the sibling is recovered from the block text (run.Node), the shared
					// tail is borrowed from it (a suffix of >= 4 letters), and the occurrence is dropped iff
					// head+tail rejoins to a real German word. Additive and per-occurrence: it only ever
					// removes a finding, and only when BOTH a suspension marker (- followed by / und oder
					// bzw. sowie) is present AND some borrowed-tail rejoin passes the dictionary — so a head
					// typo and a markerless non-word still surface. The dictionary is the sole arbiter;
					// broken markup (control chars, missing space, wrong tail) simply fails the rejoin and
					// stays flagged. The germanInSet/IsUpper gates keep this off non-German pages and off
					// the lowercase-tail case the rescue above already owns.
					if (germanInSet
						&& token.Text.Length > 0
						&& char.IsUpper(token.Text[0])
						&& run.Node != null
						&& TryBorrowSuspendedHeadTail(run.Node, token.Text, languages, check))
					{
						continue;
					}

					// German reduced indefinite article ('nem = einem, 'ne = eine, 'nen = einen,
					// 'ner = einer, 'nes = eines). The tokenizer drops a leading elision apostrophe
					// ("mit 'nem" → bare "nem"), and the stem is not a dictionary word, so it flags. When
					// the page is German, the token is one of the reduced-article stems, AND a proclitic
					// apostrophe immediately precedes it at a left boundary, it is a colloquial elision, not
					// a typo — drop it. No dictionary call: these stems are never words, so the apostrophe-
					// in-context IS the signal. Additive and per-occurrence: a bare "nem"/"ne" with no
					// leading apostrophe still flags, so a real typo that spells a stem is never masked.
					// Limited to the reduced indefinite articles — the only closed, unambiguously-safe
					// clitic family (the single-letter clitics 'n/'m/'r, the enclitic 's that stays inside
					// its token in "geht's", and the apostrophe-less du-clitics "haste"/"biste" are all out
					// of scope and would each need their own mechanism).
					if (germanInSet
						&& GermanReducedArticleClitics.Contains(token.Text)
						&& IsProcliticApostrophe(canonical, token.Start))
					{
						continue;
					}

					// German parenthesised optional suffix ("Nachhaltigkeit(smanagement)" reads as both
					// "Nachhaltigkeit" and "Nachhaltigkeitsmanagement"). The tokenizer splits the
					// parentheses off, so the in-paren fragment ("smanagement", Fugen-s included) is checked
					// alone and fails as a non-word, while the base ("Nachhaltigkeit") is its own token and
					// validated normally. When the page is German, the flagged token sits inside parentheses
					// "(token)" with a base word immediately before the "(", and base+token rejoins to a real
					// word, drop the fragment. The dictionary is the sole arbiter: a junk fragment
					// ("Haus(blah)" → "Hausblah") still flags. This is the suffix-after-base mirror of
					// SpellChecker.TryParenthesizedPrefixJoin (which handles "(prefix-)stem"); the infix form
					// "Kund(inn)en" is deliberately out of scope (multiple fragments, two readings).
					if (germanInSet
						&& TryParenthesizedSuffixJoin(canonical, token.Start, token.Length, token.Text, languages, check))
					{
						continue;
					}

					// Script-only: drop a flagged token that is the HEAD of a LONE JavaScript property
					// access — "head.prop" where "prop" is a single lowercase identifier word standing
					// against a boundary (the end of the literal, or anything that is NOT the start of
					// continuing prose). a state flag written as "node.disabled" is the canonical case: a
					// code fragment, never a word. Two guards keep a German missing-space typo surfaced: the
					// tail must start LOWERCASE (a real sentence break is "Wort.Großwort", a capital tail,
					// never matched), and nothing that looks like continuing prose may follow it (a
					// lowercase word after a space — "verfügbar.bitte erneut …" — leaves the head surfaced).
					// Additive and per-occurrence: it can only ever remove a finding, and only for script.
					if (run.Source == RunSource.Script
						&& IsLonePropertyAccessHead(canonical, token.Start + token.Length))
					{
						continue;
					}

					yield return new SpellFinding(
						token.Text,
						suggestions,
						languageTag,
						run.Source,
						run.SourcePath,
						run.Node,
						token.Start,
						token.Length)
					{
						// Script findings carry their excerpt context: a raw-source window around the
						// literal (the surrounding assignment/call), built at extraction time and held on
						// the run. Falls back to the canonical literal if no window was set. Every other
						// source rebuilds its excerpt from the node, so this stays null for them.
						ExcerptText = run.Source == RunSource.Script ? (run.ScriptContext ?? canonical) : null,
					};
				}
			}
		}

		// 643: strip class-2 merge-tag placeholders (##WZ##, ##GKLfrom##) to a space. Token-shaped
		// body only (\w+ between double-hashes), so it removes machine merge tags without reaching
		// into ordinary prose that might use a stray '#'. Compiled once. Script + ratio-gate only.
		private static readonly Regex ScriptPlaceholderRegex = new(@"##\w+##", RegexOptions.Compiled);

		private static string StripScriptPlaceholders(string s) => ScriptPlaceholderRegex.Replace(s, " ");

		// 644: gate-local, CURATED denylist of universal web / JS / CSS code tokens that are words in NO
		// natural language and that the per-literal classifier does not catch by shape (they are plain
		// lowercase, no case transition / digit / hyphen). Kept HERE, gate-local, NOT added to the shared
		// ValueClassifier.ScriptCodeVocabulary, so the live per-page path and its tests are untouched.
		// Safety property mirrors that set: only EXACT correctly-spelled code tokens are members, so a
		// misspelling is never a member and stays surfaced. Widen by curation on observed need only —
		// never by prefix/shape (shape is handled separately and language-agnostically below).
		private static readonly IReadOnlySet<string> ScriptCodeTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"json", "xml", "html", "svg", "dom", "api", "url", "uri", "utf", "ascii",
			"rgba", "rgb", "hsl", "px", "em", "rem", "vw", "vh", "regex", "regexp",
			"polyfill", "iterable", "bigint", "wasm", "uint", "zlib", "gzip", "keyframe", "keyframes",
			"init", "async", "await", "enum", "const", "nan", "concat", "unshift", "textarea", "config",
			"scrollend", "beforetoggle", "minh", "maxh", "minw", "maxw", "cmap", "mdat", "ajv", "latlng", "latlngs",
		};

		// 644: is the FLAGGED TOKEN itself a non-prose token (structurally code/markup), rather than a
		// misspelled word? Language-agnostic — every branch keys on shape or a universal code vocabulary,
		// never on a specific natural language, so a real misspelling in ANY configured dictionary is
		// never suppressed. Applied only on the gate (file-scan) path.
		private static bool IsNonProseToken(string token, IReadOnlySet<string>? scriptTokensToFilter)
		{
			if (token.Length <= 1)
			{
				return true; // a single character is not a word in any language
			}

			if (IsAllCaps(token))
			{
				return true; // ALLCAPS acronym / constant (MUI, ADTS, WZ) — the classifier's acronym rule
							 // only catches MIXED-case acronym-words, so pure ALLCAPS needs this
			}

			if (ScriptCodeTokens.Contains(token))
			{
				return true; // universal web/JS/CSS code term (json, xml, regex, init, …)
			}

			if (IsLowercaseKebabSlug(token))
			{
				return true; // all-lowercase hyphen-joined slug / kebab key (aria-labelledby,
							 // x-www-form-urlencoded, betriebliche-altersvorsorge) — no natural-language word
			}

			// Reuse the proven, tested per-literal classifier on the lone token: catches camelCase /
			// PascalCase machine slugs, dotted or digit-bearing identifiers, hex, markup, id-ref shapes,
			// and the shared code-vocabulary. The token only reached here because it rode inside a
			// multi-word literal, so the per-literal gate never judged it on its own.
			return !ValueClassifier.ClassifyScriptLiteral(token, scriptTokensToFilter).ShouldCheck;
		}

		// True when every letter is uppercase (digits allowed, but no lowercase letter) and there is at
		// least one letter and length >= 2. Natural-language prose words are not written ALLCAPS; an
		// ALLCAPS token is an acronym or constant, not a misspelling.
		private static bool IsAllCaps(string s)
		{
			bool hasLetter = false;
			foreach (var c in s)
			{
				if (char.IsLetter(c))
				{
					hasLetter = true;
					if (!char.IsUpper(c))
					{
						return false;
					}
				}
				else if (char.IsLower(c))
				{
					return false;
				}
			}

			return hasLetter && s.Length >= 2;
		}

		// A hyphen-joined slug whose every letter is LOWERCASE (letters/digits/hyphens only, at least one
		// hyphen, not leading/trailing): "aria-labelledby", "x-www-form-urlencoded", "prefers-color-scheme",
		// "betriebliche-altersvorsorge". A genuine hyphenated prose compound is Title-cased per segment
		// ("Work-Life-Balance", "E-Mail-Adresse") and carries an uppercase letter, so it is NOT matched.
		// The token reaches this test only as a union-MISS, so a correctly-spelled hyphenated word (which
		// the dictionary accepts) is never even a candidate.
		private static bool IsLowercaseKebabSlug(string s)
		{
			if (s.Length < 3 || s[0] == '-' || s[s.Length - 1] == '-')
			{
				return false;
			}

			bool hasHyphen = false;
			foreach (var c in s)
			{
				if (c == '-')
				{
					hasHyphen = true;
					continue;
				}

				if (char.IsUpper(c) || !char.IsLetterOrDigit(c))
				{
					return false;
				}
			}

			return hasHyphen;
		}

		// [KEEP] Identifier-lookup call signatures: the text that must IMMEDIATELY precede a quoted
		// string literal for that literal to be treated as a DOM identifier reference (id / name /
		// class), not prose, and therefore dropped from spell-checking. This list looks like it
		// could grow freely — it must NOT. Every entry has been vetted against ALL of:
		//   1. it is a CALL signature ("name(" or "chain.method("), never a property key ("key:");
		//   2. the callee's contract makes the argument a SINGLE id / name / class identifier;
		//   3. the argument CANNOT embed a prose expression — this is what excludes the selector
		//      family (querySelector / querySelectorAll / closest / matches): a CSS selector can
		//      carry an attribute match like [title="Bitte warten"], i.e. real prose inside the
		//      string. Selector APIs must never be added here;
		//   4. the signature carries its own ANCHOR (the "classList."/"className."/"getElementsBy…"
		//      prefix), so it cannot collide with an unrelated same-named method — bare "contains("
		//      would match node.contains(node); bare "indexOf(" would match bodyText.indexOf("…prose…").
		// Adding an entry that fails any of these re-opens prose suppression. Object-property keys
		// whose VALUE is a fixed non-word (e.g. an ajax dataType of "json") do not belong here —
		// they go to SpellCheckJavaScript.TokensToFilter, where an exact non-word token is safe.
		private static readonly string[] IdentifierLookupSignatures =
		{
			"getElementById(",
			"getElementsByName(",
			"getElementsByClassName(",
			"classList.contains(",
			"className.indexOf(",
		};

		// True when <paramref name="literal"/> appears in <paramref name="scriptContext"/> as the
		// quoted argument immediately following one of the vetted identifier-lookup signatures
		// (quote-agnostic, tolerant of collapsed whitespace after '('). Anchored to THIS literal:
		// the signature must be followed by a quote, then exactly this literal, then the matching
		// quote — so a window holding several lookups never mis-attributes one literal's verdict to
		// another. Used by the Script branch to drop DOM id/name/class references shape cannot catch.
		private static bool IsIdentifierLookupArgument(string literal, string? scriptContext)
		{
			if (string.IsNullOrEmpty(literal) || string.IsNullOrEmpty(scriptContext))
			{
				return false;
			}

			foreach (var signature in IdentifierLookupSignatures)
			{
				int from = 0;
				while (true)
				{
					int at = scriptContext.IndexOf(signature, from, StringComparison.Ordinal);
					if (at < 0)
					{
						break;
					}

					int i = at + signature.Length;
					while (i < scriptContext.Length && char.IsWhiteSpace(scriptContext[i]))
					{
						i++;
					}

					if (i < scriptContext.Length && (scriptContext[i] == '"' || scriptContext[i] == '\''))
					{
						char quote = scriptContext[i];
						int litStart = i + 1;
						int litEnd = litStart + literal.Length;
						if (litEnd < scriptContext.Length
							&& string.CompareOrdinal(scriptContext, litStart, literal, 0, literal.Length) == 0
							&& scriptContext[litEnd] == quote)
						{
							return true;
						}
					}

					from = at + signature.Length;
				}
			}

			return false;
		}

		// True when the token carries at least one letter — i.e. it is a WORD token rather than one
		// of the tokenizer's standalone punctuation tokens. Unicode-aware so accented letters count.
		private static bool HasLetter(string s)
		{
			foreach (var c in s)
			{
				if (char.IsLetter(c))
				{
					return true;
				}
			}

			return false;
		}

		// True when the text immediately after a flagged token is ".prop" — a single lowercase
		// identifier word — that stands ALONE against a boundary rather than beginning continuing
		// prose. This is the JS property-access shape "head.prop" (e.g. "node.disabled"): the head
		// is the flagged token; the dot and lowercase tail follow it directly in the canonical literal.
		//   • The tail MUST start lowercase — "Wort.Bitte" (capitalised) is a real sentence break, not a
		//     property access, so it is never matched here.
		//   • After the tail, the only thing allowed is a boundary: the end of the literal, or
		//     punctuation, or whitespace that is NOT followed by another lowercase word. A lowercase
		//     word continuing after a space ("verfügbar.bitte erneut …") is prose with a missing space
		//     and is deliberately left surfaced.
		// A dotted CHAIN ("foo.bar.baz") is matched as a side effect (the second '.' after the first
		// tail is a boundary, never the start of prose), which is correct — a chain is even more clearly
		// code. The caller restricts this to script runs; here it judges the surrounding text only.
		private static bool IsLonePropertyAccessHead(string text, int afterHead)
		{
			// Need ".x" right after the head: a dot, then a lowercase letter.
			if (afterHead + 1 >= text.Length || text[afterHead] != '.' || !char.IsLower(text[afterHead + 1]))
			{
				return false;
			}

			// Consume the lowercase-initial identifier tail word.
			int te = afterHead + 1;
			while (te < text.Length && (char.IsLetterOrDigit(text[te]) || text[te] == '_'))
			{
				te++;
			}

			// The tail running to the end of the literal is a lone property access.
			if (te >= text.Length)
			{
				return true;
			}

			// Otherwise prose "continues" only if, past optional spaces, a lowercase letter begins a
			// new word — that is the missing-space-typo shape, which must stay surfaced.
			int k = te;
			while (k < text.Length && text[k] == ' ')
			{
				k++;
			}

			bool proseContinues = k < text.Length && char.IsLower(text[k]);
			return !proseContinues;
		}

		// A token at <paramref name="start"/> is the tail of a German suspended compound
		// (Ergänzungsstrich) when it is immediately preceded by a hyphen that is itself anchored to a
		// real suspension marker: '/' directly before the hyphen (the "X/-tail" form, e.g.
		// "Straßenlaterne/-lampe"), or — scanning left past optional whitespace — a ',' (the
		// "X, -tail" enumeration form, e.g. "Firmenschriftzüge, -logos"). A bare "X/tail" (no hyphen)
		// or "X -tail" (word then spaced hyphen, which the tokenizer joins into one token) is NOT a
		// suspension and is deliberately not matched here.
		private static bool IsErgaenzungsstrichTail(string text, int start)
		{
			if (start < 1 || text[start - 1] != '-')
			{
				return false;
			}

			int k = start - 2; // the character immediately before the hyphen
			if (k < 0)
			{
				return false;
			}

			if (text[k] == '/')
			{
				return true; // X/-tail
			}

			while (k >= 0 && char.IsWhiteSpace(text[k]))
			{
				k--;
			}

			return k >= 0 && text[k] == ','; // X, -tail
		}

		// German reduced indefinite articles (proclitic ein- elisions): "'ne" (eine), "'nem" (einem),
		// "'nen" (einen), "'ner" (einer), "'nes" (eines). The tokenizer drops the leading elision
		// apostrophe, leaving these bare non-word stems that fail the dictionary. Deliberately ONLY the
		// reduced articles — NOT the single-letter clitics ("'n"/"'m"/"'r"; a lone apostrophe+letter is
		// more often a stray quote than a clitic), NOT the enclitic "'s" ("geht's" keeps the apostrophe
		// INSIDE the token so the stem never appears bare), and NOT the apostrophe-less du-clitics
		// ("haste"/"biste"). Tested case-insensitively so a sentence-initial "'Ne …" is covered.
		//
		// Intentionally SEPARATE from the config-driven per-language elision profiles in
		// Crawler.Quality.Quotes (ContentQualityApostropheElisions): those serve quote balancing
		// (elision vs. quote-opener) and may be broad — including real words ("'mal" → "mal") is
		// harmless there. Here the contract is the opposite: a suppression must never hide a typo, so the
		// set is a closed, hardcoded, spell-safe subset, never widened by config.
		private static readonly IReadOnlySet<string> GermanReducedArticleClitics =
			new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				"ne", "nem", "nen", "ner", "nes",
			};

		// True when the token at <paramref name="start"/> is immediately preceded by a PROCLITIC elision
		// apostrophe: an apostrophe at a left boundary (start-of-string, whitespace, or any non-letter),
		// as in "mit 'nem" → token "nem" with the leading "'" dropped by the tokenizer. An INTERNAL
		// apostrophe ("geht's", between two letters) is not proclitic — the tokenizer keeps it inside the
		// token there, so this never matches. Curly apostrophes are already folded to "'" upstream.
		private static bool IsProcliticApostrophe(string text, int start)
		{
			if (start < 1 || text[start - 1] != '\'')
			{
				return false;
			}

			// The char before the apostrophe must be a left boundary: start-of-string, or a non-letter
			// (space, punctuation, quote). A letter there ("x'nem") is not a real proclitic.
			return start - 2 < 0 || !char.IsLetter(text[start - 2]);
		}

		// German parenthesised optional suffix: the token at <paramref name="start"/> sits wholly inside
		// "(token)" — "(" immediately before, ")" immediately after — with a base word of letters ending
		// at the "(". Accepts when base+token rejoins to a real word ("Nachhaltigkeit(smanagement)" →
		// "Nachhaltigkeitsmanagement"), reusing the same dictionary the word would normally get. The
		// suffix-after-base mirror of SpellChecker.TryParenthesizedPrefixJoin ("(prefix-)stem"). The base
		// is its own token and validated separately; this only rescues the in-paren fragment.
		private static bool TryParenthesizedSuffixJoin(string text, int start, int length, string token, IReadOnlyList<string> languages, RunCheck check)
		{
			if (start < 1 || text[start - 1] != '(')
			{
				return false;
			}

			int afterToken = start + length;
			if (afterToken >= text.Length || text[afterToken] != ')')
			{
				return false;
			}

			// Base: the maximal run of letters ending immediately before the "(".
			int baseEnd = start - 1; // index of "("
			int baseStart = baseEnd;
			while (baseStart > 0 && char.IsLetter(text[baseStart - 1]))
			{
				baseStart--;
			}

			if (baseStart >= baseEnd)
			{
				return false; // no base word before the "("
			}

			var baseWord = text[baseStart..baseEnd];
			return DictionaryAcceptsAnyRejoin(
				baseWord, new HashSet<string>(StringComparer.Ordinal) { token }, languages, check);
		}

		// Re-checks the CAPITALIZED form of an elided lowercase tail against the page's German
		// language(s) only. Returns true if any German dictionary accepts it (i.e. the lowercase flag
		// was a false positive from a suspended compound). Capitalises the first letter only, which is
		// all a German noun head needs ("auflösung" → "Auflösung").
		private static bool GermanAcceptsCapitalized(string lowerToken, IReadOnlyList<string> languages, RunCheck check)
		{
			if (lowerToken.Length == 0)
			{
				return false;
			}

			string capitalized = char.ToUpperInvariant(lowerToken[0]) + lowerToken.Substring(1);
			foreach (var language in languages)
			{
				if (language == null || !language.StartsWith("de", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var misses = check(capitalized, language);
				bool missedHere = misses != null
					&& misses.Any(m => string.Equals(m.Word, capitalized, StringComparison.Ordinal));
				if (!missedHere)
				{
					return true; // a German dictionary accepts the capitalized noun
				}
			}

			return false;
		}

		// True if some page language's dictionary accepts head+tail for ANY of the severed tails.
		// Mirrors GermanAcceptsCapitalized: runs the injected checker on the rejoined variant and
		// treats "not a miss" as accepted, so it is the exact check the word would normally get.
		// Used to mute an anchor-severed head whose reunified word is real (see the anchor-split
		// guard above and Crawler.Suppressions.AnchorSplitSpellSuppression).
		private static bool DictionaryAcceptsAnyRejoin(string head, IReadOnlySet<string> tails, IReadOnlyList<string> languages, RunCheck check)
		{
			if (string.IsNullOrEmpty(head) || tails == null)
			{
				return false;
			}

			foreach (var tail in tails)
			{
				if (string.IsNullOrEmpty(tail))
				{
					continue;
				}

				string rejoined = head + tail;
				foreach (var language in languages)
				{
					if (language == null)
					{
						continue;
					}

					var misses = check(rejoined, language);
					bool missedHere = misses != null
						&& misses.Any(m => string.Equals(m.Word, rejoined, StringComparison.Ordinal));
					if (!missedHere)
					{
						return true; // some language's dictionary accepts the reunified word
					}
				}
			}

			return false;
		}

		// True if some page language's dictionary accepts ANY of the candidate words as-is. Same
		// "run the injected checker, treat 'not a miss' as accepted" contract as
		// DictionaryAcceptsAnyRejoin, but the candidates are already whole words (no head to prepend) —
		// used to mute a fragment of an ADJACENT_ANCHOR fracture whose reunified word is real (see the
		// adjacent-anchor guard above and Crawler.Suppressions.AdjacentAnchorSpellSuppression).
		private static bool DictionaryAcceptsAny(IReadOnlySet<string> words, IReadOnlyList<string> languages, RunCheck check)
		{
			if (words == null)
			{
				return false;
			}

			foreach (var word in words)
			{
				if (string.IsNullOrEmpty(word))
				{
					continue;
				}

				foreach (var language in languages)
				{
					if (language == null)
					{
						continue;
					}

					var misses = check(word, language);
					bool missedHere = misses != null
						&& misses.Any(m => string.Equals(m.Word, word, StringComparison.Ordinal));
					if (!missedHere)
					{
						return true; // some language's dictionary accepts the reunified word
					}
				}
			}

			return false;
		}
		// (the maximal letter run before the trailing Ergänzungsstrich), group 2 the sibling's leading
		// letter run. The left lookbehind keeps the head at a word boundary so "Hochschul-" is not read
		// as "Schul-". The connector alternation carries its own trailing separator ('/' tolerates none,
		// the words require whitespace) so "und"/"oder" cannot match inside a longer word.
		private static readonly Regex SuspendedHeadPattern =
			new(@"(?<!\p{L})(\p{L}+)-\s*(?:/\s*|(?:und|oder|bzw\.|sowie)\s+)(\p{L}+)",
				RegexOptions.Compiled | RegexOptions.CultureInvariant);

		// Shortest shared tail we will borrow from the sibling compound. Below 4, a coincidental short
		// suffix can rejoin to an unintended real word; at >= 4 the borrowed tail is a meaningful
		// compound part ("reise", "kredit"). This deliberately declines to rescue 3-letter shared tails
		// ("-weg", "-bau", "-hof"); raising or lowering the floor is a one-line change.
		private const int MinBorrowedSuffixLength = 4;

		// Every suffix of <paramref name="sibling"/> at least <see cref="MinBorrowedSuffixLength"/>
		// letters long — the candidate shared tails offered to the rejoin check. The convention shares
		// the part of the sibling AFTER its own head ("Avalkredit" → "kredit"), but the exact split is
		// unknown, so all qualifying suffixes are tried and the dictionary picks: any rejoin to a real
		// word means the head legitimately abbreviates that compound, which is exactly the rescue case.
		private static IReadOnlySet<string> BorrowedTailSuffixes(string sibling)
		{
			var set = new HashSet<string>(StringComparer.Ordinal);
			if (string.IsNullOrEmpty(sibling))
			{
				return set;
			}

			for (int start = 0; start + MinBorrowedSuffixLength <= sibling.Length; start++)
			{
				set.Add(sibling.Substring(start));
			}

			return set;
		}

		// Recovers the coordinated sibling compound for a suspended head from the head's BLOCK text and
		// tests whether borrowing the sibling's tail rejoins the head to a real German word. The block
		// text (run.Node) is canonicalized with the same rule as the run so entity/whitespace forms
		// match, then scanned for "<head>- <connector> <sibling>". For each sibling found after THIS
		// exact head, the borrowed-tail suffixes are offered to DictionaryAcceptsAnyRejoin; the head is
		// rescued if any head+suffix is accepted. A cheap no-hyphen early-out keeps the common
		// capitalized-miss-without-suspension case to a single scan.
		private static bool TryBorrowSuspendedHeadTail(HtmlNode node, string head, IReadOnlyList<string> languages, RunCheck check)
		{
			if (node == null || string.IsNullOrEmpty(head))
			{
				return false;
			}

			string raw = node.InnerText;
			if (string.IsNullOrEmpty(raw) || raw.IndexOf('-') < 0)
			{
				return false; // no hyphen anywhere in the block — no suspension is possible
			}

			string blockText = Canonicalizer.Canonicalize(raw);
			if (blockText.Length == 0)
			{
				return false;
			}

			foreach (Match m in SuspendedHeadPattern.Matches(blockText))
			{
				if (!string.Equals(m.Groups[1].Value, head, StringComparison.Ordinal))
				{
					continue; // a different head's suspension — not this token
				}

				var tails = BorrowedTailSuffixes(m.Groups[2].Value);
				if (tails.Count > 0 && DictionaryAcceptsAnyRejoin(head, tails, languages, check))
				{
					return true;
				}
			}

			return false;
		}

		// A run originating from a data-* attribute. SourcePath is built as "{tag}[@{attr}]" (meta
		// content is "meta[@name=...]"), so a data-* attribute run always contains "[@data-".
		// Case-insensitive; text-node and meta runs never match.
		private static bool IsDataAttributeRun(string sourcePath)
			=> sourcePath != null && sourcePath.IndexOf("[@data-", StringComparison.OrdinalIgnoreCase) >= 0;

		// True if the attribute name in the SourcePath carries an "align" tell (e.g. data-data-align,
		// data-align-mobile, *-valign). Extracts the name from "{tag}[@{name}]" and matches "align"
		// as a case-insensitive substring — itself a strong alignment signal.
		private static bool AttributeNameHasAlignTell(string sourcePath)
		{
			if (sourcePath == null)
			{
				return false;
			}

			int at = sourcePath.IndexOf("[@", StringComparison.Ordinal);
			if (at < 0)
			{
				return false;
			}

			int start = at + 2;
			int close = sourcePath.IndexOf(']', start);
			string name = close > start ? sourcePath.Substring(start, close - start) : sourcePath.Substring(start);
			return name.IndexOf("align", StringComparison.OrdinalIgnoreCase) >= 0;
		}
	}
}
