using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ContentQuality's pure helper methods — the small, decidable
	/// pieces that the larger check methods compose. Each helper is pure
	/// (string in, string/structure out) with no I/O.
	///
	/// Introduced in #307 as part of the targeted-coverage pass: identify
	/// each previously-uncovered method, decide whether it's worth testing
	/// or should carry [ExcludeFromCodeCoverage]. These five are the worth-
	/// testing set; the orchestration / logger-output / log-writer wrappers
	/// in the same class are excluded with documented justifications.
	/// </summary>
	public class ContentQualityHelpersTests
	{
		// ── NameArchitectInvisible ───────────────────────────────────────
		// Maps a char to its operator-facing name. Pure switch over codepoints.

		[Theory]
		[InlineData('\u200B', "ZWSP (U+200B)")]
		[InlineData('\u200C', "ZWNJ (U+200C)")]
		[InlineData('\u200D', "ZWJ (U+200D)")]
		[InlineData('\u2060', "WJ (U+2060)")]
		[InlineData('\uFEFF', "ZWNBSP/BOM (U+FEFF)")]
		[InlineData('\u2028', "LINE SEPARATOR (U+2028)")]
		[InlineData('\u2029', "PARAGRAPH SEPARATOR (U+2029)")]
		public void NameArchitectInvisible_NamedCodepoint_ReturnsKnownLabel(char ch, string expected)
		{
			Assert.Equal(expected, ContentQuality.NameArchitectInvisible(ch));
		}

		[Theory]
		[InlineData('\u202A')]
		[InlineData('\u202B')]
		[InlineData('\u202C')]
		[InlineData('\u202D')]
		[InlineData('\u202E')]
		public void NameArchitectInvisible_BidiControlRange_FormatsAsBidiControl(char ch)
		{
			var result = ContentQuality.NameArchitectInvisible(ch);
			Assert.StartsWith("bidi control (U+", result);
			Assert.Contains($"{(int)ch:X4}", result);
		}

		[Theory]
		[InlineData('\u2066')]
		[InlineData('\u2067')]
		[InlineData('\u2068')]
		[InlineData('\u2069')]
		public void NameArchitectInvisible_BidiIsolateRange_FormatsAsBidiIsolate(char ch)
		{
			var result = ContentQuality.NameArchitectInvisible(ch);
			Assert.StartsWith("bidi isolate (U+", result);
			Assert.Contains($"{(int)ch:X4}", result);
		}

		[Theory]
		[InlineData('\u0001')]
		[InlineData('\u0007')]
		[InlineData('\u001F')]
		public void NameArchitectInvisible_C0Control_FormatsAsC0(char ch)
		{
			var result = ContentQuality.NameArchitectInvisible(ch);
			Assert.StartsWith("C0 control (U+", result);
		}

		[Theory]
		[InlineData('\u0080')]
		[InlineData('\u0090')]
		[InlineData('\u009F')]
		public void NameArchitectInvisible_C1Control_FormatsAsC1(char ch)
		{
			var result = ContentQuality.NameArchitectInvisible(ch);
			Assert.StartsWith("C1 control (U+", result);
		}

		[Fact]
		public void NameArchitectInvisible_UnknownInvisible_FormatsAsGenericInvisible()
		{
			// U+FFF9 (Interlinear Annotation Anchor) — not in any named branch.
			var result = ContentQuality.NameArchitectInvisible('\uFFF9');
			Assert.StartsWith("invisible (U+", result);
			Assert.Contains("FFF9", result);
		}

		// ── FindFirstControlChar ─────────────────────────────────────────
		// Scans a string for the first control/invisible char; returns codepoint + label.

		[Fact]
		public void FindFirstControlChar_EmptyString_ReturnsNull()
		{
			Assert.Null(ContentQuality.FindFirstControlChar(""));
		}

		[Fact]
		public void FindFirstControlChar_NoControls_ReturnsNull()
		{
			Assert.Null(ContentQuality.FindFirstControlChar("plain ASCII text"));
		}

		[Theory]
		[InlineData('\r', "CR (U+000D)")]
		[InlineData('\n', "LF (U+000A)")]
		[InlineData('\t', "TAB (U+0009)")]
		public void FindFirstControlChar_KnownShortForm_ReturnsShortLabel(char ch, string expected)
		{
			var result = ContentQuality.FindFirstControlChar($"hello{ch}world");
			Assert.NotNull(result);
			Assert.Equal((int)ch, result.Value.Codepoint);
			Assert.Equal(expected, result.Value.Name);
		}

		[Theory]
		[InlineData('\u200B', "ZWSP (U+200B)")]
		[InlineData('\u200C', "ZWNJ (U+200C)")]
		[InlineData('\u200D', "ZWJ (U+200D)")]
		[InlineData('\uFEFF', "BOM/ZWNBSP (U+FEFF)")]
		[InlineData('\u2028', "LINE SEPARATOR (U+2028)")]
		[InlineData('\u2029', "PARAGRAPH SEPARATOR (U+2029)")]
		public void FindFirstControlChar_KnownInvisible_ReturnsLabel(char ch, string expected)
		{
			var result = ContentQuality.FindFirstControlChar($"x{ch}y");
			Assert.NotNull(result);
			Assert.Equal(expected, result.Value.Name);
		}

		[Theory]
		[InlineData('\u202A')]
		[InlineData('\u202E')]
		[InlineData('\u2066')]
		[InlineData('\u2069')]
		public void FindFirstControlChar_BidiRange_ReturnsFormattedLabel(char ch)
		{
			var result = ContentQuality.FindFirstControlChar($"x{ch}y");
			Assert.NotNull(result);
			Assert.Contains("bidi", result.Value.Name);
		}

		[Fact]
		public void FindFirstControlChar_C0ControlNonShortForm_ReturnsFormattedC0()
		{
			// U+0007 (BEL) is C0 but not CR/LF/TAB.
			var result = ContentQuality.FindFirstControlChar("a\u0007b");
			Assert.NotNull(result);
			Assert.Equal(0x0007, result.Value.Codepoint);
			Assert.StartsWith("C0 control (U+", result.Value.Name);
		}

		[Fact]
		public void FindFirstControlChar_C1Control_ReturnsFormattedC1()
		{
			var result = ContentQuality.FindFirstControlChar("a\u0090b");
			Assert.NotNull(result);
			Assert.StartsWith("C1 control (U+", result.Value.Name);
		}

		[Fact]
		public void FindFirstControlChar_ReturnsFirstOnly_NotSubsequent()
		{
			// Two control chars; only the first is reported.
			var result = ContentQuality.FindFirstControlChar("a\rb\nc");
			Assert.NotNull(result);
			Assert.Equal('\r', (char)result.Value.Codepoint);
		}

		// ── TruncateForLog ───────────────────────────────────────────────
		// Replaces invisible chars with operator-readable markers; caps at 250.

		[Fact]
		public void TruncateForLog_EmptyString_ReturnsEmpty()
		{
			Assert.Equal(string.Empty, ContentQuality.TruncateForLog(""));
		}

		[Fact]
		public void TruncateForLog_PlainText_ReturnsUnchanged()
		{
			Assert.Equal("plain ASCII", ContentQuality.TruncateForLog("plain ASCII"));
		}

		[Theory]
		[InlineData("\r", "[CR]")]
		[InlineData("\n", "[LF]")]
		[InlineData("\t", "[TAB]")]
		public void TruncateForLog_ShortFormControls_UseShortMarker(string input, string expected)
		{
			Assert.Equal(expected, ContentQuality.TruncateForLog(input));
		}

		[Theory]
		[InlineData('\u2028', "[INVISIBLE LINE SEPARATOR U+2028]")]
		[InlineData('\u2029', "[INVISIBLE PARAGRAPH SEPARATOR U+2029]")]
		[InlineData('\u200B', "[INVISIBLE ZERO-WIDTH SPACE U+200B]")]
		[InlineData('\u200C', "[INVISIBLE ZERO-WIDTH NON-JOINER U+200C]")]
		[InlineData('\u200D', "[INVISIBLE ZERO-WIDTH JOINER U+200D]")]
		[InlineData('\uFEFF', "[INVISIBLE BOM U+FEFF]")]
		public void TruncateForLog_NamedInvisible_UsesVerboseMarker(char ch, string expected)
		{
			Assert.Equal(expected, ContentQuality.TruncateForLog(ch.ToString()));
		}

		[Theory]
		[InlineData('\u202A')]
		[InlineData('\u202E')]
		public void TruncateForLog_BidiControl_FormatsAsBidiControl(char ch)
		{
			var result = ContentQuality.TruncateForLog(ch.ToString());
			Assert.StartsWith("[INVISIBLE BIDI CONTROL U+", result);
			Assert.EndsWith("]", result);
		}

		[Theory]
		[InlineData('\u2066')]
		[InlineData('\u2069')]
		public void TruncateForLog_BidiIsolate_FormatsAsBidiIsolate(char ch)
		{
			var result = ContentQuality.TruncateForLog(ch.ToString());
			Assert.StartsWith("[INVISIBLE BIDI ISOLATE U+", result);
		}

		[Fact]
		public void TruncateForLog_OtherC0Control_FormatsAsGenericControl()
		{
			// BEL (U+0007) — not CR/LF/TAB, not in named invisible list.
			var result = ContentQuality.TruncateForLog("\u0007");
			Assert.Equal("[INVISIBLE CONTROL U+0007]", result);
		}

		[Fact]
		public void TruncateForLog_C1Control_FormatsAsGenericControl()
		{
			var result = ContentQuality.TruncateForLog("\u0090");
			Assert.Equal("[INVISIBLE CONTROL U+0090]", result);
		}

		[Fact]
		public void TruncateForLog_MixedContent_AllMarkersAppliedInPlace()
		{
			var result = ContentQuality.TruncateForLog("a\rb\u200Bc");
			Assert.Equal("a[CR]b[INVISIBLE ZERO-WIDTH SPACE U+200B]c", result);
		}

		[Fact]
		public void TruncateForLog_AtExactLimit_NoEllipsis()
		{
			var s = new string('a', 250);
			var result = ContentQuality.TruncateForLog(s);
			Assert.Equal(250, result.Length);
			Assert.DoesNotContain("…", result);
		}

		[Fact]
		public void TruncateForLog_OverLimit_TruncatesAndAppendsEllipsis()
		{
			var s = new string('a', 300);
			var result = ContentQuality.TruncateForLog(s);
			// 250 'a' + 1 '…' character
			Assert.Equal(251, result.Length);
			Assert.EndsWith("…", result);
		}

		[Fact]
		public void TruncateForLog_LimitMeasuredAfterMarkerExpansion_NotBeforeReplacement()
		{
			// Input has 240 plain chars + one ZWSP. After marker expansion the
			// total exceeds 250. Confirm the trim happens on expanded text.
			var s = new string('a', 240) + "\u200B";
			var result = ContentQuality.TruncateForLog(s);
			Assert.EndsWith("…", result);
		}

		// ── QuoteExcerpt + FindFirstQuoteContext ─────────────────────────
		// Sentence-boundary-aware excerpt builder used for quote findings.

		[Fact]
		public void QuoteExcerpt_FullSentenceFalse_DelegatesToFixedRadiusExcerpt()
		{
			var text = "lorem ipsum dolor sit amet consectetur";
			// pos=15 is inside "dolor"; with full-sentence off, we get a centred fixed-radius excerpt.
			var result = ContentQuality.QuoteExcerpt(text, pos: 15, fullSentence: false, maxLength: 10);
			Assert.Contains("dolor", result.Replace("…", string.Empty));
		}

		[Fact]
		public void QuoteExcerpt_FullSentence_ExpandsToSentenceBoundaries()
		{
			// pos lands inside the second sentence; excerpt should start after the first '.'
			// and end at the second '.'.
			var text = "First sentence here. Second sentence with quotes. Third.";
			// Position of 'q' in "quotes".
			int pos = text.IndexOf("quotes");
			var result = ContentQuality.QuoteExcerpt(text, pos, fullSentence: true, maxLength: 200);
			Assert.Contains("Second sentence with quotes.", result);
			Assert.DoesNotContain("First sentence here", result);
		}

		[Fact]
		public void QuoteExcerpt_FullSentence_PrefixEllipsisWhenNotAtStart()
		{
			var text = "First sentence here. Second sentence with quotes. Third.";
			int pos = text.IndexOf("quotes");
			var result = ContentQuality.QuoteExcerpt(text, pos, fullSentence: true, maxLength: 200);
			Assert.StartsWith("...", result);
		}

		[Fact]
		public void QuoteExcerpt_FullSentence_SuffixEllipsisWhenNotAtEnd()
		{
			var text = "First sentence here. Second sentence with quotes. Third.";
			int pos = text.IndexOf("quotes");
			var result = ContentQuality.QuoteExcerpt(text, pos, fullSentence: true, maxLength: 200);
			Assert.EndsWith("...", result);
		}

		[Fact]
		public void QuoteExcerpt_FullSentence_NoSurroundingSentences_NoEllipsis()
		{
			// Single sentence, pos in the middle; expansion hits both edges with no
			// surrounding text, so no prefix/suffix ellipsis.
			var text = "Only one sentence with target word here";
			int pos = text.IndexOf("target");
			var result = ContentQuality.QuoteExcerpt(text, pos, fullSentence: true, maxLength: 200);
			Assert.DoesNotContain("...", result);
			Assert.Contains("target", result);
		}

		[Fact]
		public void QuoteExcerpt_FullSentence_CapsToMaxLength()
		{
			// One long sentence with no boundaries — should still be capped.
			var text = new string('x', 100) + " target " + new string('y', 100);
			int pos = text.IndexOf("target");
			var result = ContentQuality.QuoteExcerpt(text, pos, fullSentence: true, maxLength: 50);
			// Body capped to maxLength; with possible prefix/suffix ellipsis the
			// final length is bounded — never wildly larger than maxLength.
			Assert.True(result.Length <= 60, $"Expected ~50 chars, got {result.Length}: {result}");
		}

		[Fact]
		public void QuoteExcerpt_FullSentence_NewlinesAndCRsReplacedWithSpaces()
		{
			var text = "First line.\nSecond line with \"target\" mid.\rThird.";
			int pos = text.IndexOf("target");
			var result = ContentQuality.QuoteExcerpt(text, pos, fullSentence: true, maxLength: 200);
			Assert.DoesNotContain('\n', result);
			Assert.DoesNotContain('\r', result);
		}

		[Fact]
		public void FindFirstQuoteContext_NoOpenersInText_ReturnsEmpty()
		{
			Assert.Equal("", ContentQuality.FindFirstQuoteContext("no quotes here", fullSentence: false, maxLength: 100));
		}

		[Fact]
		public void FindFirstQuoteContext_FirstOpenerWins()
		{
			// German double-low opener „ (U+201E) and an English curly „opener" later.
			var text = "before \u201Eopener one\u201C plus \u201Cother quote\u201D after";
			var result = ContentQuality.FindFirstQuoteContext(text, fullSentence: false, maxLength: 100);
			// First opener is U+201E; excerpt should be centred around it.
			Assert.Contains("\u201E", result);
		}
	}
}
