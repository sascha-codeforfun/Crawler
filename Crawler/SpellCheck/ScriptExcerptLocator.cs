namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Text;

	/// <summary>
	/// Locates the RAW source span (offset + length into a raw script excerpt) that DECODES to a given
	/// whole word, so the triage UI can highlight a misspelled token inside a raw script context that
	/// still carries JS escapes. The checker flags the DECODED form ("Auto-Scroll"), but the 623
	/// context window shows raw source ("Auto\u002DScroll"), so a plain substring search fails. This
	/// decodes the excerpt alongside a parallel raw-offset map, finds the word in the decoded text with
	/// the shared whole-word locator, and maps the match back to raw coordinates.
	///
	/// The escape handling mirrors <see cref="JsStringLiteralExtractor"/> (the authority). Any case it
	/// does not reproduce simply yields no match, so the caller falls back to an unhighlighted excerpt
	/// — the pre-existing behaviour, never a wrong highlight.
	/// </summary>
	public static class ScriptExcerptLocator
	{
		/// <summary>
		/// Returns the (start, length) of the raw span in <paramref name="rawExcerpt"/> that decodes to
		/// the whole word <paramref name="decodedWord"/> (case-insensitive), or (-1, 0) if not found.
		/// The returned span covers the raw form, so an escaped word like "Auto\u002DScroll" highlights
		/// in full even though the decoded word is shorter.
		/// </summary>
		public static (int Start, int Length) LocateRawSpan(string rawExcerpt, string decodedWord)
		{
			if (string.IsNullOrEmpty(rawExcerpt) || string.IsNullOrEmpty(decodedWord))
			{
				return (-1, 0);
			}

			var (decoded, rawAt) = Decode(rawExcerpt);
			int idx = SpellTokenizer.IndexOfWholeWord(decoded, decodedWord, ignoreCase: true);
			if (idx < 0)
			{
				return (-1, 0);
			}

			int rawStart = rawAt[idx];
			int rawEnd = rawAt[idx + decodedWord.Length];
			return (rawStart, rawEnd - rawStart);
		}

		// Decode JS string escapes in `raw`, returning the decoded text and a map `rawAt` where
		// rawAt[k] is the raw index at which decoded char k begins, and rawAt[decoded.Length] is the
		// raw index just past the last consumed char (so the raw end of char k is rawAt[k + 1]). The
		// switch mirrors JsStringLiteralExtractor.AppendEscape; an escape it cannot decode degrades to
		// the JS "\\z is z" rule, which at worst yields a non-match upstream.
		private static (string Decoded, int[] RawAt) Decode(string raw)
		{
			int n = raw.Length;
			var sb = new StringBuilder(n);
			var rawAt = new List<int>(n + 1);
			int i = 0;

			while (i < n)
			{
				int charStart = i;
				char c = raw[i];
				if (c != '\\' || i + 1 >= n)
				{
					sb.Append(c);
					rawAt.Add(charStart);
					i++;
					continue;
				}

				char e = raw[i + 1];
				switch (e)
				{
					case 'n': sb.Append('\n'); rawAt.Add(charStart); i += 2; continue;
					case 't': sb.Append('\t'); rawAt.Add(charStart); i += 2; continue;
					case 'r': sb.Append('\r'); rawAt.Add(charStart); i += 2; continue;
					case 'b': sb.Append('\b'); rawAt.Add(charStart); i += 2; continue;
					case 'f': sb.Append('\f'); rawAt.Add(charStart); i += 2; continue;
					case 'v': sb.Append('\v'); rawAt.Add(charStart); i += 2; continue;
					case '0': sb.Append('\0'); rawAt.Add(charStart); i += 2; continue;
					case '\\': sb.Append('\\'); rawAt.Add(charStart); i += 2; continue;
					case '\'': sb.Append('\''); rawAt.Add(charStart); i += 2; continue;
					case '"': sb.Append('"'); rawAt.Add(charStart); i += 2; continue;
					case '`': sb.Append('`'); rawAt.Add(charStart); i += 2; continue;
					case '/': sb.Append('/'); rawAt.Add(charStart); i += 2; continue;

					case '\r': // line continuation \<CR>(<LF>) — emits no character
						i += 2;
						if (i < n && raw[i] == '\n') i++;
						continue;
					case '\n': // line continuation \<LF> — emits no character
						i += 2;
						continue;

					case 'x':
						if (i + 3 < n && TryHex(raw, i + 2, 2, out int vx))
						{
							sb.Append((char)vx);
							rawAt.Add(charStart);
							i += 4;
							continue;
						}

						break;

					case 'u':
						if (i + 2 < n && raw[i + 2] == '{')
						{
							int j = i + 3;
							int start = j;
							while (j < n && raw[j] != '}') j++;
							if (j < n && j > start && TryHex(raw, start, j - start, out int cp) && cp <= 0x10FFFF)
							{
								foreach (char ch in char.ConvertFromUtf32(cp))
								{
									sb.Append(ch);
									rawAt.Add(charStart);
								}

								i = j + 1;
								continue;
							}

							break;
						}

						if (i + 5 < n && TryHex(raw, i + 2, 4, out int u))
						{
							sb.Append((char)u);
							rawAt.Add(charStart);
							i += 6;
							continue;
						}

						break;
				}

				// Unknown / malformed escape: JS treats \z as the character z (drop the backslash).
				sb.Append(e);
				rawAt.Add(charStart);
				i += 2;
			}

			rawAt.Add(i); // sentinel: raw end of the last decoded char
			return (sb.ToString(), rawAt.ToArray());
		}

		private static bool TryHex(string s, int start, int count, out int value)
		{
			value = 0;
			if (count <= 0 || start + count > s.Length)
			{
				return false;
			}

			return int.TryParse(s.AsSpan(start, count), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
		}
	}
}
