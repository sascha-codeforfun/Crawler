namespace Crawler.SpellCheck
{
	using System.Collections.Generic;
	using System.Text.RegularExpressions;

	/// <summary>
	/// Cuts a <see cref="TextRun"/> into span-bearing <see cref="Token"/>s. Each token keeps
	/// its character offset into the run, so the originating node and exact position stay
	/// recoverable for excerpt building and highlighting — no re-derivation, ever.
	///
	/// This word pattern is the single shared source for tokenization: both Tokenize (this
	/// pipeline) and TokenizeText (used by the legacy SpellChecker) run off it, so the two
	/// paths tokenize identically by construction. That parity matters for trustworthy
	/// side-by-side comparison of old vs new findings. (The pattern was previously duplicated
	/// in a separate regex holder and kept in sync by hand; that duplicate was removed once the
	/// new path was proven.)
	///
	/// This stage tokenizes the run text AS GIVEN. Canonicalization (entity decoding, soft
	/// hyphen, dashes, smart quotes) is a separate stage and is applied to the run text before
	/// tokenization in the assembled pipeline, so the tokenizer and the excerpt see the same
	/// canonical form and cannot diverge.
	/// </summary>
	public static partial class SpellTokenizer
	{
		public static IEnumerable<Token> Tokenize(TextRun run)
		{
			// Pre-scan the run for spans whose characters are NOT prose — emails and URLs/domains —
			// then drop any word token that falls inside one. The word pattern would otherwise split
			// "name@example.de" or "www.example.de" on '@' / '.' into fake words (name, example,
			// de). Suppressing by SPAN (not by editing the word pattern) preserves tokenization
			// parity with the legacy tokenizer for everything else, and keeps surrounding prose checked.
			//
			//  - Email: '@'-anchored — unambiguous, no prose token contains '@'.
			//  - URL/domain: a scheme (http://, https://), a www. prefix, or a dotted host ending in a
			//    known TLD. The TLD anchor is the safety property: it matches host.de / sub-domain.info
			//    but NOT German abbreviations like "z.B." or "e.g" (B / g are not TLDs), so sentence
			//    fragments and abbreviations are never eaten. Scheme URLs in ATTRIBUTES are already
			//    handled by ValueClassifier; this span scan additionally covers the visible-TEXT case.
			string text = run.RawText;
			List<(int Start, int End)> skipSpans = CollectSkipSpans(text);

			foreach (Match m in WordPattern().Matches(text))
			{
				if (OverlapsAny(m.Index, m.Length, skipSpans))
				{
					continue;
				}

				yield return new Token(run, m.Value, m.Index, m.Length);
			}
		}

		private static List<(int Start, int End)> CollectSkipSpans(string text)
		{
			var spans = new List<(int Start, int End)>();
			foreach (Match m in EmailPattern().Matches(text))
			{
				spans.Add((m.Index, m.Index + m.Length));
			}

			foreach (Match m in UrlOrDomainPattern().Matches(text))
			{
				spans.Add((m.Index, m.Index + m.Length));
			}

			return spans;
		}

		private static bool OverlapsAny(int start, int length, List<(int Start, int End)> spans)
		{
			int end = start + length;
			foreach (var s in spans)
			{
				if (start < s.End && s.Start < end)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Index of the first occurrence of <paramref name="word"/> in <paramref name="text"/> that
		/// is a WHOLE token under this tokenizer: it tokenizes the text with the real word pattern
		/// and returns the offset of the first token whose text equals <paramref name="word"/>. So
		/// "Adress" is found in "Adress Line" but NOT inside "Adressen" (one token) or "Adress-daten"
		/// (a hyphenated compound is one token, tight or spaced). Returns -1 when the word is not
		/// present as a whole token. The single locator shared by the excerpt builder, the raw-HTML
		/// triage view, and the per-occurrence highlight, so all three agree with what the tokenizer
		/// flagged instead of re-deriving with regex \b (which treats '-' as a boundary and matches
		/// inside a compound).
		/// </summary>
		internal static int IndexOfWholeWord(string text, string word, bool ignoreCase = false)
		{
			if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word))
			{
				return -1;
			}

			var comparison = ignoreCase ? System.StringComparison.OrdinalIgnoreCase : System.StringComparison.Ordinal;
			foreach (Match m in WordPattern().Matches(text))
			{
				if (m.Value.Equals(word, comparison))
				{
					return m.Index;
				}
			}

			return -1;
		}

		internal static IEnumerable<string> TokenizeText(string text)
		{
			return WordPattern().Matches(text).Cast<Match>().Select(m => m.Value);
		}

		internal static bool IdentifyWord(string token)
		{
			return WordIdentifier().IsMatch(token);
		}

		// The single source for the word-token pattern, shared by Tokenize (this pipeline) and TokenizeText (the legacy SpellChecker path).
		[GeneratedRegex(@"\b[\w'äöüßÄÖÜ-]+(?:\s*-\s*[\w'äöüßÄÖÜ]+)*\b|[.,!?;:()""[\]<>]", RegexOptions.Compiled)]
		private static partial Regex WordPattern();

		// Email shape: local-part @ domain with a dotted TLD. Used only to SUPPRESS word tokens that
		// fall within an email span (never to validate addresses), so it is deliberately permissive
		// on the local part and conservative in requiring a dotted, alphabetic TLD.
		[GeneratedRegex(@"[\w.!#$%&'*+/=?^`{|}~-]+@[\w-]+(?:\.[\w-]+)*\.[a-zA-Z]{2,}", RegexOptions.Compiled)]
		private static partial Regex EmailPattern();

		// URL / bare domain in text. Three unambiguous forms, all optionally followed by a path:
		//   1. scheme://…            (http, https)
		//   2. www.host.tld          (the www. prefix is never prose)
		//   3. host(.sub)*.<TLD>     (bare domain anchored on a KNOWN TLD)
		// The fixed TLD list (case-insensitive) is the safety anchor: it excludes German abbreviations
		// ("z.B.", "e.g.", "d.h.") and decimals because their trailing segment is not a TLD. The list
		// is intentionally explicit (NOT \w{2,}) so nothing accidentally matches an abbreviation. Used
		// only to SUPPRESS overlapping word tokens, never to validate URLs.
		[GeneratedRegex(
			@"\b(?:https?://[^\s]+|(?:www\.)[\w-]+(?:\.[\w-]+)*|[\w-]+(?:\.[\w-]+)*\.(?:de|com|org|net|info|eu|biz|xyz|io|app|gov|edu|at|ch|fr|it|es|nl|uk|co))\b(?:/[^\s]*)?",
			RegexOptions.Compiled | RegexOptions.IgnoreCase)]
		private static partial Regex UrlOrDomainPattern();

		[GeneratedRegex(@"^[a-zA-ZäöüßÄÖÜ-]+(-[a-zA-ZäöüßÄÖÜ]+)*$", RegexOptions.Compiled)]
		private static partial Regex WordIdentifier();
	}
}
