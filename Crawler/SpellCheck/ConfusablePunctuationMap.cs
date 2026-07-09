namespace Crawler.SpellCheck
{
	using System.Collections.Generic;

	// Codepoint → ASCII canonical map for punctuation/syntax confusables. Used by
	// ConfusablePunctuationDetector to recognise a non-ASCII look-alike standing in
	// for an ASCII syntax character inside a code identifier (e.g. U+FF70 where a
	// hyphen belongs, in "paddingｰtop").
	//
	// The map is intentionally a curated, code-relevant CORE — not the full Unicode
	// confusables table (thousands of entries, deliberately over-inclusive per UTS
	// #39). Extending it later (e.g. a → COLON set) is purely additive.
	//
	// Each entry also carries the Unicode character name, so the operator-facing
	// copy can spell out exactly which character was found.
	internal static class ConfusablePunctuationMap
	{
		// Confusables of ASCII HYPHEN-MINUS (U+002D).
		//
		// Authoritative entries are taken from the Unicode confusables data, filtered
		// to rows whose target is the single character 002D:
		//   Source: Unicode confusables.txt, UTS #39, Version 17.0.0 (2025-07-22).
		//   https://www.unicode.org/Public/security/latest/confusables.txt
		// (Multi-character targets such as 2E1A → "002D 0308" are deliberately excluded;
		// this map is codepoint → a single ASCII char only.)
		//
		// LOCAL ADDITIONS — the "dash gap": characters that READ as a hyphen/dash in a
		// Latin/code context but that the authoritative confusables data does NOT map to
		// 002D. confusables.txt groups dash-like marks by visual width: a short-hyphen
		// cluster collapses to '-' (above), while a long/wide cluster (em dash, horizontal
		// bar, fullwidth hyphen, both prolonged sound marks) maps to ITSELF — correct in
		// CJK rendering, where these span a full em, but blind to the hyphen illusion that
		// appears when such a mark is dropped into Latin/monospace code. (Independently
		// verified: confusables is_confusable('-', U+30FC) and (U+FF70) → False; FF70 is
		// not even a key in the table.) Added on purpose for the code-context perceptual
		// model. UTS #39 conformance clause C2-2 permits documented additions — this is it.
		//   U+FF70 HALFWIDTH KATAKANA-HIRAGANA PROLONGED SOUND MARK ← paddingｰtop culprit
		//   U+30FC KATAKANA-HIRAGANA PROLONGED SOUND MARK           ← fullwidth sibling (JP IME)
		//   U+FF0D FULLWIDTH HYPHEN-MINUS                           ← CJK fullwidth input
		//   U+2015 HORIZONTAL BAR                                   ← typographic / CJK
		//   U+2014 EM DASH                                          ← Word/Docs autoformat.
		//          NOTE: legitimate in PROSE. The enricher gates on code source, but a
		//          string literal inside a .js (content:"a—b") could still false-positive;
		//          accepted deliberately (Unicode isn't human-distinguishable, so detecting
		//          is the right default — mitigate only if real fallout appears).
		internal static readonly IReadOnlyDictionary<int, (char Canonical, string UnicodeName)> HyphenConfusables =
			new Dictionary<int, (char, string)>
			{
				// ── Authoritative (confusables.txt v17.0.0, → 002D) ──────────────
				[0x2010] = ('-', "HYPHEN"),
				[0x2011] = ('-', "NON-BREAKING HYPHEN"),
				[0x2012] = ('-', "FIGURE DASH"),
				[0x2013] = ('-', "EN DASH"),
				[0x2043] = ('-', "HYPHEN BULLET"),
				[0x02D7] = ('-', "MODIFIER LETTER MINUS SIGN"),
				[0x2212] = ('-', "MINUS SIGN"),
				[0x2796] = ('-', "HEAVY MINUS SIGN"),
				[0xFE58] = ('-', "SMALL EM DASH"),
				[0x06D4] = ('-', "ARABIC FULL STOP"),
				[0x2CBB] = ('-', "COPTIC SMALL LETTER DIALECT-P NI"),
				[0x2CBA] = ('-', "COPTIC CAPITAL LETTER DIALECT-P NI"),

				// ── Local additions: the dash gap (documented above) ─────────────
				[0xFF70] = ('-', "HALFWIDTH KATAKANA-HIRAGANA PROLONGED SOUND MARK"),
				[0x30FC] = ('-', "KATAKANA-HIRAGANA PROLONGED SOUND MARK"),
				[0xFF0D] = ('-', "FULLWIDTH HYPHEN-MINUS"),
				[0x2015] = ('-', "HORIZONTAL BAR"),
				[0x2014] = ('-', "EM DASH"),
			};
	}
}
