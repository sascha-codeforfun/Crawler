namespace Crawler.SpellCheck
{
	using System.Collections.Generic;
	using System.Globalization;

	// Detects a defect class in spelling findings: a single token containing BOTH
	// Cyrillic and Latin letters. This is almost always corruption — typically a
	// look-alike (homoglyph) letter standing in for the correct one, e.g. Latin
	// 'A' (U+0041) in place of Cyrillic 'А' (U+0410) in "Aвтоматическая".
	//
	// Such tokens can never match any single-language dictionary, so they surface
	// as mystery spelling findings. This detector labels them deterministically so
	// triage can point an operator straight at the offending character.
	//
	// Detection is intentionally narrow:
	//   • Only LETTERS are considered. Digits, punctuation, whitespace and marks
	//     are ignored, so tokens like "КОД-2" or "Сектор-1" are not flagged for a
	//     dash or digit — only a genuine Latin+Cyrillic letter mix trips it.
	//   • Diacritics do not count as a second alphabet: "größe" stays single-script.
	//
	// Scope is Cyrillic vs Latin only, by design. This class is STRUCTURAL ONLY:
	// it returns the facts (which alphabet is the minority, which characters, where).
	// The operator-facing copy lives in the enricher/triage layer — detection is a
	// SpellCheck concern, presentation is not. Keep the split.
	public static class HomoglyphDetector
	{
		// Stable identifier for this enrichment class.
		public const string Kind = "MIXED_ALPHABET_CYRILLIC_LATIN";

		// Returns true and yields a HomoglyphFinding when the token mixes Cyrillic
		// and Latin letters; otherwise returns false.
		public static bool TryDetect(string token, out HomoglyphFinding finding)
		{
			finding = null!;
			if (string.IsNullOrEmpty(token))
			{
				return false;
			}

			var cyrillic = new List<HomoglyphChar>();
			var latin = new List<HomoglyphChar>();

			// Iterate by Unicode code point (handles surrogate pairs safely).
			var index = 0;
			var elements = StringInfo.GetTextElementEnumerator(token);
			while (elements.MoveNext())
			{
				var element = (string)elements.Current;

				// Gate on letter category BEFORE the range check. The Cyrillic/Latin
				// ranges below contain a few NON-letters: × ÷ (U+00D7/U+00F7, category Sm)
				// sit in the Latin range; U+0482 (So) and the U+2DE0–2DFF combining block
				// (Mn) sit in the Cyrillic range. Without this gate a glued token like
				// "2×Кратность" false-positives (the × counts as a "Latin letter" and
				// collides with the Cyrillic). char.IsLetter is surrogate-safe and true
				// only for Lu/Ll/Lt/Lm/Lo, so after this the ranges mean "which script",
				// not "is this a letter".
				if (!char.IsLetter(element, 0))
				{
					index += element.Length;
					continue;
				}

				var cp = char.ConvertToUtf32(element, 0);

				if (IsCyrillicLetter(cp))
				{
					cyrillic.Add(new HomoglyphChar(index, element, cp));
				}
				else if (IsLatinLetter(cp))
				{
					latin.Add(new HomoglyphChar(index, element, cp));
				}

				index += element.Length;
			}

			if (cyrillic.Count == 0 || latin.Count == 0)
			{
				return false; // single-alphabet (or no letters) — clean
			}

			// The minority alphabet is the likely intruder; point the operator at it.
			var intruderIsLatin = latin.Count <= cyrillic.Count;
			var intruders = intruderIsLatin ? latin : cyrillic;
			var intruderAlphabet = intruderIsLatin ? "Latin" : "Cyrillic";
			var majorityAlphabet = intruderIsLatin ? "Cyrillic" : "Latin";

			finding = new HomoglyphFinding(intruderAlphabet, majorityAlphabet, intruders);
			return true;
		}

		private static bool IsCyrillicLetter(int cp) =>
			// Cyrillic, Supplement, Extended-A, Extended-B
			(cp >= 0x0400 && cp <= 0x04FF) ||
			(cp >= 0x0500 && cp <= 0x052F) ||
			(cp >= 0x2DE0 && cp <= 0x2DFF) ||
			(cp >= 0xA640 && cp <= 0xA69F);

		private static bool IsLatinLetter(int cp) =>
			// Basic Latin letters + Latin-1 Supplement / Extended-A,B + Extended Additional
			(cp >= 0x0041 && cp <= 0x005A) ||
			(cp >= 0x0061 && cp <= 0x007A) ||
			(cp >= 0x00C0 && cp <= 0x024F) ||
			(cp >= 0x1E00 && cp <= 0x1EFF);
	}

	// A single offending character: its position, the char itself, and its code point.
	public sealed record HomoglyphChar(int Index, string Char, int CodePoint)
	{
		// Code point formatted as U+XXXX for display (e.g. "U+0041").
		public string CodePointLabel => "U+" + CodePoint.ToString("X4");
	}

	// Result of a positive homoglyph detection. IntruderAlphabet is the minority
	// alphabet (the suspect); MajorityAlphabet is the token's main script; Intruders
	// lists exactly which characters to highlight. Structural facts only — no copy.
	public sealed record HomoglyphFinding(
		string IntruderAlphabet,
		string MajorityAlphabet,
		IReadOnlyList<HomoglyphChar> Intruders);
}
