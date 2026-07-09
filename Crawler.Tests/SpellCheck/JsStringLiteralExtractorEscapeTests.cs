using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Targeted tests for JsStringLiteralExtractor's escape decoder (AppendEscape),
	/// which the existing JsStringLiteralExtractorTests only spot-checks (\/, \z,
	/// line continuation). These exercise the named control escapes, the \xHH and
	/// \uHHHH / \u{...} hex forms (valid and malformed → literal fall-through), the
	/// lone-trailing-backslash guard, and the \r / \r\n line continuations.
	///
	/// SYNTHETIC inputs. Each source is a single string literal; Decode returns its
	/// decoded Text. Malformed escapes follow JS's "drop the backslash, keep the
	/// escape char" rule (e.g. \xZZ → xZZ).
	/// </summary>
	public class JsStringLiteralExtractorEscapeTests
	{
		private static string Decode(string js) =>
			JsStringLiteralExtractor.Extract(js).Single().Text;

		[Theory]
		[InlineData("'\\n'", "\n")]
		[InlineData("'\\t'", "\t")]
		[InlineData("'\\r'", "\r")]
		[InlineData("'\\b'", "\b")]
		[InlineData("'\\f'", "\f")]
		[InlineData("'\\v'", "\v")]
		[InlineData("'\\0'", "\0")]
		[InlineData("'\\\\'", "\\")]
		[InlineData("'\\''", "'")]
		[InlineData("'\\\"'", "\"")]
		[InlineData("'\\`'", "`")]
		public void SimpleEscapes_Decode(string js, string expected)
		{
			Assert.Equal(expected, Decode(js));
		}

		[Theory]
		[InlineData("'\\x41'", "A")]      // valid \xHH
		[InlineData("'\\xZZ'", "xZZ")]    // malformed → literal fall-through
		[InlineData("'\\u0041'", "A")]    // valid \uHHHH
		[InlineData("'\\u00GG'", "u00GG")] // malformed \uHHHH → fall-through
		[InlineData("'\\u{41}'", "A")]    // valid \u{H..}
		[InlineData("'\\u{ZZ}'", "u{ZZ}")] // malformed \u{...} → fall-through
		public void HexAndBracedUnicode_Decode(string js, string expected)
		{
			Assert.Equal(expected, Decode(js));
		}

		[Fact]
		public void AstralBracedUnicode_DecodesToSurrogatePair()
		{
			Assert.Equal("\U0001F600", Decode("'\\u{1F600}'"));
		}

		[Fact]
		public void LoneTrailingBackslash_IsConsumedWithoutAppend()
		{
			// Unterminated literal ending in a backslash: the backslash is swallowed
			// by the i+1>=n guard and the literal up to it is still emitted.
			Assert.Equal("abc", Decode("'abc\\"));
		}

		[Theory]
		[InlineData("'a\\\rb'", "ab")]    // \<CR> continuation
		[InlineData("'a\\\r\nb'", "ab")] // \<CR><LF> continuation
		public void CarriageReturnLineContinuation_IsRemoved(string js, string expected)
		{
			Assert.Equal(expected, Decode(js));
		}
	}
}
