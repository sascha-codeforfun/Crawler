using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins how <see cref="RunChecker"/> handles <see cref="RunSource.Script"/> runs: they are gated
	/// by <see cref="ValueClassifier.ClassifyScriptLiteral"/> (not the data-*/universal path), and a
	/// surviving Script finding carries its canonical text on <c>ExcerptText</c> so the excerpt can
	/// be built without the DOM node. Other sources keep <c>ExcerptText</c> null.
	///
	/// Uses a fake <see cref="RunCheck"/> — no Hunspell/dictionaries. Fixtures are invented.
	/// </summary>
	public class RunCheckerScriptTests
	{
		private static TextRun ScriptRun(string text) =>
			new(HtmlNode.CreateNode("<script>x</script>"), RunSource.Script, "script[L1:1]", text);

		private static TextRun TextNodeRun(string text) =>
			new(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text);

		private static RunCheck FlagWords(params string[] words)
		{
			var set = new HashSet<string>(words);
			return (canonicalText, lang) =>
				SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", canonicalText))
					.Select(t => t.Text)
					.Where(set.Contains)
					.Distinct()
					.Select(w => new CheckMiss(w, "suggestion"));
		}

		[Fact]
		public void ProseLiteral_IsChecked_AndCarriesCanonicalExcerptText()
		{
			var run = ScriptRun("hallo wolrd");
			var f = Assert.Single(RunChecker.Check(run, "de", FlagWords("wolrd")).ToList());

			Assert.Equal("wolrd", f.Word);
			Assert.Equal(RunSource.Script, f.Source);
			Assert.Equal("script[L1:1]", f.SourcePath);
			Assert.Equal("hallo wolrd", f.ExcerptText); // canonical run text rides on the finding
			Assert.Equal(6, f.Start);
		}

		[Theory]
		[InlineData("fooBar")]        // machine slug
		[InlineData("/a/b/c")]        // path
		[InlineData("ABCwidget")]     // acronym-word
		public void NonProseLiteral_IsGatedOut_NoFindings(string literal)
		{
			// Even though the fake checker is told to flag the whole token, the script gate drops the
			// literal before checking, so nothing surfaces.
			var findings = RunChecker.Check(ScriptRun(literal), "de", FlagWords(literal)).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void StructuredLiteral_IsGatedOut()
		{
			// A JSON-in-a-string payload is dropped wholesale (SkipStructured) — its inner tokens
			// never reach the checker.
			var findings = RunChecker.Check(ScriptRun("{\"k\": \"v\"}"), "de", FlagWords("k", "v")).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void TextNodeFinding_HasNullExcerptText()
		{
			// ExcerptText is populated for Script findings only; text nodes rebuild from the node.
			var f = Assert.Single(RunChecker.Check(TextNodeRun("hallo wolrd"), "de", FlagWords("wolrd")).ToList());
			Assert.Null(f.ExcerptText);
		}
	}
}
