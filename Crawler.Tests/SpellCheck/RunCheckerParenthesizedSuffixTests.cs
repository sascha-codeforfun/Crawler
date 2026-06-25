using System;
using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the German parenthesised-suffix rescue in <see cref="RunChecker"/>. An optional word
	/// component in parentheses after a base word — "Nachhaltigkeit(smanagement)" — reads as both
	/// "Nachhaltigkeit" and "Nachhaltigkeitsmanagement". The tokenizer splits the parentheses off, so
	/// the in-paren fragment ("smanagement", Fugen-s included) is checked alone and fails as a non-word,
	/// while the base is its own token and validated normally. When German is in the set, the flagged
	/// token sits wholly inside "(token)" with a base word right before the "(", and base+token rejoins
	/// to a real word, the fragment is dropped.
	///
	/// Additive and per-occurrence, dictionary-gated: a junk fragment ("Haus(blah)" → "Hausblah"), a
	/// missing base or closing paren, and a non-German page all leave the fragment flagged. This is the
	/// suffix-after-base mirror of SpellChecker.TryParenthesizedPrefixJoin ("(prefix-)stem"); the infix
	/// form "Kund(inn)en" is deliberately out of scope. All fixtures are invented generic German.
	/// </summary>
	public class RunCheckerParenthesizedSuffixTests
	{
		// The base words and the JOINED compound are valid; the in-paren fragments are not.
		private static readonly HashSet<string> GermanDict = new(StringComparer.Ordinal)
		{
			"die", "hier", "Nachhaltigkeit", "Nachhaltigkeitsmanagement", "Haus", "da", "text",
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

		[Fact]
		public void ParenthesisedSuffix_JoinedReadingReal_IsRescued()
		{
			// base "Nachhaltigkeit" (its own token, valid) + fragment "smanagement" → the joined reading
			// "Nachhaltigkeitsmanagement" is in the dictionary, so the fragment is dropped.
			Assert.Empty(Words(TextNode("die Nachhaltigkeit(smanagement) hier")));
		}

		[Fact]
		public void ParenthesisedSuffix_JoinedReadingNotReal_StillFlags()
		{
			// base+fragment ("Hausblah") is not a word — the fragment stays flagged.
			Assert.Contains("blah", Words(TextNode("Haus(blah) da")));
		}

		[Fact]
		public void NoBaseBeforeParen_StillFlags()
		{
			// Parenthesised fragment with no base word before the "(" — nothing to join to.
			Assert.Contains("smanagement", Words(TextNode("(smanagement) text")));
		}

		[Fact]
		public void NoClosingParen_StillFlags()
		{
			// The joined reading IS in the dictionary, but without a closing ")" the token is not a
			// parenthesised suffix — the structural gate, not the dictionary, blocks the rescue.
			Assert.Contains("smanagement", Words(TextNode("Nachhaltigkeit(smanagement text")));
		}

		[Fact]
		public void NonGermanPage_IsNotRescued()
		{
			// German-gated: with no German in the set, the fragment surfaces as before.
			Assert.Contains("smanagement", Words(TextNode("die Nachhaltigkeit(smanagement) hier"), "en"));
		}
	}
}
