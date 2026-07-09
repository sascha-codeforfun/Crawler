using System;
using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the DICTIONARY half of the ADJACENT_ANCHOR → spelling cross-pass dedup: the guard in
	/// <see cref="RunChecker"/> that mutes a flagged fracture fragment only when the verbatim join
	/// supplied per file (textA+textB) is a real word in the page's language. The fragment map is the
	/// output of <see cref="Crawler.Suppressions.AdjacentAnchorSpellSuppression"/>, injected here
	/// directly so the gate is exercised in isolation. Dictionary holds the JOINED word ("Android")
	/// but not the fragments ("And"/"roid"), exactly as the live bundle would.
	/// </summary>
	public class RunCheckerAdjacentAnchorTests
	{
		private static readonly HashSet<string> GermanDict = new(StringComparer.Ordinal)
		{
			"Android", "die", "Seite",
		};

		private static RunCheck Checker() => (text, language) =>
			SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text))
				.Select(t => t.Text)
				.Where(w => w.Any(char.IsLetter) && !GermanDict.Contains(w))
				.Distinct()
				.Select(w => new CheckMiss(w, string.Empty));

		private static TextRun TextNode(string text) =>
			new(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text);

		private static IReadOnlyDictionary<string, IReadOnlySet<string>> Joins(params (string token, string join)[] entries)
		{
			var d = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
			foreach (var (token, join) in entries)
			{
				if (!d.TryGetValue(token, out var s))
				{
					s = new HashSet<string>(StringComparer.Ordinal);
					d[token] = s;
				}

				s.Add(join);
			}

			return d.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal);
		}

		private static List<string> Words(TextRun run, IReadOnlyDictionary<string, IReadOnlySet<string>>? map) =>
			RunChecker.Check(run, "de", Checker(), adjacentAnchorJoins: map).Select(f => f.Word).ToList();

		[Fact]
		public void RightFragment_JoinReal_IsMuted()
		{
			// "roid" alone is not a word, but its fracture join "Android" is — mute it.
			Assert.Empty(Words(TextNode("roid"), Joins(("roid", "Android"))));
		}

		[Fact]
		public void LeftFragment_JoinReal_IsMuted()
		{
			Assert.Empty(Words(TextNode("And"), Joins(("And", "Android"))));
		}

		[Fact]
		public void JoinNotReal_StillFlags()
		{
			// A mispaired / junk join is not a word — the fragment stays flagged.
			Assert.Contains("roid", Words(TextNode("roid"), Joins(("roid", "Xyzzy"))));
		}

		[Fact]
		public void TokenNotInMap_StillFlags()
		{
			// A flagged token that is not a recorded fracture fragment is untouched.
			Assert.Contains("blah", Words(TextNode("blah"), Joins(("roid", "Android"))));
		}

		[Fact]
		public void NoMap_StillFlags()
		{
			Assert.Contains("roid", Words(TextNode("roid"), null));
		}

		[Fact]
		public void RealWordsAround_Unaffected()
		{
			// The guard only ever removes; real words are never touched.
			Assert.Empty(Words(TextNode("die Seite"), Joins(("roid", "Android"))));
		}
	}
}
