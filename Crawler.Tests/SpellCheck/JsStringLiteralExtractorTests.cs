using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins <see cref="JsStringLiteralExtractor"/> — Layer 1 of script spell-checking. The scanner's
	/// contract is "yield exactly the string literals a programmer wrote, decoded, with their raw
	/// source span, dropping object keys, and never mistaking a comment, regex, or template
	/// expression for a string." These tests fix each of those behaviours, the escape-decoding rules,
	/// and the position-bearing output.
	///
	/// All fixtures are invented, neutral JavaScript — no content is carried over from any real
	/// crawled page.
	/// </summary>
	public class JsStringLiteralExtractorTests
	{
		private static List<string> Texts(string js)
			=> JsStringLiteralExtractor.Extract(js).Select(x => x.Text).ToList();

		private static List<ScriptStringLiteral> Lits(string js)
			=> JsStringLiteralExtractor.Extract(js).ToList();

		// ---- basics: the three delimiters ----

		[Fact]
		public void DoubleQuoted_IsExtracted()
			=> Assert.Equal(new[] { "hello" }, Texts("var x = \"hello\";"));

		[Fact]
		public void SingleQuoted_IsExtracted()
			=> Assert.Equal(new[] { "hello" }, Texts("var x = 'hello';"));

		[Fact]
		public void Backtick_IsExtracted()
			=> Assert.Equal(new[] { "hello" }, Texts("var x = `hello`;"));

		[Fact]
		public void NullOrEmptySource_YieldsNothing()
		{
			Assert.Empty(Lits(null!));
			Assert.Empty(Lits(string.Empty));
		}

		[Fact]
		public void IdentifiersAndNumbers_AreNotLiterals()
			=> Assert.Empty(Lits("var total = count + 42 * ratio;"));

		// ---- position-bearing output ----

		[Fact]
		public void RawSpan_PointsAtOpeningQuoteAndCoversBothQuotes()
		{
			// index:  0123456
			//         x = "abc"
			var lit = Assert.Single(Lits("x = \"abc\""));
			Assert.Equal("abc", lit.Text);
			Assert.Equal(4, lit.RawStart);   // opening quote
			Assert.Equal(5, lit.RawLength);  // "abc" including both quotes
		}

		[Fact]
		public void RawSpan_IsCorrectForSecondLiteral()
		{
			//           1111
			// 0123456789012345
			// a('one','two')
			var lits = Lits("a('one','two')");
			Assert.Equal(2, lits.Count);
			Assert.Equal("one", lits[0].Text);
			Assert.Equal(2, lits[0].RawStart);
			Assert.Equal("two", lits[1].Text);
			Assert.Equal(8, lits[1].RawStart);
		}

		// ---- escape decoding ----

		[Theory]
		[InlineData(@"'\u00E4'", "\u00E4")]          // ä
		[InlineData(@"'\u{1F600}'", "\U0001F600")]    // 😀
		[InlineData(@"'\x41'", "A")]
		[InlineData(@"'a\tb'", "a\tb")]
		[InlineData(@"'a\\b'", "a\\b")]
		[InlineData(@"'a\/b'", "a/b")]               // not a JS escape, tolerated → '/'
		[InlineData(@"'a\zb'", "azb")]               // unknown escape \z → z
		[InlineData("'a\\\nb'", "ab")]                // line continuation removed
		public void Escapes_AreDecoded(string js, string expected)
			=> Assert.Equal(new[] { expected }, Texts(js));

		[Fact]
		public void EscapedQuote_DoesNotCloseLiteral()
			=> Assert.Equal(new[] { "a\"b" }, Texts("x = \"a\\\"b\";"));

		// ---- object keys are dropped (both sides required) ----

		[Fact]
		public void ObjectKeys_AreDropped_ValuesKept()
		{
			// { "alpha": "one", 'beta': "two" }  -> keys alpha/beta dropped, values kept
			var js = "var o = { \"alpha\": \"one\", 'beta': \"two\" };";
			Assert.Equal(new[] { "one", "two" }, Texts(js));
		}

		[Fact]
		public void KeyDetection_RequiresBraceOrComma_AND_TrailingColon_TernaryKept()
		{
			// Ternary value has a trailing ':' but its previous token is '?', so it is NOT a key.
			Assert.Equal(new[] { "yes", "no" }, Texts("var x = c ? 'yes' : 'no';"));
		}

		[Fact]
		public void ArrayElements_AreKept()
			=> Assert.Equal(new[] { "red", "green" }, Texts("var a = ['red', 'green'];"));

		[Fact]
		public void CallArguments_AreKept()
			=> Assert.Equal(new[] { "first", "second" }, Texts("fn('first', 'second');"));

		[Fact]
		public void LabelColon_IsNotAKey()
			// prev token is ';', not '{' / ',', so the literal is kept.
			=> Assert.Equal(new[] { "kept" }, Texts("loop: 'kept';"));

		[Fact]
		public void UnquotedKey_WithQuotedValue_KeepsOnlyValue()
			=> Assert.Equal(new[] { "val" }, Texts("var o = { key: 'val' };"));

		[Fact]
		public void KeyFollowedByColonAcrossComment_IsStillDropped()
			=> Assert.Equal(new[] { "v" }, Texts("var o = { 'k' /* c */ : 'v' };"));

		// ---- comments are not literals ----

		[Fact]
		public void LineComment_IsSkipped()
			=> Assert.Equal(new[] { "real" }, Texts("// 'fake'\nvar x = 'real';"));

		[Fact]
		public void BlockComment_IsSkipped()
			=> Assert.Equal(new[] { "real" }, Texts("/* 'fake' */ var x = 'real';"));

		// ---- regex literals are not strings ----

		[Fact]
		public void RegexContainingQuotes_IsNotReadAsString()
		{
			// /['"]/ holds quote characters that must NOT open a literal. The only real string is "".
			var js = "var s = text.replace(/['\"]/g, '');";
			Assert.Equal(new[] { "" }, Texts(js));
		}

		[Fact]
		public void RegexWithSlashInCharClass_IsSkippedWhole()
		{
			// The '/' inside [/] does not close the regex; nothing should be emitted.
			Assert.Empty(Lits("var r = /[a/b]+/g;"));
		}

		[Fact]
		public void Division_IsNotMistakenForRegex()
			=> Assert.Empty(Lits("var x = 10 / 2 / 5;"));

		[Fact]
		public void DivisionThenString_StillExtractsString()
			=> Assert.Equal(new[] { "ok" }, Texts("var x = a / b; var y = 'ok';"));

		[Fact]
		public void RegexAfterReturnKeyword_IsTreatedAsRegex()
		{
			// After 'return', a '/' begins a regex; the quote inside must not open a string.
			Assert.Empty(Lits("function f(){ return /'/; }"));
		}

		// ---- template literals ----

		[Fact]
		public void Template_StaticPartsKept_InterpolationDroppedAndSpaced()
		{
			var lit = Assert.Single(Lits("var m = `Hello ${user.name} bye`;"));
			Assert.Equal("Hello   bye", lit.Text); // ${...} -> single space, between existing spaces
		}

		[Fact]
		public void Template_InterpolationWithNestedBracesAndString_IsBalanced()
		{
			var lit = Assert.Single(Lits("var m = `a${ {k: '}'} }b`;"));
			Assert.Equal("a b", lit.Text); // brace inside nested string does not end the interpolation
		}

		// ---- malformed input tolerance ----

		[Fact]
		public void UnterminatedSingleQuote_EndsAtLineBreak()
		{
			// The literal stops at the newline; the next line is scanned normally.
			Assert.Equal(new[] { "oops", "next" }, Texts("var a = 'oops\nvar b = 'next';"));
		}

		[Fact]
		public void EmptyLiteral_IsEmitted_LexerDoesNotJudgeContent()
			=> Assert.Equal(new[] { "" }, Texts("var x = '';"));

		// ---- a small realistic object (generic content) ----

		[Fact]
		public void RealisticConfigObject_DropsKeys_KeepsValueProse()
		{
			var js =
				"widget.init({\n" +
				"  'mode': 'compact',\n" +
				"  'title': 'Welcome aboard',\n" +
				"  'path': '/a/b/c',\n" +
				"  'count': 3\n" +
				"});";

			// Keys (mode/title/path/count) dropped; only the three string VALUES remain.
			Assert.Equal(new[] { "compact", "Welcome aboard", "/a/b/c" }, Texts(js));
		}
	}
}
