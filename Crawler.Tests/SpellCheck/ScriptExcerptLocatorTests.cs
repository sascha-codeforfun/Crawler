using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery 628: ScriptExcerptLocator pins the raw-span lookup that lets triage highlight a decoded
	/// word inside a raw script excerpt carrying JS escapes. The returned span covers the RAW form, so
	/// "Auto\u002DScroll" highlights in full though the decoded word "Auto-Scroll" is shorter. Whole-
	/// word boundaries come from the shared SpellTokenizer locator. All fixtures synthetic and generic.
	/// </summary>
	public class ScriptExcerptLocatorTests
	{
		[Fact]
		public void PlainExcerpt_NoEscapes_FindsWordExactly()
		{
			const string excerpt = "'label': 'hello world'";
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "hello");

			Assert.True(start >= 0);
			Assert.Equal("hello", excerpt.Substring(start, length));
		}

		[Fact]
		public void UnicodeEscape_SpansFullRawForm()
		{
			// The literal string contains the six raw characters \ u 0 0 2 D, not a hyphen.
			const string excerpt = "'k': 'Auto\\u002DScroll x'";
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "Auto-Scroll");

			Assert.True(start >= 0);
			Assert.Equal("Auto\\u002DScroll", excerpt.Substring(start, length));
		}

		[Fact]
		public void HexEscape_SpansFullRawForm()
		{
			const string excerpt = "'k': 'Co\\x2Dop value'";
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "Co-op");

			Assert.True(start >= 0);
			Assert.Equal("Co\\x2Dop", excerpt.Substring(start, length));
		}

		[Fact]
		public void IgnoresCase_WhenMatching()
		{
			const string excerpt = "'k': 'Wert'";
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "wert");

			Assert.True(start >= 0);
			Assert.Equal("Wert", excerpt.Substring(start, length));
		}

		[Fact]
		public void WordNotPresent_ReturnsMinusOne()
		{
			var (start, length) = ScriptExcerptLocator.LocateRawSpan("'k': 'value'", "missing");

			Assert.Equal(-1, start);
			Assert.Equal(0, length);
		}

		[Fact]
		public void SubstringButNotWholeWord_ReturnsMinusOne()
		{
			// "item" sits inside "subitemx" — not a whole token, so no highlight span.
			var (start, _) = ScriptExcerptLocator.LocateRawSpan("'k': 'subitemx'", "item");

			Assert.Equal(-1, start);
		}

		[Fact]
		public void NullOrEmpty_ReturnsMinusOne()
		{
			Assert.Equal(-1, ScriptExcerptLocator.LocateRawSpan(string.Empty, "x").Start);
			Assert.Equal(-1, ScriptExcerptLocator.LocateRawSpan("abc", string.Empty).Start);
		}

		// ── escape decoding arms (Decode) ───────────────────────────────────
		// Each excerpt carries the escape immediately before a clean word; the
		// escape decodes to a non-word char (a boundary), so "helo" stays a whole
		// word and its raw span maps back exactly. This exercises every simple
		// escape case while asserting the offset map stays coherent.

		[Theory]
		[InlineData("n")]
		[InlineData("t")]
		[InlineData("r")]
		[InlineData("b")]
		[InlineData("f")]
		[InlineData("v")]
		[InlineData("0")]
		[InlineData("\\")]
		[InlineData("'")]
		[InlineData("\"")]
		[InlineData("`")]
		[InlineData("/")]
		public void SimpleEscape_DecodesAndLocatesFollowingWord(string esc)
		{
			string excerpt = "\\" + esc + "helo"; // raw: backslash, esc-char, h, e, l, o
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "helo");

			Assert.True(start >= 0);
			Assert.Equal("helo", excerpt.Substring(start, length));
		}

		[Fact]
		public void LineContinuation_Lf_EmitsNothing()
		{
			const string excerpt = "\\\nhelo"; // backslash + LF (line continuation) + word
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "helo");

			Assert.True(start >= 0);
			Assert.Equal("helo", excerpt.Substring(start, length));
		}

		[Fact]
		public void LineContinuation_Cr_EmitsNothing()
		{
			const string excerpt = "\\\rhelo"; // backslash + CR (no trailing LF)
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "helo");

			Assert.True(start >= 0);
			Assert.Equal("helo", excerpt.Substring(start, length));
		}

		[Fact]
		public void LineContinuation_CrLf_ConsumesBothAndEmitsNothing()
		{
			const string excerpt = "\\\r\nhelo"; // backslash + CRLF
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "helo");

			Assert.True(start >= 0);
			Assert.Equal("helo", excerpt.Substring(start, length));
		}

		[Fact]
		public void BracedUnicodeEscape_Bmp_SpansFullRawForm()
		{
			// \u{68} decodes to 'h'; the located span covers the whole braced form.
			const string excerpt = "\\u{68}elo";
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "helo");

			Assert.True(start >= 0);
			Assert.Equal("\\u{68}elo", excerpt.Substring(start, length));
		}

		[Fact]
		public void BracedUnicodeEscape_AstralSurrogatePair_LocatesFollowingWord()
		{
			// \u{1F600} decodes to a 2-char surrogate pair; the following word still maps.
			const string excerpt = "\\u{1F600}helo";
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "helo");

			Assert.True(start >= 0);
			Assert.Equal("helo", excerpt.Substring(start, length));
		}

		[Fact]
		public void BracedUnicodeEscape_BadHex_DegradesToLiteral()
		{
			// \u{zz} is not valid hex → degrades; the later whole word still resolves.
			const string excerpt = "\\u{zz}helo";
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "helo");

			Assert.True(start >= 0);
			Assert.Equal("helo", excerpt.Substring(start, length));
		}

		[Fact]
		public void HexEscape_BadHex_DegradesToLiteral()
		{
			const string excerpt = "\\xZZ helo"; // \xZZ not valid hex → degrades
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "helo");

			Assert.True(start >= 0);
			Assert.Equal("helo", excerpt.Substring(start, length));
		}

		[Fact]
		public void UnicodeEscape_BadHex_DegradesToLiteral()
		{
			const string excerpt = "\\uZZZZ helo"; // \uZZZZ not valid hex → degrades
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "helo");

			Assert.True(start >= 0);
			Assert.Equal("helo", excerpt.Substring(start, length));
		}

		[Fact]
		public void UnknownEscape_DropsBackslashAndKeepsChar()
		{
			// \z is not a recognized escape → JS rule: \z is z. Span covers "\zebra".
			const string excerpt = "\\zebra";
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "zebra");

			Assert.True(start >= 0);
			Assert.Equal("\\zebra", excerpt.Substring(start, length));
		}

		[Fact]
		public void TrailingBackslash_TreatedAsLiteral()
		{
			const string excerpt = "helo\\"; // backslash at end with no following char
			var (start, length) = ScriptExcerptLocator.LocateRawSpan(excerpt, "helo");

			Assert.True(start >= 0);
			Assert.Equal("helo", excerpt.Substring(start, length));
		}
	}
}
