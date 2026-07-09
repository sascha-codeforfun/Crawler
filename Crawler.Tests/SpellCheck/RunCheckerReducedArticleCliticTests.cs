using System;
using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the German reduced-indefinite-article clitic rescue in <see cref="RunChecker"/>. The
	/// tokenizer drops a leading elision apostrophe ("mit 'nem" → bare "nem"), and the stem is not a
	/// dictionary word, so it flags. When German is in the set, the token is one of the reduced-article
	/// stems ('ne/'nem/'nen/'ner/'nes = eine/einem/einen/einer/eines), AND a proclitic apostrophe sits
	/// immediately before it at a left boundary, the occurrence is dropped — no dictionary call, the
	/// apostrophe-in-context is the signal.
	///
	/// Additive and per-occurrence: a bare stem with no leading apostrophe still flags (so a real typo
	/// spelling a stem is never masked), a non-German page is untouched, and the set is closed (a
	/// non-article stem with an apostrophe still surfaces). All fixtures are invented generic German.
	/// </summary>
	public class RunCheckerReducedArticleCliticTests
	{
		// Surrounding real words are in the dict; the reduced-article stems (ne/nem/nen/ner/nes) are
		// deliberately absent — they are not words, which is the whole premise of the rescue.
		private static readonly HashSet<string> GermanDict = new(StringComparer.Ordinal)
		{
			"mit", "Freund", "Das", "ist", "gute", "Idee", "Ich", "hab", "Hund", "wegen", "Sache",
			"trotz", "Fehlers", "Hier", "Fehler", "hier",
		};

		private static RunCheck Checker() => (text, language) =>
			SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text))
				.Select(t => t.Text)
				.Where(w => w.Any(char.IsLetter) && !GermanDict.Contains(w))
				.Distinct()
				.Select(w => new CheckMiss(w, string.Empty));

		private static TextRun TextNode(string text) =>
			new(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text);

		private static List<string> Words(TextRun run, string language = "de") =>
			RunChecker.Check(run, language, Checker()).Select(f => f.Word).ToList();

		[Theory]
		[InlineData("mit 'nem Freund")]        // 'nem = einem
		[InlineData("Das ist 'ne gute Idee")]  // 'ne  = eine
		[InlineData("Ich hab 'nen Hund")]      // 'nen = einen
		[InlineData("wegen 'ner Sache")]       // 'ner = einer
		[InlineData("trotz 'nes Fehlers")]     // 'nes = eines
		public void ReducedArticleClitic_IsRescued(string text)
		{
			Assert.Empty(Words(TextNode(text)));
		}

		[Fact]
		public void SentenceInitialCapitalised_IsRescued()
		{
			// "'Ne gute Idee." → token "Ne"; membership is case-insensitive, the apostrophe is at the
			// start-of-string boundary.
			Assert.Empty(Words(TextNode("'Ne gute Idee")));
		}

		[Fact]
		public void BareStem_NoApostrophe_StillFlags()
		{
			// A real typo that happens to spell a stem, with no leading apostrophe, must still surface.
			Assert.Equal(new[] { "nem" }, Words(TextNode("Hier ist nem Fehler")));
		}

		[Fact]
		public void NonGermanPage_IsNotRescued()
		{
			// The rescue is German-gated: with no German in the set, the stem surfaces as before.
			Assert.Equal(new[] { "nem" }, Words(TextNode("mit 'nem Freund"), "en"));
		}

		[Fact]
		public void NonArticleStem_WithApostrophe_StillFlags()
		{
			// The set is closed: an apostrophe in front of a stem that is NOT a reduced article does not
			// rescue it.
			Assert.Contains("xyz", Words(TextNode("mit 'xyz hier")));
		}
	}
}
