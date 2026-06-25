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
	/// Pins the anchor-split → spelling cross-pass suppression. Two halves:
	/// (1) the MARKUP half (<see cref="AnchorSplitSpellSuppression"/>) extracts (head, tail)
	///     from a SPLIT_WORD_ANCHOR excerpt and gates the tail to exactly one letter;
	/// (2) the DICTIONARY half (the guard in <see cref="RunChecker"/>) mutes a flagged head
	///     only when head+tail rejoins to a real word, in the page's language.
	///
	/// Fixtures: invented one-letter-tail splits (boxe&lt;/a&gt;s → boxes, flie&lt;/a&gt;s →
	/// flies) for the accepted path, and the longer-tail word-puzzle examples
	/// (vill&lt;/a&gt;age, pengu&lt;/a&gt;in, …) as the rejected path that documents WHY the cap
	/// is one letter. No site content is used.
	/// </summary>
	public class AnchorSplitSpellSuppressionTests
	{
		// ── MARKUP half: HeadTails extraction ───────────────────────────

		[Fact]
		public void HeadTails_OneLetterTail_YieldsPair()
		{
			Assert.Equal(
				new[] { ("boxe", "s") },
				AnchorSplitSpellSuppression.HeadTails("dies ist boxe</a>s und mehr").ToArray());
		}

		[Fact]
		public void HeadTails_TightContext_YieldsPair()
		{
			Assert.Equal(
				new[] { ("flie", "s") },
				AnchorSplitSpellSuppression.HeadTails("flie</a>s").ToArray());
		}

		[Fact]
		public void HeadTails_HeadKeepsUmlautAndEszett()
		{
			// Letters are char.IsLetter, so a German head survives intact.
			Assert.Equal(
				new[] { ("straß", "e") },
				AnchorSplitSpellSuppression.HeadTails("die straß</a>e dort").ToArray());
		}

		[Theory]
		[InlineData("vill</a>age")] // tail "age" = 3 letters
		[InlineData("rea</a>son")]  // "son" = 3
		[InlineData("eleph</a>ant")]// "ant" = 3
		[InlineData("pengu</a>in")] // "in"  = 2
		[InlineData("ribb</a>on")]  // "on"  = 2
		[InlineData("descri</a>be")]// "be"  = 2
		public void HeadTails_TailLongerThanOneLetter_Skipped(string context)
		{
			// The deliberately-waived cap: only a single severed letter qualifies. These longer
			// tails are exactly the camouflaged real-word splits the cap exists to leave alone.
			Assert.Empty(AnchorSplitSpellSuppression.HeadTails(context));
		}

		[Theory]
		[InlineData("head</a> word")] // whitespace immediately after </a> → nothing severed
		[InlineData("head</a>")]       // end of string → no tail
		[InlineData("head</a>.")]      // punctuation tail → not a letter
		[InlineData("head</a>2")]      // digit tail → cannot rejoin to a word
		[InlineData("</a>s")]          // no head before </a>
		public void HeadTails_NoSeveredWord_Skipped(string context)
		{
			Assert.Empty(AnchorSplitSpellSuppression.HeadTails(context));
		}

		[Fact]
		public void HeadTails_MultipleAnchors_YieldsEach()
		{
			Assert.Equal(
				new[] { ("boxe", "s"), ("flie", "s") },
				AnchorSplitSpellSuppression.HeadTails("aa boxe</a>s bb flie</a>s cc").ToArray());
		}

		// ── Matcher: filtering, keying, ForFile ─────────────────────────

		private static QualityIssue Issue(string filename, string type, string context) =>
			new(filename, type, "detail", context);

		[Fact]
		public void Matcher_BuildsPerFileHeadTailMap()
		{
			var m = new AnchorSplitSpellSuppression(new[]
			{
				Issue("file1", AnchorSplitSpellSuppression.AnchorSplitType, "x boxe</a>s y"),
			});

			Assert.False(m.IsEmpty);
			var heads = m.ForFile("file1");
			Assert.True(heads.ContainsKey("boxe"));
			Assert.Equal(new[] { "s" }, heads["boxe"].ToArray());
			Assert.Empty(m.ForFile("unknown-file"));
		}

		[Fact]
		public void Matcher_IgnoresOtherIssueTypes()
		{
			var m = new AnchorSplitSpellSuppression(new[]
			{
				Issue("file1", "WORD_COLLISION", "x boxe</a>s y"),
			});

			Assert.True(m.IsEmpty);
		}

		[Fact]
		public void Matcher_NullIssues_IsEmpty()
		{
			Assert.True(new AnchorSplitSpellSuppression(null).IsEmpty);
		}

		[Fact]
		public void Matcher_SameHeadDifferentTails_CollectsBoth()
		{
			var m = new AnchorSplitSpellSuppression(new[]
			{
				Issue("file1", AnchorSplitSpellSuppression.AnchorSplitType, "boxe</a>s"),
				Issue("file1", AnchorSplitSpellSuppression.AnchorSplitType, "boxe</a>n"),
			});

			Assert.Equal(new[] { "n", "s" }, m.ForFile("file1")["boxe"].OrderBy(t => t).ToArray());
		}

		// ── DICTIONARY half: the RunChecker guard end-to-end ────────────

		private static TextRun TextNode(string text) =>
			new(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text);

		// Fake checker: every letter-bearing token NOT in the accepted set is a miss (mirrors the
		// real verdict shape). So a non-word head misses, and its rejoin passes iff it is accepted.
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

		private static IReadOnlyDictionary<string, IReadOnlySet<string>> Tails(string head, params string[] tails) =>
			new Dictionary<string, IReadOnlySet<string>>
			{
				[head] = new HashSet<string>(tails),
			};

		private static List<string> Words(TextRun run, RunCheck checker, IReadOnlyDictionary<string, IReadOnlySet<string>>? tails) =>
			RunChecker.Check(run, "en", checker, anchorSplitTails: tails).Select(f => f.Word).ToList();

		[Fact]
		public void Guard_RejoinValid_MutesHead()
		{
			// "boxe" is a miss; severed tail "s" rejoins to "boxes", which is accepted → muted.
			Assert.Empty(Words(TextNode("boxe"), Checker("boxes"), Tails("boxe", "s")));
		}

		[Fact]
		public void Guard_RejoinNotAWord_HeadSurfaces()
		{
			// Nothing accepts "boxes" either, so the rejoin is not a word → the head stays flagged.
			Assert.Equal(new[] { "boxe" }, Words(TextNode("boxe"), Checker(), Tails("boxe", "s")));
		}

		[Fact]
		public void Guard_NoTailMap_HeadSurfaces()
		{
			// Without the cross-pass input the guard never fires.
			Assert.Equal(new[] { "boxe" }, Words(TextNode("boxe"), Checker("boxes"), null));
		}

		[Fact]
		public void Guard_DifferentHeadInMap_NotMuted()
		{
			Assert.Equal(new[] { "boxe" }, Words(TextNode("boxe"), Checker("boxes"), Tails("flie", "s")));
		}

		[Fact]
		public void Guard_AnyTailValid_Mutes()
		{
			// "boxex" is not a word but "boxes" is; suppress-if-any-valid.
			Assert.Empty(Words(TextNode("boxe"), Checker("boxes"), Tails("boxe", "x", "s")));
		}

		[Fact]
		public void Guard_BackstopPreserved_UnrelatedTypoStillSurfaces()
		{
			// A genuine typo that is not a severed head still surfaces, even on a page that has an
			// anchor split — only the head named in the map is eligible for muting.
			Assert.Equal(new[] { "teh" }, Words(TextNode("teh"), Checker("the", "boxes"), Tails("boxe", "s")));
		}
	}
}
