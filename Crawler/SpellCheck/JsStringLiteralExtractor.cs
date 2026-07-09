namespace Crawler.SpellCheck
{
	using System.Collections.Generic;
	using System.Globalization;
	using System.Text;

	/// <summary>
	/// One string literal lifted from a block of JavaScript source, carrying its DECODED text and
	/// the character span of the RAW literal (opening quote through closing quote) back into the
	/// source it came from. The raw span is the load-bearing extra over a bare string: a later
	/// stage needs the literal's origin offset to compute a usable source location (line/column
	/// relative to the &lt;script&gt; node) and a context excerpt — neither of which can be
	/// re-derived from the DOM node afterwards, because the node holds the raw, undecoded body, not
	/// the decoded string that was actually checked. <see cref="RawStart"/> is the index of the
	/// opening quote in the source passed to <see cref="JsStringLiteralExtractor.Extract"/>;
	/// <see cref="RawLength"/> covers both quotes.
	/// </summary>
	public readonly record struct ScriptStringLiteral(string Text, int RawStart, int RawLength);

	/// <summary>
	/// LAYER 1 of script spell-checking: a single-pass character scanner that lifts the string
	/// LITERALS out of a block of JavaScript source and hands back their DECODED content. It does
	/// NOT decide what is prose — that is Layer 2's job (the value classifier). Its sole contract
	/// is "give me exactly the strings a programmer wrote between quotes, decoded, with their source
	/// position, and nothing that was never a string."
	///
	/// What it recognises:
	///   * The three literal delimiters — double quote, single quote, and template backtick. These
	///     are the ONLY things that bound a literal; a hyphen, slash, or brace never does.
	///   * JavaScript escape sequences inside a literal, DECODED to their actual characters:
	///     \uXXXX, \u{...}, \xXX, the simple set (\n \t \r \b \f \v \0 \\ \' \" \` \/), line
	///     continuations, and the JS rule that an unknown escape \z is just the character z.
	///     Decoding is mandatory and unconditional: a word written "Gesch\u00E4fte" renders as
	///     "Geschäfte" on the page, so that is the form a dictionary must see. Raw-UTF-8 source
	///     passes through the decoder untouched, so decode-then-check is correct for both styles.
	///   * Template interpolations ${ ... }: the expression is CODE, not prose, so it is skipped
	///     (brace-balanced, respecting nested strings) and replaced by a single space so a word is
	///     never glued across the hole.
	///
	/// What it deliberately steps over so it is never mistaken for a literal:
	///   * Line (//) and block (/* */) comments — developer-facing, never user prose, dropped.
	///   * Regular-expression literals — a regex such as /['"]/ contains quote characters that a
	///     naive scanner would read as the start of a string. The scanner disambiguates a leading
	///     '/' as regex-vs-division by expression position (the previous significant token), the
	///     same rule a JS parser uses, and skips a regex whole (honouring \ escapes and [...] char
	///     classes) so its contents never leak.
	///
	/// What it drops even though it IS a literal:
	///   * Object-literal KEYS. In native JS an object key is a first-class string literal
	///     ({ 'entryid': 1 }), but it is an identifier-by-another-name, never prose. A literal is a
	///     key when its previous significant token is '{' or ',' AND its next significant token is
	///     ':'. Both sides are required on purpose: a ternary branch (cond ? 'yes' : 'no') has a
	///     trailing ':' but its previous token is '?', not '{' / ',', so it is correctly KEPT.
	///
	/// The scanner is intentionally tolerant of malformed input (an unterminated single/double-quote
	/// literal ends at the line break rather than swallowing the rest of the file), because the
	/// crawler routinely meets imperfect, hand-edited, or truncated markup.
	/// </summary>
	public static class JsStringLiteralExtractor
	{
		// Keywords after which a leading '/' begins a REGEX, not a division — i.e. they leave the
		// parser in expression position. An identifier that is NOT one of these is a value, so a
		// following '/' is division. This is the standard regex/division heuristic; the set is the
		// common, safe subset (widen only on observed need).
		private static readonly HashSet<string> RegexPermittingKeywords = new(System.StringComparer.Ordinal)
		{
			"return", "typeof", "instanceof", "in", "of", "do", "else", "case",
			"void", "delete", "new", "throw", "yield", "await"
		};

		/// <summary>
		/// Scan <paramref name="source"/> once and yield every string literal that is NOT an object
		/// key, decoded, with its raw source span. Empty and whitespace-only literals are still
		/// emitted (the scanner does not judge content); Layer 2 discards them. Order is source
		/// order. A null/empty input yields nothing.
		/// </summary>
		public static IEnumerable<ScriptStringLiteral> Extract(string? source)
		{
			if (string.IsNullOrEmpty(source))
			{
				yield break;
			}

			int n = source.Length;
			int i = 0;

			// prevSig: the previous SIGNIFICANT character (whitespace and comments do not count).
			// Only its identity as '{' or ',' matters, for the object-key test; for every value-like
			// token (identifier, number, string, template, regex) it is set to a neutral marker.
			char prevSig = '\0';

			// regexAllowed: true when a '/' at the current position would begin a regex (expression
			// position). Start of input is expression position.
			bool regexAllowed = true;

			while (i < n)
			{
				char c = source[i];

				// --- whitespace: no effect on prevSig / regexAllowed ---
				if (char.IsWhiteSpace(c))
				{
					i++;
					continue;
				}

				// --- comments: skipped, no effect on prevSig / regexAllowed ---
				if (c == '/' && i + 1 < n && source[i + 1] == '/')
				{
					i += 2;
					while (i < n && source[i] != '\n') i++;
					continue;
				}

				if (c == '/' && i + 1 < n && source[i + 1] == '*')
				{
					i += 2;
					while (i + 1 < n && !(source[i] == '*' && source[i + 1] == '/')) i++;
					i = (i + 1 < n) ? i + 2 : n; // step past the closing */ (or to EOF if unterminated)
					continue;
				}

				// --- regex literal (only in expression position) ---
				if (c == '/' && regexAllowed)
				{
					i = SkipRegex(source, i);
					prevSig = 'v';        // a regex is a value
					regexAllowed = false; // a following '/' is division
					continue;
				}

				// --- string / template literal ---
				if (c == '"' || c == '\'' || c == '`')
				{
					char keyPrev = prevSig;            // capture BEFORE the literal
					int rawStart = i;
					string text = ScanLiteral(source, ref i, c);
					int rawLength = i - rawStart;

					bool isObjectKey =
						(keyPrev == '{' || keyPrev == ',')
						&& NextSignificantIsColon(source, i);

					prevSig = 'v';        // a string is a value
					regexAllowed = false; // a following '/' is division

					if (!isObjectKey)
					{
						yield return new ScriptStringLiteral(text, rawStart, rawLength);
					}

					continue;
				}

				// --- identifier / keyword ---
				if (IsIdentifierStart(c))
				{
					int start = i;
					i++;
					while (i < n && IsIdentifierPart(source[i])) i++;
					string word = source.Substring(start, i - start);

					prevSig = 'v';
					regexAllowed = RegexPermittingKeywords.Contains(word);
					continue;
				}

				// --- number ---
				if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(source[i + 1])))
				{
					i++;
					while (i < n && IsNumberPart(source[i])) i++;
					prevSig = 'v';
					regexAllowed = false;
					continue;
				}

				// --- division operator '/' (not a comment, not a regex) ---
				if (c == '/')
				{
					prevSig = '/';
					regexAllowed = true; // an expression follows a division operator
					i++;
					continue;
				}

				// --- any other punctuation / operator (single char) ---
				prevSig = c;
				// A closing ), ], } leaves a value/closure (division follows); everything else
				// (openers, separators, operators) leaves expression position (regex allowed).
				regexAllowed = !(c == ')' || c == ']' || c == '}');
				i++;
			}
		}

		/// <summary>
		/// Scan a string or template literal starting at the opening quote <paramref name="quote"/>
		/// at index <paramref name="i"/>; advance <paramref name="i"/> to just past the closing quote
		/// (or to the line break / EOF for an unterminated single/double-quote literal) and return the
		/// DECODED content. Template interpolations are skipped and replaced by a single space.
		/// </summary>
		private static string ScanLiteral(string s, ref int i, char quote)
		{
			int n = s.Length;
			bool template = quote == '`';
			var sb = new StringBuilder();
			i++; // step past the opening quote

			while (i < n)
			{
				char c = s[i];

				if (c == '\\')
				{
					AppendEscape(s, ref i, sb);
					continue;
				}

				if (c == quote)
				{
					i++; // consume closing quote
					return sb.ToString();
				}

				if (template && c == '$' && i + 1 < n && s[i + 1] == '{')
				{
					i = SkipInterpolation(s, i + 2); // past "${"
					sb.Append(' ');                  // do not glue words across the hole
					continue;
				}

				if (!template && (c == '\n' || c == '\r'))
				{
					// Unterminated single/double-quote literal — end it at the line break rather
					// than consuming the rest of the document. Do not consume the newline.
					return sb.ToString();
				}

				sb.Append(c);
				i++;
			}

			return sb.ToString(); // EOF before a closing quote — return what we have
		}

		/// <summary>
		/// Decode the escape sequence beginning at the backslash at <paramref name="i"/>, append the
		/// decoded character(s) to <paramref name="sb"/>, and advance <paramref name="i"/> past the
		/// whole sequence.
		/// </summary>
		private static void AppendEscape(string s, ref int i, StringBuilder sb)
		{
			int n = s.Length;
			if (i + 1 >= n)
			{
				i++; // lone trailing backslash
				return;
			}

			char e = s[i + 1];
			switch (e)
			{
				case 'n': sb.Append('\n'); i += 2; return;
				case 't': sb.Append('\t'); i += 2; return;
				case 'r': sb.Append('\r'); i += 2; return;
				case 'b': sb.Append('\b'); i += 2; return;
				case 'f': sb.Append('\f'); i += 2; return;
				case 'v': sb.Append('\v'); i += 2; return;
				case '0': sb.Append('\0'); i += 2; return;
				case '\\': sb.Append('\\'); i += 2; return;
				case '\'': sb.Append('\''); i += 2; return;
				case '"': sb.Append('"'); i += 2; return;
				case '`': sb.Append('`'); i += 2; return;
				case '/': sb.Append('/'); i += 2; return; // not a JS escape, but tolerated → '/'

				case '\r': // line continuation \<CR>(<LF>)
					i += 2;
					if (i < n && s[i] == '\n') i++;
					return;
				case '\n': // line continuation \<LF>
					i += 2;
					return;

				case 'x':
				{
					if (i + 3 < n && TryHex(s, i + 2, 2, out int v))
					{
						sb.Append((char)v);
						i += 4;
						return;
					}
					break; // malformed → fall through to literal handling
				}

				case 'u':
				{
					if (i + 2 < n && s[i + 2] == '{')
					{
						int j = i + 3;
						int start = j;
						while (j < n && s[j] != '}') j++;
						if (j < n && j > start && TryHexSpan(s, start, j - start, out int cp) && cp <= 0x10FFFF)
						{
							sb.Append(char.ConvertFromUtf32(cp));
							i = j + 1; // past '}'
							return;
						}
						break; // malformed \u{...}
					}

					if (i + 5 < n && TryHex(s, i + 2, 4, out int u))
					{
						sb.Append((char)u);
						i += 6;
						return;
					}
					break; // malformed \uXXXX
				}
			}

			// Unknown / malformed escape: JS treats \z as the character z (drop the backslash).
			sb.Append(e);
			i += 2;
		}

		/// <summary>
		/// Skip a regex literal starting at the leading '/' at <paramref name="start"/>; return the
		/// index just past the closing '/' and any trailing flag letters. Honours backslash escapes
		/// and character classes ([...], inside which '/' does not close the regex). An unterminated
		/// regex ends at the line break.
		/// </summary>
		private static int SkipRegex(string s, int start)
		{
			int n = s.Length;
			int i = start + 1; // past leading '/'
			bool inClass = false;

			while (i < n)
			{
				char c = s[i];
				if (c == '\\') { i += 2; continue; }
				if (c == '\n') { return i; } // unterminated — stop at line break
				if (c == '[') { inClass = true; i++; continue; }
				if (c == ']') { inClass = false; i++; continue; }
				if (c == '/' && !inClass) { i++; break; } // closing slash
				i++;
			}

			while (i < n && char.IsLetter(s[i])) i++; // flags
			return i;
		}

		/// <summary>
		/// Skip a template interpolation body. <paramref name="i"/> is the index just past "${";
		/// return the index just past the matching '}'. Brace-balanced, and steps over nested string
		/// literals so a brace inside a string does not miscount.
		/// </summary>
		private static int SkipInterpolation(string s, int i)
		{
			int n = s.Length;
			int depth = 1;

			while (i < n && depth > 0)
			{
				char c = s[i];
				if (c == '\\') { i += 2; continue; }
				if (c == '"' || c == '\'' || c == '`')
				{
					i = SkipNestedString(s, i, c);
					continue;
				}
				if (c == '{') { depth++; i++; continue; }
				if (c == '}') { depth--; i++; continue; }
				i++;
			}

			return i;
		}

		/// <summary>Skip a nested string literal (inside an interpolation) to just past its close.</summary>
		private static int SkipNestedString(string s, int i, char quote)
		{
			int n = s.Length;
			i++; // opening quote
			while (i < n)
			{
				char c = s[i];
				if (c == '\\') { i += 2; continue; }
				if (c == quote) { return i + 1; }
				if (quote != '`' && (c == '\n' || c == '\r')) { return i; } // tolerate unterminated
				i++;
			}
			return i;
		}

		/// <summary>
		/// True if, skipping whitespace and comments forward from <paramref name="i"/>, the next
		/// significant character is a colon. Used to confirm a literal sits in object-key position.
		/// </summary>
		private static bool NextSignificantIsColon(string s, int i)
		{
			int n = s.Length;
			while (i < n)
			{
				char c = s[i];
				if (char.IsWhiteSpace(c)) { i++; continue; }
				if (c == '/' && i + 1 < n && s[i + 1] == '/')
				{
					i += 2;
					while (i < n && s[i] != '\n') i++;
					continue;
				}
				if (c == '/' && i + 1 < n && s[i + 1] == '*')
				{
					i += 2;
					while (i + 1 < n && !(s[i] == '*' && s[i + 1] == '/')) i++;
					i = (i + 1 < n) ? i + 2 : n;
					continue;
				}
				return c == ':';
			}
			return false;
		}

		private static bool IsIdentifierStart(char c)
			=> char.IsLetter(c) || c == '_' || c == '$';

		private static bool IsIdentifierPart(char c)
			=> char.IsLetterOrDigit(c) || c == '_' || c == '$';

		// Permissive numeric continuation: covers decimals, exponents, hex/octal/binary prefixes and
		// BigInt 'n'. The exact numeric grammar does not matter — numbers are never emitted; this
		// only needs to consume the run so the characters are not re-scanned as something else.
		private static bool IsNumberPart(char c)
			=> char.IsLetterOrDigit(c) || c == '.' || c == '_';

		private static bool TryHex(string s, int start, int count, out int value)
			=> TryHexSpan(s, start, count, out value);

		private static bool TryHexSpan(string s, int start, int count, out int value)
		{
			value = 0;
			if (start + count > s.Length || count <= 0)
			{
				return false;
			}

			return int.TryParse(
				s.AsSpan(start, count),
				NumberStyles.HexNumber,
				CultureInfo.InvariantCulture,
				out value);
		}
	}
}
