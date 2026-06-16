namespace Crawler.SpellCheck
{
	using System.Collections.Generic;
	using Crawler.Boilerplate;

	/// <summary>
	/// Config model for the "7a Spell Checking Engine" block (System.Text.Json POCOs, mirroring
	/// the existing Config style: public get/set properties with defaults). This block holds ONLY
	/// the genuinely-new concepts — boilerplate groups and the parallel-store output paths.
	/// Dictionaries, prefixes, fugenelemente, languages, cross-language regions, meta names and
	/// exclusions are read from the EXISTING config keys via the existing loaders, never here
	/// (so the user/site dictionaries and the dic/aff checksum layer are reused intact).
	/// </summary>
	public sealed class SpellCheckEngineConfig
	{
		/// <summary>
		/// ISO 639-1 language code used as the fallback when a page declares no html lang
		/// attribute and no meta language tag. Must match a LanguageCode in DictionaryBundles.
		/// Absent → "en".
		/// </summary>
		public string DefaultLanguage { get; set; } = "en";

		/// <summary>
		/// Controls spell checking of &lt;script&gt; element content (inline JS string literals).
		/// When <see cref="JavaScriptSpellCheckOptions.Enabled"/> is false (the default), script
		/// bodies are NOT checked — they are mostly JavaScript (identifiers, API calls, random
		/// tokens) that flood the output with non-words. Set Enabled true to check user-facing prose
		/// kept in inline JS string literals; a layered prose-from-JS filter (universal value gate +
		/// script shape heuristics + a hardcoded code-vocabulary) keeps code noise down, and
		/// <see cref="JavaScriptSpellCheckOptions.TokensToFilter"/> lets a site drop its own residual
		/// non-prose identifiers. &lt;style&gt; content is always skipped (pure CSS) regardless.
		///
		/// BREAKING: this key was previously a plain bool (<c>"SpellCheckJavaScript": true</c>). It is
		/// now an object (<c>"SpellCheckJavaScript": { "Enabled": true, "TokensToFilter": [ … ] }</c>).
		/// The prior bool default was false and the key was experimental/undocumented, so the object
		/// form (absent key → Enabled false) preserves the effective default.
		/// </summary>
		public JavaScriptSpellCheckOptions SpellCheckJavaScript { get; set; } = new();

		/// <summary>
		/// When true (the default), data-* attribute VALUES are additionally judged by a set of
		/// non-prose SHAPE heuristics on top of the universal value gate: a value that is a CSS
		/// selector, a list of id references, or a single machine-token slug (camelCase, dotted
		/// alphanumeric, or letter+digit mixed) is treated as non-prose and skipped. Values that
		/// could be prose — including a bare single-case word (all-lower or ALL-CAPS) with no other
		/// machine signal — stay CHECKED, so an unforeseen prose data-* (e.g. a data-tooltip holding
		/// real text) still surfaces rather than being silently swallowed.
		///
		/// This is a HEURISTIC layer, not a spec guarantee like the built-in non-prose attribute
		/// set: the verbose name is deliberate so that the one time this key appears in a config —
		/// added by hand, after reading the docs, to set it FALSE — it is unmistakable what was
		/// turned off and that it was a judgment call. Setting it false reverts data-* handling to
		/// the universal value gate alone (the prior behaviour). Scoped strictly to data-* attribute
		/// runs; never applied to text-node or script-derived content.
		/// </summary>
		public bool HeuristicNonProseDataAttributeSuppression { get; set; } = true;

		/// <summary>
		/// When true (the default), a spell finding that is merely the merged twin of a
		/// WORD_COLLISION already reported by content-quality is muted in spell, so the one physical
		/// defect is reported once — by CQ, which shows the surrounding markup. Matching is exact:
		/// only the seam-merged token CQ actually emitted for that file is muted, so a genuine
		/// misspelling sharing the same sentence still surfaces. Set false to report both passes'
		/// views of the collision independently (the prior behaviour). No effect if content-quality
		/// did not run or found no collisions.
		/// </summary>
		public bool SuppressSpellFindingsCoveredByWordCollision { get; set; } = true;

		public List<BoilerplateGroupConfig> BoilerplateGroups { get; set; } = [];

		/// <summary>
		/// Global xpath selectors for purely TECHNICAL, non-content elements that must be ignored on
		/// EVERY page — entry/check pages included — regardless of boilerplate group. Each matched
		/// element and its subtree is pruned (never tokenized or checked). For plumbing that is noise
		/// either way: tracking pixels, analytics beacons, hidden technical injections (e.g.
		/// "//img[@id='trackingpixel']"). This is ORTHOGONAL to BoilerplateGroups: boilerplate is a
		/// per-branch "is this chrome I might want to proof?" judgment (and is deliberately CHECKED on
		/// entry pages); GlobalXpathToIgnore is "this is not content, flat-out ignore it, everywhere".
		/// Operator decision: is it global AND do I never need to proof its text? → here. Otherwise →
		/// boilerplate.
		/// </summary>
		public List<string> GlobalXpathToIgnore { get; set; } = [];

		/// <summary>
		/// Optional OVERRIDE for which &lt;meta&gt; content values are spellchecked, by meta name.
		/// The engine ships checking "description" and "keywords" — the only meta names that carry
		/// human prose; every other meta (viewport, robots, format-detection, http-equiv, og:*, …)
		/// holds directives/tokens, never prose, and is ditched. Leave this key ABSENT and that
		/// built-in default applies — no config needed, and the old top-level MetaContentNamesToSpellCheck
		/// / MetaContentExclusionPatterns become unnecessary (the allowlist makes the denylist moot).
		/// DECLARE it (any list, including empty) to REPLACE the default entirely: an explicit list is
		/// the complete truth (declare it, declare it right), and [] means check NO meta content.
		/// null = absent = use built-in default; non-null = replace.
		/// </summary>
		public List<string>? MetaContentNamesToSpellCheck { get; set; }

		/// <summary>
		/// Optional OVERRIDE for the built-in set of HTML attributes that are non-prose BY DEFINITION
		/// (the engine ships ignoring "class", "id", "style" — a CSS class or element id is never
		/// natural-language text to spellcheck, on any document). Leave this key ABSENT and the
		/// built-in default applies — the tool just works, no config needed. DECLARE it (any list,
		/// including empty) to REPLACE the built-in default entirely — for free-shape XML or unusual
		/// documents where you need full control over which structural attributes are ignored (or to
		/// stop ignoring one). null = absent = use built-in default; non-null = replace.
		/// </summary>
		public List<string>? GlobalNonProseHtmlAttributesThatWillBeIgnored { get; set; }

		/// <summary>
		/// Optional OVERRIDE for the built-in set of HTML BOOLEAN attributes (disabled, checked,
		/// selected, novalidate, …) whose value is non-prose by definition (presence is the signal;
		/// the value is only "" or the name echoed). Kept separate from the non-prose attribute
		/// override to keep the two concerns independent. Leave ABSENT → built-in default applies
		/// (tool just works). DECLARE it (any list, including empty) to REPLACE the default entirely —
		/// e.g. to stop ignoring one ("your funeral": a boolean attribute's value is never prose, so
		/// un-ignoring it only adds noise). null = absent = use built-in default; non-null = replace.
		/// </summary>
		public List<string>? GlobalBooleanHtmlAttributesThatWillBeIgnored { get; set; }

		/// <summary>
		/// Site/vendor-SPECIFIC technical attributes whose values are tokens, never prose, skipped
		/// entirely (never tokenized or checked). For the shape-INVISIBLE residual — e.g. short
		/// random salts (a 10-char mixed-case token) indistinguishable from real words by shape.
		/// Matched case-insensitively by exact name — NO pattern/substring inference. Config-driven
		/// (not hardcoded) because these vary per site and a vendor rename should be a config edit,
		/// not an app recompile. Always applied IN ADDITION to the non-prose default/override above.
		/// </summary>
		public List<string> GlobalAttributesToIgnore { get; set; } = [];

		/// <summary>
		/// HTML element tag names whose entire subtree is removed before spell-check — the element,
		/// its descendants, their text and attributes are never tokenized or checked. For element
		/// types that hold non-prose content: embedded/foreign objects (object, embed, param, iframe),
		/// graphics/markup (svg, math, canvas, template), and (operator-enabled) form controls
		/// (input, select) whose option/value/label text is data-driven noise. Matched case-insensitively
		/// by exact tag name. Config-driven (absent = strip nothing extra, like GlobalAttributesToIgnore)
		/// so each site declares its own set; the shipped template carries a sensible default. Note that
		/// &lt;script&gt; and &lt;style&gt; are handled separately (the former by SpellCheckJavaScript; the
		/// latter always-stripped) and need not be listed here.
		/// </summary>
		public List<string> GlobalHtmlTagsToIgnore { get; set; } = [];

		/// <summary>
		/// Per-page multilingual override: pages whose real content mixes languages (or sits on a
		/// branch whose detected language is wrong for them) are checked against an EXPLICIT set of
		/// languages instead of the single branch-detected one. Key = URL PATH PREFIX (from the first
		/// slash, NO domain), matched case-insensitively by StartsWith; when several keys match a page
		/// the LONGEST (most specific) wins (lex specialis). Value = the complete language set for that
		/// page, checked as a UNION — a word is flagged only if EVERY listed dictionary misses it
		/// (passes if any accepts), and the finding is tagged with the full failed set, e.g.
		/// "wörrd (de, en, fr)". Every language listed MUST have a loaded dictionary bundle: a missing
		/// bundle is a configuration error and halts before the run (fail-fast). Absent/empty = no
		/// overrides (every page uses its single branch language).
		/// Example: { "/intl/home/": ["de","en"], "/intl/welcome": ["de","en","fr"] }.
		/// </summary>
		public Dictionary<string, List<string>> PageLanguageOverrides { get; set; } = [];

		/// <summary>
		/// Known, accepted chrome/template language defects to mute from findings — the CMS emits the
		/// same offending text from the same element on (nearly) every page, producing one logical
		/// defect that would otherwise pollute the output sitewide. This is an explicit, auditable
		/// allowlist (NOT lossy dedup): each entry names exactly WHERE (the finding's source-path) and
		/// WHAT text offends, so only the declared defect is muted — anything else from the same element
		/// still surfaces (fail-loud preserved).
		///
		/// Key = source-path exactly as it appears in findings (e.g. "div[@data-pagenav-title]").
		/// Value = list of patterns matched against the run's VALUE:
		///   - exact:   "Seiteninhalt"  → matches when the value equals it;
		///   - prefix:  "Springe zu*"   → matches when the value STARTS WITH the literal before '*'.
		/// Only words within the LITERAL portion are muted: "Springe zu*" mutes "Springe" and "zu" but
		/// the varying tail (e.g. a per-page title like "Modernising") is still checked — so genuine
		/// per-page content defects are never hidden. '*' is permitted only at the end (exact or prefix,
		/// not full glob). Absent/empty = mute nothing.
		/// Example: { "div[@data-pagenav-global-label]": ["Seiteninhalt"], "div[@data-pagenav-title]": ["Springe zu*"] }.
		/// </summary>
		public Dictionary<string, List<string>> KnownChromeLanguageDefects { get; set; } = [];
	}

	/// <summary>
	/// Options for &lt;script&gt;-content spell checking (the "SpellCheckJavaScript" config block).
	/// </summary>
	public sealed class JavaScriptSpellCheckOptions
	{
		/// <summary>
		/// When false (the default), &lt;script&gt; content is not checked. Set true to check prose
		/// held in inline JS string literals. (Replaces the former plain-bool SpellCheckJavaScript.)
		/// </summary>
		public bool Enabled { get; set; }

		/// <summary>
		/// Site-specific non-prose script literals to filter out, matched WHOLE-LITERAL and
		/// case-insensitively against each decoded JS string literal (a literal whose entire value
		/// equals an entry is dropped). This is the place for one site's own residual identifiers —
		/// component names, internal codes, domain abbreviations — that the generic shape/vocabulary
		/// heuristics cannot and should not know about, keeping site specifics OUT of the tool's code.
		/// Whole-literal only (never a substring/per-token match), so it can never reach into and
		/// suppress a word inside a real sentence. Absent/empty = filter nothing.
		/// </summary>
		public List<string> TokensToFilter { get; set; } = new();

		/// <summary>
		/// Optional fallback dictionary for SCRIPT literals only, named by its dictionary key — the
		/// language code under which it is loaded in the DictionaryBundles (e.g. "en"). When set AND
		/// that key resolves to a loaded bundle, a token inside an inline-script literal that fails the
		/// page's declared language(s) but IS a valid word in this fallback dictionary is not flagged.
		/// This strips JS/markup keywords and English UI strings ("this", "img", "close") that are not
		/// German typos, cutting script noise without per-literal curation. Empty (the default) disables
		/// it; a value that does not match a loaded dictionary key is ignored. Script-scoped on purpose:
		/// prose (text nodes, attributes, meta) is still held to the page's declared languages only, so
		/// a real typo that happens to be a valid foreign word never goes silent in prose.
		/// </summary>
		public string ScriptFallbackDictionary { get; set; } = string.Empty;

		/// <summary>
		/// Diagnostic, default OFF. When true, a SEPARATE pass (isolated from the live per-page spell
		/// engine) harvests every inline &lt;script&gt; body from the downloaded HTML into one blob
		/// (log 28), then scans that blob as a single stream (log 29). Two purposes: (1) prove the
		/// scanner is aggregation/scale-robust — the blob's findings should reproduce the per-page run's
		/// known set, nothing new, nothing lost; (2) audit config blind spots — the blob is scanned with
		/// NO boilerplate pruning and NO TokensToFilter, so anything those config-driven suppressions
		/// hide (e.g. boilerplate that DIFFERS between pages and was pruned everywhere but its keeper)
		/// surfaces here. The proven structural gates (ClassifyScriptLiteral, markup, property-access)
		/// stay ON. Inline &lt;script&gt; bodies only this pass — on* / attribute JS is not harvested.
		/// </summary>
		public bool BulkScanPageScript { get; set; }

		/// <summary>
		/// Diagnostic, default OFF. Scans the external .js files sitting in the download directory (the
		/// large site bundles), reusing the inline blob-scan mechanic with the prose-ratio gate + token
		/// veto. Checks against <see cref="ScriptFileScanDictionaries"/> (its OWN list, not the inline
		/// blob's), writing the full debug log (30) and the trimmed triage log (31).
		/// </summary>
		public bool ScanScriptFilesInDownload { get; set; }

		/// <summary>
		/// Dictionaries the INLINE bulk script scan (<see cref="BulkScanPageScript"/>, log 29) checks
		/// against, named by their loaded dictionary key (e.g. [ "de", "en" ]). The tool cannot know
		/// which dictionaries an operator has installed, so the set is named explicitly. A word is a miss
		/// only if EVERY listed dictionary rejects it (the same union-miss the per-page engine uses).
		/// Empty (default) → the inline scan is a clean no-op with a note in log 29, never a crash.
		/// </summary>
		public List<string> ScriptBulkScanDictionaries { get; set; } = new();

		/// <summary>
		/// Dictionaries the EXTERNAL .js FILE scan (<see cref="ScanScriptFilesInDownload"/>, logs 30/31)
		/// checks against — deliberately SEPARATE from <see cref="ScriptBulkScanDictionaries"/>. Minified
		/// vendor bundles can carry i18n prose in ANY language, so this list is meant to be BROAD: list
		/// every dictionary key whose language may appear in the bundles (e.g. the operator's full loaded
		/// set). The union-miss rule then flags a token only when EVERY listed dictionary rejects it, so
		/// correctly-spelled prose in any listed language resolves to zero findings and only genuine
		/// non-words (code identifiers, slugs, garbage) or true cross-language misspellings surface. The
		/// tool cannot know which dictionaries an operator has installed, so the set is named explicitly.
		/// Empty (default) → see ValidateJsFileScan: enabling the file scan with an empty list is an
		/// explicit misconfiguration and halts at load (it asked for a scan it cannot deliver).
		/// </summary>
		public List<string> ScriptFileScanDictionaries { get; set; } = new();

		/// <summary>
		/// 647 — reach cutoff for routing a JS-file spelling finding. A bundle loaded on this many
		/// pages or fewer is treated as locally fixable (findings routed per-page); above it, the
		/// bundle is site-wide and findings collapse to one per-bundle entry. Default 5.
		/// </summary>
		public int JsFindingPageReachThreshold { get; set; } = 5;
	}
}
