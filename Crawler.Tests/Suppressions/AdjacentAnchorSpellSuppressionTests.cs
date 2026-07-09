using System.Collections.Generic;
using System.Linq;
using Crawler.Quality;
using Crawler.Suppressions;
using Xunit;

namespace Crawler.Tests.Suppressions
{
	/// <summary>
	/// Pins the MARKUP half of the ADJACENT_ANCHOR → spelling cross-pass dedup
	/// (<see cref="AdjacentAnchorSpellSuppression"/>): parsing the finding DETAIL
	/// ("[boundaryAt] „textA“ + „textB“"), ordering by boundary, and forming each fracture's two
	/// non-empty fragments into a (left, right) pair — directly when one finding carries both texts,
	/// or across the empty-anchor bridge when an empty &lt;a&gt; splits them into two findings. The
	/// DICTIONARY half (the gate that a join must be a real word) lives in RunChecker and is pinned
	/// separately. All fixtures are invented; "And"/"roid" → "Android" stands in for the CMS shape.
	/// </summary>
	public class AdjacentAnchorSpellSuppressionTests
	{
		private const string Type = "ADJACENT_ANCHOR";

		// Mirrors the emitter's format in Crawler.Quality.MisplacedAnchors (German quotes U+201E/U+201C).
		private static string Detail(int pos, string a, string b) => $"[{pos}] \u201e{a}\u201c + \u201e{b}\u201c";

		private static QualityIssue Issue(string file, string detail) => new(file, Type, detail, "ctx");

		private static List<(int, string, string)> F(params (int, string, string)[] items) => items.ToList();

		// ── Pairs: ordering / empty-bridge chaining ─────────────────────────

		[Fact]
		public void Pairs_BothTextsInOneFinding_PairsThem()
		{
			Assert.Equal(
				new[] { ("And", "roid") },
				AdjacentAnchorSpellSuppression.Pairs(F((10, "And", "roid"))).ToArray());
		}

		[Fact]
		public void Pairs_EmptyAnchorBetween_ChainsAcrossTwoFindings()
		{
			// <a>And</a><a></a><a>roid</a> → finding (And,"") then ("",roid).
			Assert.Equal(
				new[] { ("And", "roid") },
				AdjacentAnchorSpellSuppression.Pairs(F((10, "And", ""), (40, "", "roid"))).ToArray());
		}

		[Fact]
		public void Pairs_OrdersByBoundaryFirst()
		{
			// Same chain, findings supplied out of boundary order — sort must reassemble it.
			Assert.Equal(
				new[] { ("And", "roid") },
				AdjacentAnchorSpellSuppression.Pairs(F((40, "", "roid"), (10, "And", ""))).ToArray());
		}

		[Fact]
		public void Pairs_LeadingEmpty_NoPair()
		{
			// <a></a><a>Word</a> → ("", "Word"): a whole word after a stray empty anchor, not a fracture.
			Assert.Empty(AdjacentAnchorSpellSuppression.Pairs(F((10, "", "Word"))));
		}

		[Fact]
		public void Pairs_TrailingEmpty_NoPair()
		{
			// <a>Word</a><a></a> → ("Word", ""): no following ("", x) to bridge to.
			Assert.Empty(AdjacentAnchorSpellSuppression.Pairs(F((10, "Word", ""))));
		}

		[Fact]
		public void Pairs_ThreeNonEmptyFragments_DoesNotWholeChain()
		{
			// <a>A</a><a>nd</a><a>roid</a>: scope is two fragments. Adjacent findings pair (A,nd) and
			// (nd,roid); there is no (A,roid) whole-run join, so "roid" is not silently absorbed.
			var pairs = AdjacentAnchorSpellSuppression.Pairs(F((10, "A", "nd"), (20, "nd", "roid"))).ToList();
			Assert.Contains(("A", "nd"), pairs);
			Assert.Contains(("nd", "roid"), pairs);
			Assert.DoesNotContain(("A", "roid"), pairs);
		}

		// ── ForFile: the emitted fragment → join map ─────────────────────────

		[Fact]
		public void ForFile_RecordsJoinUnderBothFragments()
		{
			var sut = new AdjacentAnchorSpellSuppression(new[]
			{
				Issue("file1", Detail(10, "And", "")),
				Issue("file1", Detail(40, "", "roid")),
			});

			var map = sut.ForFile("file1");
			Assert.True(map.TryGetValue("And", out var leftJoins));
			Assert.Contains("Android", leftJoins);
			Assert.True(map.TryGetValue("roid", out var rightJoins));
			Assert.Contains("Android", rightJoins);
		}

		[Fact]
		public void ForFile_UnknownFile_Empty()
		{
			var sut = new AdjacentAnchorSpellSuppression(new[] { Issue("file1", Detail(10, "And", "roid")) });
			Assert.Empty(sut.ForFile("other"));
		}

		[Fact]
		public void NonAdjacentAnchorIssueType_Ignored()
		{
			var sut = new AdjacentAnchorSpellSuppression(new[]
			{
				new QualityIssue("file1", "SPLIT_WORD_ANCHOR", Detail(10, "And", "roid"), "ctx"),
			});
			Assert.True(sut.IsEmpty);
		}

		[Fact]
		public void MalformedDetail_Ignored()
		{
			var sut = new AdjacentAnchorSpellSuppression(new[] { Issue("file1", "no brackets or quotes here") });
			Assert.True(sut.IsEmpty);
		}

		[Fact]
		public void NullIssues_Empty()
		{
			var sut = new AdjacentAnchorSpellSuppression(null);
			Assert.True(sut.IsEmpty);
		}
	}
}
