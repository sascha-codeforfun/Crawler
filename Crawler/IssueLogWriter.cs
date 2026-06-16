namespace Crawler
{
	using System.Text;

	/// <summary>
	/// Central writer for all delimited log files produced by the crawler.
	///
	/// SECURITY: every field written through this class is sanitized to remove
	/// characters that would corrupt the log format — CR / LF / tab (which would
	/// split or scramble line-oriented parsing) and control / bidi / zero-width
	/// codepoints (which can appear in CMS content from copy-paste or, in the
	/// worst case, can be used as an injection vector by a malicious page).
	///
	/// The delimiter character itself is also stripped from field values — a
	/// page whose content contains the delimiter must not be able to inject a
	/// synthetic field. Replacement with '/' is conservative: no information
	/// loss, no parser confusion.
	///
	/// All writers to delimited logs in this codebase must route through this
	/// class. Do not call File.AppendAllLines / WriteAllLines / StreamWriter
	/// directly for log files whose content is parsed downstream — sanitization
	/// is mandatory at the writer boundary because content-extraction-boundary
	/// sanitization is best-effort and may have gaps.
	/// </summary>
	internal static class IssueLogWriter
	{
		/// <summary>
		/// Default field separator for content-quality, spell, IssueTracking,
		/// canonical, and most other crawler logs.
		/// </summary>
		public const char PipeDelimiter = '|';

		/// <summary>
		/// SEO log uses '@@@' (a string, not a single char). Treated as a
		/// special-case separator string in <see cref="WriteRecord(string, string, string[])"/>
		/// overloads that take a separator string.
		/// </summary>
		public const string SeoDelimiter = "@@@";

		/// <summary>
		/// UTF-8 encoding that emits the byte-order-mark (EF BB BF) at the start of
		/// every file. Use this for ALL log writers in the codebase — the BOM
		/// signals UTF-8 to downstream consumers (PowerQuery, Excel, log readers)
		/// that would otherwise apply locale-default decoding.
		///
		/// The non-streaming helpers in this class (<see cref="Append"/>,
		/// <see cref="AppendMany"/>, <see cref="Write(string, char, IEnumerable{string?[]})"/>,
		/// <see cref="Write(string, string, IEnumerable{string?[]})"/>) route through
		/// <see cref="File.WriteAllLines(string, IEnumerable{string}, Encoding)"/> /
		/// <see cref="File.AppendAllLines(string, IEnumerable{string}, Encoding)"/>
		/// with <see cref="Encoding.UTF8"/>, which is itself a BOM-emitting
		/// encoding (.NET's static <c>Encoding.UTF8</c> is
		/// <c>new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)</c>) — those
		/// helpers therefore already produce BOM-prefixed logs without using this
		/// constant directly. The constant exists for log writers that use
		/// <see cref="StreamWriter"/> directly (the streaming writer below, and the
		/// SEO and self-link scanners) where the encoding must be specified explicitly.
		/// </summary>
		public static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

		/// <summary>
		/// Replacement for stripped delimiter / CR / LF / tab characters in field
		/// content. Visible and parseable — no information loss, no parser confusion.
		/// </summary>
		private const char Replacement = '/';

		// ── Sanitization ──────────────────────────────────────────────────

		/// <summary>
		/// Sanitize a string for inclusion in a delimited log line.
		/// Returns the cleaned string plus a flag indicating whether ANY change
		/// was made — callers wanting to flag the source content as defective
		/// (e.g. <c>CONTROL_CHARS_IN_CONTENT</c>) check the flag.
		///
		/// Replaces:
		///   - CR (U+000D), LF (U+000A), TAB (U+0009) → space
		///   - Other C0 controls (U+0000–U+001F except above) → stripped
		///   - C1 controls (U+0080–U+009F) → stripped
		///   - Zero-width chars (U+200B, U+200C, U+200D, U+FEFF) → stripped
		///   - Bidirectional formatting (U+202A–U+202E, U+2066–U+2069) → stripped
		///   - The delimiter character itself → '/'
		///
		/// Multiple consecutive spaces produced by sanitization are NOT collapsed —
		/// callers can do that themselves if needed. The goal is minimal change,
		/// not aesthetic cleanup.
		/// </summary>
		public static (string Cleaned, bool Changed) SanitizeField(string? raw, char delimiter = PipeDelimiter)
		{
			if (string.IsNullOrEmpty(raw))
			{
				return (raw ?? string.Empty, false);
			}

			var sb = new StringBuilder(raw.Length);
			bool changed = false;

			foreach (var ch in raw)
			{
				if (ch == delimiter)
				{
					sb.Append(Replacement);
					changed = true;
					continue;
				}
				if (ch == '\r' || ch == '\n' || ch == '\t'
					|| ch == '\u2028' || ch == '\u2029')
				{
					// CR / LF / TAB plus U+2028 LINE SEPARATOR and U+2029
					// PARAGRAPH SEPARATOR — the Unicode line-break characters.
					// .NET's StreamReader.ReadLine does NOT split on the latter
					// two, so a record containing them parses correctly, but
					// many text editors render them as visual line breaks
					// (misleading operator view) and other tooling may split
					// on them. Treat them as line breaks for sanitization
					// purposes — replace with space.
					sb.Append(' ');
					changed = true;
					continue;
				}
				// C0 controls except the above (already handled).
				if (ch < 0x20)
				{
					changed = true;
					continue;
				}
				// C1 controls.
				if (ch >= 0x80 && ch <= 0x9F)
				{
					changed = true;
					continue;
				}
				// Zero-width chars.
				if (ch == '\u200B' || ch == '\u200C' || ch == '\u200D' || ch == '\uFEFF')
				{
					changed = true;
					continue;
				}
				// Bidirectional formatting controls.
				if ((ch >= '\u202A' && ch <= '\u202E') || (ch >= '\u2066' && ch <= '\u2069'))
				{
					changed = true;
					continue;
				}
				sb.Append(ch);
			}

			return (sb.ToString(), changed);
		}

		/// <summary>Convenience: sanitize a multi-character delimiter (e.g. "@@@") by
		/// also stripping any occurrence of the delimiter string from the field.
		/// First applies the single-char sanitizer for control chars, then
		/// replaces the delimiter string.</summary>
		public static (string Cleaned, bool Changed) SanitizeField(string? raw, string delimiter)
		{
			if (delimiter.Length == 1)
			{
				return SanitizeField(raw, delimiter[0]);
			}

			// First pass: control / bidi / zero-width using a delimiter that
			// won't conflict (use char 0xFFFF which we strip below as C0/C1 doesn't
			// match it; effectively no-op for delimiter substitution here).
			var (cleaned, changed1) = SanitizeField(raw, '\uFFFF');

			// Second pass: replace any occurrence of the string delimiter.
			bool changed2 = false;
			if (cleaned.Contains(delimiter, StringComparison.Ordinal))
			{
				cleaned = cleaned.Replace(delimiter, new string(Replacement, delimiter.Length));
				changed2 = true;
			}

			return (cleaned, changed1 || changed2);
		}

		// ── Line composition ──────────────────────────────────────────────

		/// <summary>
		/// Build a single delimited line from a set of fields. Each field is
		/// sanitized; the line is composed by joining with the delimiter.
		/// </summary>
		public static string ComposeLine(char delimiter, params string?[] fields)
		{
			if (fields.Length == 0)
			{
				return string.Empty;
			}

			var sanitized = new string[fields.Length];
			for (int i = 0; i < fields.Length; i++)
			{
				sanitized[i] = SanitizeField(fields[i], delimiter).Cleaned;
			}

			return string.Join(delimiter, sanitized);
		}

		/// <summary>Compose with a string delimiter (e.g. "@@@").</summary>
		public static string ComposeLine(string delimiter, params string?[] fields)
		{
			if (fields.Length == 0)
			{
				return string.Empty;
			}

			var sanitized = new string[fields.Length];
			for (int i = 0; i < fields.Length; i++)
			{
				sanitized[i] = SanitizeField(fields[i], delimiter).Cleaned;
			}

			return string.Join(delimiter, sanitized);
		}

		// ── File operations ───────────────────────────────────────────────

		/// <summary>
		/// Append a single record to a delimited log file. Each field is
		/// sanitized before composition. Line terminator is the platform default.
		/// </summary>
		public static void Append(string path, char delimiter, params string?[] fields)
		{
			var line = ComposeLine(delimiter, fields);
			// Retry-on-lock so an operator inspecting the log (or a transient
			// copy-job handle) does not crash the run. Append semantics preserved —
			// AppendAllLinesWithRetry wraps File.AppendAllLines, not WriteAllLines.
			FileIo.AppendAllLinesWithRetry(path, [line], Path.GetFileName(path));
		}

		/// <summary>
		/// Append multiple records to a delimited log file. Each field is
		/// sanitized; one file open for the whole batch.
		/// </summary>
		public static void AppendMany(string path, char delimiter,
			IEnumerable<string?[]> records)
		{
			var lines = records.Select(r => ComposeLine(delimiter, r));
			// Retry-on-lock; append semantics preserved (File.AppendAllLines).
			FileIo.AppendAllLinesWithRetry(path, lines, Path.GetFileName(path));
		}

		/// <summary>
		/// Overwrite a delimited log file with the given records. Header line,
		/// if any, is passed as the first record (and sanitized like any other —
		/// callers should not include delimiters in header field names).
		/// </summary>
		public static void Write(string path, char delimiter,
			IEnumerable<string?[]> records)
		{
			var lines = records.Select(r => ComposeLine(delimiter, r));
			// Retry-on-lock (overwrite semantics — File.WriteAllLines).
			FileIo.WriteAllLinesWithRetry(path, lines, Path.GetFileName(path));
		}

		/// <summary>
		/// Overwrite a delimited log file with the given records, using a string
		/// delimiter (e.g. "@@@"). Used by the SEO log.
		/// </summary>
		public static void Write(string path, string delimiter,
			IEnumerable<string?[]> records)
		{
			var lines = records.Select(r => ComposeLine(delimiter, r));
			// Retry-on-lock (overwrite semantics — File.WriteAllLines).
			FileIo.WriteAllLinesWithRetry(path, lines, Path.GetFileName(path));
		}

		// ── Streaming writer ──────────────────────────────────────────────

		/// <summary>
		/// Streaming writer for cases where records are produced incrementally
		/// (e.g. spell-error log, written one page at a time during async
		/// processing). Each call to <see cref="StreamingWriter.WriteRecord"/>
		/// sanitizes the fields before composing the line.
		///
		/// Usage:
		///   using var writer = IssueLogWriter.OpenStreaming(path, '|');
		///   await writer.WriteRecordAsync(field1, field2, field3);
		///
		/// Wraps a StreamWriter; the underlying writer flushes/closes on Dispose.
		/// </summary>
		public static StreamingWriter OpenStreaming(string path, char delimiter,
			bool append = true)
		{
			return new StreamingWriter(path, delimiter, append);
		}

		/// <summary>
		/// Disposable writer for incremental record emission. See
		/// <see cref="OpenStreaming(string, char, bool)"/>.
		/// </summary>
		internal sealed class StreamingWriter : IDisposable, IAsyncDisposable
		{
			private readonly StreamWriter _inner;
			private readonly char _delimiter;

			internal StreamingWriter(string path, char delimiter, bool append)
			{
				// Use Utf8WithBom (BOM-emitting) for consistency with the rest of
				// the log corpus. Earlier this was `new UTF8Encoding(false)`,
				// producing BOM-less logs for the streaming-written
				// 11-spell-error-sources.log — inconsistent with the rest.
				_inner = new StreamWriter(path, append, Utf8WithBom);
				_delimiter = delimiter;
			}

			/// <summary>Write a record synchronously. Fields are sanitized.</summary>
			public void WriteRecord(params string?[] fields)
			{
				_inner.WriteLine(ComposeLine(_delimiter, fields));
			}

			/// <summary>Write a record asynchronously. Fields are sanitized.</summary>
			public Task WriteRecordAsync(params string?[] fields)
			{
				return _inner.WriteLineAsync(ComposeLine(_delimiter, fields));
			}

			/// <summary>Write a pre-composed line (rare — escape hatch for
			/// headers or non-record markers). The line is still sanitized
			/// for CR / LF / control chars to avoid breaking line orientation,
			/// but the delimiter is NOT substituted (the line may legitimately
			/// contain delimiters as part of its structure).</summary>
			public Task WriteRawLineAsync(string line)
			{
				// Strip newlines and control chars; preserve delimiters.
				var (cleaned, _) = SanitizeField(line, '\uFFFF');
				return _inner.WriteLineAsync(cleaned);
			}

			public void Dispose() => _inner.Dispose();
			public ValueTask DisposeAsync() => _inner.DisposeAsync();
		}
	}
}
