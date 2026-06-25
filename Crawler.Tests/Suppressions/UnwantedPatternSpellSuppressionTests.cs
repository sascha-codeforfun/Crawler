using System.Collections.Generic;
using System.Linq;
using Crawler.Quality;
using Crawler.SpellCheck;
using Crawler.Suppressions;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.Suppressions
{
	/// <summary>
	/// Pins the unwanted-pattern → spelling cross-pass suppression. Two halves:
	/// (1) the MARKUP half (<see cref="UnwantedPatternSpellSuppression"/>) qualifies a configured
	///     pattern as a delimiter (>= 2 special chars, where special excludes letters, digits,
	///     '-' and ':'), takes its whitespace/&lt;/&gt;/'-'/':'-bounded run from a CQ excerpt, and
	///     collects the word tokens inside;
	/// (2) the spelling guard (<see cref="RunChecker"/>) mutes any finding whose token is in that
	///     file's set, with no dictionary check.
	///
	/// All fixtures use INVENTED delimiters ("$[", "]$", "#{") and synthetic inner tokens — never
	/// site content.
	/// </summary>
	public class UnwantedPatternSpellSuppressionTests
	{
		// ── MARKUP half: WordsInDelimiterRuns (run extraction) ──────────

		private static readonly string[] Dollar = { "$[" };

		[Fact]
		public void Run_SingleInnerToken_Collected()
		{
			Assert.Equal(
				new[] { "foobar" },
				UnwantedPatternSpellSuppression.WordsInDelimiterRuns("text $[foobar]$ more", Dollar).ToArray());
		}

		[Fact]
		public void Run_MultipleInnerTokens_AllCollected()
		{
			Assert.Equal(
				new[] { "foo", "bar" },
				UnwantedPatternSpellSuppression.WordsInDelimiterRuns("x $[foo.bar]$ y", Dollar).ToArray());
		}

		[Fact]
		public void Run_WhitespaceEndsRun()
		{
			// A space ends the run: "bar" is past it, so it is NOT collected.
			Assert.Equal(
				new[] { "foo" },
				UnwantedPatternSpellSuppression.WordsInDelimiterRuns("$[foo bar]$", Dollar).ToArray());
		}

		[Theory]
		[InlineData("$[foo<b]$")]  // '<' ends the run
		[InlineData("$[foo>b]$")]  // '>' ends the run
		[InlineData("$[foo-bar]$")] // '-' ends the run (hyphen is a run-ender)
		[InlineData("$[foo:bar]$")] // ':' ends the run (colon is a run-ender)
		public void Run_StructuralCharsEndRun(string excerpt)
		{
			Assert.Equal(
				new[] { "foo" },
				UnwantedPatternSpellSuppression.WordsInDelimiterRuns(excerpt, Dollar).ToArray());
		}

		[Fact]
		public void Run_MultipleOccurrences_EachCollected()
		{
			Assert.Equal(
				new[] { "foo", "bar" },
				UnwantedPatternSpellSuppression.WordsInDelimiterRuns("$[foo]$ and $[bar]$", Dollar).ToArray());
		}

		[Fact]
		public void Run_NoAnchor_Empty()
		{
			Assert.Empty(UnwantedPatternSpellSuppression.WordsInDelimiterRuns("nothing to see here", Dollar));
		}

		// ── Qualifier: which configured patterns may anchor ─────────────

		private static QualityIssue Issue(string filename, string context) =>
			new(filename, UnwantedPatternSpellSuppression.UnwantedPatternType, "detail", context);

		[Fact]
		public void Qualifier_TwoSpecialChars_Anchors()
		{
			var m = new UnwantedPatternSpellSuppression(
				new[] { Issue("file1", "x $[foobar]$ y") },
				new[] { "$[", "]$" });

			Assert.False(m.IsEmpty);
			Assert.Contains("foobar", m.WordsForFile("file1"));
			Assert.Empty(m.WordsForFile("other"));
		}

		[Theory]
		[InlineData("legacyword")] // 0 special chars — a bare word never anchors (backstop kept)
		[InlineData("foo.")]        // 1 special char — below the >= 2 gate
		[InlineData("ab-cd")]       // '-' is NOT special, so this has 0 specials
		[InlineData("a:b")]         // ':' is NOT special, so this has 0 specials
		public void Qualifier_BelowTwoSpecials_DoesNotAnchor(string pattern)
		{
			var m = new UnwantedPatternSpellSuppression(
				new[] { Issue("file1", $"x {pattern}foobar y") },
				new[] { pattern });

			Assert.True(m.IsEmpty);
		}

		[Fact]
		public void Matcher_IgnoresOtherIssueTypes()
		{
			var other = new QualityIssue("file1", "SPLIT_WORD_ANCHOR", "detail", "x $[foobar]$ y");
			var m = new UnwantedPatternSpellSuppression(new[] { other }, new[] { "$[" });
			Assert.True(m.IsEmpty);
		}

		[Fact]
		public void Matcher_NullInputs_IsEmpty()
		{
			Assert.True(new UnwantedPatternSpellSuppression(null, new[] { "$[" }).IsEmpty);
			Assert.True(new UnwantedPatternSpellSuppression(new[] { Issue("f", "$[foo]$") }, null).IsEmpty);
		}

		// ── Spelling guard: the RunChecker mute end-to-end ──────────────

		private static TextRun TextNode(string text) =>
			new(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text);

		private static RunCheck Checker(params string[] accepted)
		{
			var set = new HashSet<string>(accepted, System.StringComparer.Ordinal);
			return (text, language) =>
				SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text))
					.Select(t => t.Text)
					.Where(w => w.Any(char.IsLetter) && !set.Contains(w))
					.Distinct()
					.Select(w => new CheckMiss(w, string.Empty));
		}

		private static IReadOnlySet<string> Words(params string[] words) =>
			new HashSet<string>(words, System.StringComparer.Ordinal);

		private static List<string> Findings(TextRun run, RunCheck checker, IReadOnlySet<string>? unwanted) =>
			RunChecker.Check(run, "de", checker, unwantedPatternWords: unwanted).Select(f => f.Word).ToList();

		[Fact]
		public void Guard_TokenInSet_Muted()
		{
			// "foobar" is a miss (not accepted) but sits in the unwanted-pattern set → muted.
			Assert.Empty(Findings(TextNode("foobar"), Checker(), Words("foobar")));
		}

		[Fact]
		public void Guard_NoSet_TokenSurfaces()
		{
			Assert.Equal(new[] { "foobar" }, Findings(TextNode("foobar"), Checker(), null));
		}

		[Fact]
		public void Guard_DifferentWordInSet_NotMuted()
		{
			Assert.Equal(new[] { "foobar" }, Findings(TextNode("foobar"), Checker(), Words("wibble")));
		}

		[Fact]
		public void Guard_BackstopPreserved_UnrelatedTypoStillSurfaces()
		{
			// A genuine typo not in the set still surfaces, even when the set is non-empty.
			Assert.Equal(new[] { "teh" }, Findings(TextNode("teh"), Checker("the"), Words("foobar")));
		}
	}
}
