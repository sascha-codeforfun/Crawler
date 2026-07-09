namespace Crawler.Quality
{
	internal static partial class WordSplits
	{
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
		private static partial System.Text.RegularExpressions.Regex Pattern();

		internal static IEnumerable<QualityIssue> Check(
			string filename, string html, ContentQualityConfig config)
		{
			foreach (System.Text.RegularExpressions.Match m in Pattern().Matches(html))
			{
				// Decode HTML entities for the excerpt so it matches the user-visible text,
				// consistent with how all other checks produce their excerpts.
				var decoded = System.Net.WebUtility.HtmlDecode(html);
				yield return new QualityIssue(
					filename,
					"SPLIT_WORD_ANCHOR",
					$"Anchor closes mid-word — stray text after </a>: '{m.Groups[1].Value}'",
					Excerpt.Around(decoded, m.Index, config.ContentQualityExcerptRadius));
			}
		}
	}
}
