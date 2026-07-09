namespace Crawler.SpellCheck
{
	using System;
	using Crawler.Lexicon;
	using System.Collections.Generic;
	using System.Diagnostics.CodeAnalysis;
	using System.Linq;

	/// <summary>
	/// The spell-checking pass: tokenises text and reports words that no dictionary
	/// tier accepts. Brand/compound prefix handling is delegated to
	/// <see cref="WordPrefix"/>; German trailing-hyphen compound stems are resolved
	/// by <see cref="CheckTrailingHyphenStem"/>, and the parenthesised optional-prefix
	/// form "(prefix-)stem" by <see cref="TryParenthesizedPrefixJoin"/>. Extracted from Tools.
	/// </summary>
	public static class SpellChecker
	{
		/// <summary>
		/// Spell-check <paramref name="text"/> against <paramref name="dictionary"/> for
		/// <paramref name="language"/>. Words starting with a configured prefix followed
		/// by a hyphen have the prefix stripped before lookup — the remainder is checked
		/// and reported instead of the full token, so errors are immediately actionable.
		/// </summary>
		[ExcludeFromCodeCoverage(Justification =
			"Coupled to Hunspell WordList.Check across multi-tier dictionary bundles. " +
			"Testing requires real .aff/.dic fixtures; exercises Hunspell library " +
			"behaviour rather than Tools logic.")]
		public static IEnumerable<KeyValuePair<string, List<string>>> Check(
			string text,
			Bundle dictionary,
			string language,
			IReadOnlyList<string>? prefixesToStrip = null,
			IReadOnlyList<string>? fugenelemente = null)
		{
			var prefixes = prefixesToStrip ?? [];
			var fugen = fugenelemente ?? [];
			Dictionary<string, List<string>> errors = [];

			foreach (var word in SpellTokenizer.TokenizeText(text))
			{
				var trimmedWord = word?.Trim();
				if (string.IsNullOrEmpty(trimmedWord) || !SpellTokenizer.IdentifyWord(trimmedWord))
				{
					continue;
				}

				var wordToCheck = WordPrefix.Strip(trimmedWord, prefixes);
				if (wordToCheck == null)
				{
					continue;
				}

				wordToCheck = wordToCheck.Replace("\u00AD", ""); // strip soft hyphen from stale normalized text
				if (string.IsNullOrEmpty(wordToCheck))
				{
					continue;
				}
				// Trailing-hyphen compound prefix — two cases:
				// 1. Token ends with "-" (captured by tokenizer in some patterns)
				// 2. Token appears as "Word-," or "Word- " in source text — the tokenizer
				//    drops the trailing hyphen at a word boundary, so we check the source.
				bool isTrailingHyphenStem = wordToCheck.EndsWith('-')
					|| text.Contains(wordToCheck + "-", StringComparison.Ordinal);

				if (isTrailingHyphenStem)
				{
					if (CheckTrailingHyphenStem(
						wordToCheck.EndsWith('-') ? wordToCheck : wordToCheck + "-",
						dictionary, fugen))
					{
						continue;
					}

					// German "(prefix-)stem" optional-prefix form: the bare prefix is a
					// bound morpheme, valid only joined to the immediately following word.
					// Accept when that join is a real word (see TryParenthesizedPrefixJoin).
					if (TryParenthesizedPrefixJoin(wordToCheck, text, dictionary))
					{
						continue;
					}
				}
				else
				{
					if (dictionary.Check(wordToCheck))
					{
						continue;
					}
				}

				var reportWord = wordToCheck;
				if (!errors.TryGetValue(reportWord, out var languages))
				{
					languages = [];
					errors[reportWord] = languages;
				}

				if (!languages.Contains(language))
				{
					languages.Add(language);
				}
			}

			return errors;
		}

		// [KEEP] Seam: trailing-hyphen compound checking sits BETWEEN two spelling
		// engines. Hunspell (the current dic/aff substrate) cannot validate a German
		// compound stem left by a trailing hyphen, so this method bridges the gap. A
		// future spelling engine may handle hyphenation natively — if so, this is the
		// seam to revisit. Do not silently drop this note; it marks a cross-engine boundary.
		/// <summary>
		/// When a token ends with a hyphen (German compound-word prefix), checks the stem
		/// against the dictionary: (1) strip the hyphen and check as-is; (2) strip each
		/// Fugenelement (longest first) and check the stem. True if any stem form is accepted.
		/// </summary>
		internal static bool CheckTrailingHyphenStem(
			string word,
			Bundle dictionary,
			IReadOnlyList<string> fugenelemente)
		{
			// Strip the trailing hyphen.
			var stem = word[..^1];

			// Check stem as-is first.
			if (dictionary.Check(stem))
			{
				return true;
			}

			// Try stripping each Fugenelement (longest first).
			foreach (var fuge in fugenelemente.OrderByDescending(f => f.Length))
			{
				if (!string.IsNullOrEmpty(fuge) && stem.EndsWith(fuge, StringComparison.OrdinalIgnoreCase))
				{
					var stripped = stem[..^fuge.Length];
					if (!string.IsNullOrEmpty(stripped) && dictionary.Check(stripped))
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>
		/// German optional-prefix construction "(prefix-)stem": a parenthesised prefix
		/// with a completion hyphen that attaches to the immediately following word, so
		/// the text reads as either "stem" or "prefixstem". The bare prefix is a bound
		/// morpheme — not a standalone word — so it can only be validated joined to the
		/// next word. Scans <paramref name="text"/> for the literal shape "(prefix-)" and,
		/// for each occurrence, joins <paramref name="prefix"/> to the following run of
		/// letters and checks the compound against <paramref name="dictionary"/>. Returns
		/// true if any such join is accepted. Deliberately scoped to the parenthesised
		/// form only — bare trailing hyphens are handled by <see cref="CheckTrailingHyphenStem"/>.
		/// The suffix-after-base mirror ("Base(suffix)", e.g. "Nachhaltigkeit(smanagement)") lives as
		/// a per-occurrence rescue in <c>Crawler.SpellCheck.RunChecker.TryParenthesizedSuffixJoin</c>.
		/// </summary>
		internal static bool TryParenthesizedPrefixJoin(string prefix, string text, Bundle dictionary)
		{
			if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(text))
			{
				return false;
			}

			var needle = "(" + prefix + "-)";
			int from = 0;
			while (true)
			{
				int hit = text.IndexOf(needle, from, StringComparison.Ordinal);
				if (hit < 0)
				{
					return false;
				}

				int afterNeedle = hit + needle.Length;
				int end = afterNeedle;
				while (end < text.Length && IsCompoundLetter(text[end]))
				{
					end++;
				}

				if (end > afterNeedle)
				{
					var nextWord = text[afterNeedle..end];
					if (dictionary.Check(prefix + nextWord))
					{
						return true;
					}
				}

				from = hit + 1;
			}
		}

		// Letters that may form the stem joined to a parenthesised prefix: ASCII letters
		// plus the German umlauts/eszett. Intentionally excludes digits, '_' and '-' so
		// the join takes exactly the following word, not a hyphenated continuation.
		private static bool IsCompoundLetter(char c) =>
			(c >= 'a' && c <= 'z')
			|| (c >= 'A' && c <= 'Z')
			|| c is 'ä' or 'ö' or 'ü' or 'ß' or 'Ä' or 'Ö' or 'Ü';
	}
}
