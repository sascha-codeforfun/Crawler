namespace Crawler
{
	using System.Collections.Generic;
	using System.Text;

	/// <summary>
	/// Builds and sanitizes the per-download HTTP header sidecar (the ".header" file
	/// saved alongside each download). SanitizeHeaderValue renders line-break-class
	/// and invisible characters as visible ASCII markers so a value can never corrupt
	/// the one-line-per-header format while preserving the forensic fact that the
	/// server emitted them; FormatHeaderSidecar assembles the verbatim two-section
	/// (REQUEST / RESPONSE) text from already-extracted header pairs, keeping it pure
	/// and unit-testable. Deliberately separate from ContentQuality's CMS-editor
	/// marker formatter (see the protected note on SanitizeHeaderValue).
	/// </summary>
	public static class HeaderSidecar
	{
		/// <summary>The extension used for the per-download header sidecar file.</summary>
		public const string HeaderSidecarExtension = ".header";

		/// <summary>
		/// [KEEP] Header-sidecar value sanitizer. Renders line-break-class characters
		/// as visible ASCII markers ([CR], [LF], [INVISIBLE … U+XXXX]) so a single
		/// header value can never split across physical lines (which would corrupt
		/// the "Name: Value"-per-line sidecar format and mislead a reader), WHILE
		/// preserving the forensic fact that the server emitted the character — a
		/// silent space would erase exactly the signal DevOps wants when chasing a
		/// malformed-header incident.
		///
		/// [KEEP] This is deliberately SEPARATE from ContentQuality's CMS-editor
		/// excerpt marker formatter, even though both use the same visible-marker
		/// idea. They serve different use cases — CMS-editor diagnostics vs. header
		/// forensics — and are expected to change independently. Do NOT unify them in
		/// a future consolidation pass: coupling them would mean a change driven by
		/// one use case silently alters the other. The shared idea is intentional
		/// duplication, not accidental.
		///
		/// TAB is left verbatim: it is legal and common in header values and does not
		/// break the one-line-per-header format (it is not a line break). Other C0/C1
		/// controls and zero-width characters are rendered as [INVISIBLE … U+XXXX] so
		/// nothing is silently dropped.
		/// </summary>
		public static string SanitizeHeaderValue(string? raw)
		{
			if (string.IsNullOrEmpty(raw))
			{
				return string.Empty;
			}

			var sb = new StringBuilder(raw.Length);
			foreach (var ch in raw)
			{
				switch (ch)
				{
					case '\r': sb.Append("[CR]"); continue;
					case '\n': sb.Append("[LF]"); continue;
					case '\t': sb.Append('\t'); continue; // legal in header values; kept verbatim
					case '\u2028': sb.Append("[INVISIBLE LINE SEPARATOR U+2028]"); continue;
					case '\u2029': sb.Append("[INVISIBLE PARAGRAPH SEPARATOR U+2029]"); continue;
					case '\u200B': sb.Append("[INVISIBLE ZERO-WIDTH SPACE U+200B]"); continue;
					case '\u200C': sb.Append("[INVISIBLE ZERO-WIDTH NON-JOINER U+200C]"); continue;
					case '\u200D': sb.Append("[INVISIBLE ZERO-WIDTH JOINER U+200D]"); continue;
					case '\uFEFF': sb.Append("[INVISIBLE BOM U+FEFF]"); continue;
				}

				// Other C0 controls (except TAB handled above) and C1 controls.
				if (ch < 0x20 || (ch >= 0x80 && ch <= 0x9F))
				{
					sb.Append($"[INVISIBLE CONTROL U+{(int)ch:X4}]");
					continue;
				}

				sb.Append(ch);
			}
			return sb.ToString();
		}

		/// <summary>
		/// Builds the verbatim two-section header sidecar text. Request and
		/// response header pairs are supplied already-extracted (name, value) so this
		/// stays pure and unit-testable — the HTTP-typed extraction lives at the
		/// download call sites. All occurrences are preserved in order (a repeated
		/// header such as Set-Cookie yields one line each); values pass through
		/// <see cref="SanitizeHeaderValue"/>. Names are emitted as-is (HTTP tokens
		/// cannot contain a colon, so a reader splits on the FIRST ':' only — a value
		/// may freely contain further colons).
		/// </summary>
		/// <param name="requestLine">e.g. "GET https://example.test/page".</param>
		/// <param name="requestHeaders">Request header (name, value) pairs, in order.</param>
		/// <param name="statusLine">e.g. "HTTP/1.1 200 OK".</param>
		/// <param name="responseHeaders">Response header (name, value) pairs, in order.</param>
		public static string FormatHeaderSidecar(
			string requestLine,
			IEnumerable<(string Name, string Value)> requestHeaders,
			string statusLine,
			IEnumerable<(string Name, string Value)> responseHeaders)
		{
			var sb = new StringBuilder();
			sb.Append("=== REQUEST ===").Append('\n');
			sb.Append(SanitizeHeaderValue(requestLine)).Append('\n');
			foreach (var (name, value) in requestHeaders)
			{
				sb.Append(name).Append(": ").Append(SanitizeHeaderValue(value)).Append('\n');
			}

			sb.Append("=== RESPONSE ===").Append('\n');
			sb.Append(SanitizeHeaderValue(statusLine)).Append('\n');
			foreach (var (name, value) in responseHeaders)
			{
				sb.Append(name).Append(": ").Append(SanitizeHeaderValue(value)).Append('\n');
			}

			return sb.ToString();
		}
	}
}
