namespace Crawler.Quality
{
	internal static class LogExcerpt
	{
		/// <summary>
		/// Renders content text with invisible characters replaced by operator-
		/// readable markers, then truncates. Designed for non-technical CMS
		/// authors reviewing CONTROL_CHARS_IN_CONTENT flags: each marker says
		/// what KIND of invisible character is present (so an editor can
		/// understand the issue) AND its codepoint (for search / self-help).
		///
		/// Markers use plain ASCII only — the tool detects exotic non-ASCII
		/// characters, so using exotic non-ASCII in its own diagnostic output
		/// would be confusing. Short forms ([CR], [LF], [TAB]) for the three
		/// universally-known control chars; the prefix [INVISIBLE &lt;kind&gt;
		/// U+XXXX] for everything else.
		/// </summary>
		internal static string Truncate(string s, int maxLength = 250)
		{
			if (string.IsNullOrEmpty(s))
			{
				return string.Empty;
			}

			var sb = new System.Text.StringBuilder(s.Length);
			foreach (var ch in s)
			{
				// Universally-known short forms.
				if (ch == '\r') { sb.Append("[CR]"); continue; }
				if (ch == '\n') { sb.Append("[LF]"); continue; }
				if (ch == '\t') { sb.Append("[TAB]"); continue; }

				// Obscure codepoints — operator-friendly long form.
				if (ch == '\u2028') { sb.Append("[INVISIBLE LINE SEPARATOR U+2028]"); continue; }
				if (ch == '\u2029') { sb.Append("[INVISIBLE PARAGRAPH SEPARATOR U+2029]"); continue; }
				if (ch == '\u200B') { sb.Append("[INVISIBLE ZERO-WIDTH SPACE U+200B]"); continue; }
				if (ch == '\u200C') { sb.Append("[INVISIBLE ZERO-WIDTH NON-JOINER U+200C]"); continue; }
				if (ch == '\u200D') { sb.Append("[INVISIBLE ZERO-WIDTH JOINER U+200D]"); continue; }
				if (ch == '\uFEFF') { sb.Append("[INVISIBLE BOM U+FEFF]"); continue; }
				if (ch == '\u00AD') { sb.Append("[INVISIBLE SOFT HYPHEN U+00AD]"); continue; }
				if (ch == '\u007F') { sb.Append("[INVISIBLE DEL U+007F]"); continue; }
				if (ch == '\u2060') { sb.Append("[INVISIBLE WORD JOINER U+2060]"); continue; }
				if (ch == '\u061C' || ch == '\u200E' || ch == '\u200F')
				{ sb.Append($"[INVISIBLE BIDI MARK U+{(int)ch:X4}]"); continue; }
				if (ch >= '\u2061' && ch <= '\u2064')
				{ sb.Append($"[INVISIBLE MATH U+{(int)ch:X4}]"); continue; }
				if (ch >= '\u202A' && ch <= '\u202E')
				{ sb.Append($"[INVISIBLE BIDI CONTROL U+{(int)ch:X4}]"); continue; }
				if (ch >= '\u2066' && ch <= '\u2069')
				{ sb.Append($"[INVISIBLE BIDI ISOLATE U+{(int)ch:X4}]"); continue; }

				// Other C0 and C1 controls fall here — generic INVISIBLE
				// CONTROL marker. C0 = U+0000..001F (minus CR/LF/TAB above),
				// C1 = U+0080..009F.
				if (ch < 0x20 || (ch >= 0x80 && ch <= 0x9F))
				{ sb.Append($"[INVISIBLE CONTROL U+{(int)ch:X4}]"); continue; }

				// Everything else — legitimate content character, keep as-is.
				sb.Append(ch);
			}

			var visible = sb.ToString();
			// Caps the rendered excerpt at maxLength (default 250; the CQ
			// control-char callers pass ContentQualityMaxExcerpt so a full
			// paragraph survives intact). Markers are already rendered here, so
			// keep maxLength comfortably above one element's text to avoid
			// slicing a marker at the boundary.
			return visible.Length > maxLength ? visible[..maxLength] + "…" : visible;
		}
	}
}
