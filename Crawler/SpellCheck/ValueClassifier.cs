namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.Text.RegularExpressions;

	/// <summary>Why a value was skipped, or that it is a prose candidate. Diagnostic + testable.</summary>
	public enum ValueVerdict
	{
		Check,            // prose candidate — hand on to tokenizer + per-token gate
		SkipUrl,          // has a scheme (http:, mailto:, tel:, etc.)
		SkipPath,         // absolute or multi-segment path
		SkipQuery,        // contains a query-string key=value
		SkipTemplate,     // mustache / templating placeholders dominate
		SkipStructured,   // JSON / bracketed structure
		SkipDigits,       // long pure-digit run
		SkipHex,          // long hex run (hash)
		SkipHighEntropy,  // random-looking token (key/hash/base64)
		SkipConfigLiteral,// universal boolean/null literal
		SkipTooShort,     // single token below the prose floor
		SkipCssSelector,  // data-* heuristic: value is a CSS selector (.class, #id, :nth-, combinators)
		SkipIdRefList,    // data-* heuristic: whitespace-separated list of id-shaped tokens
		SkipSlug,         // data-* heuristic: single machine-token (camelCase / dotted / letter+digit)
		SkipLeadingUnderscore, // script heuristic: single token starting with '_' (e.g. _top, _blank)
		SkipAcronymWord,  // script heuristic: single token with an UPPER UPPER…lower run (e.g. ABCwidget)
		SkipCodeVocabulary, // script heuristic: literal is a hardcoded universal JS/DOM/web code token
		SkipOptionsString, // script heuristic: literal is a key=value options/feature/query string
		SkipAttributeName, // script heuristic: literal is an HTML attribute name (data-* custom attr, or a known ARIA name)
		SkipSiteToken,     // script: literal matches a site-configured TokensToFilter entry (whole-literal, case-insensitive)
		SkipMarkup         // script: literal is HTML/DOM construction (tag delimiters + name="…" attributes), never prose
	}

	public readonly record struct ValueClass(bool ShouldCheck, ValueVerdict Verdict);

	/// <summary>
	/// Whole-value gate run BEFORE tokenization. It decides, from the value's intrinsic SHAPE
	/// only (never the attribute's name), whether a value is a prose candidate or a technical
	/// token to skip. This is what stops a URL or path from being tokenized into fake word
	/// tokens (e.g. a path's segments becoming "content", "dam", "work" — letters-only
	/// non-words that would surface as false typos).
	///
	/// The entropy / all-digits / all-hex math MIRRORS the proven engine in
	/// <c>AttributeNoiseDetector</c> (whose helpers are private). It is duplicated here
	/// deliberately rather than coupling to that class — its public API (IsNoisy) has the
	/// opposite polarity (default-keep, for block hashing) and a name-based shortcut we must
	/// not inherit. Dedup is a later cleanup concern; the existing class stays untouched.
	///
	/// Structural skips (url / path / query / template / structured) are NOT entropy-based:
	/// paths and URLs are LOW entropy and full of real words, so entropy alone never catches
	/// them. They need shape rules. This is the correction the real-world attribute soup proved
	/// necessary — entropy is one signal among several, not the whole gate.
	/// </summary>
	public static partial class ValueClassifier
	{
		// Mirror of AttributeNoiseDetector thresholds — keep in sync until deduplicated.
		private const double EntropyThreshold = 3.8;
		private const int MinLengthForEntropy = 8;
		private const int MinHexLength = 16;
		private const int MinDigitOnlyLength = 8;

		// Prose floor: a single token shorter than this carries no spellable prose worth
		// surfacing. Multi-word values are never subject to the floor.
		private const int MinSingleTokenLength = 3;

		// Universal web config literals — NOT a site-specific list. These appear as attribute
		// values across the entire web as machine flags, never as prose to spellcheck.
		private static readonly HashSet<string> ConfigLiterals = new(StringComparer.OrdinalIgnoreCase)
		{
			"true", "false", "null", "yes", "no", "on", "off"
		};

		// Hardcoded, CLOSED set of universal JavaScript / DOM / web CODE tokens that are correctly
		// spelled English and never natural-language prose. A SCRIPT string literal whose ENTIRE
		// value is one of these is skipped. Same safety property as ConfigLiterals: every member is
		// correctly spelled, so matching one can only ever decline to flag a non-typo — it can never
		// suppress a misspelling. Whole-literal match (not per-token) is deliberate: it fires only
		// when the literal IS the token (a bare "keydown"), so it never touches one of these words
		// sitting inside a sentence, an options string, or an error message (those are content).
		// Universal only — site/vendor-specific identifiers and jargon never belong here. Grows by
		// curation as real runs surface more; it is intentionally explicit and reviewable.
		private static readonly HashSet<string> ScriptCodeVocabulary = new(StringComparer.OrdinalIgnoreCase)
		{
			// JS keywords / literals
			"var", "function", "return", "true", "false", "undefined", "this",
			// DOM events / handler names
			"keydown", "onkeydown", "keyup", "onkeyup", "click", "onclick", "blur",
			// DOM / form / input attribute values and property names
			"value", "type", "target", "text", "password", "submit", "select-one", "disabled", "action",
			// CSS / visibility / layout tokens
			"none", "hidden", "visible", "invisible", "fixed", "mode",
			// HTML / resource / markup tokens
			"link", "head", "script", "stylesheet", "css", "string", "src", "href", "img", "MIME", "text/css", "body", "button",
			// web / environment / config tokens
			"yes", "no", "prod", "localhost", "same-origin",
			// common app / UI code tokens (correctly-spelled English; some may not occur as standalone
			// literals on every site, but cost nothing by the set's safety property)
			"preview", "test", "term",

			// 649 — universal web/JS/CSS/media/math/crypto identifiers seen as standalone literals in
			// external bundles (mathjs, hls/media player, JWT/OIDC, DOM/CSS, build tooling). These are
			// CODE on any site, not site specifics — site-specific component names belong in the config
			// TokensToFilter list, not here. Same safety property as the rest: correctly spelled,
			// whole-literal, so a match can only decline to flag a non-typo, never reach inside prose.
			// Deliberately conservative — anything that resembles a real word or could be a genuine typo
			// ("octect", "writable"), proper-noun algorithm names (Chebyshev, Lanczos), and i18n strings
			// are EXCLUDED and stay checked.
			// JS runtime / language / patterns
			"typeof", "instanceof", "btoa", "args", "ctor", "func", "mixin", "memoized", "nullish",
			"falsey", "truthy", "deserialize", "destructure", "stringify", "stringified", "stringifier",
			"rerender", "polyfilled", "transpilation", "evented", "devtools", "stacktrace", "xhr", "idx",
			"ptr", "params", "nexttick", "onmessage", "unregister", "requeuing", "inflight", "whitespace",
			"ecmascript", "subexpressions",
			// DOM / HTML / CSS
			"colspan", "rowspan", "tabindex", "tabindexes", "valign", "figcaption", "hgroup", "datalist",
			"srcset", "hsla", "highp", "oklch", "woff", "truetype", "focusin", "focusout", "dblclick",
			"checkboxradio", "xhtml",
			// media / streaming
			"hls", "dts", "cbcs", "cenc", "emsg", "pssh", "ttml", "sinf", "transmuxer", "demuxer",
			"demuxing", "midrow", "seekable", "seeked", "framerate", "playhead",
			// crypto / auth (JWT / OIDC)
			"jwk", "oidc", "dpop", "nbf", "azp", "userinfo", "keylen", "keyset",
			// math (mathjs)
			"acosh", "acoth", "acsch", "asinh", "atanh", "cbrt", "cumsum", "eigs", "erf", "expm", "freqz",
			"hypot", "invmod", "kldivergence", "lcm", "lgamma", "lsolve", "lusolve", "lyap", "pinv",
			"powerset", "usolve", "xgcd", "multiset", "quickselect", "minmax", "cartesian", "elementwise",
			"nonnegative", "nonzero", "nonempty",
			// libraries / namespaces / build tooling
			"redux", "lottie", "highcharts", "flatbush", "mathjs", "nodeca", "swc", "polyline", "goog",
			// data / encoding / misc
			"crc", "bignumber", "numstr", "printstr", "objid", "typeid", "seqof", "setof", "vsindex",
			"ufeff", "bmpstr", "copytext", "filepath", "datetime", "asc", "std", "expr",
			// 657 — HTML/web shim tokens, build-tool verbs, and boolean/logic-gate operators. Universal in
			// any JS/web bundle: the iframe family, the classic IE @font-face shims (iefix/iebug), the
			// precompile build verbs, and standalone gate operators. Whole-literal + case-insensitive, so
			// "xor" and "XOR" both pass on one entry ("writable" is already above). Deliberately EXCLUDES
			// the prose doubles AND/OR/NOT (correctly-spelled English — with the en dictionary loaded they
			// never flag; baking them in spends generality for nothing) and the VLSI-specific AOI/OAI
			// (And-Or-Invert / Or-And-Invert — chip-design domain, not universal JS → site config).
			"iframe", "iframes", "iebug", "iefix",
			"precompile", "precompiled", "precompiler",
			"XOR", "XNOR", "NAND", "NOR"
		};

		// Curated, CLOSED set of ARIA attribute NAMES that are skipped when a script literal is
		// EXACTLY one of them (a bare "aria-label"). Deliberately a by-NAME allowlist, NOT an
		// "aria-*" prefix rule: several ARIA attributes carry human-readable PROSE as their value
		// (aria-label, aria-roledescription, aria-placeholder, ...), so a prefix rule would risk
		// suppressing a real typo if a literal ever held such a value. Matching the exact name only
		// skips the attribute-name token itself, never a value. Widen by curation, never by prefix.
		private static readonly HashSet<string> AriaAttributeNames = new(StringComparer.OrdinalIgnoreCase)
		{
			"aria-label"
		};

		// CSS box-position keywords — the closed, universal, language-agnostic vocabulary used as
		// alignment values. Stored lowercase; the case rule is applied separately. This set is the
		// load-bearing safety boundary of IsExactPositionalKeyword: ONLY these correctly-spelled
		// tokens are eligible, so the rule can never suppress a misspelling (e.g. "middel" is not a
		// member and stays checked). Deliberately minimal — widen only on observed need.
		private static readonly HashSet<string> PositionalKeywords = new(StringComparer.Ordinal)
		{
			"top", "right", "bottom", "left", "center", "middle"
		};

		/// <summary>
		/// True if the value is an EXACT, correctly-spelled CSS positional keyword in an accepted
		/// case: all-lowercase ("center") or Title-case ("Center"). Odd internal casing ("cEnter")
		/// and all-caps ("CENTER") are rejected — they are shape-ambiguous and stay checked, like a
		/// bare shouted token. This is a value test ONLY; the caller is responsible for the "align"
		/// name guard, so the rule can only ever decline to flag a correctly-spelled English
		/// positional word that sits in an alignment attribute — never a misspelling and never a
		/// word outside that context.
		/// </summary>
		public static bool IsExactPositionalKeyword(string rawValue)
		{
			string v = (rawValue ?? string.Empty).Trim();
			if (v.Length == 0)
			{
				return false;
			}

			string lower = v.ToLowerInvariant();
			if (!PositionalKeywords.Contains(lower))
			{
				return false; // not a member (this is where misspellings fall out)
			}

			// Accept all-lowercase, or Title-case (first char the uppercase of lower[0], rest equal
			// to lower[1..]). Reject anything else (cEnter, CENTER, ...).
			if (v == lower)
			{
				return true;
			}

			return v.Length == lower.Length
				&& v[0] == char.ToUpperInvariant(lower[0])
				&& string.CompareOrdinal(v, 1, lower, 1, lower.Length - 1) == 0;
		}

		public static ValueClass Classify(string rawValue)
		{
			string v = (rawValue ?? string.Empty).Trim();
			if (v.Length == 0)
			{
				return new ValueClass(false, ValueVerdict.SkipTooShort);
			}

			// --- Structural skips (shape, not entropy) ---
			if (UrlScheme().IsMatch(v)) return new ValueClass(false, ValueVerdict.SkipUrl);
			if (Mustache().IsMatch(v)) return new ValueClass(false, ValueVerdict.SkipTemplate);
			if (QueryPair().IsMatch(v)) return new ValueClass(false, ValueVerdict.SkipQuery);
			if (Structured().IsMatch(v)) return new ValueClass(false, ValueVerdict.SkipStructured);
			if (v[0] == '/' || MultiSegmentPath().IsMatch(v)) return new ValueClass(false, ValueVerdict.SkipPath);

			bool hasSpace = v.IndexOf(' ') >= 0;

			// --- Multi-word values are prose candidates; hand to the tokenizer + per-token gate. ---
			if (hasSpace)
			{
				return new ValueClass(true, ValueVerdict.Check);
			}

			// --- Single-token zone: the ambiguous one. Resolve by shape. ---
			if (ConfigLiterals.Contains(v)) return new ValueClass(false, ValueVerdict.SkipConfigLiteral);
			if (v.Length < MinSingleTokenLength) return new ValueClass(false, ValueVerdict.SkipTooShort);
			if (v.Length >= MinDigitOnlyLength && IsAllDigits(v)) return new ValueClass(false, ValueVerdict.SkipDigits);
			if (v.Length >= MinHexLength && IsAllHex(v)) return new ValueClass(false, ValueVerdict.SkipHex);
			if (v.Length >= MinLengthForEntropy && ComputeShannonEntropy(v) > EntropyThreshold)
			{
				return new ValueClass(false, ValueVerdict.SkipHighEntropy);
			}

			// A single low-entropy word (e.g. a compound noun) — let the dictionary/compound
			// checker decide. Prose-leaning by design: a wrongly-kept token costs one easy
			// triage entry; a wrongly-skipped word is a silently missed typo.
			return new ValueClass(true, ValueVerdict.Check);
		}

		/// <summary>
		/// The data-* HEURISTIC gate (only used when HeuristicNonProseDataAttributeSuppression is on,
		/// and only for data-* attribute runs). Applies the universal <see cref="Classify"/> first —
		/// so data-* values still benefit from the url/path/digits/entropy skips — then, only if that
		/// would CHECK the value, layers three additional non-prose SHAPE verdicts that the universal
		/// (deliberately prose-leaning) gate lets through:
		///   • CSS selector  — leading .class / #id, or a CSS pseudo/combinator;
		///   • id-ref list   — multiple whitespace-separated tokens that are ALL id-shaped;
		///   • machine slug  — a single token with an internal case transition, dotted alphanumeric
		///                     segments, or letter+digit mixing.
		///
		/// SAFETY: a value with no such machine signal stays CHECKED. In particular a BARE single
		/// token — all-lowercase (e.g. a plain word) or ALL-CAPS (e.g. a shouted word / brand) — is
		/// never skipped here, because shape cannot tell it from a misspelled word; the honest answer
		/// is to check it. This mirrors the universal gate's prose-leaning bias. Name-agnostic: the
		/// caller decides "this is a data-* run"; this method judges the value's shape only.
		/// </summary>
		public static ValueClass ClassifyDataAttribute(string rawValue)
		{
			var baseVerdict = Classify(rawValue);
			if (!baseVerdict.ShouldCheck)
			{
				return baseVerdict; // already skipped by the universal gate (url/path/digits/...)
			}

			string v = (rawValue ?? string.Empty).Trim();

			if (IsCssSelector(v)) return new ValueClass(false, ValueVerdict.SkipCssSelector);
			if (IsIdRefList(v)) return new ValueClass(false, ValueVerdict.SkipIdRefList);
			if (IsMachineSlug(v)) return new ValueClass(false, ValueVerdict.SkipSlug);

			return new ValueClass(true, ValueVerdict.Check);
		}

		/// <summary>
		/// The gate for a DECODED JavaScript string LITERAL (the output of the script lexer). Like
		/// <see cref="ClassifyDataAttribute"/> it applies the universal <see cref="Classify"/> first —
		/// so a literal that is a url/path/JSON/digit/hex/high-entropy token is dropped exactly as
		/// anywhere else — then, only if that would CHECK the value, layers the non-prose SHAPE skips.
		/// It reuses the same three shape heuristics as the data-* gate (CSS selector, id-ref list,
		/// machine slug) plus two skips specific to the kinds of token that flood inline scripts:
		///   • leading underscore — a single token beginning with '_' (e.g. _top, _blank, _self): an
		///     HTML link target / private-name convention, never prose;
		///   • acronym-word — a single token carrying an UPPER UPPER…lower run (two or more adjacent
		///     capitals immediately followed by a lowercase letter, e.g. ABCwidget, IObox).
		///     This is the residual code-identifier shape the lower→upper slug rule misses (its first
		///     boundary is upper→upper, not lower→upper).
		///
		/// CASE POLICY for a single alpha token (the deliberate boundary):
		///   Hello  → Title-case prose, CHECKED (a lone leading capital is never a signal);
		///   HEllo  → two leading caps then lower, SKIPPED (unnatural — shift-key noise);
		///   HELlo  → caps run then lower, SKIPPED (acronym glued to a word);
		///   HELLO  → all-caps with no trailing lowercase, CHECKED (kept, like the data-* gate's
		///            bias: a shouted word / brand cannot be told from a misspelling by shape).
		///
		/// The two NEW skips (underscore, acronym) are applied to SINGLE-TOKEN values only: a
		/// multi-word literal is prose by construction (it already returned Check from Classify), and
		/// an odd token sitting inside a sentence must not drop the whole sentence — it is the
		/// tokenizer's job to surface that one token. Name-agnostic: this judges the literal's shape
		/// only, never where in the script it came from.
		/// </summary>
		public static ValueClass ClassifyScriptLiteral(string rawValue, IReadOnlySet<string>? siteTokensToFilter = null)
		{
			var baseVerdict = Classify(rawValue);
			if (!baseVerdict.ShouldCheck)
			{
				return baseVerdict; // already skipped by the universal gate (url/path/digits/...)
			}

			string v = (rawValue ?? string.Empty).Trim();

			// Site-specific filter (config: SpellCheckJavaScript.TokensToFilter) — whole-literal,
			// case-insensitive. Lets a site drop its OWN non-prose script identifiers (component
			// names, internal codes) WITHOUT baking site specifics into this generic tool. Same
			// safety property as the hardcoded vocabulary: it can only ever drop a literal that
			// EXACTLY equals a configured token, never reach into a sentence. Checked first so a
			// configured token is reported as SkipSiteToken (clear, site-attributable in the log).
			if (siteTokensToFilter != null && siteTokensToFilter.Contains(v))
			{
				return new ValueClass(false, ValueVerdict.SkipSiteToken);
			}

			// Hardcoded universal code vocabulary — whole-literal exact match (a bare "keydown",
			// "value", "none", …). Cheapest and most common script skip, so it runs first.
			if (ScriptCodeVocabulary.Contains(v)) return new ValueClass(false, ValueVerdict.SkipCodeVocabulary);

			// Options / feature / query string — the WHOLE literal is one or more key=value pairs
			// joined by , ; or & (e.g. a window.open feature string "status=no,scrollbars=yes").
			// This is config, never prose, so the whole literal is dropped rather than tokenized.
			if (IsOptionsString(v)) return new ValueClass(false, ValueVerdict.SkipOptionsString);

			// HTML/DOM markup being constructed in JS — a literal (very often a FRAGMENT split across
			// string concatenations, e.g. '<div id="'+id+'" class="…"><a href="#" onclick="…">…<img
			// src="') that carries tag delimiters and/or name="…" attribute assignments. This is markup
			// construction, never user-facing prose; without this gate every code token inside it (href,
			// onclick, class, src, title, …) surfaces as a false typo — and whole-literal TokensToFilter
			// cannot reach them because they sit EMBEDDED in the larger literal, not as standalone values.
			// The threshold is TWO or more structural markers (a tag opener "<x"/"</x" or a name="…"
			// attribute): the smallest leaking fragment ('<div id="') already has two, while a single
			// stray marker — a lone "<div>" referenced in a sentence, or one name="x" — leaves the literal
			// CHECKED, so genuine prose that merely mentions a tag or attribute is never dropped. Decoded
			// input (run.RawText), so the markers are real "<"/"=" characters, not entity/escape forms.
			if (IsMarkupConstruction(v)) return new ValueClass(false, ValueVerdict.SkipMarkup);

			// HTML attribute name appearing as a code literal (a bare "data-toggle" passed to
			// setAttribute, or a known ARIA name). data-* is the spec's author-defined custom-data
			// namespace — an attribute NAME there is a developer-coined identifier, never prose. The
			// data-* match is a strict lowercase-kebab shape (^data-[a-z0-9-]+$); ARIA is by-name only.
			if (DataAttributeName().IsMatch(v) || AriaAttributeNames.Contains(v))
			{
				return new ValueClass(false, ValueVerdict.SkipAttributeName);
			}

			if (IsCssSelector(v)) return new ValueClass(false, ValueVerdict.SkipCssSelector);
			if (IsIdRefList(v)) return new ValueClass(false, ValueVerdict.SkipIdRefList);
			if (IsMachineSlug(v)) return new ValueClass(false, ValueVerdict.SkipSlug);

			// Single-token-only skips: a multi-word value is prose and must not be dropped wholesale.
			if (v.IndexOf(' ') < 0)
			{
				if (v[0] == '_') return new ValueClass(false, ValueVerdict.SkipLeadingUnderscore);
				if (IsAcronymWord(v)) return new ValueClass(false, ValueVerdict.SkipAcronymWord);
			}

			return new ValueClass(true, ValueVerdict.Check);
		}

		// True when the token contains an "UPPER UPPER…lower" run: two adjacent uppercase letters
		// immediately followed by a lowercase letter (anywhere — leading or internal). That single
		// condition implements the case policy in ClassifyScriptLiteral: "HEllo" (HE+l), "HELlo"
		// (EL+l) and "ABCwidget" (BC+w) all match; a lone leading capital ("Hello") never does (only
		// one uppercase precedes the lowercase), and an all-caps token with no trailing lowercase
		// ("HELLO") never does (no lowercase to close the run). Unicode-aware via char.IsUpper/Lower
		// so accented capitals count too. The letter+digit / dotted cases are already SkipSlug, so a
		// digit-bearing token never reaches here.
		// A key=value options / feature / query string: the ENTIRE value is one or more "key=value"
		// pairs joined by ',', ';' or '&' (e.g. "status=no,scrollbars=yes" passed to window.open, or a
		// bare "mode=prod"). Each non-empty segment must be an identifier-shaped key, then '=', then a
		// whitespace-free value. This is configuration, never natural-language prose, so the whole
		// literal is dropped. The key/value shape keeps prose safe: a segment whose key carries a space
		// (e.g. "Breite = 100"), whose value carries a space ("Preis=100 Euro und mehr"), or that has no
		// '=' at all ("a=b, this is text") fails, so the literal stays checked. This mirrors the existing
		// query-string skip in <see cref="Classify"/> — option values, like query values, are not prose.
		private static bool IsOptionsString(string v)
		{
			if (v.Length == 0 || v.IndexOf('=') < 0) return false;

			foreach (var raw in v.Split(new[] { ',', ';', '&' }, StringSplitOptions.RemoveEmptyEntries))
			{
				var seg = raw.Trim();
				if (seg.Length == 0) continue;

				int eq = seg.IndexOf('=');
				if (eq <= 0) return false; // no key before '=' (or no '=' in this segment)

				for (int i = 0; i < eq; i++)
				{
					char c = seg[i];
					if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) return false;
				}

				for (int i = eq + 1; i < seg.Length; i++)
				{
					if (char.IsWhiteSpace(seg[i])) return false; // option values are tokens, not prose
				}
			}

			return true;
		}

		private static bool IsAcronymWord(string v)
		{
			for (int i = 2; i < v.Length; i++)
			{
				if (char.IsLower(v[i]) && char.IsUpper(v[i - 1]) && char.IsUpper(v[i - 2]))
				{
					return true;
				}
			}

			return false;
		}

		// A CSS selector: starts with a .class or #id selector token, or carries an unmistakable
		// CSS pseudo / combinator (:nth-..., ::pseudo). No natural-language prose has this shape.
		private static bool IsCssSelector(string v)
		{
			if (v.Length >= 2 && (v[0] == '.' || v[0] == '#')
				&& (char.IsLetter(v[1]) || v[1] == '-' || v[1] == '_'))
			{
				return true;
			}

			return CssPseudoOrCombinator().IsMatch(v);
		}

		// A whitespace-separated list of id references: TWO OR MORE tokens, EVERY one id-shaped
		// (lowercase-leading AND carrying an internal '-' / '_' / digit). The internal-signal
		// requirement is what separates "alpha-label beta-label" (skip) from a two-word lowercase
		// prose phrase (kept) — a phrase like "color painting" has no per-token machine signal.
		private static bool IsIdRefList(string v)
		{
			var parts = v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2)
			{
				return false;
			}

			foreach (var p in parts)
			{
				if (!IsIdShapedToken(p))
				{
					return false;
				}
			}

			return true;
		}

		private static bool IsIdShapedToken(string t)
		{
			if (t.Length == 0 || !(t[0] >= 'a' && t[0] <= 'z'))
			{
				return false; // must lead with an ASCII lowercase letter
			}

			bool hasInternalSignal = false;
			foreach (var c in t)
			{
				if (c == '-' || c == '_' || (c >= '0' && c <= '9'))
				{
					hasInternalSignal = true;
				}
				else if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')))
				{
					return false; // any other char (dot, slash, diacritic, ...) → not a plain id token
				}
			}

			return hasInternalSignal;
		}

		// A single machine-token slug: NO whitespace, and at least one unambiguous machine signal —
		// an internal case transition (lower→upper, e.g. camelCase), dotted alphanumeric segments,
		// or letter+digit mixing. A bare all-lower or ALL-CAPS alpha token (no transition, no dot,
		// no digit) has NONE of these and is intentionally NOT a slug → it stays checked.
		private static bool IsMachineSlug(string v)
		{
			if (v.IndexOf(' ') >= 0)
			{
				return false; // single-token rule only
			}

			bool hasLetter = false, hasDigit = false, internalCaseTransition = false;
			for (int i = 0; i < v.Length; i++)
			{
				char c = v[i];
				if (char.IsLetter(c)) hasLetter = true;
				if (c >= '0' && c <= '9') hasDigit = true;
				if (i > 0 && char.IsUpper(c) && char.IsLower(v[i - 1]))
				{
					internalCaseTransition = true; // lower→Upper boundary (camelCase / PascalCase)
				}
			}

			if (internalCaseTransition) return true;
			if (hasLetter && hasDigit) return true;
			if (DottedAlnum().IsMatch(v)) return true;

			return false;
		}

		// True when the literal carries TWO or more HTML structural markers — a tag delimiter
		// ("<div", "</a", "<img") or a name="…"/name='…' attribute assignment — i.e. it is markup /
		// DOM construction rather than prose. Counting (not mere presence) is what keeps prose safe: a
		// single stray "<tag>" mention or one name="x" scores 1 and stays CHECKED, while real markup
		// (which always pairs a tag with an attribute, or stacks several) scores 2+. The two marker
		// regexes never overlap on the same characters (a tag opener is "<"+letter; an attribute is
		// letter…+'='+quote), so a marker is never double-counted.
		private static bool IsMarkupConstruction(string v)
		{
			int markers = TagDelimiter().Matches(v).Count + AttributeAssignment().Matches(v).Count;
			return markers >= 2;
		}

		// scheme://  or  scheme:  (http, https, mailto, tel, ftp, data, javascript, ...)
		[GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:")]
		private static partial Regex UrlScheme();

		// An HTML tag delimiter: '<' or '</' immediately followed by a letter (anchored on the letter
		// so a bare '<' in prose — "a < b", "3<5" — never matches). Matches <div, <a, <img, </div, …
		[GeneratedRegex(@"</?[A-Za-z]")]
		private static partial Regex TagDelimiter();

		// An HTML attribute assignment: an attribute name immediately followed by ="…" or ='…'. The
		// '=' must sit directly between the name and the quote (no spaces), which is the markup form
		// (class="x"); a prose 'x = "y"' has spaces and does not match.
		[GeneratedRegex("[A-Za-z][\\w-]*=[\"']")]
		private static partial Regex AttributeAssignment();

		// a custom data-* attribute NAME: "data-" then a lowercase-kebab token (^data-[a-z0-9-]+$)
		[GeneratedRegex(@"^data-[a-z0-9-]+$")]
		private static partial Regex DataAttributeName();

		// mustache / handlebars templating placeholders
		[GeneratedRegex(@"\{\{.*?\}\}")]
		private static partial Regex Mustache();

		// query-string style key=value introduced by ? or &
		[GeneratedRegex(@"[?&][\w.\-]+=")]
		private static partial Regex QueryPair();

		// JSON / bracketed structure
		[GeneratedRegex(@"[{}\[\]]|"":\s")]
		private static partial Regex Structured();

		// two or more slash-separated segments (relative multi-segment path)
		[GeneratedRegex(@"[^/\s]+/[^/\s]+/")]
		private static partial Regex MultiSegmentPath();

		// CSS pseudo-class/element or descendant/child/sibling combinator joining selector tokens
		[GeneratedRegex(@":{1,2}[a-zA-Z][\w-]*|[.#][-\w]+\s*[>~+]\s*|\s[.#][-\w]")]
		private static partial Regex CssPseudoOrCombinator();

		// a dot joining two alphanumeric runs (e.g. dotted identifier segments) — not a sentence stop
		[GeneratedRegex(@"[A-Za-z0-9]\.[A-Za-z0-9]")]
		private static partial Regex DottedAlnum();

		// --- engine mirror of AttributeNoiseDetector (private there) ---

		private static double ComputeShannonEntropy(string value)
		{
			var freq = new Dictionary<char, int>();
			foreach (var c in value)
			{
				freq.TryGetValue(c, out int n);
				freq[c] = n + 1;
			}

			double entropy = 0.0;
			double len = value.Length;
			foreach (var count in freq.Values)
			{
				double p = count / len;
				entropy -= p * Math.Log2(p);
			}

			return entropy;
		}

		private static bool IsAllDigits(string value)
		{
			foreach (var c in value)
			{
				if (c < '0' || c > '9')
				{
					return false;
				}
			}

			return true;
		}

		private static bool IsAllHex(string value)
		{
			foreach (var c in value)
			{
				if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
				{
					return false;
				}
			}

			return true;
		}
	}
}
