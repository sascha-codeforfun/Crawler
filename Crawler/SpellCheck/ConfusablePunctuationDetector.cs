namespace Crawler.SpellCheck
{
	using System.Collections.Generic;
	using System.Text;

	// Detects a defect class in spelling findings: a token containing a non-ASCII
	// look-alike of an ASCII syntax character WEDGED BETWEEN two Latin letters —
	// e.g. U+FF70 HALFWIDTH KATAKANA-HIRAGANA PROLONGED SOUND MARK between 'g' and
	// 't' in "paddingｰtop", where the intended token was "padding-top".
	//
	// This is a sibling of HomoglyphDetector, NOT a generalisation of it. The two
	// share a surface shape ("a token uniform in one category except a confusable
	// minority") but encode DIFFERENT intents: homoglyph catches confusable LETTERS
	// (Cyrillic/Latin); this catches confusable PUNCTUATION standing in for ASCII
	// syntax. They are kept as separate, self-contained detectors on purpose — the
	// duplication is cheaper than a shared abstraction that a future maintainer might
	// "tidy" without realising the two intents are only coincidentally alike.
	//
	// The "between two Latin letters" gate is what makes it airtight in code:
	//   • a CSS string value (content: "—") is NOT a Latin identifier, so its
	//     look-alikes are not flanked by Latin letters → not flagged (the content:
	//     exemption falls out for free);
	//   • a confusable SEPARATOR (a look-alike colon between an identifier and a
	//     space) is not flanked by letters either → not flagged (that is a different
	//     shape, deliberately out of scope here).
	// Note the gate alone is NOT sufficient: a confusable hyphen between Latin letters
	// CAN be legitimate typography in PROSE (German "E‑Mail" uses U+2011). The code-
	// context decision is made by the CONSUMER (the enricher only fires on script-
	// scan sources); this detector reports the structural fact and nothing more.
	//
	// STRUCTURAL ONLY: returns the facts (which characters, where, and the ASCII
	// canonicalised suggestion). Operator-facing copy lives in the enricher.
	public static class ConfusablePunctuationDetector
	{
		// Stable identifier for this enrichment class.
		public const string Kind = "CONFUSABLE_PUNCTUATION_IN_CODE";

		// Returns true and yields a ConfusablePunctuationFinding when the token has at
		// least one mapped confusable flanked by ASCII Latin letters; else false.
		public static bool TryDetect(string token, out ConfusablePunctuationFinding finding)
		{
			finding = null!;
			if (string.IsNullOrEmpty(token) || token.Length < 3)
			{
				return false; // need a char with a letter on each side
			}

			List<ConfusableHit>? hits = null;

			// Every mapped confusable is a single BMP code unit, and the flanking test
			// is on ASCII letters, so a plain char scan is correct here (no surrogate
			// handling needed for either the offender or its neighbours).
			for (var i = 1; i < token.Length - 1; i++)
			{
				if (!ConfusablePunctuationMap.HyphenConfusables.TryGetValue(token[i], out var entry))
				{
					continue;
				}

				if (!IsAsciiLetter(token[i - 1]) || !IsAsciiLetter(token[i + 1]))
				{
					continue; // not embedded in a Latin identifier — see class note
				}

				(hits ??= new List<ConfusableHit>()).Add(
					new ConfusableHit(i, token[i].ToString(), token[i], entry.Canonical, entry.UnicodeName));
			}

			if (hits == null)
			{
				return false;
			}

			// Deterministic suggestion: replace each offender with its ASCII canonical.
			var builder = new StringBuilder(token);
			foreach (var hit in hits)
			{
				builder[hit.Index] = hit.Canonical;
			}

			finding = new ConfusablePunctuationFinding(token, builder.ToString(), hits);
			return true;
		}

		private static bool IsAsciiLetter(char c) =>
			(c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
	}

	// A single offending character: its position, the char, its code point, the ASCII
	// character it should be, and the Unicode name (for operator copy).
	public sealed record ConfusableHit(int Index, string Char, int CodePoint, char Canonical, string UnicodeName)
	{
		// Code point formatted as U+XXXX for display (e.g. "U+FF70").
		public string CodePointLabel => "U+" + CodePoint.ToString("X4");
	}

	// Result of a positive detection. Original is the token as found; Suggestion is
	// the ASCII-canonicalised form (e.g. "padding-top"); Hits lists exactly which
	// characters to highlight. Structural facts only — no operator copy.
	public sealed record ConfusablePunctuationFinding(
		string Original,
		string Suggestion,
		IReadOnlyList<ConfusableHit> Hits);
}
