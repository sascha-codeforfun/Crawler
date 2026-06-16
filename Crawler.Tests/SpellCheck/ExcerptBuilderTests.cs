using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery: the SourceKind-aware excerpt builder — the piece that makes a finding's
	/// "Context:" line correct by construction. Context is built from the finding's own node and
	/// from canonical text, so it cannot point at the wrong occurrence and cannot mismatch the
	/// flagged word. Fixtures are synthetic and neutral.
	///
	/// Findings are produced the way production does — DomTraverser yields the runs, RunChecker
	/// turns them into located findings — so the tests exercise the real path, not a hand-rolled
	/// shortcut.
	/// </summary>
	public class SpellCheckExcerptBuilderTests
	{
		// Run the real traversal + checker over a fragment, with a fake checker that flags one
		// word, and return the finding whose SourcePath matches the kind we want to inspect.
		private static SpellFinding FindingIn(string html, string word, string sourcePathContains)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);

			RunCheck flag = (text, lang) =>
				SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text))
					.Select(t => t.Text)
					.Where(w => w == word)
					.Distinct()
					.Select(w => new CheckMiss(w, ""));

			return DomTraverser.Traverse(doc)
				.SelectMany(run => RunChecker.Check(run, "de", flag))
				.First(f => f.Word == word && f.SourcePath.Contains(sourcePathContains));
		}

		[Fact]
		public void TextNode_Context_IsSurroundingBlockText()
		{
			var finding = FindingIn("<p>Dies ist ein Satz mit dem Wort Beispl darin.</p>", "Beispl", "#text");
			var excerpt = ExcerptBuilder.Build(finding);

			Assert.Contains("Beispl", excerpt);
			Assert.Contains("Dies ist ein Satz", excerpt);
		}

		[Fact]
		public void TextNode_Context_WindowsLongBlock_AroundWord()
		{
			var filler = string.Join(" ", Enumerable.Repeat("wort", 60));
			var finding = FindingIn($"<div>{filler} Zielfehler {filler}</div>", "Zielfehler", "#text");
			var excerpt = ExcerptBuilder.Build(finding);

			Assert.Contains("Zielfehler", excerpt);
			Assert.Contains("\u2026", excerpt);            // windowed (ellipsis)
			Assert.True(excerpt.Length < filler.Length);
		}

		[Fact]
		public void TextNode_Context_WalksThroughInlineToBlockAncestor()
		{
			var finding = FindingIn("<p>Anfang <span>Mittl Teil</span> Ende</p>", "Mittl", "#text");
			var excerpt = ExcerptBuilder.Build(finding);

			Assert.Contains("Mittl", excerpt);
			Assert.Contains("Anfang", excerpt);
			Assert.Contains("Ende", excerpt);
		}

		[Fact]
		public void Attribute_Context_IsTheAttributeValue()
		{
			var finding = FindingIn("<img alt=\"Ein Bidl hier\">", "Bidl", "@alt");
			var excerpt = ExcerptBuilder.Build(finding);

			Assert.Equal("Ein Bidl hier", excerpt);
		}

		[Fact]
		public void Meta_Context_IsTheContentValue_NotTheNameLabel()
		{
			var finding = FindingIn("<meta name=\"description\" content=\"Eine Beschribung hier\">", "Beschribung", "meta[@name=description]");
			var excerpt = ExcerptBuilder.Build(finding);

			Assert.Equal("Eine Beschribung hier", excerpt);
		}

		[Fact]
		public void Excerpt_ShowsFlaggedWordWhole_WhenSourceHadSoftHyphen()
		{
			// Block text carries a soft hyphen inside the word; canonicalization joins it, so the
			// flagged word appears WHOLE and contiguous in the excerpt — the guarantee the human
			// relies on. (Soft-hyphen stripping itself is unit-tested on Canonicalizer directly.)
			//
			// NOTE: we assert with Ordinal comparison and check the excerpt has no U+00AD byte at
			// all. Culture-aware Contains treats U+00AD as ignorable, so a soft-hyphen needle would
			// spuriously match a stripped string — hence the explicit ordinal byte check below.
			var finding = FindingIn("<p>Ein Ver\u00ADtrog im Text</p>", "Vertrog", "#text");
			var excerpt = ExcerptBuilder.Build(finding);

			Assert.Contains("Vertrog", excerpt);
			Assert.Equal(-1, excerpt.IndexOf('\u00AD'));        // no soft-hyphen byte survives
			Assert.Contains("Vertrog", excerpt, System.StringComparison.Ordinal);
		}

		[Fact]
		public void TextNode_Context_WindowsOnWholeWord_NotSubstringInLongerWord()
		{
			// "Adress" is flagged — a real typo for "Address" in "Adress Line". The same block also
			// contains "Adressen" earlier, where "Adress" is only a prefix. A substring locator pulls
			// the window onto "Adressen"; the whole-word locator must window on the real standalone
			// "Adress Line" occurrence instead. Regression for a live case.
			var filler = string.Join(" ", Enumerable.Repeat("wort", 40));
			var finding = FindingIn(
				$"<p>Bei unstrukturierten Adressen {filler} im Feld Adress Line erfolgen.</p>",
				"Adress", "#text");
			var excerpt = ExcerptBuilder.Build(finding);

			Assert.Contains("Adress Line", excerpt);       // windowed on the real occurrence
			Assert.DoesNotContain("Adressen", excerpt);     // not the substring-in-longer-word
			Assert.Contains("\u2026", excerpt);             // windowed (ellipsis present)
		}

		[Fact]
		public void TextNode_Context_SkipsHyphenCompound_WindowsOnStandalone()
		{
			// Same block holds "Adress-daten" (one tokenizer token) early and standalone "Adress
			// Line" late. The window must land on the standalone, not the compound — the live
			// [M] case generalised into the excerpt path.
			var filler = string.Join(" ", Enumerable.Repeat("wort", 40));
			var finding = FindingIn(
				$"<p>Angabe von Adress-daten {filler} im Feld Adress Line erfolgen.</p>",
				"Adress", "#text");
			var excerpt = ExcerptBuilder.Build(finding);

			Assert.Contains("Adress Line", excerpt);
			Assert.DoesNotContain("Adress-daten", excerpt);
			Assert.Contains("\u2026", excerpt);
		}
	}
}
