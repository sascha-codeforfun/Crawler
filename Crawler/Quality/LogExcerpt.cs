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
				// Invisible / control / zero-width → operator-readable marker,
				// from the shared vocabulary (see Marker). Everything else is a
				// legitimate content character and is kept as-is. Display does NOT
				// escape '[' or '|' (that is an identity-only concern); the excerpt
				// is for human reading, not for use as a delimiter-safe key.
				sb.Append(Marker(ch) ?? ch.ToString());
			}

			var visible = sb.ToString();
			// Caps the rendered excerpt at maxLength (default 250; the CQ
			// control-char callers pass ContentQualityMaxExcerpt so a full
			// paragraph survives intact). Markers are already rendered here, so
			// keep maxLength comfortably above one element's text to avoid
			// slicing a marker at the boundary.
			return visible.Length > maxLength ? visible[..maxLength] + "…" : visible;
		}

		/// <summary>
		/// Renders text as a STABLE, LOSSLESS, delimiter-safe identity string —
		/// the form used to build a tracking key, not to display. Distinct from
		/// <see cref="Truncate"/> in three ways that matter for identity:
		///
		/// <list type="number">
		///   <item><description>
		///     NO truncation. Two long elements can share a long prefix and differ
		///     only in their tail; truncating would collide them. The full text is
		///     the identity.
		///   </description></item>
		///   <item><description>
		///     Invisibles render to the SAME operator-readable markers as
		///     <see cref="Truncate"/> (shared vocabulary via <see cref="Marker"/>),
		///     IN PLACE, so position is preserved (leading vs trailing invisible
		///     yields a different identity) and the identity is stable across runs
		///     for identical ground truth. The markers are also what make the raw
		///     invisibles survive the ledger's lossy field sanitisation — a stored
		///     raw U+200B would be stripped on write; "[INVISIBLE …]" survives.
		///   </description></item>
		///   <item><description>
		///     The two characters that are dangerous to the ledger FORMAT rather
		///     than to the reader are escaped losslessly: the marker introducer
		///     '[' becomes "[BRACKET]" (FIRST, so real bracketed text in content
		///     cannot be confused with a marker), and the pipe field delimiter '|'
		///     becomes "[PIPE]". Escaping '[' first closes the grammar: the output
		///     is fully reversible and no content byte can be mistaken for either a
		///     marker or a delimiter. This is lossless — unlike stripping, which
		///     would collapse "a|b" and "a[slash]b" style variants into one
		///     identity and silently drop a finding.
		///   </description></item>
		/// </list>
		///
		/// The result is pure printable ASCII with no raw delimiter, control, or
		/// zero-width character, so it round-trips through the pipe-delimited
		/// ledger writer/reader unchanged.
		///
		/// The marker vocabulary is a STABILITY CONTRACT once it feeds an identity:
		/// renaming a marker (e.g. "[LF]" → "[NEWLINE]") would invalidate every
		/// stored key and orphan open tickets. Additions are safe; renames are not.
		/// </summary>
		internal static string EncodeForIdentity(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return string.Empty;
			}

			var sb = new System.Text.StringBuilder(s.Length);
			foreach (var ch in s)
			{
				// Escape the marker introducer FIRST so literal "[…]" text in
				// content cannot be confused with a real marker (grammar closure).
				if (ch == '[') { sb.Append("[BRACKET]"); continue; }

				// The one field delimiter the ledger splits on. Lossless, so two
				// texts differing only by a pipe stay distinct identities (the
				// ledger's own SanitizeField is lossy here — '|' → '/' — which
				// would otherwise collide them; encoding before storage avoids it).
				if (ch == '|') { sb.Append("[PIPE]"); continue; }

				var marker = Marker(ch);
				if (marker != null) { sb.Append(marker); continue; }

				// Legitimate content character — keep as-is.
				sb.Append(ch);
			}

			return sb.ToString();
		}

		/// <summary>
		/// Single source of truth for the invisible-character marker vocabulary,
		/// shared by <see cref="Truncate"/> (display) and
		/// <see cref="EncodeForIdentity"/> (tracking key). Returns the operator-
		/// readable marker for an invisible / control / zero-width character, or
		/// null for a legitimate content character. Keeping both consumers on one
		/// table guarantees display and identity never diverge and that the
		/// stability contract covers both.
		/// </summary>
		private static string? Marker(char ch)
		{
			// Universally-known short forms.
			if (ch == '\r') { return "[CR]"; }
			if (ch == '\n') { return "[LF]"; }
			if (ch == '\t') { return "[TAB]"; }

			// Obscure codepoints — operator-friendly long form.
			if (ch == '\u2028') { return "[INVISIBLE LINE SEPARATOR U+2028]"; }
			if (ch == '\u2029') { return "[INVISIBLE PARAGRAPH SEPARATOR U+2029]"; }
			if (ch == '\u200B') { return "[INVISIBLE ZERO-WIDTH SPACE U+200B]"; }
			if (ch == '\u200C') { return "[INVISIBLE ZERO-WIDTH NON-JOINER U+200C]"; }
			if (ch == '\u200D') { return "[INVISIBLE ZERO-WIDTH JOINER U+200D]"; }
			if (ch == '\uFEFF') { return "[INVISIBLE BOM U+FEFF]"; }
			if (ch == '\u00AD') { return "[INVISIBLE SOFT HYPHEN U+00AD]"; }
			if (ch == '\u007F') { return "[INVISIBLE DEL U+007F]"; }
			if (ch == '\u2060') { return "[INVISIBLE WORD JOINER U+2060]"; }
			if (ch == '\u061C' || ch == '\u200E' || ch == '\u200F')
			{ return $"[INVISIBLE BIDI MARK U+{(int)ch:X4}]"; }
			if (ch >= '\u2061' && ch <= '\u2064')
			{ return $"[INVISIBLE MATH U+{(int)ch:X4}]"; }
			if (ch >= '\u202A' && ch <= '\u202E')
			{ return $"[INVISIBLE BIDI CONTROL U+{(int)ch:X4}]"; }
			if (ch >= '\u2066' && ch <= '\u2069')
			{ return $"[INVISIBLE BIDI ISOLATE U+{(int)ch:X4}]"; }

			// Other C0 and C1 controls — generic INVISIBLE CONTROL marker.
			// C0 = U+0000..001F (minus CR/LF/TAB above), C1 = U+0080..009F.
			if (ch < 0x20 || (ch >= 0x80 && ch <= 0x9F))
			{ return $"[INVISIBLE CONTROL U+{(int)ch:X4}]"; }

			return null;
		}
	}
}
