using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ConsoleTriage's Layer A (pure helpers) and rendering helpers,
	/// introduced in fileset #296.
	///
	/// Scope: everything that doesn't invoke Console.ReadKey or the retry loop
	/// is covered here. The two I/O pieces (Console.ReadKey itself and the
	/// re-prompt-on-invalid loop) are eyeball-verified by the operator during
	/// migration of each existing triage in subsequent filesets — their surface
	/// is ~5 lines of trivial wiring per public method.
	/// </summary>
	public class ConsoleTriageTests
	{
		// ── ResolveDefault ───────────────────────────────────────────────

		[Fact]
		public void ResolveDefault_EnterWithDefault_ReturnsDefault()
		{
			Assert.Equal(ConsoleKey.Y,
				ConsoleTriage.ResolveDefault(ConsoleKey.Enter, ConsoleKey.Y));
		}

		[Fact]
		public void ResolveDefault_EnterWithoutDefault_ReturnsEnter()
		{
			Assert.Equal(ConsoleKey.Enter,
				ConsoleTriage.ResolveDefault(ConsoleKey.Enter, defaultKey: null));
		}

		[Fact]
		public void ResolveDefault_NonEnterKey_PassesThrough()
		{
			Assert.Equal(ConsoleKey.S,
				ConsoleTriage.ResolveDefault(ConsoleKey.S, ConsoleKey.Y));
		}

		[Fact]
		public void ResolveDefault_NonEnterKey_NoDefault_PassesThrough()
		{
			Assert.Equal(ConsoleKey.X,
				ConsoleTriage.ResolveDefault(ConsoleKey.X, defaultKey: null));
		}

		// ── IsValidChoice ────────────────────────────────────────────────

		[Fact]
		public void IsValidChoice_KeyInList_ReturnsTrue()
		{
			Assert.True(ConsoleTriage.IsValidChoice(
				ConsoleKey.S, [ConsoleKey.Y, ConsoleKey.N, ConsoleKey.S]));
		}

		[Fact]
		public void IsValidChoice_KeyNotInList_ReturnsFalse()
		{
			Assert.False(ConsoleTriage.IsValidChoice(
				ConsoleKey.X, [ConsoleKey.Y, ConsoleKey.N, ConsoleKey.S]));
		}

		[Fact]
		public void IsValidChoice_EmptyList_ReturnsFalse()
		{
			Assert.False(ConsoleTriage.IsValidChoice(ConsoleKey.Y, []));
		}

		// ── IsYesKey (the established Y/N convention) ────────────────────

		[Fact]
		public void IsYesKey_Y_ReturnsTrue()
		{
			Assert.True(ConsoleTriage.IsYesKey(ConsoleKey.Y));
		}

		[Theory]
		[InlineData(ConsoleKey.N)]
		[InlineData(ConsoleKey.Enter)]
		[InlineData(ConsoleKey.Escape)]
		[InlineData(ConsoleKey.Spacebar)]
		[InlineData(ConsoleKey.A)]
		[InlineData(ConsoleKey.D1)]
		public void IsYesKey_AnythingElse_ReturnsFalse(ConsoleKey key)
		{
			Assert.False(ConsoleTriage.IsYesKey(key));
		}

		// ── MapKeyToNavigationAction ─────────────────────────────────────

		[Theory]
		[InlineData(ConsoleKey.N, TriageAction.Next)]
		[InlineData(ConsoleKey.RightArrow, TriageAction.Next)]
		[InlineData(ConsoleKey.B, TriageAction.Back)]
		[InlineData(ConsoleKey.LeftArrow, TriageAction.Back)]
		[InlineData(ConsoleKey.S, TriageAction.Skip)]
		[InlineData(ConsoleKey.Q, TriageAction.Quit)]
		[InlineData(ConsoleKey.Escape, TriageAction.Quit)]
		public void MapKeyToNavigationAction_StandardKeys_MapCorrectly(
			ConsoleKey key, TriageAction expected)
		{
			Assert.Equal(expected, ConsoleTriage.MapKeyToNavigationAction(key));
		}

		[Theory]
		[InlineData(ConsoleKey.A)]
		[InlineData(ConsoleKey.D1)]
		[InlineData(ConsoleKey.T)]
		[InlineData(ConsoleKey.Spacebar)]
		public void MapKeyToNavigationAction_UnknownKeys_ReturnNull(ConsoleKey key)
		{
			Assert.Null(ConsoleTriage.MapKeyToNavigationAction(key));
		}

		// ── ComposeChoicesLine ───────────────────────────────────────────

		[Fact]
		public void ComposeChoicesLine_SingleChoice_RendersWithBrackets()
		{
			Assert.Equal("[F] Fix",
				ConsoleTriage.ComposeChoicesLine(
					[new ChoiceOption(ConsoleKey.F, "Fix")]));
		}

		[Fact]
		public void ComposeChoicesLine_MultipleChoices_SeparatedByFourSpaces()
		{
			var actual = ConsoleTriage.ComposeChoicesLine(
				[new ChoiceOption(ConsoleKey.F, "Fix"),
				 new ChoiceOption(ConsoleKey.I, "Ignore"),
				 new ChoiceOption(ConsoleKey.S, "Skip"),
				 new ChoiceOption(ConsoleKey.Q, "Quit")]);
			Assert.Equal("[F] Fix    [I] Ignore    [S] Skip    [Q] Quit", actual);
		}

		[Fact]
		public void ComposeChoicesLine_EmptyList_ReturnsEmpty()
		{
			Assert.Equal("", ConsoleTriage.ComposeChoicesLine([]));
		}

		// ── RenderFindingHeader ──────────────────────────────────────────

		[Fact]
		public void RenderFindingHeader_WithSteps_IncludesStepCount()
		{
			Assert.Equal("BARE_TEXT_IN_CONTAINER — step 3 of 47",
				ConsoleTriage.RenderFindingHeader("BARE_TEXT_IN_CONTAINER", 3, 47));
		}

		[Fact]
		public void RenderFindingHeader_ZeroTotal_OmitsStepCount()
		{
			Assert.Equal("SOME_TITLE",
				ConsoleTriage.RenderFindingHeader("SOME_TITLE", 0, 0));
		}

		[Fact]
		public void RenderFindingHeader_NegativeTotal_OmitsStepCount()
		{
			// Defensive — total ≤ 0 means "no step counter".
			Assert.Equal("X",
				ConsoleTriage.RenderFindingHeader("X", 1, -1));
		}

		// ── ComputeExcerptWindow ─────────────────────────────────────────

		[Fact]
		public void ComputeExcerptWindow_HighlightInMiddle_BalancesContext()
		{
			var text = new string('a', 200) + "DEFECT" + new string('b', 200);
			//                     ^200       ^206
			var (start, length) = ConsoleTriage.ComputeExcerptWindow(text, 200, 6);

			// Should centre around the defect, ExcerptHalfWidth (60) each side.
			Assert.Equal(140, start);                  // 200 - 60
			Assert.True(length <= ConsoleTriage.ExcerptMaxLength);
		}

		[Fact]
		public void ComputeExcerptWindow_HighlightAtStart_StartsAtZero()
		{
			var text = "DEFECT" + new string('b', 200);
			var (start, length) = ConsoleTriage.ComputeExcerptWindow(text, 0, 6);

			Assert.Equal(0, start);
			Assert.True(length > 0);
		}

		[Fact]
		public void ComputeExcerptWindow_HighlightAtEnd_EndsAtTextEnd()
		{
			var text = new string('a', 200) + "DEFECT";
			var (start, length) = ConsoleTriage.ComputeExcerptWindow(text, 200, 6);

			Assert.Equal(text.Length, start + length);
		}

		[Fact]
		public void ComputeExcerptWindow_EmptyText_ReturnsZeros()
		{
			Assert.Equal((0, 0), ConsoleTriage.ComputeExcerptWindow("", 0, 0));
		}

		[Fact]
		public void ComputeExcerptWindow_HighlightBeyondText_ClampsLength()
		{
			// HighlightLength extending past text end gets clamped.
			var text = "short";
			var (start, length) = ConsoleTriage.ComputeExcerptWindow(text, 2, 100);

			Assert.True(start + length <= text.Length);
		}

		[Fact]
		public void ComputeExcerptWindow_NegativeStart_NormalisedToZero()
		{
			var text = "some text";
			var (start, _) = ConsoleTriage.ComputeExcerptWindow(text, -5, 3);
			Assert.True(start >= 0);
		}

		// ── RenderHighlightedExcerpt ─────────────────────────────────────

		[Fact]
		public void RenderHighlightedExcerpt_HighlightInMiddle_WrapsWithMarkers()
		{
			var result = ConsoleTriage.RenderHighlightedExcerpt(
				"hello DEFECT world", 6, 6);
			Assert.Contains("[DEFECT]", result);
		}

		[Fact]
		public void RenderHighlightedExcerpt_CustomMarkers_UsesThem()
		{
			var result = ConsoleTriage.RenderHighlightedExcerpt(
				"hello DEFECT world", 6, 6, "<<", ">>");
			Assert.Contains("<<DEFECT>>", result);
		}

		[Fact]
		public void RenderHighlightedExcerpt_TextLongerThanWindow_AddsEllipsis()
		{
			var text = new string('a', 300) + "DEFECT" + new string('b', 300);
			var result = ConsoleTriage.RenderHighlightedExcerpt(text, 300, 6);

			// Truncation indicators on at least one side.
			Assert.Contains("…", result);
			Assert.Contains("[DEFECT]", result);
		}

		[Fact]
		public void RenderHighlightedExcerpt_HighlightAtStart_NoLeadingEllipsis()
		{
			var result = ConsoleTriage.RenderHighlightedExcerpt(
				"DEFECT trailing context here", 0, 6);
			Assert.StartsWith("[DEFECT]", result);
		}

		[Fact]
		public void RenderHighlightedExcerpt_EmptyText_ReturnsEmpty()
		{
			Assert.Equal("", ConsoleTriage.RenderHighlightedExcerpt("", 0, 0));
		}

		[Fact]
		public void RenderHighlightedExcerpt_ZeroHighlight_StillIncludesMarkers()
		{
			// Edge case: a "highlight" of length 0 (e.g. a cursor position).
			var result = ConsoleTriage.RenderHighlightedExcerpt(
				"hello world", 5, 0);
			Assert.Contains("[]", result);
		}

		// ── KeyToLetter ──────────────────────────────────────────────────

		[Theory]
		[InlineData(ConsoleKey.A, 'A')]
		[InlineData(ConsoleKey.Z, 'Z')]
		[InlineData(ConsoleKey.F, 'F')]
		public void KeyToLetter_AlphaKeys_ReturnLetter(ConsoleKey key, char expected)
		{
			Assert.Equal(expected, ConsoleTriage.KeyToLetter(key));
		}

		[Theory]
		[InlineData(ConsoleKey.D0, '0')]
		[InlineData(ConsoleKey.D5, '5')]
		[InlineData(ConsoleKey.D9, '9')]
		public void KeyToLetter_DigitKeys_ReturnDigit(ConsoleKey key, char expected)
		{
			Assert.Equal(expected, ConsoleTriage.KeyToLetter(key));
		}

		[Theory]
		[InlineData(ConsoleKey.Enter, '⏎')]
		[InlineData(ConsoleKey.Escape, '⎋')]
		[InlineData(ConsoleKey.LeftArrow, '←')]
		[InlineData(ConsoleKey.RightArrow, '→')]
		public void KeyToLetter_SpecialKeys_ReturnGlyph(ConsoleKey key, char expected)
		{
			Assert.Equal(expected, ConsoleTriage.KeyToLetter(key));
		}

		// ── TriageFinding record ─────────────────────────────────────────

		[Fact]
		public void TriageFinding_RecordEquality_SharedHighlightsInstance_AreEqual()
		{
			// Record equality on TriageFinding uses default equality for the
			// Highlights field, which is reference equality for IReadOnlyList.
			// Two records sharing the SAME list instance are equal; two records
			// with separate-but-structurally-equal lists are NOT. This matches
			// C# record semantics for collection-typed fields; structural
			// equality on the list would require a custom Equals override which
			// adds complexity for no current consumer need (TriageFinding is
			// passed between methods, not persisted or compared in production).
			var sharedHighlights = new HighlightSpan[] { new(0, 4) };
			var a = new TriageFinding(
				"f.html", "https://x", "T", "<div>", "text",
				sharedHighlights, "d", 1, 1);
			var b = new TriageFinding(
				"f.html", "https://x", "T", "<div>", "text",
				sharedHighlights, "d", 1, 1);
			Assert.Equal(a, b);
		}

		[Fact]
		public void TriageFinding_RecordEquality_DifferentFields_NotEqual()
		{
			var hs = new HighlightSpan[] { new(0, 4) };
			var a = new TriageFinding(
				"f.html", "https://x", "T", "<div>", "text", hs, "d", 1, 1);
			var b = a with { StepNumber = 2 };
			Assert.NotEqual(a, b);
		}

		// ── HighlightSpan record ─────────────────────────────────────────

		[Fact]
		public void HighlightSpan_RecordEquality_ByValue()
		{
			Assert.Equal(new HighlightSpan(3, 5), new HighlightSpan(3, 5));
			Assert.NotEqual(new HighlightSpan(3, 5), new HighlightSpan(3, 6));
		}

		// ── ComputeExcerptWindow with span list ──────────────────────────

		[Fact]
		public void ComputeExcerptWindow_SpanList_Empty_FallsBackToTextStart()
		{
			var (start, length) = ConsoleTriage.ComputeExcerptWindow("hello world", []);
			Assert.Equal(0, start);
			Assert.True(length > 0);
		}

		[Fact]
		public void ComputeExcerptWindow_SpanList_SingleSpan_MatchesSingleSpanOverload()
		{
			var text = new string('a', 200) + "DEFECT" + new string('b', 200);
			var listResult = ConsoleTriage.ComputeExcerptWindow(text, [new HighlightSpan(200, 6)]);
			var scalarResult = ConsoleTriage.ComputeExcerptWindow(text, 200, 6);
			Assert.Equal(scalarResult, listResult);
		}

		[Fact]
		public void ComputeExcerptWindow_SpanList_MultipleClose_BoundingBox()
		{
			// Two spans 20 chars apart should fit in one window centred between them.
			var text = new string('.', 300);
			var spans = new[] { new HighlightSpan(100, 5), new HighlightSpan(125, 5) };
			var (start, length) = ConsoleTriage.ComputeExcerptWindow(text, spans);
			// Window covers both spans (start ≤ 100, start+length ≥ 130).
			Assert.True(start <= 100);
			Assert.True(start + length >= 130);
		}

		[Fact]
		public void ComputeExcerptWindow_SpanList_TooWide_FallsBackToFirst()
		{
			// Spans 500 apart cannot share a window of ExcerptMaxLength.
			var text = new string('.', 1000);
			var spans = new[] { new HighlightSpan(100, 5), new HighlightSpan(700, 5) };
			var (start, length) = ConsoleTriage.ComputeExcerptWindow(text, spans);
			// Should centre on the first span — window includes offset 100 region.
			Assert.True(start < 100);
			Assert.True(start + length < 700);   // does NOT extend to second span
		}

		// ── RenderHighlightedExcerpt with span list ──────────────────────

		[Fact]
		public void RenderHighlightedExcerpt_SpanList_SingleSpan_WrapsWithMarkers()
		{
			var result = ConsoleTriage.RenderHighlightedExcerpt(
				"hello DEFECT world", [new HighlightSpan(6, 6)]);
			Assert.Contains("[DEFECT]", result);
		}

		[Fact]
		public void RenderHighlightedExcerpt_SpanList_MultipleSpans_EachWrapped()
		{
			var result = ConsoleTriage.RenderHighlightedExcerpt(
				"alpha BETA gamma DELTA omega",
				[new HighlightSpan(6, 4), new HighlightSpan(17, 5)]);
			Assert.Contains("[BETA]", result);
			Assert.Contains("[DELTA]", result);
		}

		[Fact]
		public void RenderHighlightedExcerpt_SpanList_OverlappingSpans_AreCoalesced()
		{
			// Two spans that overlap → render as one combined span.
			// Spans cover offsets:
			//   (5, 5) → 5..9  = "ABCDE"
			//   (8, 4) → 8..11 = "DEFG"
			// Merged: 5..11 = "ABCDEFG". The "H" at offset 12 is outside the merge.
			var result = ConsoleTriage.RenderHighlightedExcerpt(
				"text ABCDEFGH more",
				[new HighlightSpan(5, 5),
				 new HighlightSpan(8, 4)]);
			Assert.Contains("[ABCDEFG]", result);
			Assert.Contains("[ABCDEFG]H", result);
			// Crucially: no nested or sequential overlapping markers.
			Assert.DoesNotContain("][", result);
		}

		[Fact]
		public void RenderHighlightedExcerpt_SpanList_TouchingSpans_AreCoalesced()
		{
			// Touching spans (end == next.start) → coalesced too.
			var result = ConsoleTriage.RenderHighlightedExcerpt(
				"text ABCDE more",
				[new HighlightSpan(5, 2),    // AB
				 new HighlightSpan(7, 3)]);  // CDE
			Assert.Contains("[ABCDE]", result);
		}

		[Fact]
		public void RenderHighlightedExcerpt_SpanList_UnsortedInput_RendersInOrder()
		{
			// Caller may pass spans in arbitrary order; output must be left-to-right.
			var result = ConsoleTriage.RenderHighlightedExcerpt(
				"alpha BETA gamma DELTA omega",
				[new HighlightSpan(17, 5), new HighlightSpan(6, 4)]);  // reversed
			var betaIdx = result.IndexOf("[BETA]");
			var deltaIdx = result.IndexOf("[DELTA]");
			Assert.True(betaIdx >= 0 && deltaIdx > betaIdx);
		}

		[Fact]
		public void RenderHighlightedExcerpt_SpanList_EmptyList_NoMarkers()
		{
			var result = ConsoleTriage.RenderHighlightedExcerpt("plain text", []);
			Assert.Equal("plain text", result);
		}

		[Fact]
		public void RenderHighlightedExcerpt_SpanList_OutOfBoundsSpan_Skipped()
		{
			// Span entirely past the end of the excerpt window must not break rendering.
			var text = "short text";
			var spans = new[] { new HighlightSpan(1000, 5) };
			var result = ConsoleTriage.RenderHighlightedExcerpt(text, spans);
			// Empty bracket pairs are not generated for out-of-bounds spans.
			Assert.DoesNotContain("[]", result);
		}

		// ── ChoiceOption record ──────────────────────────────────────────

		[Fact]
		public void ChoiceOption_RecordEquality_ByValue()
		{
			var a = new ChoiceOption(ConsoleKey.F, "ix");
			var b = new ChoiceOption(ConsoleKey.F, "ix");
			Assert.Equal(a, b);
		}

		// ── FormatReviewSummary (shared by spell + content-quality review) ──

		[Fact]
		public void FormatReviewSummary_Complete_ReportsKeptRemainder()
		{
			// 10 total, 3 discarded, walked to the end → 7 kept.
			var line = ConsoleTriage.FormatReviewSummary(
				discarded: 3, reviewed: 10, total: 10, quit: false, keptLabel: "kept");
			Assert.Equal("Review complete — 3 discarded, 7 kept.", line);
		}

		[Fact]
		public void FormatReviewSummary_Complete_UsesCallerKeptLabel()
		{
			// Content-quality side uses "left as-is" instead of "kept".
			var line = ConsoleTriage.FormatReviewSummary(
				discarded: 1, reviewed: 5, total: 5, quit: false, keptLabel: "left as-is");
			Assert.Equal("Review complete — 1 discarded, 4 left as-is.", line);
		}

		[Fact]
		public void FormatReviewSummary_Complete_ZeroDiscards()
		{
			var line = ConsoleTriage.FormatReviewSummary(
				discarded: 0, reviewed: 8, total: 8, quit: false, keptLabel: "kept");
			Assert.Equal("Review complete — 0 discarded, 8 kept.", line);
		}

		[Fact]
		public void FormatReviewSummary_Quit_ReportsSeenOfTotal()
		{
			// Quit after seeing 9 of 167, having discarded 3. The kept label is
			// irrelevant on the early-quit path and must not appear.
			var line = ConsoleTriage.FormatReviewSummary(
				discarded: 3, reviewed: 9, total: 167, quit: true, keptLabel: "kept");
			Assert.Equal("Review ended early — 3 discarded, 9 of 167 reviewed.", line);
		}

		[Fact]
		public void FormatReviewSummary_QuitOnFirstItem_ReportsOneOfTotal()
		{
			var line = ConsoleTriage.FormatReviewSummary(
				discarded: 0, reviewed: 1, total: 50, quit: true, keptLabel: "left as-is");
			Assert.Equal("Review ended early — 0 discarded, 1 of 50 reviewed.", line);
		}
	}
}
