using System.Text;

namespace Crawler
{
	/// <summary>
	/// Standardised triage decision flow for console-based interactive prompts.
	/// Provides the unified look-and-feel and the unified keypress semantics
	/// (re-prompt on invalid, explicit cancel keys, verbose-labelled choices).
	///
	/// Currently used by:
	///   - ContentQualityTriage           (per-finding content-quality decisions)
	///   - SpellTriage.RunSpellCheckTriage (per-error spelling decisions,
	///                                    including the [W] Wontfix sub-menu
	///                                    and the free-text follow-ups)
	///   - InteractiveTriage.CheckSnapshotIntegrity (incomplete-snapshot dialog)
	///   - InteractiveTriage.ResolveOrphans         (per-orphan resolution)
	///
	/// Intentionally NOT migrated (each is a setup-time or session-start prompt
	/// where the cost of migration outweighs the consistency benefit):
	///   - InteractiveTriage.PromptForSnapshotChoice and
	///     PromptForEmptyDownloadRecovery — "any-key-aborts" semantic, different
	///     shape from Ask's re-prompt-on-invalid model.
	///   - InteractiveTriage.PromptForCleanSweep — deliberately conservative
	///     "Y to confirm, anything-else as no" for destructive opt-in.
	///   - SpellTriage.ShowKnownTypeMenu — numeric picker rendering a
	///     vertical list of long-text templates from TriageTicketKnownTypes
	///     config. The vertical layout is correct for the content shape
	///     (each template is a full sentence); Ask's one-line ComposeChoicesLine
	///     rendering would be unreadable here. Keypress semantics
	///     (re-prompt-on-invalid, explicit [Q] cancel) are conformant, so
	///     the only thing migration would consolidate is the
	///     keypress loop itself — not worth the substrate escape hatch
	///     (e.g. a "suppress choices line" flag on Ask) that would be needed
	///     to preserve the rendering.
	///
	/// Naming: prefixed `Console` because the implementation is console-specific.
	/// A future GUI layer (WPF, etc.) would add a sibling class (e.g. WpfTriage)
	/// using the same TriageAction enum and TriageFinding record but different
	/// rendering and input mechanics. The shared types (TriageAction, TriageFinding,
	/// ChoiceOption) are UI-agnostic on purpose.
	///
	/// Layered design:
	///   Layer A — pure helpers (no I/O): ResolveDefault, IsValidChoice, IsYesKey,
	///             ComposePromptSuffix, ComposeChoicesLine, RenderFindingHeader,
	///             ComputeExcerptWindow, RenderHighlightedExcerpt.
	///             Heavily unit-tested.
	///   Layer B — I/O orchestration:    Ask, AskYesNo, AskNavigation, ShowFinding.
	///             Thin wrappers calling ConsoleUi for output + Console.ReadKey for
	///             input. The non-unit-testable parts (Console.ReadKey itself and
	///             the retry loop on invalid input) are deliberately small.
	/// </summary>
	internal static class ConsoleTriage
	{
		// ── Constants ────────────────────────────────────────────────────────

		/// <summary>Half-width of the excerpt window rendered around the highlight.</summary>
		internal const int ExcerptHalfWidth = 60;

		/// <summary>Maximum length of the rendered excerpt (highlight markers included).</summary>
		internal const int ExcerptMaxLength = 180;

		// ── Layer A: pure helpers (testable) ─────────────────────────────────

		/// <summary>
		/// Resolves a keypress to the effective key, applying the default-key
		/// convention: Enter (or no keypress) returns <paramref name="defaultKey"/>
		/// if one is configured; otherwise returns <paramref name="pressed"/>
		/// unchanged.
		/// </summary>
		internal static ConsoleKey ResolveDefault(ConsoleKey pressed, ConsoleKey? defaultKey)
		{
			if (pressed == ConsoleKey.Enter && defaultKey.HasValue)
			{
				return defaultKey.Value;
			}

			return pressed;
		}

		/// <summary>True if <paramref name="key"/> is a member of <paramref name="validKeys"/>.</summary>
		internal static bool IsValidChoice(ConsoleKey key, IReadOnlyList<ConsoleKey> validKeys)
		{
			for (int i = 0; i < validKeys.Count; i++)
			{
				if (validKeys[i] == key)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Y/N convention used throughout the crawler: only an explicit 'Y'
		/// counts as yes. Anything else — including Enter, N, any other key —
		/// counts as no. Established by the clean-sweep prompt convention and
		/// applied consistently here.
		/// </summary>
		internal static bool IsYesKey(ConsoleKey key) => key == ConsoleKey.Y;

		/// <summary>
		/// Maps a triage navigation keypress to the corresponding TriageAction.
		/// Standard keys:
		///   N / RightArrow / Enter (when default) → Next
		///   B / LeftArrow                         → Back
		///   S                                     → Skip
		///   Q / Escape                            → Quit
		/// Unknown keys return null so the caller can re-prompt.
		/// </summary>
		internal static TriageAction? MapKeyToNavigationAction(ConsoleKey key)
		{
			return key switch
			{
				ConsoleKey.N or ConsoleKey.RightArrow => TriageAction.Next,
				ConsoleKey.B or ConsoleKey.LeftArrow => TriageAction.Back,
				ConsoleKey.S => TriageAction.Skip,
				ConsoleKey.Q or ConsoleKey.Escape => TriageAction.Quit,
				_ => null,
			};
		}

		/// <summary>
		/// Composes the operator-facing choices line for a list of labelled
		/// options. Example: [F] Fix    [I] Ignore    [S] Skip    [Q] Quit
		///
		/// Format: `[X] Label` — the bracketed keypress letter is visually
		/// separated from the label by a single space. Call sites use
		/// separate-letter labels (e.g. ChoiceOption(D, "Delete entire snapshot")),
		/// not the embedded-letter style (`[D]elete`); the space-after-bracket
		/// reads correctly with the separate-letter convention. Between
		/// adjacent entries the spacing is fixed at 4 spaces for a stable
		/// visual rhythm across triages.
		///
		/// This is the only prompt-rendering helper. The compact
		/// `[Y/n/s]` form was removed in favour of always-verbose labelled
		/// choices — operator interruption (colleagues asking about lunch,
		/// phone calls, switching windows) is the rule not the exception,
		/// and self-documenting prompts cost less to re-focus on than a
		/// compact form whose meaning has to be recalled.
		/// </summary>
		internal static string ComposeChoicesLine(IReadOnlyList<ChoiceOption> choices)
		{
			var sb = new StringBuilder();
			for (int i = 0; i < choices.Count; i++)
			{
				if (i > 0)
				{
					sb.Append("    ");
				}

				sb.Append('[').Append(KeyToLetter(choices[i].Key)).Append("] ");
				sb.Append(choices[i].Label);
			}
			return sb.ToString();
		}

		/// <summary>
		/// Composes the standard triage step header. Format:
		/// "&lt;TITLE&gt; — step N of M".
		/// Used at the top of every per-finding triage screen so the operator
		/// always knows where they are in the queue.
		/// </summary>
		internal static string RenderFindingHeader(string title, int step, int totalSteps)
		{
			if (totalSteps <= 0)
			{
				return title;
			}

			return $"{title} — step {step} of {totalSteps}";
		}

		/// <summary>
		/// Computes the start/length window of an excerpt centred on a highlight,
		/// clamped to the bounds of the underlying text. The window is at most
		/// ExcerptMaxLength characters long and balances ExcerptHalfWidth on
		/// either side of the highlight when possible.
		/// </summary>
		internal static (int Start, int Length) ComputeExcerptWindow(
			string text, int highlightStart, int highlightLength)
		{
			if (string.IsNullOrEmpty(text))
			{
				return (0, 0);
			}

			int textLen = text.Length;
			if (highlightStart < 0)
			{
				highlightStart = 0;
			}

			if (highlightStart > textLen)
			{
				highlightStart = textLen;
			}

			if (highlightLength < 0)
			{
				highlightLength = 0;
			}

			if (highlightStart + highlightLength > textLen)
			{
				highlightLength = textLen - highlightStart;
			}

			int start = highlightStart - ExcerptHalfWidth;
			int end = highlightStart + highlightLength + ExcerptHalfWidth;
			if (start < 0)
			{
				start = 0;
			}

			if (end > textLen)
			{
				end = textLen;
			}

			int length = end - start;
			if (length > ExcerptMaxLength)
			{
				length = ExcerptMaxLength;
			}

			return (start, length);
		}

		/// <summary>
		/// Computes an excerpt window that covers all supplied highlight spans.
		/// The window centres on the bounding box of the highlights when they
		/// fit within ExcerptMaxLength; otherwise centres on the first span and
		/// crops. Useful when content-quality findings have several positions
		/// to highlight in the same block (pattern matches, quote pairs).
		/// Spans are clamped to text bounds first.
		/// </summary>
		internal static (int Start, int Length) ComputeExcerptWindow(
			string text, IReadOnlyList<HighlightSpan> spans)
		{
			if (string.IsNullOrEmpty(text))
			{
				return (0, 0);
			}

			if (spans is null || spans.Count == 0)
			{
				return (0, Math.Min(text.Length, ExcerptMaxLength));
			}

			int textLen = text.Length;
			int minStart = int.MaxValue;
			int maxEnd = int.MinValue;
			foreach (var s in spans)
			{
				int sStart = Math.Max(0, Math.Min(s.Start, textLen));
				int sEnd = Math.Max(sStart, Math.Min(s.Start + s.Length, textLen));
				if (sStart < minStart)
				{
					minStart = sStart;
				}

				if (sEnd > maxEnd)
				{
					maxEnd = sEnd;
				}
			}
			if (minStart == int.MaxValue)
			{
				return (0, Math.Min(text.Length, ExcerptMaxLength));
			}

			int start = minStart - ExcerptHalfWidth;
			int end = maxEnd + ExcerptHalfWidth;
			if (start < 0)
			{
				start = 0;
			}

			if (end > textLen)
			{
				end = textLen;
			}

			int length = end - start;
			if (length > ExcerptMaxLength)
			{
				// Span set too wide — fall back to centring on the first span.
				return ComputeExcerptWindow(text, spans[0].Start, spans[0].Length);
			}
			return (start, length);
		}

		/// <summary>
		/// Renders an excerpt with the highlight span wrapped in delimiters
		/// suitable for plain text contexts (logs, tests). The actual coloured
		/// rendering in the live console is performed by
		/// <see cref="WriteHighlightedExcerpt(string,int,int)"/>. Both methods
		/// share the underlying offset math so tests on this function validate
		/// the arithmetic for both paths.
		/// </summary>
		internal static string RenderHighlightedExcerpt(
			string text, int highlightStart, int highlightLength,
			string openMarker = "[", string closeMarker = "]")
		{
			if (string.IsNullOrEmpty(text))
			{
				return string.Empty;
			}

			var (start, length) = ComputeExcerptWindow(text, highlightStart, highlightLength);
			int end = start + length;

			// Local highlight bounds inside the excerpt.
			int hStart = Math.Max(highlightStart - start, 0);
			int hEnd = Math.Min(highlightStart + highlightLength - start, length);
			if (hEnd < hStart)
			{
				hEnd = hStart;
			}

			var sb = new StringBuilder(length + openMarker.Length + closeMarker.Length + 4);
			if (start > 0)
			{
				sb.Append("…");
			}

			if (hStart > 0)
			{
				sb.Append(text, start, hStart);
			}

			sb.Append(openMarker);
			if (hEnd > hStart)
			{
				sb.Append(text, start + hStart, hEnd - hStart);
			}

			sb.Append(closeMarker);
			if (hEnd < length)
			{
				sb.Append(text, start + hEnd, length - hEnd);
			}

			if (end < text.Length)
			{
				sb.Append("…");
			}

			return sb.ToString();
		}

		/// <summary>
		/// Renders an excerpt with multiple highlight spans wrapped in delimiters.
		/// Spans are merged and sorted internally; overlapping spans are coalesced.
		/// Spans outside the computed excerpt window are dropped silently.
		/// </summary>
		internal static string RenderHighlightedExcerpt(
			string text, IReadOnlyList<HighlightSpan> spans,
			string openMarker = "[", string closeMarker = "]")
		{
			if (string.IsNullOrEmpty(text))
			{
				return string.Empty;
			}

			if (spans is null || spans.Count == 0)
			{
				var (s, l) = ComputeExcerptWindow(text, []);
				return BuildPlainExcerpt(text, s, l);
			}

			var (start, length) = ComputeExcerptWindow(text, spans);
			int end = start + length;

			// Project spans into excerpt-local coordinates, clamp, drop out-of-window,
			// sort by start, and coalesce overlaps so the rendered output never has
			// nested or overlapping marker pairs.
			var local = new List<(int LStart, int LEnd)>(spans.Count);
			foreach (var s in spans)
			{
				int sStart = Math.Max(0, s.Start - start);
				int sEnd = Math.Min(length, s.Start + s.Length - start);
				if (sEnd <= sStart)
				{
					continue;
				}

				local.Add((sStart, sEnd));
			}
			local.Sort((a, b) => a.LStart.CompareTo(b.LStart));
			// Coalesce overlapping or touching spans.
			var merged = new List<(int LStart, int LEnd)>();
			foreach (var (lStart, lEnd) in local)
			{
				if (merged.Count > 0 && lStart <= merged[^1].LEnd)
				{
					var (mStart, mEnd) = merged[^1];
					merged[^1] = (mStart, Math.Max(mEnd, lEnd));
				}
				else
				{
					merged.Add((lStart, lEnd));
				}
			}

			var sb = new StringBuilder(length + 16);
			if (start > 0)
			{
				sb.Append("…");
			}

			int cursor = 0;
			foreach (var (lStart, lEnd) in merged)
			{
				if (lStart > cursor)
				{
					sb.Append(text, start + cursor, lStart - cursor);
				}

				sb.Append(openMarker);
				sb.Append(text, start + lStart, lEnd - lStart);
				sb.Append(closeMarker);
				cursor = lEnd;
			}
			if (cursor < length)
			{
				sb.Append(text, start + cursor, length - cursor);
			}

			if (end < text.Length)
			{
				sb.Append("…");
			}

			return sb.ToString();
		}

		private static string BuildPlainExcerpt(string text, int start, int length)
		{
			if (length <= 0)
			{
				return string.Empty;
			}

			var sb = new StringBuilder(length + 2);
			if (start > 0)
			{
				sb.Append("…");
			}

			sb.Append(text, start, length);
			if (start + length < text.Length)
			{
				sb.Append("…");
			}

			return sb.ToString();
		}

		// ── Layer B: I/O orchestration (lightly tested) ──────────────────────
		//
		// The methods below combine Layer A logic with Console.ReadKey and
		// retry-on-invalid loops. Operators eyeball-verify their behaviour;
		// the surface is intentionally small (~5 lines of wiring per method).

		/// <summary>
		/// Prompts the operator with a question and waits for one of the keys
		/// in <paramref name="choices"/>. Invalid keypresses re-prompt with a
		/// warning. Enter selects <paramref name="defaultKey"/> when provided.
		///
		/// The prompt renders verbose-labelled choices —
		/// e.g. `prompt [T] Ticket  [W] Wontfix  [S] Skip  [Q] Quit > ` — so
		/// operators returning to the screen after interruption can re-focus
		/// without remembering letter-to-meaning mappings.
		///
		/// Optional <paramref name="continueOnKey"/> callback enables keys that
		/// don't end the prompt loop. When the operator presses a valid key,
		/// the callback is consulted; if it returns true, the loop continues
		/// (e.g. `[M] More` reveals additional content then re-prompts). When
		/// it returns false (or the callback is null), Ask returns the key.
		/// Used by content-quality and spell-check triages for the `[M] More`
		/// expand-context affordance.
		/// </summary>
		public static ConsoleKey Ask(
			string prompt,
			IReadOnlyList<ChoiceOption> choices,
			ConsoleKey? defaultKey = null,
			Func<ConsoleKey, bool>? continueOnKey = null)
		{
			if (choices is null || choices.Count == 0)
			{
				throw new ArgumentException("choices must contain at least one option.", nameof(choices));
			}

			var validKeys = new ConsoleKey[choices.Count];
			for (int i = 0; i < choices.Count; i++)
			{
				validKeys[i] = choices[i].Key;
			}

			var promptLine = string.IsNullOrEmpty(prompt)
				? $"{ComposeChoicesLine(choices)} > "
				: $"{prompt} {ComposeChoicesLine(choices)} > ";

			while (true)
			{
				var pressed = ConsoleUi.ReadKey(promptLine);
				var resolved = ResolveDefault(pressed, defaultKey);
				if (!IsValidChoice(resolved, validKeys))
				{
					ConsoleUi.WriteWarning($"Invalid choice — please press one of: {ComposeChoicesLine(choices)}");
					continue;
				}
				// Optional "stay in the loop" hook — handles [M] More-style keys.
				if (continueOnKey != null && continueOnKey(resolved))
				{
					continue;
				}

				return resolved;
			}
		}

		/// <summary>
		/// Y/N convention prompt. Renders verbose-labelled choices:
		/// `prompt [Y] Yes  [N] No > `. Returns true only on explicit 'Y'.
		/// Anything else — including Enter, N, and any other key — returns
		/// false. Established by the clean-sweep prompt convention and applied uniformly.
		/// </summary>
		public static bool AskYesNo(string prompt)
		{
			var key = Ask(prompt,
				[new ChoiceOption(ConsoleKey.Y, "Yes"),
				 new ChoiceOption(ConsoleKey.N, "No")]);
			return IsYesKey(key);
		}

		/// <summary>
		/// Formats the closing summary line for a triage review pass, shared by
		/// the spelling and content-quality reviews so the arithmetic is pinned
		/// in one place. When <paramref name="quit"/> is true the operator ended
		/// the review early, so the line reports how many of the total were seen;
		/// otherwise the full count is implied and the remainder is reported with
		/// the caller's noun (<paramref name="keptLabel"/> — "kept" for spelling,
		/// "left as-is" for content quality). Pure — no Console.
		/// </summary>
		public static string FormatReviewSummary(
			int discarded, int reviewed, int total, bool quit, string keptLabel)
			=> quit
				? $"Review ended early — {discarded} discarded, {reviewed} of {total} reviewed."
				: $"Review complete — {discarded} discarded, {total - discarded} {keptLabel}.";

		/// <summary>
		/// Standard navigation prompt: Next / Back / Skip / Quit, with optional
		/// extras layered on top. Returns the chosen TriageAction (for the four
		/// standard keys) or the ConsoleKey itself wrapped — callers handle
		/// extra keys by passing them in <paramref name="extras"/> and
		/// inspecting the returned key.
		/// </summary>
		public static (TriageAction? Action, ConsoleKey Key) AskNavigation(
			string prompt,
			bool canBack = true,
			IReadOnlyList<ChoiceOption>? extras = null)
		{
			var choices = new List<ChoiceOption> { new(ConsoleKey.N, "Next") };
			if (canBack)
			{
				choices.Add(new ChoiceOption(ConsoleKey.B, "Back"));
			}

			choices.Add(new ChoiceOption(ConsoleKey.S, "Skip"));
			choices.Add(new ChoiceOption(ConsoleKey.Q, "Quit"));
			if (extras is { Count: > 0 })
			{
				choices.AddRange(extras);
			}

			var key = Ask(prompt, choices, defaultKey: ConsoleKey.N);
			return (MapKeyToNavigationAction(key), key);
		}

		/// <summary>
		/// Reads a line of free text from the operator, after writing the
		/// supplied prompt. Used for follow-up details like the comment on
		/// `[W] Wontfix` decisions. Empty input is allowed and returned as
		/// the empty string.
		/// </summary>
		public static string AskFreeText(string prompt) => ConsoleUi.ReadLine(prompt);

		/// <summary>
		/// Renders a TriageFinding using the standard layout: a header line, the
		/// metadata fields, the highlighted excerpt, then a blank line ready for
		/// the choices/prompt that follows. The rendered finding is purely
		/// informational; the caller follows it with an <see cref="Ask"/> or
		/// <see cref="AskNavigation"/> call to actually collect the decision.
		/// </summary>
		public static void ShowFinding(TriageFinding finding)
		{
			ConsoleUi.WriteDivider();
			ConsoleUi.WriteHeader(RenderFindingHeader(finding.IssueType, finding.StepNumber, finding.TotalSteps));
			ConsoleUi.WriteField("File", finding.FilePath);
			if (!string.IsNullOrEmpty(finding.BlockTag))
			{
				ConsoleUi.WriteField("Block", finding.BlockTag);
			}

			ConsoleUi.WriteField("Detail", finding.Detail);

			// Excerpt — coloured when there are highlight spans, plain otherwise.
			if (!string.IsNullOrEmpty(finding.BlockText))
			{
				if (finding.Highlights is { Count: > 0 })
				{
					WriteHighlightedExcerpt(finding.BlockText, finding.Highlights);
				}
				else
				{
					ConsoleUi.WriteField("Excerpt", finding.BlockText.Length > ExcerptMaxLength
						? finding.BlockText[..ExcerptMaxLength] + "…"
						: finding.BlockText);
				}
			}

			ConsoleUi.WriteBlank();
		}

		/// <summary>
		/// Writes an excerpt of <paramref name="text"/> with the highlight span
		/// rendered in the triage highlight scheme (red-on-white), the surrounding
		/// context in the default console colours, closed with a newline. Computes
		/// the excerpt window and the single excerpt-local span here, then hands the
		/// coloured emit to <see cref="ConsoleUi.WriteExcerptWithSpans"/> so Console
		/// stays out of this flow class.
		/// </summary>
		public static void WriteHighlightedExcerpt(
			string text, int highlightStart, int highlightLength)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			var (start, length) = ComputeExcerptWindow(text, highlightStart, highlightLength);
			int end = start + length;
			int hStart = Math.Max(highlightStart - start, 0);
			int hEnd = Math.Min(highlightStart + highlightLength - start, length);
			if (hEnd < hStart)
			{
				hEnd = hStart;
			}

			// Hand the windowed substring and the single excerpt-local span to the
			// presentation layer, which owns the coloured emit. Empty highlight →
			// no span (plain windowed text).
			var windowed = text.Substring(start, length);
			var spans = hEnd > hStart
				? new[] { (hStart, hEnd - hStart) }
				: System.Array.Empty<(int, int)>();
			ConsoleUi.WriteExcerptWithSpans(windowed, spans, start > 0, end < text.Length);
		}

		/// <summary>
		/// Writes an excerpt of <paramref name="text"/> with multiple highlight
		/// spans rendered in the triage highlight scheme (red-on-white). Spans are
		/// projected to excerpt-local coordinates, clamped, sorted, and coalesced
		/// here so the output never has overlapping or nested highlights, then the
		/// coloured emit is delegated to
		/// <see cref="ConsoleUi.WriteExcerptWithSpans"/>.
		/// </summary>
		public static void WriteHighlightedExcerpt(
			string text, IReadOnlyList<HighlightSpan> spans)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			if (spans is null || spans.Count == 0)
			{
				var (s, l) = ComputeExcerptWindow(text, []);
				ConsoleUi.WriteExcerptWithSpans(
					text.Substring(s, l), System.Array.Empty<(int, int)>(),
					s > 0, s + l < text.Length);
				return;
			}

			var (start, length) = ComputeExcerptWindow(text, spans);
			int end = start + length;

			// Project, clamp, sort, coalesce — same logic as RenderHighlightedExcerpt.
			var local = new List<(int LStart, int LEnd)>(spans.Count);
			foreach (var s in spans)
			{
				int sStart = Math.Max(0, s.Start - start);
				int sEnd = Math.Min(length, s.Start + s.Length - start);
				if (sEnd <= sStart)
				{
					continue;
				}

				local.Add((sStart, sEnd));
			}
			local.Sort((a, b) => a.LStart.CompareTo(b.LStart));
			var merged = new List<(int Start, int Length)>();
			foreach (var (lStart, lEnd) in local)
			{
				if (merged.Count > 0 && lStart <= merged[^1].Start + merged[^1].Length)
				{
					var prev = merged[^1];
					merged[^1] = (prev.Start, Math.Max(prev.Start + prev.Length, lEnd) - prev.Start);
				}
				else
				{
					merged.Add((lStart, lEnd - lStart));
				}
			}

			// Emit is the presentation layer's job; merged spans are excerpt-local,
			// non-overlapping, and sorted — exactly what the primitive expects.
			ConsoleUi.WriteExcerptWithSpans(
				text.Substring(start, length), merged, start > 0, end < text.Length);
		}

		// ── Internal utilities ───────────────────────────────────────────────

		/// <summary>
		/// Maps a ConsoleKey to its display letter. Letter keys map to their
		/// natural character (A-Z); arrow keys and special keys use compact
		/// glyphs (←↑→↓⏎⎋).
		/// </summary>
		internal static char KeyToLetter(ConsoleKey key)
		{
			return key switch
			{
				>= ConsoleKey.A and <= ConsoleKey.Z => (char)('A' + (key - ConsoleKey.A)),
				>= ConsoleKey.D0 and <= ConsoleKey.D9 => (char)('0' + (key - ConsoleKey.D0)),
				ConsoleKey.Enter => '⏎',
				ConsoleKey.Escape => '⎋',
				ConsoleKey.LeftArrow => '←',
				ConsoleKey.RightArrow => '→',
				ConsoleKey.UpArrow => '↑',
				ConsoleKey.DownArrow => '↓',
				_ => '?',
			};
		}
	}

	/// <summary>
	/// Standardised triage navigation outcomes shared across all triages.
	/// UI-agnostic on purpose — both ConsoleTriage and a hypothetical future
	/// GUI triage would return these same values from their navigation prompt.
	/// </summary>
	public enum TriageAction
	{
		/// <summary>Advance to the next finding in the queue.</summary>
		Next,
		/// <summary>Return to the previous finding.</summary>
		Back,
		/// <summary>Skip this finding and continue to the next.</summary>
		Skip,
		/// <summary>Quit the triage entirely.</summary>
		Quit,
	}

	/// <summary>
	/// A labelled choice in a triage prompt. The Key is the actual key the
	/// operator presses; the Label is the text shown to the right of the key
	/// in the choices line ([F]ix → Key=F, Label="ix").
	/// </summary>
	public record ChoiceOption(ConsoleKey Key, string Label);

	/// <summary>
	/// A single highlight span within a block of text. Used to mark one or
	/// more regions of an excerpt for white-on-red rendering during triage.
	/// Multiple spans are supported because content-quality findings often
	/// have several positions to highlight: pattern matches, paired quote
	/// characters, anchor-tag pairs in misplaced-anchor findings.
	/// </summary>
	/// <param name="Start">Character offset of the highlight in the block text.</param>
	/// <param name="Length">Length in characters of the highlighted region.</param>
	public record HighlightSpan(int Start, int Length);

	/// <summary>
	/// The unified triage-finding model. Every triage workflow that displays a
	/// per-finding screen feeds this shape into <see cref="ConsoleTriage.ShowFinding"/>
	/// to get the same look-and-feel automatically.
	///
	/// Previously each triage rendered its findings differently — ContentQuality
	/// showed an excerpt with surrounding context, while SpellCheck showed only a
	/// word list with no block context. The TriageFinding shape captures the union
	/// of useful fields so every triage can present its data uniformly.
	/// </summary>
	/// <param name="FilePath">Path or filename of the source page.</param>
	/// <param name="FileUrl">Public URL of the source page (informational).</param>
	/// <param name="IssueType">e.g. "BARE_TEXT_IN_CONTAINER", "SPELLING_ERROR".</param>
	/// <param name="BlockTag">
	/// The HTML start tag of the containing block, with attributes:
	/// e.g. &lt;div class="h2"&gt;. Empty string when not applicable.
	/// </param>
	/// <param name="BlockText">
	/// The full text of the block (or a truncated excerpt) in which the
	/// defect was found. Used as the source for highlighting.
	/// </param>
	/// <param name="Highlights">
	/// Zero or more regions within <see cref="BlockText"/> to render in
	/// white-on-red. Multiple spans support content-quality cases where
	/// pattern matches, quote pairs, or anchor-tag pairs need to be marked.
	/// Empty list means "no highlighting" — the excerpt renders as plain text.
	/// </param>
	/// <param name="Detail">Human-readable description of the defect.</param>
	/// <param name="StepNumber">1-based index of this finding in the triage queue.</param>
	/// <param name="TotalSteps">Total findings in the queue (for "step N of M" header).</param>
	public record TriageFinding(
		string FilePath,
		string FileUrl,
		string IssueType,
		string BlockTag,
		string BlockText,
		IReadOnlyList<HighlightSpan> Highlights,
		string Detail,
		int StepNumber,
		int TotalSteps);
}
