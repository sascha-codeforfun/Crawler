namespace Crawler
{
	// ── ConsoleUi ─────────────────────────────────────────────────────────────
	//
	// Centralised console UI primitives used by all interactive triage flows.
	// All output uses a consistent 2-space indent ("  ") matching the existing
	// triage style. Colour constants match the palette already in use:
	//
	//   DarkCyan   — section separators and headers
	//   Cyan       — info / count summaries
	//   Green      — positive outcomes (kept, added)
	//   DarkYellow — warnings and notes
	//   Yellow     — action required
	//   White      — emphasis within a block
	//   DarkGray   — neutral / skipped outcomes
	//   Red        — errors and obsolete items
	//
	// All methods are safe to call from non-interactive code — they write to
	// Console directly without checking _silent. Callers are responsible for
	// guarding with CrawlerContext.Silent where appropriate.
	// ─────────────────────────────────────────────────────────────────────────

	internal static class ConsoleUi
	{
		// ── Layout constants ──────────────────────────────────────────────────

		internal const string Separator = "════════════════════════════════════════════════════════════════════════════════";
		internal const string Divider = "────────────────────────────────────────────────────────────────────────────────";
		internal const string Indent = "  ";

		// Timestamp of the last phase header / step row, used to time each step as the
		// gap to the previous row (rows print at the end of their step). A phase header
		// resets it; MarkStep resets it for rows emitted outside a header (e.g. cache load).
		private static long _lastStepTicks;

		/// <summary>Resets the step clock — call right before a step whose row is not
		/// preceded by a phase header, so its timing reflects only that step.</summary>
		internal static void MarkStep() => _lastStepTicks = System.Diagnostics.Stopwatch.GetTimestamp();

		// ── Section headers ───────────────────────────────────────────────────

		/// <summary>Full-width DarkCyan separator with optional title line below.</summary>
		internal static void WriteHeader(string? title = null)
		{
			Console.WriteLine();
			Console.ForegroundColor = ConsoleColor.DarkCyan;
			Console.WriteLine(Separator);
			Console.ResetColor();
			if (!string.IsNullOrEmpty(title))
			{
				Console.WriteLine($"{Indent}{title}");
			}
			_lastStepTicks = System.Diagnostics.Stopwatch.GetTimestamp();
		}

		/// <summary>Full-width DarkCyan separator — closing a section.</summary>
		internal static void WriteFooter()
		{
			Console.ForegroundColor = ConsoleColor.DarkCyan;
			Console.WriteLine(Separator);
			Console.ResetColor();
		}

		/// <summary>Narrower divider line within a section.</summary>
		internal static void WriteDivider()
		{
			Console.ForegroundColor = ConsoleColor.DarkCyan;
			Console.WriteLine(Divider);
			Console.ResetColor();
		}

		// ── Config-check halt (651) ───────────────────────────────────────────
		// The tone for an operator halt that is theirs to resolve. Renders in the SAME restrained
		// label·detail language as the configuration screen — a calm cyan banner, then Problem/Why/Fix
		// blocks — rather than a wall of red. Colour carries STATE, not volume: cyan section labels,
		// default-white prose, dim-grey data (paths, checksums, paste lines kept un-wrapped so they
		// stay copy-paste-able), and amber reserved for the few genuine attention tokens. No full-block
		// red anywhere. Silent-aware. The full plain detail belongs in the log (the caller logs it);
		// this is the human-facing screen.

		/// <summary>Colour intent of a single config-check line.</summary>
		internal enum CheckTone
		{
			/// <summary>Explanatory prose — default white, word-wrapped to the value column.</summary>
			Prose,
			/// <summary>A value the operator reads (path, on-disk checksum) — dim, neutral, never wrapped.
			/// Presented, not emphasised.</summary>
			Data,
			/// <summary>Setup-incomplete, benign — a missing DisplayName, a checksum not set yet. Amber.</summary>
			Accent,
			/// <summary>A genuine error — a missing file, a checksum that is present but wrong. Red.</summary>
			Error,
		}

		/// <summary>
		/// One config-check line. <paramref name="SubLabel"/>, when set, is a dim sub-label printed to the
		/// left of the value (e.g. "configured", "file on disk") so a finding reads label·value with the
		/// value carrying its own tone colour. Prose lines leave it empty.
		/// </summary>
		internal readonly record struct CheckLine(CheckTone Tone, string Text, string SubLabel = "");

		/// <summary>
		/// A labelled block. By default the cyan label sits on the first line with detail to its right
		/// (Problem/Why/Fix). When <paramref name="HeadingOnOwnLine"/> is set, the label is a standalone
		/// cyan heading (a bundle name) and the lines are its indented, sub-labelled findings.
		/// </summary>
		internal sealed record CheckBlock(string Label, IReadOnlyList<CheckLine> Lines, bool HeadingOnOwnLine = false);

		private static ConsoleColor ToneColor(CheckTone tone) => tone switch
		{
			CheckTone.Error => ConsoleColor.Red,
			CheckTone.Accent => ConsoleColor.DarkYellow,
			CheckTone.Data => ConsoleColor.DarkGray,
			_ => ConsoleColor.White,
		};

		/// <summary>
		/// Renders a calm "CONFIG CHECK" halt screen. Reuses the cyan banner/footer the configuration
		/// screen already speaks. Problem/Why/Fix are label·detail blocks; bundle findings are a cyan
		/// heading with sub-labelled value lines below. Colour carries STATE, not volume: prose white,
		/// neutral data dim, setup-incomplete amber, genuine errors red. No full-block red.
		/// </summary>
		internal static void WriteConfigCheck(string subtitle, IReadOnlyList<CheckBlock> blocks)
		{
			if (CrawlerContext.Silent)
			{
				return;
			}

			WriteHeader($"CONFIG CHECK · {subtitle}");
			Console.WriteLine();

			const int LabelWidth = 10;   // Problem/Why/Fix left column
			const int SubLabelWidth = 13; // configured / file on disk / path / DicFile …
			int valueColumn = Indent.Length + LabelWidth + 1;

			int windowWidth;
			try { windowWidth = Console.WindowWidth; }
			catch { windowWidth = 0; }
			if (windowWidth <= 0) { windowWidth = 120; }
			int proseWidth = System.Math.Max(24, windowWidth - valueColumn - 1);

			foreach (var block in blocks)
			{
				if (block.HeadingOnOwnLine)
				{
					// A bundle: cyan heading on its own line, findings sub-labelled beneath it.
					Console.ForegroundColor = ConsoleColor.Cyan;
					Console.WriteLine($"{Indent}{block.Label}");
					Console.ResetColor();

					foreach (var line in block.Lines)
					{
						// Sub-label dim; value in its tone colour; values never wrapped (copy-paste safe).
						Console.ForegroundColor = ConsoleColor.DarkGray;
						Console.Write($"{Indent}    {line.SubLabel.PadRight(SubLabelWidth)} ");
						Console.ForegroundColor = ToneColor(line.Tone);
						Console.WriteLine(line.Text);
						Console.ResetColor();
					}

					Console.WriteLine();
					continue;
				}

				// Problem/Why/Fix: cyan label, detail to the right. Prose wraps; Data stays whole.
				bool labelPending = true;
				foreach (var line in block.Lines)
				{
					if (line.Tone == CheckTone.Data)
					{
						WriteConfigCheckRow(labelPending ? block.Label : "", line.Text, ConsoleColor.DarkGray, LabelWidth);
						labelPending = false;
						continue;
					}

					ConsoleColor color = ToneColor(line.Tone);
					var wrapped = WrapWords(line.Text, proseWidth);
					for (int i = 0; i < wrapped.Count; i++)
					{
						WriteConfigCheckRow(labelPending && i == 0 ? block.Label : "", wrapped[i], color, LabelWidth);
					}
					labelPending = false;
				}

				Console.WriteLine();
			}

			WriteFooter();
		}

		// One label·detail row: cyan label padded to the value column, detail in the given colour.
		private static void WriteConfigCheckRow(string label, string detail, ConsoleColor detailColor, int labelWidth)
		{
			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.Write($"{Indent}{label.PadRight(labelWidth)} ");
			Console.ForegroundColor = detailColor;
			Console.WriteLine(detail);
			Console.ResetColor();
		}

		// Word-wraps prose on spaces to no wider than width. Unlike WrapDetail (which breaks only on
		// " · " for config rows) this breaks on whitespace, so halt prose reads as paragraphs.
		private static List<string> WrapWords(string text, int width)
		{
			var lines = new List<string>();
			if (string.IsNullOrEmpty(text)) { lines.Add(""); return lines; }

			var current = new System.Text.StringBuilder();
			foreach (var word in text.Split(' '))
			{
				if (current.Length == 0)
				{
					current.Append(word);
				}
				else if (current.Length + 1 + word.Length <= width)
				{
					current.Append(' ').Append(word);
				}
				else
				{
					lines.Add(current.ToString());
					current.Clear().Append(word);
				}
			}
			if (current.Length > 0) { lines.Add(current.ToString()); }
			return lines;
		}

		/// <summary>
		/// One aligned summary row for an analysis step: "  ▸ &lt;label&gt;   &lt;detail&gt;".
		/// The label is padded to a fixed column and coloured cyan; the detail (a count
		/// or short status) is emphasised. Dimmed rows (skipped/disabled steps) are
		/// rendered in dark grey. No-ops in silent/replay runs so non-interactive runs
		/// stay quiet. Pairs with <see cref="WriteHeader"/> for the phase banner.
		/// </summary>
		internal static void WriteStepRow(string label, string detail, bool dimmed = false, ConsoleColor? accent = null)
		{
			if (CrawlerContext.Silent)
			{
				return;
			}

			// Time this step as the gap since the previous row / phase header.
			string timing = "";
			if (_lastStepTicks != 0)
			{
				var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_lastStepTicks);
				if (elapsed.TotalSeconds >= 0.5)
				{
					timing = $" ({elapsed.TotalSeconds:0.0}s)";
				}
			}
			_lastStepTicks = System.Diagnostics.Stopwatch.GetTimestamp();

			const int LabelWidth = 27;
			int valueColumn = Indent.Length + 2 + LabelWidth; // Indent + "▸ " + padded label

			ConsoleColor labelColor = dimmed ? ConsoleColor.DarkGray : ConsoleColor.Cyan;
			ConsoleColor detailColor = accent ?? (dimmed ? ConsoleColor.DarkGray : ConsoleColor.White);

			// Wrap the detail to the console width on " · " boundaries, hanging-indented to the value
			// column, so a long row keeps the two-column look instead of spilling back to column 0.
			int windowWidth;
			try
			{
				windowWidth = Console.WindowWidth;
			}
			catch
			{
				windowWidth = 0; // output redirected — no console width available
			}

			if (windowWidth <= 0)
			{
				windowWidth = 120;
			}

			var detailLines = WrapDetail(detail, windowWidth - valueColumn - 1);

			Console.ForegroundColor = labelColor;
			Console.Write($"{Indent}▸ {label.PadRight(LabelWidth)}");
			Console.ForegroundColor = detailColor;
			Console.Write(detailLines[0]);

			for (int i = 1; i < detailLines.Count; i++)
			{
				Console.WriteLine();
				Console.ForegroundColor = detailColor;
				Console.Write(new string(' ', valueColumn) + detailLines[i]);
			}

			if (timing.Length > 0)
			{
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write(timing);
			}

			Console.WriteLine();
			Console.ResetColor();
		}

		// Wraps a step-row detail onto multiple lines no wider than availableWidth, breaking only at
		// " · " separators so grouped values stay intact; a continued line ends with a " ·" cue. A single
		// segment longer than the width is left whole (e.g. a path) rather than hard-split mid-token.
		internal static List<string> WrapDetail(string detail, int availableWidth)
		{
			if (availableWidth < 8)
			{
				availableWidth = 8; // sane floor for absurdly narrow windows
			}

			var lines = new List<string>();
			if (detail.Length <= availableWidth)
			{
				lines.Add(detail);
				return lines;
			}

			const string sep = " · ";
			const string trail = " ·";
			var chunks = detail.Split(sep);
			var current = new System.Text.StringBuilder();

			for (int i = 0; i < chunks.Length; i++)
			{
				string chunk = chunks[i];
				bool moreAfter = i < chunks.Length - 1;
				int projected = current.Length
					+ (current.Length == 0 ? 0 : sep.Length)
					+ chunk.Length
					+ (moreAfter ? trail.Length : 0);

				if (current.Length > 0 && projected > availableWidth)
				{
					lines.Add(current.ToString() + trail);
					current.Clear();
					current.Append(chunk);
				}
				else
				{
					if (current.Length > 0)
					{
						current.Append(sep);
					}

					current.Append(chunk);
				}
			}

			if (current.Length > 0)
			{
				lines.Add(current.ToString());
			}

			return lines;
		}

		// Like WriteStepRow but for a "count + long list" row: the summary is the headline on line 1, and
		// the list becomes its own hanging-indented block on line 2+ (wrapped via WrapDetail). Reads as a
		// small hierarchy ("16 configured" / the names) instead of one long wrapped run.
		internal static void WriteStepRowWithList(string label, string summary, string listText, bool dimmed = false, ConsoleColor? accent = null)
		{
			if (CrawlerContext.Silent)
			{
				return;
			}

			string timing = "";
			if (_lastStepTicks != 0)
			{
				var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_lastStepTicks);
				if (elapsed.TotalSeconds >= 0.5)
				{
					timing = $" ({elapsed.TotalSeconds:0.0}s)";
				}
			}
			_lastStepTicks = System.Diagnostics.Stopwatch.GetTimestamp();

			const int LabelWidth = 27;
			int valueColumn = Indent.Length + 2 + LabelWidth;
			ConsoleColor labelColor = dimmed ? ConsoleColor.DarkGray : ConsoleColor.Cyan;
			ConsoleColor detailColor = accent ?? (dimmed ? ConsoleColor.DarkGray : ConsoleColor.White);

			int windowWidth;
			try
			{
				windowWidth = Console.WindowWidth;
			}
			catch
			{
				windowWidth = 0; // output redirected — no console width available
			}

			if (windowWidth <= 0)
			{
				windowWidth = 120;
			}

			// Line 1 — label + summary headline (timing rides here, as on a normal row).
			Console.ForegroundColor = labelColor;
			Console.Write($"{Indent}▸ {label.PadRight(LabelWidth)}");
			Console.ForegroundColor = detailColor;
			Console.Write(summary);
			if (timing.Length > 0)
			{
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write(timing);
			}

			Console.WriteLine();

			// Line 2+ — the list as a hanging-indented block under the summary, wrapped to the window.
			string indent = new string(' ', valueColumn);
			foreach (var line in WrapDetail(listText, windowWidth - valueColumn - 1))
			{
				Console.ForegroundColor = detailColor;
				Console.WriteLine(indent + line);
			}

			Console.ResetColor();
		}

		// Like WriteStepRow but the detail is a sequence of independently-coloured segments joined by
		// " · " — for a row whose parts have their own state colour (e.g. a traffic-light per lever).
		// Label is cyan; each segment is painted its own colour; separators are DarkGray. Wraps across
		// whole segments (never splits one) to the value column, like the other multi-line rows.
		internal static void WriteStepRowSegments(string label, IReadOnlyList<(string Text, ConsoleColor Color)> segments)
		{
			if (CrawlerContext.Silent)
			{
				return;
			}

			string timing = "";
			if (_lastStepTicks != 0)
			{
				var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_lastStepTicks);
				if (elapsed.TotalSeconds >= 0.5)
				{
					timing = $" ({elapsed.TotalSeconds:0.0}s)";
				}
			}
			_lastStepTicks = System.Diagnostics.Stopwatch.GetTimestamp();

			const int LabelWidth = 27;
			int valueColumn = Indent.Length + 2 + LabelWidth;

			int windowWidth;
			try
			{
				windowWidth = Console.WindowWidth;
			}
			catch
			{
				windowWidth = 0;
			}

			if (windowWidth <= 0)
			{
				windowWidth = 120;
			}

			var lineCounts = WrapSegments(segments.Select(s => s.Text).ToList(), windowWidth - valueColumn - 1);

			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.Write($"{Indent}▸ {label.PadRight(LabelWidth)}");

			string contIndent = new string(' ', valueColumn);
			int idx = 0;
			for (int line = 0; line < lineCounts.Count; line++)
			{
				if (line > 0)
				{
					Console.WriteLine();
					Console.Write(contIndent);
				}

				for (int j = 0; j < lineCounts[line]; j++, idx++)
				{
					if (j > 0)
					{
						Console.ForegroundColor = ConsoleColor.DarkGray;
						Console.Write(" · ");
					}

					Console.ForegroundColor = segments[idx].Color;
					Console.Write(segments[idx].Text);
				}
			}

			if (timing.Length > 0)
			{
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write(timing);
			}

			Console.WriteLine();
			Console.ResetColor();
		}

		// Packs segments onto lines no wider than availableWidth, breaking only between whole segments
		// (each joined by " · "). Returns the number of segments per line (sum == segments.Count). Pure.
		internal static List<int> WrapSegments(IReadOnlyList<string> segments, int availableWidth)
		{
			var counts = new List<int>();
			if (segments.Count == 0)
			{
				return counts;
			}

			if (availableWidth < 8)
			{
				availableWidth = 8; // sane floor for absurdly narrow windows
			}

			const int sepLen = 3; // " · "
			int lineWidth = 0;
			int lineCount = 0;

			foreach (var seg in segments)
			{
				int add = (lineCount == 0 ? 0 : sepLen) + seg.Length;
				if (lineCount > 0 && lineWidth + add > availableWidth)
				{
					counts.Add(lineCount);
					lineWidth = seg.Length;
					lineCount = 1;
				}
				else
				{
					lineWidth += add;
					lineCount++;
				}
			}

			if (lineCount > 0)
			{
				counts.Add(lineCount);
			}

			return counts;
		}

		// ── Coloured output ───────────────────────────────────────────────────

		/// <summary>Cyan info line — counts, summaries.</summary>
		internal static void WriteInfo(string message)
		{
			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.WriteLine($"{Indent}{message}");
			Console.ResetColor();
		}

		/// <summary>Green success line — positive outcome.</summary>
		internal static void WriteSuccess(string message)
		{
			Console.ForegroundColor = ConsoleColor.Green;
			Console.WriteLine($"{Indent}{message}");
			Console.ResetColor();
		}

		/// <summary>DarkYellow warning or note.</summary>
		internal static void WriteWarning(string message)
		{
			Console.ForegroundColor = ConsoleColor.DarkYellow;
			Console.WriteLine($"{Indent}{message}");
			Console.ResetColor();
		}

		/// <summary>Yellow action-required line.</summary>
		internal static void WriteActionRequired(string message)
		{
			Console.ForegroundColor = ConsoleColor.Yellow;
			Console.WriteLine($"{Indent}{message}");
			Console.ResetColor();
		}

		/// <summary>Red error or obsolete item.</summary>
		internal static void WriteError(string message)
		{
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine($"{Indent}{message}");
			Console.ResetColor();
		}

		/// <summary>DarkGray neutral / skipped outcome.</summary>
		internal static void WriteSkipped(string message)
		{
			Console.ForegroundColor = ConsoleColor.DarkGray;
			Console.WriteLine($"{Indent}{message}");
			Console.ResetColor();
		}

		/// <summary>White emphasis within a block.</summary>
		internal static void WriteEmphasis(string message)
		{
			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine($"{Indent}{message}");
			Console.ResetColor();
		}

		/// <summary>Plain uncoloured line with indent.</summary>
		internal static void WriteLine(string message)
			=> Console.WriteLine($"{Indent}{message}");

		/// <summary>Plain uncoloured line without indent — for multi-line blocks.</summary>
		internal static void WriteRaw(string message)
			=> Console.WriteLine(message);

		/// <summary>
		/// Writes text with no trailing newline and no colour — for emitting a
		/// label/prefix immediately before a coloured highlight call on the same
		/// line (e.g. WriteInline(prefix) followed by WriteWith*Highlight(value)).
		/// Keeps all raw Console access inside ConsoleUi so triage flows stay
		/// presentation-agnostic.
		/// </summary>
		internal static void WriteInline(string text)
			=> Console.Write(text);

		/// <summary>Blank line.</summary>
		internal static void WriteBlank()
			=> Console.WriteLine();

		// ── Field display ─────────────────────────────────────────────────────

		/// <summary>
		/// Displays a labelled field — White label, reset value.
		/// e.g. "  Word    : Schreibfehler"
		/// </summary>
		internal static void WriteField(string label, string value)
		{
			Console.ForegroundColor = ConsoleColor.White;
			Console.Write($"{Indent}{label,-9}: ");
			Console.ResetColor();
			Console.WriteLine(value);
		}

		/// <summary>
		/// <see cref="WriteField"/> variant that tints a leading token of the value in amber
		/// (DarkYellow foreground), with the remainder plain. A subtle "note this" cue — not the
		/// aggressive red of the flagged word — used to flag the "script" kind on a script-sourced
		/// spell finding so the operator reads it as a technical token, not a normal prose typo.
		/// Same label column and reset behaviour as <see cref="WriteField"/>.
		/// </summary>
		internal static void WriteFieldWithAmberToken(string label, string amberToken, string rest)
		{
			Console.ForegroundColor = ConsoleColor.White;
			Console.Write($"{Indent}{label,-9}: ");
			Console.ForegroundColor = ConsoleColor.DarkYellow;
			Console.Write(amberToken);
			Console.ResetColor();
			Console.WriteLine(rest);
		}

		/// <summary>
		/// One consistent card title for the per-item triages (content quality,
		/// spelling, dictionary): "[n/m]  Label : value" with the counter dimmed.
		/// Pair with a preceding WriteDivider() so every card opens the same way.
		/// </summary>
		internal static void WriteCardHeader(int index, int total, string label, string value, string? tag = null)
		{
			Console.ForegroundColor = ConsoleColor.DarkGray;
			Console.Write($"[{index}/{total}]  ");
			Console.ForegroundColor = ConsoleColor.White;
			Console.Write($"{label} : ");
			Console.ResetColor();
			Console.Write(value);
			if (!string.IsNullOrEmpty(tag))
			{
				Console.ForegroundColor = ConsoleColor.DarkYellow;
				Console.Write($"  · {tag}");
				Console.ResetColor();
			}

			Console.WriteLine();
		}

		/// <summary>
		/// Inline variant of <see cref="WriteField"/> — same label column convention
		/// (Indent + 9-wide left-padded label + ": ") but no terminating newline, so
		/// the caller can follow with a custom-coloured value paint (e.g. the
		/// content-quality highlighters). Use this instead of hand-built padded
		/// strings to keep the column alignment consistent with WriteField across
		/// the codebase.
		/// </summary>
		internal static void WriteFieldInline(string label)
		{
			Console.ForegroundColor = ConsoleColor.White;
			Console.Write($"{Indent}{label,-9}: ");
			Console.ResetColor();
		}

		/// <summary>Indented sub-item line (4-space indent).</summary>
		internal static void WriteSubItem(string message)
			=> Console.WriteLine($"    {message}");

		// ── Progress ──────────────────────────────────────────────────────────

		/// <summary>In-place progress update on the current line.</summary>
		internal static void WriteProgress(string message)
			=> Console.Write($"\r{Indent}{message}");

		/// <summary>Clears the current progress line.</summary>
		internal static void ClearProgress()
			=> Console.Write("\r" + new string(' ', 60) + "\r");

		// ── Prompts and input ─────────────────────────────────────────────────

		/// <summary>
		/// Writes a prompt and reads a single keypress without echo.
		/// Returns the ConsoleKey pressed.
		/// </summary>
		internal static ConsoleKey ReadKey(string prompt)
		{
			Console.Write($"{Indent}{prompt}");
			var key = Console.ReadKey(intercept: true).Key;
			Console.WriteLine();
			return key;
		}

		/// <summary>
		/// Writes a prompt and reads a line of text.
		/// Returns the trimmed input, or empty string if null.
		/// </summary>
		internal static string ReadLine(string prompt)
		{
			Console.Write($"{Indent}{prompt}");
			return Console.ReadLine()?.Trim() ?? string.Empty;
		}

		/// <summary>
		/// Writes "  > " prompt and reads a line — used for list entry.
		/// </summary>
		internal static string ReadEntry()
		{
			Console.Write($"{Indent}> ");
			return Console.ReadLine()?.Trim() ?? string.Empty;
		}

		/// <summary>
		/// Reads a line with a pre-populated default value the user can edit:
		/// the default is echoed, then Backspace deletes from the end and any
		/// printable key appends. Enter accepts. Returns the trimmed result.
		/// Kept here so the raw ReadKey/echo loop stays inside ConsoleUi.
		/// </summary>
		internal static string ReadLineWithDefault(string defaultValue)
		{
			// Write the default, let user backspace/retype from end.
			Console.Write(defaultValue); // inline edit — keep raw
			var sb = new System.Text.StringBuilder(defaultValue);
			while (true)
			{
				var ki = Console.ReadKey(intercept: true);
				if (ki.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
				if (ki.Key == ConsoleKey.Backspace && sb.Length > 0)
				{
					sb.Remove(sb.Length - 1, 1);
					Console.Write("\b \b");
				}
				else if (!char.IsControl(ki.KeyChar))
				{
					sb.Append(ki.KeyChar);
					Console.Write(ki.KeyChar);
				}
			}
			return sb.ToString().Trim();
		}

		/// <summary>
		/// Reads a secret (e.g. a password) without echoing it: each printable
		/// keypress appends to the buffer and renders a '*' mask; Backspace
		/// removes the last character and erases one mask; Enter finishes.
		/// Writes the supplied prompt first (with the standard indent). Returns
		/// the secret UNTRIMMED — passwords may legitimately contain leading or
		/// trailing whitespace, so trimming here would silently corrupt them.
		/// Kept here so the raw ReadKey/echo loop stays inside ConsoleUi (the
		/// presentation layer), not in a flow class.
		/// </summary>
		internal static string ReadMaskedSecret(string prompt)
		{
			Console.Write($"{Indent}{prompt}");
			var secret = new System.Text.StringBuilder();
			while (true)
			{
				var key = Console.ReadKey(intercept: true);
				if (key.Key == ConsoleKey.Enter)
				{
					Console.WriteLine();
					break;
				}
				if (key.Key == ConsoleKey.Backspace)
				{
					if (secret.Length > 0)
					{
						secret.Remove(secret.Length - 1, 1);
						Console.Write("\b \b"); // erase one mask — keep raw
					}
				}
				else if (key.KeyChar != '\0')
				{
					secret.Append(key.KeyChar);
					Console.Write('*'); // password mask — keep raw
				}
			}
			return secret.ToString();
		}

		/// <summary>
		/// Displays "Press Enter to exit..." and waits.
		/// </summary>
		internal static void PressEnterToExit()
		{
			Console.WriteLine();
			Console.WriteLine("Press Enter to exit...");
			Console.ReadLine();
		}

		// ── Composite helpers ─────────────────────────────────────────────────

		/// <summary>
		/// Writes a framed action-required block with indented body lines.
		/// The heading is highlighted in yellow; body lines are emitted as-is
		/// (with a 4-space indent) so callers can mix bare descriptive text,
		/// key/value lines, or their own numbering scheme as the content
		/// requires. This helper previously auto-numbered every body line —
		/// fine for true step lists but wrong-shaped for key/value metadata
		/// (the snapshot integrity warning) and double-numbering for call
		/// sites that already number their actions inside the strings (e.g.
		/// the triage action menus).
		/// </summary>
		internal static void WriteActionBlock(string heading, IEnumerable<string> steps)
		{
			Console.ForegroundColor = ConsoleColor.Yellow;
			Console.WriteLine(Separator);
			Console.WriteLine($"{Indent}{heading}");
			Console.ResetColor();
			Console.WriteLine();
			foreach (var step in steps)
			{
				Console.WriteLine($"    {step}");
			}

			Console.WriteLine();
		}

		/// <summary>
		/// Writes a warning block with optional sub-items.
		/// </summary>
		internal static void WriteWarningBlock(string heading, IEnumerable<string>? items = null)
		{
			Console.ForegroundColor = ConsoleColor.DarkYellow;
			Console.WriteLine($"{Indent}{heading}");
			if (items != null)
			{
				foreach (var item in items)
				{
					Console.WriteLine($"    {item}");
				}
			}

			Console.ResetColor();
		}

		/// <summary>
		/// Writes a red error block with optional sub-items.
		/// </summary>
		internal static void WriteErrorBlock(string heading, IEnumerable<string>? items = null)
		{
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine($"{Indent}{heading}");
			if (items != null)
			{
				foreach (var item in items)
				{
					Console.WriteLine($"    {item}");
				}
			}

			Console.ResetColor();
		}
		// ── Inline highlighting ─────────────────────────────────────────────
		// Character-level red-on-white highlighting for triage displays.
		// All write inline (no trailing newline) so callers control line breaks.

		/// <summary>
		/// Writes plain prefix, red-on-white highlight, plain suffix, then newline.
		/// Use for single-word highlights in context lines.
		/// e.g. "  ...before " + [hit] + "after..."
		/// </summary>
		internal static void WriteLineWithHighlight(string prefix, string highlight, string suffix)
		{
			Console.Write(prefix);
			Console.BackgroundColor = ConsoleColor.Red;
			Console.ForegroundColor = ConsoleColor.White;
			Console.Write(highlight);
			Console.ResetColor();
			WriteSuffixAndFinishLine(suffix);
		}

		// Writes the (plain) suffix, then explicitly paints the rest of the current
		// row with default-background spaces before the newline. Reordering the
		// newline after ResetColor() was not enough on conhost: when a row that
		// contained a coloured span is terminated, the trailing cells get filled
		// with the last non-default background, leaving a red smear from the end of
		// the suffix to the right edge. Relying on the newline's own fill does not
		// clear it. Painting the remainder ourselves (default background, our own
		// space writes) forces those cells to default regardless of conhost's fill
		// behaviour. Guarded: Console.WindowWidth / CursorLeft throw or are
		// meaningless under redirected / headless output, so any failure falls back
		// to a plain newline (the pre-existing behaviour — no worse than before).
		// Operator-eyeball verified: terminal trailing-fill is
		// environment-specific and not covered by any automated test.
		private static void WriteSuffixAndFinishLine(string suffix)
		{
			Console.Write(suffix);
			try
			{
				int width = Console.WindowWidth;
				int col = Console.CursorLeft;
				// Pad to one short of the right edge, not the edge itself: writing the
				// final column auto-wraps the cursor on some terminals, so a following
				// WriteLine() would then emit a spurious blank line. width-1 paints the
				// smear-prone remainder while leaving the cursor safely before the edge.
				if (width > 1 && col < width - 1)
				{
					Console.Write(new string(' ', width - 1 - col));
				}
			}
			catch
			{
				// Width/cursor unavailable (redirected output, no console) — skip the
				// padding; the smear only manifests on an interactive terminal anyway.
			}
			Console.WriteLine();
		}

		// ── Wrap-aware emission (multi-span smear fix) ──────────────────
		// The multi-span highlighters below interleave many coloured spans through
		// long text that wraps across several terminal rows. conhost back-fills a
		// row's trailing cells with the LAST non-default background when the cursor
		// crosses a row boundary while a coloured background is active — leaving a
		// smear from the end of a wrapping coloured span to the right edge of every
		// INTERMEDIATE row it crosses, not just the final one. WriteSuffixAndFinish-
		// Line's "pad the final row" trick (the single-span fix) cannot reach
		// an interior row's tail, so it is insufficient here.
		//
		// The robust fix: never let a coloured background be live as the cursor
		// crosses a row boundary. WriteWrapped emits a single (optionally coloured)
		// chunk and, at each predicted wrap point, RESETS colour, repaints the row
		// remainder with default-background spaces, emits a newline, then restores
		// colour and continues on the next row. No coloured cell is ever stranded
		// at a row tail. The column arithmetic (where do the breaks fall, given a
		// start column and width) is the pure, unit-tested SplitIntoRowChunks; the
		// Console emission here is operator-eyeball-only.
		//
		// Width model: one char == one column (monospace console; the rest of this
		// file already counts string .Length against WindowWidth). Wide/zero-width/
		// combining characters and tabs are NOT width-corrected — out of scope; the
		// triage content is HTML/Latin text. Guarded exactly like WriteSuffixAnd-
		// FinishLine: if WindowWidth/CursorLeft are unavailable (redirected/headless
		// output) the whole thing degrades to a single plain Console.Write — the
		// pre-existing behaviour, and the smear only manifests on a real terminal.

		/// <summary>
		/// Splits <paramref name="textLength"/> characters, starting at column
		/// <paramref name="startCol"/> on a row of <paramref name="width"/> columns,
		/// into per-row run lengths. The first run fills the current row from
		/// startCol; each subsequent run is a full <paramref name="width"/> (the last
		/// may be shorter). Wrapping happens at column <paramref name="width"/>-1 (one
		/// short of the edge) to match WriteSuffixAndFinishLine — writing the final
		/// column auto-wraps the cursor on some terminals and would emit spurious
		/// blank lines. Pure: no Console access, fully unit-testable.
		/// </summary>
		/// <returns>
		/// Run lengths in order. Sum equals <paramref name="textLength"/>. A run
		/// length of 0 is never emitted. When width is unusable (&lt;= 1) or
		/// textLength is 0, returns the whole length as a single run (caller falls
		/// back to a plain write).
		/// </returns>
		internal static IReadOnlyList<int> SplitIntoRowChunks(int startCol, int textLength, int width)
		{
			if (textLength <= 0)
			{
				return [];
			}
			// Usable columns per row = width - 1 (leave the last column unwritten).
			int usable = width - 1;
			if (usable < 1)
			{
				return [textLength]; // degenerate width — caller writes it plainly.
			}

			var runs = new List<int>();
			int col = startCol < 0 ? 0 : startCol;
			int remaining = textLength;

			// First (partial) row: from the current column to the wrap point.
			int firstRoom = usable - col;
			if (firstRoom <= 0)
			{
				// Already at/past the wrap column — the whole emission starts on a
				// fresh row. Caller treats a leading 0-room as "newline first"; we
				// model that by not emitting a 0 run and letting the full-row loop
				// below carry everything.
			}
			else
			{
				int take = Math.Min(firstRoom, remaining);
				runs.Add(take);
				remaining -= take;
			}

			// Subsequent full rows.
			while (remaining > 0)
			{
				int take = Math.Min(usable, remaining);
				runs.Add(take);
				remaining -= take;
			}
			return runs;
		}

		// Emits one chunk of text, optionally coloured, wrapping at the terminal
		// width and repainting each completed row's remainder to the default
		// background so no coloured cell is stranded at a row tail. Mutates nothing
		// the caller needs back except the cursor position (left where the last
		// char landed, no trailing newline — callers control line breaks, matching
		// the existing multi-span writers). coloured == false writes plain text but
		// still wraps with default-background repaint, keeping wrap behaviour
		// uniform across plain and highlighted segments of one logical line.
		private static void WriteWrapped(string text, bool coloured, ConsoleColor bg, ConsoleColor fg)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			int width, startCol;
			try
			{
				width = Console.WindowWidth;
				startCol = Console.CursorLeft;
			}
			catch
			{
				// Width/cursor unavailable (redirected/headless) — degrade to a
				// single plain coloured write, the pre-existing behaviour.
				WriteColoured(text, coloured, bg, fg);
				return;
			}

			var runs = SplitIntoRowChunks(startCol, text.Length, width);
			if (runs.Count <= 1)
			{
				// Fits on the current row (or width degenerate) — no wrap handling
				// needed; a single coloured write matches old behaviour exactly.
				WriteColoured(text, coloured, bg, fg);
				return;
			}

			int pos = 0;
			for (int i = 0; i < runs.Count; i++)
			{
				int len = runs[i];
				WriteColoured(text.Substring(pos, len), coloured, bg, fg);
				pos += len;

				// After every run except the last, we are at a row boundary: reset
				// colour, repaint the row remainder to default, newline, then (if
				// more text follows in a coloured chunk) the next WriteColoured call
				// re-applies the colour. Repaint guards against the smear on THIS row.
				if (i < runs.Count - 1)
				{
					Console.ResetColor();
					RepaintRowTail(width);
					Console.WriteLine();
				}
			}
			// NOTE: no final-row repaint here. WriteWrapped does not know whether the
			// caller will continue inline on the same row (the common case: a coloured
			// <a> segment followed by its plain href="…" on the same line). Repainting
			// the tail here would push that continuation to the right edge and wreck
			// the layout. The end-of-LINE repaint is the caller's responsibility via
			// FinishHighlightedLine, called once after its segment loop iff the line
			// ended on a coloured segment.
		}

		// Called by the multi-span Core writers ONCE after their segment loop, when
		// the line contained ANY highlight (not only when the LAST segment was
		// coloured). On Windows Terminal the row's background fill persists once a
		// coloured span has been written on that row: it leaks FORWARD through any
		// subsequently-written plain cells all the way to the right edge, so a line
		// ending in plain text after a highlight (e.g. `<a` then a plain `href="…"`
		// tail) still smears red. Repainting the final row's remainder with default-
		// background spaces forces those cells to default — the multi-span analogue
		// of WriteSuffixAndFinishLine's final-row pad for the single-span path. No
		// newline; callers still control breaks.
		private static void FinishHighlightedLine()
		{
			try { RepaintRowTail(Console.WindowWidth); }
			catch { /* width unavailable — nothing to repaint on a non-terminal */ }
		}

		// Paints default-background spaces from the current cursor column to one
		// short of the right edge (width-1, matching WriteSuffixAndFinishLine — the
		// final column auto-wraps on some terminals and would emit a spurious blank
		// line). Forces the smear-prone trailing cells to default regardless of
		// conhost's own fill behaviour. Leaves the cursor where the padding ends
		// (no newline). Guarded: CursorLeft is meaningless under redirected output.
		private static void RepaintRowTail(int width)
		{
			try
			{
				int col = Console.CursorLeft;
				if (width > 1 && col < width - 1)
				{
					Console.Write(new string(' ', width - 1 - col));
				}
			}
			catch { /* width/cursor unavailable — skip; smear only shows on a real terminal */ }
		}

		// Writes text with the highlight colours applied iff coloured, resetting
		// after. The single low-level Console.Write choke point for the wrap-aware
		// path; keeps colour set/reset symmetric in one place.
		private static void WriteColoured(string text, bool coloured, ConsoleColor bg, ConsoleColor fg)
		{
			if (!coloured) { Console.Write(text); return; }
			Console.BackgroundColor = bg;
			Console.ForegroundColor = fg;
			Console.Write(text);
			Console.ResetColor();
		}

		/// <summary>
		/// Muted variant of <see cref="WriteLineWithHighlight"/> — black-on-
		/// dark-yellow instead of white-on-red. Used by the review pass so
		/// the highlighted word reads as "note this" rather than the aggressive
		/// alarm style of live triage, making clear the review is not triage
		/// itself. Writes plain prefix, muted highlight, plain suffix, newline.
		/// </summary>
		internal static void WriteLineWithMutedHighlight(string prefix, string highlight, string suffix)
		{
			Console.Write(prefix);
			Console.BackgroundColor = ConsoleColor.DarkYellow;
			Console.ForegroundColor = ConsoleColor.Black;
			Console.Write(highlight);
			Console.ResetColor();
			WriteSuffixAndFinishLine(suffix);
		}

		/// <summary>
		/// Writes <paramref name="text"/> with every occurrence of the injected
		/// <paramref name="marker"/> token highlighted in the WCAG-violation
		/// scheme (white-on-DarkBlue). The caller is responsible for having
		/// injected the marker into the right position(s) in <paramref name="text"/>
		/// beforehand (see ContentQualityTriage.InjectEmptyAnchorMarker); this
		/// primitive only lights an already-present token, it does no matching of
		/// the underlying defect. Non-marker text renders uncoloured. Matches are
		/// case-sensitive (Ordinal) — the marker is a fixed literal we emit
		/// ourselves, so there is no case to fold. No trailing newline: the caller
		/// writes WriteBlank() after, matching the WriteWith*Highlight contract
		/// (and unlike the line-terminating field writers). If <paramref name="marker"/>
		/// is empty or never occurs, the text is written unchanged.
		/// </summary>
		internal static void WriteWithWcagMarkerHighlight(string text, string marker)
		{
			if (string.IsNullOrEmpty(marker) || string.IsNullOrEmpty(text))
			{
				WriteWrapped(text ?? string.Empty, coloured: false, HighlightBgWcag, HighlightFgWcag);
				return;
			}

			var pos = 0;
			var anyColoured = false;
			while (pos < text.Length)
			{
				var hit = text.IndexOf(marker, pos, StringComparison.Ordinal);
				if (hit < 0)
				{
					WriteWrapped(text[pos..], coloured: false, HighlightBgWcag, HighlightFgWcag);
					break;
				}
				if (hit > pos)
				{
					WriteWrapped(text[pos..hit], coloured: false, HighlightBgWcag, HighlightFgWcag);
				}

				WriteWrapped(marker, coloured: true, HighlightBgWcag, HighlightFgWcag);
				anyColoured = true;
				pos = hit + marker.Length;
			}
			if (anyColoured)
			{
				FinishHighlightedLine();
			}
		}

		// ── Highlight colour schemes ──────────────────────────────────────────
		// Live triage uses the aggressive red-on-white "alarm" scheme; the review
		// pass uses the muted amber (DarkYellow-on-Black) scheme so the operator
		// reads it as review, not triage. The span-finding logic is identical for
		// both — only the colour pair differs, so each highlighter delegates to a
		// colour-parameterised core.
		//
		// The WCAG scheme (white-on-DarkBlue) is reserved for injected
		// accessibility-violation markers (e.g. [WCAG-VIOLATION-EMPTY-LINK]). It
		// is deliberately ONE scheme used in both triage and review contexts — the
		// marker names a standards violation that is equally a defect whichever
		// pass surfaces it, so it does not get a muted/alarm pair like the
		// structural highlighters above. New WCAG marker types reuse this pair.
		//
		// The split-word scheme renders a SPLIT_WORD_ANCHOR finding as three spans:
		// the <a>/</a> TAGS stay red (HighlightBgTriage — structural boundary,
		// consistent with the other anchor highlighters); the INSIDE link text is
		// DarkCyan; the orphaned TAIL after </a> is DarkBlue. The two-colour
		// straddle of the red close tag shows "linked part / escaped part" without
		// the reader needing to understand the language — load-bearing for a
		// non-fluent operator routing multilingual findings. One scheme in both
		// triage and review (a split is a defect whichever pass surfaces it).
		//
		// [KEEP] Colour choices are CVD-conscious:
		//   - Tail is DarkBlue, NOT DarkGreen. The earlier DarkGreen choice
		//     created a red/green discrimination problem for ~5% of operators
		//     (deuteranopia/protanopia); DarkBlue is on the CVD-safe blue/yellow
		//     axis and reads cleanly for all viewers.
		//   - DarkBlue is also the WCAG-marker colour. The reuse is intentional
		//     and safe: the two consumers never appear in the same finding type
		//     (WCAG markers appear in MISPLACED_ANCHOR_EMPTY only; the split tail
		//     in SPLIT_WORD_ANCHOR only). The Type: line at the top of every
		//     finding screen disambiguates, and the content shape differs (WCAG
		//     marker is the fixed literal "[WCAG-VIOLATION-EMPTY-LINK]"; the tail
		//     is variable free-form text after </a>).
		//   - All FG values on coloured backgrounds are White for legibility:
		//     consoles default to white-on-black, and saturated dark backgrounds
		//     with Black FG (the previous SPLIT_WORD inside/tail choice) are hard
		//     to read on long spans. Review keeps Black FG because DarkYellow is
		//     bright enough to carry it and the legacy alarm-vs-review pair
		//     deliberately encodes the difference in FG too.

		private const ConsoleColor HighlightBgTriage = ConsoleColor.Red;
		private const ConsoleColor HighlightFgTriage = ConsoleColor.White;
		private const ConsoleColor HighlightBgReview = ConsoleColor.DarkYellow;
		private const ConsoleColor HighlightFgReview = ConsoleColor.Black;
		private const ConsoleColor HighlightBgWcag = ConsoleColor.DarkBlue;
		private const ConsoleColor HighlightFgWcag = ConsoleColor.White;
		private const ConsoleColor HighlightBgSplitInside = ConsoleColor.DarkCyan;
		private const ConsoleColor HighlightFgSplitInside = ConsoleColor.White;
		private const ConsoleColor HighlightBgSplitTail = ConsoleColor.DarkBlue;
		private const ConsoleColor HighlightFgSplitTail = ConsoleColor.White;

		// ADJACENT_ANCHOR same-href hint palette. The ADJACENT_ANCHOR detector
		// flags any literal </a><a collision honestly — but adjacency itself is too
		// broad to claim a defect: button rows, JS-trigger icon widgets, and
		// editor-accident word-splits all share the structural shape. The detector
		// stays honest; the visualization layer surfaces additional structural facts
		// (href values, title values, anchor text scope) via colour so the operator's
		// eye can interpret which class of adjacency this is. The tool does NOT
		// classify — every coloured span is a fact (this is a literal "</a><a"; these
		// hrefs match as strings; this href is the placeholder "#"). Interpretation
		// is the operator's, always.
		//
		// [KEEP] CVD-safety reasoning (operator is tetrachromat, ~5% of operators
		// have red-green CVD):
		//   - DarkMagenta (HrefMatch) is the load-bearing CVD-safe "matches" colour
		//     vs Red (alarm). Magenta has a blue component that shifts the hue
		//     away from the red-green axis; deuteranopes/protanopes distinguish
		//     magenta from red reliably. Less perfect for protanopes specifically,
		//     mitigated by spatial separation (the red </a><a collision sits
		//     between the two magenta href spans, not adjacent to them) and by the
		//     all-white FG keeping brightness uniform.
		//   - DarkGray (HrefBaseline, AnchorTextHint) carries no hue claim — pure
		//     brightness emphasis. CVD-bulletproof. Says "set apart, look here"
		//     without claiming a match.
		//   - DarkYellow (HrefPlaceholder) reuses the review-pass colour. Safe
		//     reuse because review-pass paints the ENTIRE excerpt in DarkYellow
		//     (uniform context); HrefPlaceholder paints only the small "#" attribute
		//     value (selective). The shape difference disambiguates. The CVD
		//     yellow/blue axis is also the safest, so this is a strong choice for
		//     "be aware, JS-trigger territory" without conflating with the alarm.
		//   - DarkCyan (TitleMatch) reuses the SPLIT_WORD inside colour. Safe reuse
		//     because the two consumers never appear in the same finding type
		//     (SPLIT_WORD inside is SPLIT_WORD_ANCHOR only; TitleMatch is
		//     ADJACENT_ANCHOR only). The family meaning "matching content/attribute"
		//     extends consistently across both uses.
		//   - All FG values are White. White-on-coloured-BG discipline
		//     (consoles default to white-on-black; saturated dark BGs need
		//     bright FG for long-span readability — these spans can be hrefs of
		//     70+ chars).
		//
		// [KEEP] This palette is a HINT layer, not a classification. The detector
		// flags adjacency broadly because adjacency-vs-design cannot be reliably
		// distinguished by code. Colour
		// language surfaces the structural facts; the operator's eye is the gate.
		// Do not add findings that "claim" what an adjacency means — extend the
		// hint vocabulary instead (new colour for a new attribute match).

		private const ConsoleColor HighlightBgHrefBaseline = ConsoleColor.DarkGray;
		private const ConsoleColor HighlightFgHrefBaseline = ConsoleColor.White;
		private const ConsoleColor HighlightBgHrefMatch = ConsoleColor.DarkMagenta;
		private const ConsoleColor HighlightFgHrefMatch = ConsoleColor.White;
		private const ConsoleColor HighlightBgHrefPlaceholder = ConsoleColor.DarkYellow;
		private const ConsoleColor HighlightFgHrefPlaceholder = ConsoleColor.White;
		private const ConsoleColor HighlightBgTitleMatch = ConsoleColor.DarkCyan;
		private const ConsoleColor HighlightFgTitleMatch = ConsoleColor.White;
		private const ConsoleColor HighlightBgAnchorTextHint = ConsoleColor.DarkGray;
		private const ConsoleColor HighlightFgAnchorTextHint = ConsoleColor.White;

		/// <summary>
		/// Writes text with each occurrence of any pattern string highlighted
		/// red-on-white. Handles multiple patterns — earliest hit wins each pass.
		/// No trailing newline — caller writes Console.WriteLine() after.
		/// </summary>
		internal static void WriteWithPatternHighlight(string text, string[] patterns)
			=> WriteWithPatternHighlightCore(text, patterns, HighlightBgTriage, HighlightFgTriage);

		/// <summary>
		/// Muted (amber) variant of <see cref="WriteWithPatternHighlight"/> for the
		/// review pass. Same pattern detection, review colour scheme.
		/// </summary>
		internal static void WriteWithPatternHighlightMuted(string text, string[] patterns)
			=> WriteWithPatternHighlightCore(text, patterns, HighlightBgReview, HighlightFgReview);

		private static void WriteWithPatternHighlightCore(
			string text, string[] patterns, ConsoleColor bg, ConsoleColor fg)
		{
			var pos = 0;
			var anyColoured = false;
			while (pos < text.Length)
			{
				var bestIdx = -1;
				var bestLen = 0;
				foreach (var p in patterns)
				{
					if (string.IsNullOrEmpty(p))
					{
						continue;
					}

					var hit = text.IndexOf(p, pos, StringComparison.OrdinalIgnoreCase);
					if (hit >= 0 && (bestIdx < 0 || hit < bestIdx))
					{
						bestIdx = hit;
						bestLen = p.Length;
					}
				}
				if (bestIdx < 0) { WriteWrapped(text[pos..], coloured: false, bg, fg); break; }
				if (bestIdx > pos)
				{
					WriteWrapped(text[pos..bestIdx], coloured: false, bg, fg);
				}

				WriteWrapped(text.Substring(bestIdx, bestLen), coloured: true, bg, fg);
				anyColoured = true;
				pos = bestIdx + bestLen;
			}
			if (anyColoured)
			{
				FinishHighlightedLine();
			}
		}

		/// <summary>
		/// Writes HTML text with anchor tags highlighted red-on-white.
		/// Highlights "&lt;a" and "&lt;/a&gt;" — not full tag content.
		/// No trailing newline.
		/// </summary>
		internal static void WriteWithAnchorTagHighlight(string text)
			=> WriteWithAnchorTagHighlightCore(text, HighlightBgTriage, HighlightFgTriage);

		// ── URL fragment highlighting (Config.TriageUrlHighlight) ─────────────
		// Colours slash-bounded path segments in a triage "URL :" line so the
		// operator can place a finding by language/section at a glance. The whole
		// system is background-based, so URL highlighting matches: each slot is a
		// (bg, fg) pair drawn from the existing CVD-aware palette, NOT a new colour.

		/// <summary>A coloured run within a URL string, in URL-LOCAL coordinates.</summary>
		internal readonly record struct UrlHighlightSpan(int Start, int Length, int Slot);

		/// <summary>
		/// Maps a palette slot (1-5) to a (background, foreground) pair, reusing the
		/// application's existing highlight colours so URL highlighting inherits the
		/// same colour-blind-aware choices. Slot 0 / out-of-range falls back to the
		/// default colours uncoloured-style (caller should not emit a span for it;
		/// ValidateConfig rejects out-of-range slots at load, so this is defensive).
		/// </summary>
		private static (ConsoleColor Bg, ConsoleColor Fg) ResolveUrlHighlightSlot(int slot) => slot switch
		{
			1 => (HighlightBgTriage, HighlightFgTriage),          // red / white
			2 => (HighlightBgWcag, HighlightFgWcag),              // blue / white
			3 => (HighlightBgReview, HighlightFgReview),          // amber / black
			4 => (HighlightBgHrefMatch, HighlightFgHrefMatch),    // magenta / white
			5 => (HighlightBgAnchorTextHint, HighlightFgAnchorTextHint), // grey / white
			_ => (Console.BackgroundColor, Console.ForegroundColor),
		};

		/// <summary>
		/// Number of palette slots exposed to Config.TriageUrlHighlight (1..N).
		/// ValidateConfig uses this to bound-check the Highlight index.
		/// </summary>
		internal const int UrlHighlightSlotCount = 5;

		/// <summary>
		/// Computes the coloured spans for a URL given the configured rules. Pure —
		/// no Console — so the matching logic is unit-tested here.
		///
		/// Matching: each rule's Value is a slash-bounded fragment (e.g. "/en/").
		/// Only the URL's PATH is searched (scheme, host, and any "?query"/"#fragment"
		/// are excluded), so a fragment can never light up inside the host or query.
		/// For a rule "/x/", every occurrence of "/x/" in the path contributes a span
		/// covering just the segment "x" (the bounding slashes are match anchors and
		/// are never coloured — this both prevents matching "x" inside a longer word
		/// and leaves the slash between two adjacent highlighted segments uncoloured,
		/// so consecutive colours stay visually separated).
		///
		/// Overlapping matches from different rules are resolved first-by-position;
		/// a later span that would overlap one already emitted is skipped, so output
		/// is sorted ascending by Start and non-overlapping (the writer requires this).
		/// </summary>
		internal static System.Collections.Generic.List<UrlHighlightSpan> ComputeUrlHighlightSpans(
			string url, System.Collections.Generic.IReadOnlyList<UrlHighlightRule> rules)
		{
			var spans = new System.Collections.Generic.List<UrlHighlightSpan>();
			if (string.IsNullOrEmpty(url) || rules is null || rules.Count == 0)
			{
				return spans;
			}

			// Bound the search to the path: from the first single '/' after an
			// optional "scheme://host" prefix, up to the first '?' or '#'.
			int pathStart = 0;
			int schemeIdx = url.IndexOf("://", StringComparison.Ordinal);
			if (schemeIdx >= 0)
			{
				int hostSlash = url.IndexOf('/', schemeIdx + 3);
				pathStart = hostSlash >= 0 ? hostSlash : url.Length;
			}
			int pathEnd = url.Length;
			int q = url.IndexOfAny(['?', '#'], pathStart);
			if (q >= 0)
			{
				pathEnd = q;
			}

			foreach (var rule in rules)
			{
				if (rule.Values is null)
				{
					continue;
				}
				foreach (var frag in rule.Values)
				{
					if (string.IsNullOrEmpty(frag)
						|| frag.Length < 2 || frag[0] != '/' || frag[^1] != '/')
					{
						// Not a slash-bounded fragment — ignore (ValidateConfig flags it).
						continue;
					}

					// Search within the path window only.
					int from = pathStart;
					while (true)
					{
						int hit = url.IndexOf(frag, from, StringComparison.OrdinalIgnoreCase);
						if (hit < 0 || hit + frag.Length > pathEnd)
						{
							break;
						}

						// The coloured segment is the text BETWEEN the bounding slashes.
						int segStart = hit + 1;
						int segLen = frag.Length - 2;
						if (segLen > 0)
						{
							spans.Add(new UrlHighlightSpan(segStart, segLen, rule.Highlight));
						}

						// Advance by one char (not frag.Length): consecutive fragments
						// like "/en/global/" share the middle slash, so the next search
						// must be able to see a fragment that begins at that shared slash.
						from = hit + 1;
					}
				}
			}

			if (spans.Count <= 1)
			{
				return spans;
			}

			// Sort by Start, then drop any span overlapping one already kept.
			spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Length.CompareTo(b.Length));
			var result = new System.Collections.Generic.List<UrlHighlightSpan>(spans.Count);
			int cursor = 0;
			foreach (var s in spans)
			{
				if (s.Start < cursor)
				{
					continue; // overlaps an already-emitted span — skip
				}
				result.Add(s);
				cursor = s.Start + s.Length;
			}

			return result;
		}

		/// <summary>
		/// Writes a URL with the given highlight spans applied as background blocks,
		/// the rest in default colours. Spans must be URL-local, sorted ascending by
		/// Start, and non-overlapping (ComputeUrlHighlightSpans guarantees this). No
		/// trailing newline-by-default beyond the standard finish; the caller emits
		/// the field label inline first.
		/// </summary>
		internal static void WriteWithUrlHighlight(string url, IReadOnlyList<UrlHighlightSpan> spans)
		{
			if (string.IsNullOrEmpty(url))
			{
				return;
			}
			if (spans is null || spans.Count == 0)
			{
				WriteWrapped(url, coloured: false, Console.BackgroundColor, Console.ForegroundColor);
				return;
			}

			int pos = 0;
			bool anyColoured = false;
			foreach (var span in spans)
			{
				if (span.Start < pos || span.Start >= url.Length || span.Length <= 0)
				{
					continue;
				}
				int end = Math.Min(span.Start + span.Length, url.Length);

				if (span.Start > pos)
				{
					WriteWrapped(url[pos..span.Start], coloured: false, Console.BackgroundColor, Console.ForegroundColor);
				}

				var (bg, fg) = ResolveUrlHighlightSlot(span.Slot);
				WriteWrapped(url[span.Start..end], coloured: true, bg, fg);
				anyColoured = true;
				pos = end;
			}

			if (pos < url.Length)
			{
				WriteWrapped(url[pos..], coloured: false, Console.BackgroundColor, Console.ForegroundColor);
			}
			if (anyColoured)
			{
				FinishHighlightedLine();
			}
		}

		/// <summary>
		/// Writes a triage "URL :" field, highlighting configured path fragments
		/// when <paramref name="rules"/> is non-empty and otherwise rendering the
		/// plain field (unchanged behaviour). Single home for the URL-line render so
		/// the content-quality and spell-check triage flows stay identical and the
		/// empty-rules fallback is defined once.
		/// </summary>
		internal static void WriteUrlField(string url, IReadOnlyList<UrlHighlightRule> rules)
		{
			var spans = ComputeUrlHighlightSpans(url, rules);
			if (spans.Count == 0)
			{
				WriteField("URL", url);
				return;
			}
			WriteFieldInline("URL");
			WriteWithUrlHighlight(url, spans);
			WriteBlank();
		}

		/// <summary>
		/// Muted (amber) variant of <see cref="WriteWithAnchorTagHighlight"/> for
		/// the review pass. Same anchor detection, review colour scheme.
		/// </summary>
		internal static void WriteWithAnchorTagHighlightMuted(string text)
			=> WriteWithAnchorTagHighlightCore(text, HighlightBgReview, HighlightFgReview);

		private static void WriteWithAnchorTagHighlightCore(string text, ConsoleColor bg, ConsoleColor fg)
		{
			var pos = 0;
			var anyColoured = false;
			while (pos < text.Length)
			{
				var closeIdx = text.IndexOf("</a>", pos, StringComparison.OrdinalIgnoreCase);
				var openIdx = text.IndexOf("<a", pos, StringComparison.OrdinalIgnoreCase);
				int bestIdx, bestLen;
				if (closeIdx >= 0 && (openIdx < 0 || closeIdx <= openIdx))
				{ bestIdx = closeIdx; bestLen = 4; }
				else if (openIdx >= 0)
				{ bestIdx = openIdx; bestLen = 2; }
				else
				{ WriteWrapped(text[pos..], coloured: false, bg, fg); break; }
				if (bestIdx > pos)
				{
					WriteWrapped(text[pos..bestIdx], coloured: false, bg, fg);
				}

				WriteWrapped(text.Substring(bestIdx, bestLen), coloured: true, bg, fg);
				anyColoured = true;
				pos = bestIdx + bestLen;
			}
			if (anyColoured)
			{
				FinishHighlightedLine();
			}
		}

		/// <summary>
		/// Writes excerpt text with ADJACENT_ANCHOR defect-shapes highlighted
		/// red-on-white: ONLY the literal "&lt;/a&gt;&lt;a" collisions (6 chars
		/// each — the close tag immediately abutting the next open tag's first
		/// two characters) are highlighted. Everything else in the excerpt,
		/// including any non-colliding "&lt;a&gt;" / "&lt;/a&gt;" tags elsewhere,
		/// stays plain. Match is case-insensitive (mirrors the detector's
		/// post-filter in ContentQuality.cs).
		///
		/// Distinct from <see cref="WriteWithAnchorTagHighlight"/> (which lights
		/// every anchor tag boundary, a useful general utility) — this primitive
		/// is scoped to the ADJACENT_ANCHOR finding type so the highlight matches
		/// exactly what the detector claims. Lighting unrelated tag markup was
		/// noise the operator's eye had to filter out; the narrow match keeps
		/// the eye on the defect.
		/// </summary>
		internal static void WriteWithAdjacentAnchorHighlight(string text)
			=> WriteWithAdjacentAnchorHighlightCore(text, HighlightBgTriage, HighlightFgTriage);

		/// <summary>
		/// Muted (amber) variant of <see cref="WriteWithAdjacentAnchorHighlight"/>
		/// for the review pass. Same narrow "&lt;/a&gt;&lt;a"-only match, review
		/// colour scheme.
		/// </summary>
		internal static void WriteWithAdjacentAnchorHighlightMuted(string text)
			=> WriteWithAdjacentAnchorHighlightCore(text, HighlightBgReview, HighlightFgReview);

		private static void WriteWithAdjacentAnchorHighlightCore(string text, ConsoleColor bg, ConsoleColor fg)
		{
			// Highlight every literal "</a><a" collision; everything else stays plain.
			// The 6-char span is the entire highlighted region — narrowest honest
			// rendering of "the source bytes literally have these tags abutting."
			const string needle = "</a><a";
			var pos = 0;
			var anyColoured = false;
			while (pos < text.Length)
			{
				var hitIdx = text.IndexOf(needle, pos, StringComparison.OrdinalIgnoreCase);
				if (hitIdx < 0)
				{
					WriteWrapped(text[pos..], coloured: false, bg, fg);
					break;
				}
				if (hitIdx > pos)
				{
					WriteWrapped(text[pos..hitIdx], coloured: false, bg, fg);
				}
				WriteWrapped(text.Substring(hitIdx, needle.Length), coloured: true, bg, fg);
				anyColoured = true;
				pos = hitIdx + needle.Length;
			}
			if (anyColoured)
			{
				FinishHighlightedLine();
			}
		}

		/// <summary>
		/// Span kind for split-word-anchor rendering. Tag = &lt;a…&gt;/&lt;/a&gt;
		/// (red), Inside = the anchor's link text (DarkCyan), Tail = the orphaned
		/// run after &lt;/a&gt; (DarkGreen).
		/// </summary>
		internal enum SplitSpanKind { Tag, Inside, Tail, BrSpacer }

		/// <summary>
		/// A coloured span within a split-word excerpt: a half-open range
		/// [Start, Start+Length) and its kind. Computed by the (pure, tested)
		/// caller — ContentQualityTriage.ComputeSplitWordSpans — so this primitive
		/// stays a dumb renderer with no parsing logic of its own.
		/// </summary>
		internal readonly record struct SplitSpan(int Start, int Length, SplitSpanKind Kind);

		/// <summary>
		/// Writes <paramref name="text"/> rendering each precomputed
		/// <paramref name="spans"/> in its split-word colour (tags red, inside
		/// DarkCyan, tail DarkGreen) and everything between/around them uncoloured.
		/// Spans must be ordered by Start and must not overlap (the caller
		/// guarantees this). No trailing newline — caller writes WriteBlank(),
		/// matching the WriteWith*Highlight contract. If <paramref name="spans"/>
		/// is empty the text is written verbatim.
		/// </summary>
		internal static void WriteWithSplitWordHighlight(string text, IReadOnlyList<SplitSpan> spans)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			if (spans is null || spans.Count == 0)
			{
				WriteWrapped(text, coloured: false, HighlightBgTriage, HighlightFgTriage);
				return;
			}

			var pos = 0;
			var anyColoured = false;
			foreach (var span in spans)
			{
				// Defensive clamp — a span past the end (excerpt truncation) is
				// skipped rather than throwing.
				if (span.Start < pos || span.Start >= text.Length || span.Length <= 0)
				{
					continue;
				}
				var end = Math.Min(span.Start + span.Length, text.Length);

				// Plain gap before this span.
				if (span.Start > pos)
				{
					WriteWrapped(text[pos..span.Start], coloured: false, HighlightBgTriage, HighlightFgTriage);
				}

				var (bg, fg) = span.Kind switch
				{
					SplitSpanKind.Tag => (HighlightBgTriage, HighlightFgTriage),
					SplitSpanKind.Inside => (HighlightBgSplitInside, HighlightFgSplitInside),
					SplitSpanKind.Tail => (HighlightBgSplitTail, HighlightFgSplitTail),
					SplitSpanKind.BrSpacer => (HighlightBgHrefMatch, HighlightFgHrefMatch),
					_ => (HighlightBgTriage, HighlightFgTriage),
				};
				WriteWrapped(text[span.Start..end], coloured: true, bg, fg);
				anyColoured = true;
				pos = end;
			}

			// Plain remainder after the last span.
			if (pos < text.Length)
			{
				WriteWrapped(text[pos..], coloured: false, HighlightBgTriage, HighlightFgTriage);
			}
			if (anyColoured)
			{
				FinishHighlightedLine();
			}
		}

		/// <summary>
		/// Foreground colour kinds for a MISPLACED_ANCHOR_EMPTY excerpt: the anchor
		/// Structure (&lt;a / &gt; / &lt;/a&gt;), the Href attribute, other Attr(ibutes),
		/// the injected Marker, and surrounding Context.
		/// </summary>
		internal enum EmptyAnchorSpanKind { Structure, Href, Attr, Marker, Context }

		internal readonly record struct EmptyAnchorSpan(int Start, int Length, EmptyAnchorSpanKind Kind);

		/// <summary>
		/// Paints a MISPLACED_ANCHOR_EMPTY excerpt (marker already injected) from the
		/// foreground colour map computed by ContentQualityTriage.ComputeEmptyAnchorSpans:
		/// the anchor structure (&lt;a / &gt; / &lt;/a&gt;) Red, the href target DarkYellow,
		/// other attributes and the surrounding markup DarkGray (dimmed, so the eye lands on
		/// the structure + href), and the injected [WCAG-VIOLATION-EMPTY-LINK] marker in the
		/// white-on-DarkBlue WCAG scheme. The coloured parts are foreground-only (no
		/// background to smear on wrap); only the marker carries a fill, routed through
		/// WriteWrapped so a mid-marker wrap stays clean. No trailing newline — the caller
		/// writes WriteBlank(). Empty/absent spans → plain text.
		/// </summary>
		internal static void WriteWithEmptyAnchorSpans(string text, IReadOnlyList<EmptyAnchorSpan> spans)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			if (spans is null || spans.Count == 0)
			{
				Console.Write(text);
				return;
			}

			var pos = 0;
			foreach (var span in spans)
			{
				if (span.Start < pos || span.Start >= text.Length || span.Length <= 0)
				{
					continue;
				}
				var end = Math.Min(span.Start + span.Length, text.Length);

				// Gap before this span → dimmed context.
				if (span.Start > pos)
				{
					WriteAnchorRun(text[pos..span.Start], ConsoleColor.DarkGray, marker: false);
				}

				switch (span.Kind)
				{
					case EmptyAnchorSpanKind.Structure:
						WriteAnchorRun(text[span.Start..end], ConsoleColor.Red, marker: false);
						break;
					case EmptyAnchorSpanKind.Href:
						WriteAnchorRun(text[span.Start..end], ConsoleColor.DarkYellow, marker: false);
						break;
					case EmptyAnchorSpanKind.Marker:
						WriteAnchorRun(text[span.Start..end], default, marker: true);
						break;
					default:   // Attr, Context — both dimmed
						WriteAnchorRun(text[span.Start..end], ConsoleColor.DarkGray, marker: false);
						break;
				}
				pos = end;
			}

			if (pos < text.Length)
			{
				WriteAnchorRun(text[pos..], ConsoleColor.DarkGray, marker: false);
			}
		}

		private static void WriteAnchorRun(string s, ConsoleColor fg, bool marker)
		{
			if (string.IsNullOrEmpty(s))
			{
				return;
			}
			if (marker)
			{
				// Background fill → reuse the wrap-safe path so a mid-marker wrap does not
				// smear DarkBlue to the row edge.
				WriteWrapped(s, coloured: true, HighlightBgWcag, HighlightFgWcag);
				return;
			}

			Console.ForegroundColor = fg;
			Console.Write(s);
			Console.ResetColor();
		}

		// Foreground-only dim write (DarkGray), no background fill — the console
		// wraps it naturally and there is nothing to smear at a row boundary.
		private static void WriteDimmed(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return;
			}

			Console.ForegroundColor = ConsoleColor.DarkGray;
			Console.Write(s);
			Console.ResetColor();
		}

		/// <summary>
		/// One typographic quote glyph in a quote-issue excerpt. IsTrigger marks the
		/// glyph the finding is actually about (painted as the offender); all other
		/// quote glyphs are context. Computed by the pure
		/// ContentQualityTriage.ComputeQuoteSpans; this layer only paints.
		/// </summary>
		internal readonly record struct QuoteHighlightSpan(int Start, int Length, bool IsTrigger);

		/// <summary>
		/// Live-triage quote highlight: the trigger glyph in white-on-red
		/// (HighlightBgTriage — same offender colour as the anchor findings), all
		/// other quote glyphs in white-on-blue (HighlightBgWcag — "checks out,
		/// context"). Non-quote text is plain. Trigger vs context is carried by the
		/// background fill, so the distinction survives even where hue cannot be told
		/// apart. Spans must be ordered by Start and non-overlapping (the pure caller
		/// guarantees this). No trailing newline — caller writes WriteBlank().
		/// </summary>
		internal static void WriteWithQuoteSpans(string text, IReadOnlyList<QuoteHighlightSpan> spans)
			=> WriteWithQuoteSpansCore(text, spans,
				triggerBg: HighlightBgTriage, triggerFg: HighlightFgTriage,
				contextBg: HighlightBgWcag, contextFg: HighlightFgWcag,
				dimGaps: true);

		/// <summary>
		/// Review-pass variant of <see cref="WriteWithQuoteSpans"/>: trigger glyph in
		/// the review emphasis colour (HighlightBgReview, amber), context quotes in
		/// the same blue. Same span grammar so live and review mark identical glyphs.
		/// </summary>
		internal static void WriteWithQuoteSpansMuted(string text, IReadOnlyList<QuoteHighlightSpan> spans)
			=> WriteWithQuoteSpansCore(text, spans,
				triggerBg: HighlightBgReview, triggerFg: HighlightFgReview,
				contextBg: HighlightBgWcag, contextFg: HighlightFgWcag,
				dimGaps: true);

		private static void WriteWithQuoteSpansCore(
			string text, IReadOnlyList<QuoteHighlightSpan> spans,
			ConsoleColor triggerBg, ConsoleColor triggerFg,
			ConsoleColor contextBg, ConsoleColor contextFg,
			bool padHighlight = false, bool dimGaps = false)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			if (spans is null || spans.Count == 0)
			{
				// No spans — write verbatim, no colour. (Empty block, or a finding
				// whose glyphs could not be located: degrade to plain, never wrong.)
				WriteWrapped(text, coloured: false, contextBg, contextFg);
				return;
			}

			var pos = 0;
			var anyColoured = false;
			var inside = false;   // dimGaps: toggles at each quote glyph (text between a
			                      // glyph pair is "inside" a quote, kept as-is; outside dims)
			foreach (var span in spans)
			{
				// Defensive clamp — a span past the end is skipped, not thrown.
				if (span.Start < pos || span.Start >= text.Length || span.Length <= 0)
				{
					continue;
				}
				var end = Math.Min(span.Start + span.Length, text.Length);

				// Gap before this quote glyph: dimmed when it lies outside any quote
				// (dimGaps && !inside), otherwise plain. Inside-quote text stays as-is.
				if (span.Start > pos)
				{
					if (dimGaps && !inside)
					{
						WriteDimmed(text[pos..span.Start]);
					}
					else
					{
						WriteWrapped(text[pos..span.Start], coloured: false, contextBg, contextFg);
					}
				}

				var (bg, fg) = span.IsTrigger ? (triggerBg, triggerFg) : (contextBg, contextFg);
				// padHighlight: emit the glyph plus a trailing space *in the highlight
				// colours* so a glyph drawn wider than its cell (e.g. the fi/fl
				// ligatures, U+FB0x) has a same-coloured cell to overflow into,
				// rather than spilling onto the next char (overlap) or being clobbered
				// by the following cell's fill (swallowed). The space is render-only —
				// `text` and the span offsets are untouched, so multi-glyph span math
				// is unaffected.
				var rendered = padHighlight ? text[span.Start..end] + " " : text[span.Start..end];
				WriteWrapped(rendered, coloured: true, bg, fg);
				anyColoured = true;
				pos = end;
				inside = !inside;   // crossed a quote glyph → flip inside/outside
			}

			// Remainder after the last glyph: dimmed when outside a quote, else plain.
			if (pos < text.Length)
			{
				if (dimGaps && !inside)
				{
					WriteDimmed(text[pos..]);
				}
				else
				{
					WriteWrapped(text[pos..], coloured: false, contextBg, contextFg);
				}
			}
			if (anyColoured)
			{
				FinishHighlightedLine();
			}
		}

		/// <summary>
		/// Span kind for ADJACENT_ANCHOR same-href hint rendering. The
		/// detector flags any literal &lt;/a&gt;&lt;a collision; the visualization
		/// layer surfaces additional structural facts via colour so the operator
		/// can interpret design-vs-defect without the tool claiming a verdict.
		/// Spans:
		///   Collision      — the literal "&lt;/a&gt;&lt;a" 6-char defect shape (red)
		///   HrefBaseline   — any anchor's href value (mild gray visibility)
		///   HrefMatch      — anchor hrefs that match across the adjacent pair (DarkMagenta)
		///   HrefPlaceholder— href="#" placeholder (DarkYellow — "JS-trigger, be aware")
		///   TitleMatch     — title attributes matching across pair (DarkCyan)
		///   AnchorTextHint — anchor inner text when paired same-href (DarkGray, set apart)
		/// </summary>
		internal enum AdjacentHintSpanKind
		{
			Collision,
			HrefBaseline,
			HrefMatch,
			HrefPlaceholder,
			TitleMatch,
			AnchorTextHint,
		}

		/// <summary>
		/// A coloured span within an ADJACENT_ANCHOR excerpt. Computed by the
		/// (pure, tested) caller — ContentQualityTriage.ComputeAdjacentHintSpans —
		/// so this primitive stays a dumb renderer with no parsing of its own.
		/// </summary>
		internal readonly record struct AdjacentHintSpan(int Start, int Length, AdjacentHintSpanKind Kind);

		/// <summary>
		/// Live-triage variant of the ADJACENT_ANCHOR hint renderer. See
		/// <see cref="AdjacentHintSpanKind"/> for the colour grammar.
		/// </summary>
		internal static void WriteWithAdjacentAnchorHintHighlight(string text, IReadOnlyList<AdjacentHintSpan> spans)
			=> WriteWithAdjacentAnchorHintHighlightCore(text, spans, muted: false);

		/// <summary>
		/// Review-pass variant of the ADJACENT_ANCHOR hint renderer. The Collision
		/// span uses the review-pass colour pair (DarkYellow/Black) instead of the
		/// live triage red; all OTHER hint spans use their normal palette since
		/// they are descriptive (not alarm-shaped) and read the same regardless of
		/// pass. This deliberately differs from the live triage variant only at the
		/// Collision span — matching how WriteWithAnchorTagHighlight/Muted differ.
		/// </summary>
		internal static void WriteWithAdjacentAnchorHintHighlightMuted(string text, IReadOnlyList<AdjacentHintSpan> spans)
			=> WriteWithAdjacentAnchorHintHighlightCore(text, spans, muted: true);

		private static void WriteWithAdjacentAnchorHintHighlightCore(
			string text, IReadOnlyList<AdjacentHintSpan> spans, bool muted)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			if (spans is null || spans.Count == 0)
			{
				WriteWrapped(text, coloured: false, HighlightBgTriage, HighlightFgTriage);
				return;
			}

			var collisionBg = muted ? HighlightBgReview : HighlightBgTriage;
			var collisionFg = muted ? HighlightFgReview : HighlightFgTriage;

			var pos = 0;
			var anyColoured = false;
			foreach (var span in spans)
			{
				// Defensive clamp — a span past the end (excerpt truncation) is
				// skipped rather than throwing. Matches WriteWithSplitWordHighlight.
				if (span.Start < pos || span.Start >= text.Length || span.Length <= 0)
				{
					continue;
				}
				var end = Math.Min(span.Start + span.Length, text.Length);

				// Plain gap before this span.
				if (span.Start > pos)
				{
					WriteWrapped(text[pos..span.Start], coloured: false, HighlightBgTriage, HighlightFgTriage);
				}

				var (bg, fg) = span.Kind switch
				{
					AdjacentHintSpanKind.Collision => (collisionBg, collisionFg),
					AdjacentHintSpanKind.HrefBaseline => (HighlightBgHrefBaseline, HighlightFgHrefBaseline),
					AdjacentHintSpanKind.HrefMatch => (HighlightBgHrefMatch, HighlightFgHrefMatch),
					AdjacentHintSpanKind.HrefPlaceholder => (HighlightBgHrefPlaceholder, HighlightFgHrefPlaceholder),
					AdjacentHintSpanKind.TitleMatch => (HighlightBgTitleMatch, HighlightFgTitleMatch),
					AdjacentHintSpanKind.AnchorTextHint => (HighlightBgAnchorTextHint, HighlightFgAnchorTextHint),
					_ => (collisionBg, collisionFg),
				};
				WriteWrapped(text[span.Start..end], coloured: true, bg, fg);
				anyColoured = true;
				pos = end;
			}

			// Plain remainder after the last span.
			if (pos < text.Length)
			{
				WriteWrapped(text[pos..], coloured: false, HighlightBgTriage, HighlightFgTriage);
			}
			if (anyColoured)
			{
				FinishHighlightedLine();
			}
		}

		internal static readonly HashSet<char> HighlightQuoteChars =
		[
			'\u201C', '\u201D', '\u201E', '\u201F', // double quotes
			'\u2018', '\u2019', '\u201A',            // single quotes
			'\u00AB', '\u00BB',                      // guillemets
			'\u2039', '\u203A',                      // single guillemets
			'\u275D', '\u275E',                      // heavy quotes
		];

		// Typographic-ligature codepoints flagged by ContentQuality.CheckLigatures.
		// Kept in sync with ContentQuality.Ligatures (the detection-side dictionary);
		// this set is the render-side mirror used to locate the glyphs in an excerpt
		// for highlighting. U+FB00–U+FB06: ff fi fl ffi ffl ſt st.
		internal static readonly HashSet<char> HighlightLigatureChars =
		[
			'\uFB00', '\uFB01', '\uFB02', '\uFB03', '\uFB04', '\uFB05', '\uFB06',
		];

		/// <summary>
		/// Writes <paramref name="text"/> with each ligature span rendered in the
		/// live-triage emphasis scheme (red-on-white), the rest plain. Mirrors
		/// <see cref="WriteWithQuoteSpans"/>; ligatures have no trigger/context
		/// distinction, so every span uses the single emphasis colour. The caller
		/// (ContentQualityTriage.ComputeLigatureSpans) owns span computation.
		/// </summary>
		internal static void WriteWithLigatureSpans(string text, IReadOnlyList<QuoteHighlightSpan> spans)
			=> WriteWithQuoteSpansCore(text, spans,
				triggerBg: HighlightBgTriage, triggerFg: HighlightFgTriage,
				contextBg: HighlightBgTriage, contextFg: HighlightFgTriage,
				padHighlight: true);

		/// <summary>
		/// Review-pass variant of <see cref="WriteWithLigatureSpans"/>: ligature
		/// glyphs in the muted (amber) review scheme. Same span grammar so live and
		/// review mark identical glyphs.
		/// </summary>
		internal static void WriteWithLigatureSpansMuted(string text, IReadOnlyList<QuoteHighlightSpan> spans)
			=> WriteWithQuoteSpansCore(text, spans,
				triggerBg: HighlightBgReview, triggerFg: HighlightFgReview,
				contextBg: HighlightBgReview, contextFg: HighlightFgReview,
				padHighlight: true);

		// ── Excerpt highlighting (pre-computed spans) ─────────────────────────
		// Renders a "  Excerpt: " line where already-windowed text carries one or
		// more highlighted spans, given in excerpt-LOCAL coordinates (i.e. offsets
		// into windowedText, not the original). The caller (ConsoleTriage) owns the
		// span math — window computation, projection, clamping, sorting, coalescing
		// — which is shared with and tested via RenderHighlightedExcerpt. This
		// method owns only the coloured emit, keeping Console out of the flow class.
		// Spans must be non-overlapping and sorted ascending by Start; the caller
		// guarantees this (coalesced). Leading/trailing ellipses signal that the
		// window was clipped from the original text on that side.

		/// <summary>
		/// Writes a triage excerpt line: the "  Excerpt: " prefix, then
		/// <paramref name="windowedText"/> with each span in
		/// <paramref name="localSpans"/> rendered red-on-white and the rest in the
		/// default colours, bracketed by ellipses where the window was clipped.
		/// Closes with a newline. Spans are excerpt-local, non-overlapping, sorted.
		/// </summary>
		internal static void WriteExcerptWithSpans(
			string windowedText, IReadOnlyList<(int Start, int Length)> localSpans,
			bool leadingEllipsis, bool trailingEllipsis)
			=> WriteExcerptWithSpansCore(
				windowedText, localSpans, leadingEllipsis, trailingEllipsis,
				HighlightBgTriage, HighlightFgTriage);

		private static void WriteExcerptWithSpansCore(
			string windowedText, IReadOnlyList<(int Start, int Length)> localSpans,
			bool leadingEllipsis, bool trailingEllipsis,
			ConsoleColor bg, ConsoleColor fg)
		{
			Console.Write("  Excerpt: ");
			if (leadingEllipsis)
			{
				Console.Write("…");
			}

			int cursor = 0;
			if (localSpans != null)
			{
				foreach (var (spanStart, spanLength) in localSpans)
				{
					if (spanStart > cursor)
					{
						Console.Write(windowedText.Substring(cursor, spanStart - cursor));
					}

					Console.BackgroundColor = bg;
					Console.ForegroundColor = fg;
					Console.Write(windowedText.Substring(spanStart, spanLength));
					Console.ResetColor();
					cursor = spanStart + spanLength;
				}
			}
			if (cursor < windowedText.Length)
			{
				Console.Write(windowedText[cursor..]);
			}

			if (trailingEllipsis)
			{
				Console.Write("…");
			}

			Console.WriteLine();
		}
	}
}
