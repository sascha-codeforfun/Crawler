using Xunit;
using Crawler.Quality;

namespace Crawler.Tests.Quality
{
	public class LogExcerptTests
	{
		// Replaces invisible chars with operator-readable markers; caps at 250.

		[Fact]
		public void Truncate_EmptyString_ReturnsEmpty()
		{
			Assert.Equal(string.Empty, LogExcerpt.Truncate(""));
		}

		[Fact]
		public void Truncate_PlainText_ReturnsUnchanged()
		{
			Assert.Equal("plain ASCII", LogExcerpt.Truncate("plain ASCII"));
		}

		[Theory]
		[InlineData("\r", "[CR]")]
		[InlineData("\n", "[LF]")]
		[InlineData("\t", "[TAB]")]
		public void Truncate_ShortFormControls_UseShortMarker(string input, string expected)
		{
			Assert.Equal(expected, LogExcerpt.Truncate(input));
		}

		[Theory]
		[InlineData('\u2028', "[INVISIBLE LINE SEPARATOR U+2028]")]
		[InlineData('\u2029', "[INVISIBLE PARAGRAPH SEPARATOR U+2029]")]
		[InlineData('\u200B', "[INVISIBLE ZERO-WIDTH SPACE U+200B]")]
		[InlineData('\u200C', "[INVISIBLE ZERO-WIDTH NON-JOINER U+200C]")]
		[InlineData('\u200D', "[INVISIBLE ZERO-WIDTH JOINER U+200D]")]
		[InlineData('\uFEFF', "[INVISIBLE BOM U+FEFF]")]
		[InlineData('\u00AD', "[INVISIBLE SOFT HYPHEN U+00AD]")]
		[InlineData('\u007F', "[INVISIBLE DEL U+007F]")]
		[InlineData('\u2060', "[INVISIBLE WORD JOINER U+2060]")]
		[InlineData('\u200E', "[INVISIBLE BIDI MARK U+200E]")]
		[InlineData('\u2062', "[INVISIBLE MATH U+2062]")]
		public void Truncate_NamedInvisible_UsesVerboseMarker(char ch, string expected)
		{
			Assert.Equal(expected, LogExcerpt.Truncate(ch.ToString()));
		}

		[Theory]
		[InlineData('\u202A')]
		[InlineData('\u202E')]
		public void Truncate_BidiControl_FormatsAsBidiControl(char ch)
		{
			var result = LogExcerpt.Truncate(ch.ToString());
			Assert.StartsWith("[INVISIBLE BIDI CONTROL U+", result);
			Assert.EndsWith("]", result);
		}

		[Theory]
		[InlineData('\u2066')]
		[InlineData('\u2069')]
		public void Truncate_BidiIsolate_FormatsAsBidiIsolate(char ch)
		{
			var result = LogExcerpt.Truncate(ch.ToString());
			Assert.StartsWith("[INVISIBLE BIDI ISOLATE U+", result);
		}

		[Fact]
		public void Truncate_OtherC0Control_FormatsAsGenericControl()
		{
			// BEL (U+0007) — not CR/LF/TAB, not in named invisible list.
			var result = LogExcerpt.Truncate("\u0007");
			Assert.Equal("[INVISIBLE CONTROL U+0007]", result);
		}

		[Fact]
		public void Truncate_C1Control_FormatsAsGenericControl()
		{
			var result = LogExcerpt.Truncate("\u0090");
			Assert.Equal("[INVISIBLE CONTROL U+0090]", result);
		}

		[Fact]
		public void Truncate_MixedContent_AllMarkersAppliedInPlace()
		{
			var result = LogExcerpt.Truncate("a\rb\u200Bc");
			Assert.Equal("a[CR]b[INVISIBLE ZERO-WIDTH SPACE U+200B]c", result);
		}

		[Fact]
		public void Truncate_AtExactLimit_NoEllipsis()
		{
			var s = new string('a', 250);
			var result = LogExcerpt.Truncate(s);
			Assert.Equal(250, result.Length);
			Assert.DoesNotContain("…", result);
		}

		[Fact]
		public void Truncate_OverLimit_TruncatesAndAppendsEllipsis()
		{
			var s = new string('a', 300);
			var result = LogExcerpt.Truncate(s);
			// 250 'a' + 1 '…' character
			Assert.Equal(251, result.Length);
			Assert.EndsWith("…", result);
		}

		[Fact]
		public void Truncate_LimitMeasuredAfterMarkerExpansion_NotBeforeReplacement()
		{
			// Input has 240 plain chars + one ZWSP. After marker expansion the
			// total exceeds 250. Confirm the trim happens on expanded text.
			var s = new string('a', 240) + "\u200B";
			var result = LogExcerpt.Truncate(s);
			Assert.EndsWith("…", result);
		}
	}
}
