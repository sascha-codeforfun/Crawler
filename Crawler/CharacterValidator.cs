namespace Crawler
{
	/// <summary>
	/// Scans strings for invisible, lookalike, or non-printable Unicode characters
	/// that break string matching at runtime and can act as a silent spell-check bypass.
	/// Common sources: copy-paste from web pages, Word documents, or PDF text.
	///
	/// Two severity levels:
	///   Halt   — SpellCheckWordPrefixesToStrip and dictionary files.
	///            A contaminated entry silently disables spell-check coverage for
	///            specific words, making this a data integrity attack vector.
	///            The application exits after printing a red console block.
	///   Warn   — Other config lists where wrong behaviour is visible but not exploitable.
	///            Logged as [WARNING] and execution continues.
	/// </summary>
	public static class CharacterValidator
	{
		private static readonly HashSet<char> AllowedNonAscii =
		[
			// German
			'ä', 'ö', 'ü', 'ß', 'Ä', 'Ö', 'Ü',
			// Common Western European accented letters
			'à','á','â','ã','å','æ','ç','è','é','ê','ë',
			'ì','í','î','ï','ð','ñ','ò','ó','ô','õ','ø',
			'ù','ú','û','ý','þ','ÿ',
			'À','Á','Â','Ã','Å','Æ','Ç','È','É','Ê','Ë',
			'Ì','Í','Î','Ï','Ð','Ñ','Ò','Ó','Ô','Õ','Ø',
			'Ù','Ú','Û','Ý','Þ',
		];

		private static readonly Dictionary<char, string> KnownBadChars = new()
		{
			{ '\u00AD', "SOFT HYPHEN" },
			{ '\u2010', "HYPHEN" },
			{ '\u2011', "NON-BREAKING HYPHEN" },
			{ '\u2012', "FIGURE DASH" },
			{ '\u2013', "EN DASH" },
			{ '\u2014', "EM DASH" },
			{ '\u2015', "HORIZONTAL BAR" },
			{ '\u2212', "MINUS SIGN" },
			{ '\uFE58', "SMALL EM DASH" },
			{ '\uFE63', "SMALL HYPHEN-MINUS" },
			{ '\uFF0D', "FULLWIDTH HYPHEN-MINUS" },
			{ '\u00A0', "NO-BREAK SPACE" },
			{ '\u200B', "ZERO WIDTH SPACE" },
			{ '\u200C', "ZERO WIDTH NON-JOINER" },
			{ '\u200D', "ZERO WIDTH JOINER" },
			{ '\uFEFF', "ZERO WIDTH NO-BREAK SPACE (BOM)" },
			{ '\u2018', "LEFT SINGLE QUOTATION MARK" },
			{ '\u2019', "RIGHT SINGLE QUOTATION MARK" },
			{ '\u201C', "LEFT DOUBLE QUOTATION MARK" },
			{ '\u201D', "RIGHT DOUBLE QUOTATION MARK" },
		};

		private static readonly HashSet<char> DashLookalikes =
		[
			'\u00AD', '\u2010', '\u2011', '\u2012', '\u2013', '\u2014',
			'\u2015', '\u2212', '\uFE58', '\uFE63', '\uFF0D',
		];

		public record SuspiciousChar(
			string Source,
			int Position,
			char Character,
			string CharName,
			string Suggestion);

		/// <summary>
		/// Scans a string value and returns any suspicious characters found.
		/// </summary>
		public static IEnumerable<SuspiciousChar> Scan(string source, string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				yield break;
			}

			for (int i = 0; i < value.Length; i++)
			{
				var c = value[i];

				if (c >= 32 && c <= 126)
				{
					continue; // printable ASCII
				}

				if (AllowedNonAscii.Contains(c))
				{
					continue;
				}

				var name = KnownBadChars.TryGetValue(c, out var known)
					? known
					: $"U+{(int)c:X4}";

				var suggestion = DashLookalikes.Contains(c)
					? "Replace with a plain ASCII hyphen-minus (-)"
					: "Remove this character";

				yield return new SuspiciousChar(source, i, c, name, suggestion);
			}
		}

		/// <summary>
		/// Scans a list of strings and logs a [WARNING] per hit. Execution continues.
		/// Use for config lists where wrong behaviour is visible but not a security vector.
		/// </summary>
		public static void ValidateListWarn(string configKeyName, IEnumerable<string> values)
		{
			int index = 0;
			foreach (var value in values)
			{
				foreach (var hit in Scan($"{configKeyName}[{index}] \"{value}\"", value))
				{
					Warn(hit);
				}

				index++;
			}
		}

		/// <summary>
		/// Scans a list of strings and throws if any suspicious character is found.
		/// Use for SpellCheckWordPrefixesToStrip where contamination silently disables
		/// spell-check coverage — a potential data integrity attack vector.
		/// Prints a red console block before throwing (skipped in --silent mode).
		/// </summary>
		public static void ValidateListHalt(string configKeyName, IEnumerable<string> values, bool silent)
		{
			List<SuspiciousChar> hits = [];
			int index = 0;
			foreach (var value in values)
			{
				foreach (var hit in Scan($"{configKeyName}[{index}] \"{value}\"", value))
				{
					hits.Add(hit);
				}

				index++;
			}

			if (hits.Count == 0)
			{
				return;
			}

			foreach (var hit in hits)
			{
				Logger.LogError(FormatHit(hit));
			}

			PrintHaltBlock(hits, silent);

			throw new InvalidOperationException(
				$"Halting: {hits.Count} suspicious character(s) found in {configKeyName}. " +
				"See application.log for details.");
		}

		/// <summary>
		/// Scans a dictionary file and throws if any suspicious character is found.
		/// Prints a red console block before throwing (skipped in --silent mode).
		/// </summary>
		public static void ValidateDictionaryFileHalt(string filePath, bool silent)
		{
			if (!File.Exists(filePath))
			{
				return;
			}

			List<SuspiciousChar> hits = [];
			int lineNumber = 0;

			foreach (var raw in File.ReadLines(filePath, System.Text.Encoding.UTF8))
			{
				lineNumber++;
				var line = raw.Trim();
				if (string.IsNullOrEmpty(line) || line.Contains('/'))
				{
					continue;
				}

				// Strip the pin marker before validation — ! is a valid intentional prefix.
				var scanLine = line.TrimStart('!');
				var source = $"{Path.GetFileName(filePath)} line {lineNumber} \"{line}\"";
				foreach (var hit in Scan(source, scanLine))
				{
					hits.Add(hit);
				}
			}

			if (hits.Count == 0)
			{
				return;
			}

			foreach (var hit in hits)
			{
				Logger.LogError(FormatHit(hit));
			}

			PrintHaltBlock(hits, silent);

			throw new InvalidOperationException(
				$"Halting: {hits.Count} suspicious character(s) found in {Path.GetFileName(filePath)}. " +
				"See application.log for details.");
		}

		// ── Foreign-language dictionary: a RELAXED policy ─────────────────────
		// user_foreign_languages.dic holds researched, comment-justified words from any
		// script (čovek, öğe, izgūšana, преоразмерявате). The relaxation vs Scan(): allow
		// any Unicode LETTER, not just the Western allow-list — so foreign letters pass,
		// but the genuinely awkward characters (invisibles, dash/quote look-alikes, NBSP,
		// symbols) are NOT letters and still halt. Same data-integrity protection, minus
		// the Latin-only restriction. Lines may carry a // comment (whole-line or trailing)
		// and a leading ! pin (allowed for UI consistency with the user/site dicts); both
		// are stripped before the bare word is scanned.

		// Bare word from a foreign-dictionary line: drop a // comment (whole-line or
		// trailing), then trim and strip a leading ! pin. Empty = pure comment / blank.
		public static string ForeignDictionaryWord(string rawLine)
		{
			if (string.IsNullOrEmpty(rawLine))
			{
				return string.Empty;
			}

			var commentAt = rawLine.IndexOf("//", StringComparison.Ordinal);
			var beforeComment = commentAt >= 0 ? rawLine.Substring(0, commentAt) : rawLine;
			return beforeComment.Trim().TrimStart('!');
		}

		// Relaxed scan: like Scan() but any Unicode letter is allowed (not just the
		// Western allow-list). Everything that is neither printable ASCII nor a letter
		// is flagged — invisibles, dash/quote look-alikes, NBSP, symbols.
		public static IEnumerable<SuspiciousChar> ScanForeign(string source, string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				yield break;
			}

			for (int i = 0; i < value.Length; i++)
			{
				var c = value[i];

				if (c >= 32 && c <= 126)
				{
					continue; // printable ASCII
				}

				if (char.IsLetter(c))
				{
					continue; // any-script letter — the relaxation
				}

				var name = KnownBadChars.TryGetValue(c, out var known)
					? known
					: $"U+{(int)c:X4}";

				var suggestion = DashLookalikes.Contains(c)
					? "Replace with a plain ASCII hyphen-minus (-)"
					: "Remove this character";

				yield return new SuspiciousChar(source, i, c, name, suggestion);
			}
		}

		// Validates user_foreign_languages.dic with the relaxed policy, halting on any
		// suspicious character. Strips the // comment and ! pin before scanning the word,
		// and skips affix-flag lines (containing /) and pure comments. Prints a red block
		// before throwing (skipped in --silent mode).
		public static void ValidateForeignDictionaryFileHalt(string filePath, bool silent)
		{
			if (!File.Exists(filePath))
			{
				return;
			}

			List<SuspiciousChar> hits = [];
			int lineNumber = 0;

			foreach (var raw in File.ReadLines(filePath, System.Text.Encoding.UTF8))
			{
				lineNumber++;
				var word = ForeignDictionaryWord(raw);
				if (string.IsNullOrEmpty(word) || word.Contains('/'))
				{
					continue;
				}

				var source = $"{Path.GetFileName(filePath)} line {lineNumber} \"{word}\"";
				foreach (var hit in ScanForeign(source, word))
				{
					hits.Add(hit);
				}
			}

			if (hits.Count == 0)
			{
				return;
			}

			foreach (var hit in hits)
			{
				Logger.LogError(FormatHit(hit));
			}

			PrintHaltBlock(hits, silent);

			throw new InvalidOperationException(
				$"Halting: {hits.Count} suspicious character(s) found in {Path.GetFileName(filePath)}. " +
				"See application.log for details.");
		}

		// The first character of `word` that the STRICT policy (Scan) would reject, or
		// null if clean. Used by triage to refuse an add to the user/site dictionary
		// BEFORE writing — so a foreign-script word can't silently contaminate the file
		// and halt the next load. Same Scan() the dictionary files are validated with, so
		// the triage gate and the file gate can never drift.
		public static SuspiciousChar? FirstStrictViolation(string word) =>
			Scan(string.Empty, word).FirstOrDefault();

		// ── Invisible-only scan (script-agnostic) ─────────────────────────────
		// A DIFFERENT policy from Scan() above. Scan() allows only a Latin
		// allow-list and flags everything else — correct for spell-check prefix
		// lists and dictionary entries, but wrong for free-text config that may
		// legitimately hold any script (a Cyrillic or CJK company name in an SEO
		// title template, e.g. "Правда" or "北京公司"). Here the aim is narrower
		// and universal: forbid only what a human CANNOT SEE — zero-width chars,
		// the BOM, bidi controls, non-standard/!no-break spaces, line/paragraph
		// separators, and control characters — because those silently break the
		// strict string matching the template relies on. Every VISIBLE codepoint,
		// in any script, is allowed. Category-based (not a hand-kept codepoint
		// list) so it stays correct as scripts are added.
		public static IEnumerable<SuspiciousChar> ScanInvisibleOnly(string source, string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				yield break;
			}

			for (int i = 0; i < value.Length; i++)
			{
				var c = value[i];
				if (c == ' ')
				{
					continue;                    // ordinary space is fine
				}

				if (c >= 32 && c <= 126)
				{
					continue;         // printable ASCII
				}

				var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
				bool invisible =
					char.IsControl(c)                                                  // C0/C1 controls
					|| cat == System.Globalization.UnicodeCategory.Format              // zero-width, BOM, bidi controls (Cf)
					|| cat == System.Globalization.UnicodeCategory.LineSeparator       // U+2028
					|| cat == System.Globalization.UnicodeCategory.ParagraphSeparator  // U+2029
					|| cat == System.Globalization.UnicodeCategory.SpaceSeparator;     // NBSP and other non-ASCII spaces

				if (!invisible)
				{
					continue;                  // visible char, any script → allowed
				}

				var name = KnownBadChars.TryGetValue(c, out var known)
					? known
					: $"U+{(int)c:X4}";

				yield return new SuspiciousChar(source, i, c, name, "Remove this invisible character");
			}
		}

		/// <summary>
		/// Validates a single free-text config value, halting if it contains any
		/// invisible/control character (see <see cref="ScanInvisibleOnly"/>). Unlike
		/// <see cref="ValidateListHalt"/> this permits all visible scripts — use it
		/// for config where a non-Latin value is legitimate (e.g. an SEO title
		/// template carrying a company name in any language) but invisible
		/// contamination would silently break exact string matching.
		/// </summary>
		public static void ValidateInvisibleHalt(string configKeyName, string value, bool silent)
		{
			var hits = ScanInvisibleOnly($"{configKeyName} \"{value}\"", value).ToList();
			if (hits.Count == 0)
			{
				return;
			}

			foreach (var hit in hits)
			{
				Logger.LogError(FormatHit(hit));
			}

			PrintHaltBlock(hits, silent);

			throw new InvalidOperationException(
				$"Halting: {hits.Count} invisible character(s) found in {configKeyName}. " +
				"See application.log for details.");
		}

		private static void Warn(SuspiciousChar hit) =>
			Logger.LogWarning(FormatHit(hit));

		private static string FormatHit(SuspiciousChar hit) =>
			$"Suspicious character in {hit.Source}: " +
			$"position {hit.Position}, U+{(int)hit.Character:X4} {hit.CharName}. " +
			$"{hit.Suggestion}.";

		private static void PrintHaltBlock(List<SuspiciousChar> hits, bool silent)
		{
			if (silent)
			{
				return;
			}

			ConsoleUi.WriteBlank();
			ConsoleUi.WriteErrorBlock(
				"=== SUSPICIOUS CHARACTER(S) DETECTED — HALTING ===",
				hits.Select(FormatHit));
			ConsoleUi.WriteBlank();
			ConsoleUi.WriteError("These characters break string matching and can act as a");
			ConsoleUi.WriteError("silent spell-check bypass. Fix the file and restart.");
			ConsoleUi.WriteError("===================================================");
		}
	}
}
