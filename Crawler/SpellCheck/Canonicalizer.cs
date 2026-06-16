namespace Crawler.SpellCheck
{
	using System.Net;
	using System.Text;
	using System.Text.RegularExpressions;

	/// <summary>
	/// The single canonicalization rule for the spell pipeline. Applied ONCE to a run's
	/// text, its output feeds BOTH the tokenizer and the excerpt — so the two can never see
	/// different forms of the same text. That single source is what deletes the soft-hyphen,
	/// dash, quote and decode DIVERGENCES the old pipeline suffered, where the tokenizer
	/// normalized text the excerpt did not (and decoded a different number of times), so a
	/// flagged word could fail to match its own excerpt.
	///
	/// Rule (deliberate, documented):
	///   * HTML-decode ONCE. Single decode is the correct canonicalization: it resolves real
	///     entities (&amp;amp; → &amp;, &amp;uuml; → ü) but does NOT over-decode a literal
	///     double-encoded sequence (&amp;amp;uuml; stays the literal text "&amp;uuml;") — the
	///     silent-corruption failure mode of double decoding. Verified parity-neutral on the
	///     target corpus; revisitable if a different corpus needs another rule.
	///   * Drop soft hyphens (U+00AD) — invisible line-break hints, never part of a word.
	///   * En dash (U+2013) → hyphen.
	///   * Curly single quotes (U+2018 / U+2019) → apostrophe.
	///   * Collapse every run of whitespace to a single space, then trim.
	/// </summary>
	public static partial class Canonicalizer
	{
		public static string Canonicalize(string raw)
		{
			if (string.IsNullOrEmpty(raw))
			{
				return string.Empty;
			}

			string decoded = WebUtility.HtmlDecode(raw);

			// A literal "&shy;" can survive the single decode when the source was double-encoded
			// (e.g. "&amp;shy;" → "&shy;"). It is a soft hyphen written as a named entity; map it to
			// U+00AD so the invisible-character drop below removes it, exactly as it would the Unicode
			// soft hyphen. SCOPED DELIBERATELY to &shy; only — the soft hyphen is invisible and safe to
			// drop in any form; other literal entities are NOT converted here (their rendered behaviour
			// is unverified, and a surviving letter-entity may be a genuine content defect to surface).
			if (decoded.IndexOf("&shy;", System.StringComparison.Ordinal) >= 0)
			{
				decoded = decoded.Replace("&shy;", "\u00AD", System.StringComparison.Ordinal);
			}

			// Strip HTML tags that appear inside the (now decoded) text. Some CMS fields store rich-text
			// fragments encoded inside attributes (e.g. data-pagenav-title="…&lt;br&gt;…&lt;sup&gt;2…"),
			// which the single decode above turns into literal <br>, <sup>, … — and the tokenizer would
			// otherwise emit the tag NAMES ("br", "sup") as words. Tag-shaped text is markup or format
			// syntax (this also removes XML format-spec references like <Ctry>, <TwnNm> that documentation
			// quotes inline) — none of it is prose, so it is out of spell-check scope. Each tag becomes a
			// SPACE (a word boundary, so "A<br>B" → "A B", never "AB"); any prose carried in an alt/title
			// attribute is preserved (kept as text) since that IS human-readable content. The match is
			// anchored on a letter after '<' so a stray '<' in prose (e.g. "a < b") is never touched.
			if (decoded.IndexOf('<') >= 0)
			{
				decoded = HtmlTagPattern().Replace(decoded, m =>
				{
					var altTitle = AltTitlePattern().Match(m.Value);
					return altTitle.Success ? " " + altTitle.Groups["v"].Value + " " : " ";
				});
			}

			var sb = new StringBuilder(decoded.Length);
			bool inSpace = false;
			foreach (char c in decoded)
			{
				// Drop invisible formatting characters that are never part of a word and would
				// otherwise produce a mismatch between the flagged word and the surrounding text.
				// Soft hyphen (00AD), zero-width space/non-joiner/joiner (200B-200D), word joiner
				// (2060) and BOM/zero-width-no-break-space (FEFF).
				if (c == '\u00AD'
					|| c == '\u200B' || c == '\u200C' || c == '\u200D'
					|| c == '\u2060' || c == '\uFEFF')
				{
					continue;
				}

				char r = c switch
				{
					'\u2013' => '-',  // en dash
					'\u2018' => '\'', // left single quote
					'\u2019' => '\'', // right single quote / apostrophe
					_ => c
				};

				if (char.IsWhiteSpace(r))
				{
					if (!inSpace)
					{
						sb.Append(' ');
						inSpace = true;
					}
				}
				else
				{
					sb.Append(r);
					inSpace = false;
				}
			}

			return sb.ToString().Trim();
		}

		// An HTML tag: '<' optional '/' then a LETTER (the anchor that keeps a bare '<' in prose, e.g.
		// "a < b" or "3<5", from matching), then any non-'>' run, then '>'. Matches <br>, <br/>,
		// <sup>, </sup>, <abbr title="…">, <Ctry>, etc.
		[GeneratedRegex(@"</?[a-zA-Z][^>]*>", RegexOptions.Compiled)]
		private static partial Regex HtmlTagPattern();

		// Captures the value of an alt or title attribute (single or double quoted) within a tag, so
		// that human-readable prose carried there is preserved rather than discarded with the tag.
		[GeneratedRegex("(?:alt|title)\\s*=\\s*(?:\"(?<v>[^\"]*)\"|'(?<v>[^']*)')", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
		private static partial Regex AltTitlePattern();
	}
}
