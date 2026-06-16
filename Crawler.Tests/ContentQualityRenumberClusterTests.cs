using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ContentQualityTriage.RenumberCluster and its helpers
	/// (ExtractLeadingBracketPosition, StripLeadingBracket).
	///
	/// The renumbering exists to recover source-order within a per-page
	/// cluster of same-type findings (#454). The detector writes a raw
	/// source position in the Detail (e.g., "[1247] „And" + „"")"; the
	/// renumber step sorts by that position and re-prefixes display with
	/// sequential "[01]"/"[02]"/... Single-entry clusters get the prefix
	/// stripped entirely.
	///
	/// The helper is generic over T (the source-record type) so it works
	/// for ADJACENT_ANCHOR today and is reusable for future types with
	/// position-bearing Detail strings (see backlog: QUOTE_ISSUES uses
	/// "at position N" — different extractor, same plumbing).
	/// </summary>
	public class ContentQualityRenumberClusterTests
	{
		// ── ExtractLeadingBracketPosition ────────────────────────────────

		[Fact]
		public void ExtractLeadingBracket_BasicNumber()
			=> Assert.Equal(1247, ContentQualityTriage.ExtractLeadingBracketPosition("[1247] „And\u201c + „\u201c"));

		[Fact]
		public void ExtractLeadingBracket_NoBracket_ReturnsNull()
			=> Assert.Null(ContentQualityTriage.ExtractLeadingBracketPosition("„And\u201c + „\u201c"));

		[Fact]
		public void ExtractLeadingBracket_BracketMidString_ReturnsNull()
			=> Assert.Null(ContentQualityTriage.ExtractLeadingBracketPosition("something [123] later"));

		[Fact]
		public void ExtractLeadingBracket_NonNumeric_ReturnsNull()
			=> Assert.Null(ContentQualityTriage.ExtractLeadingBracketPosition("[abc] thing"));

		[Fact]
		public void ExtractLeadingBracket_Empty_ReturnsNull()
			=> Assert.Null(ContentQualityTriage.ExtractLeadingBracketPosition(""));

		[Fact]
		public void ExtractLeadingBracket_Null_ReturnsNull()
			=> Assert.Null(ContentQualityTriage.ExtractLeadingBracketPosition(null!));

		[Fact]
		public void ExtractLeadingBracket_LargeNumber()
			=> Assert.Equal(123456, ContentQualityTriage.ExtractLeadingBracketPosition("[123456] thing"));

		// ── StripLeadingBracket ──────────────────────────────────────────

		[Fact]
		public void StripLeadingBracket_RemovesPrefixAndSpace()
			=> Assert.Equal("„And\u201c + „\u201c", ContentQualityTriage.StripLeadingBracket("[1247] „And\u201c + „\u201c"));

		[Fact]
		public void StripLeadingBracket_NoPrefix_Unchanged()
			=> Assert.Equal("plain detail", ContentQualityTriage.StripLeadingBracket("plain detail"));

		[Fact]
		public void StripLeadingBracket_Empty_ReturnsEmpty()
			=> Assert.Equal("", ContentQualityTriage.StripLeadingBracket(""));

		[Fact]
		public void StripLeadingBracket_OnlyBracket_StripsClean()
			=> Assert.Equal("rest", ContentQualityTriage.StripLeadingBracket("[1] rest"));

		// ── RenumberCluster: size-1 cluster ──────────────────────────────

		[Fact]
		public void Renumber_SingleEntry_StripsPrefix()
		{
			var input = new[] { "[1247] „And\u201c + „\u201c" };
			var result = ContentQualityTriage.RenumberCluster(
				input,
				getDetail: s => s,
				extractPosition: ContentQualityTriage.ExtractLeadingBracketPosition);
			Assert.Single(result);
			Assert.Equal("„And\u201c + „\u201c", result[0].Detail);
		}

		[Fact]
		public void Renumber_SingleEntry_NoPrefix_Unchanged()
		{
			var input = new[] { "plain" };
			var result = ContentQualityTriage.RenumberCluster(
				input,
				getDetail: s => s,
				extractPosition: ContentQualityTriage.ExtractLeadingBracketPosition);
			Assert.Equal("plain", result[0].Detail);
		}

		// ── RenumberCluster: size-2 cluster (the Android-cluster case) ───

		[Fact]
		public void Renumber_TwoEntries_SortsByPositionAndRenumbers()
		{
			// Detector emits in undefined order (e.g., LIFO from ConcurrentBag).
			// Pass them in REVERSE source order to confirm the sort recovers
			// the right ordering.
			var input = new[]
			{
				"[8392] „\u201c + „roid\u201c",      // collision #2 in source
				"[1247] „And\u201c + „\u201c",       // collision #1 in source
			};
			var result = ContentQualityTriage.RenumberCluster(
				input,
				getDetail: s => s,
				extractPosition: ContentQualityTriage.ExtractLeadingBracketPosition);

			Assert.Equal(2, result.Count);
			Assert.Equal("[01] „And\u201c + „\u201c", result[0].Detail);
			Assert.Equal("[02] „\u201c + „roid\u201c", result[1].Detail);
		}

		[Fact]
		public void Renumber_TwoEntries_AlreadyOrdered_StaysCorrect()
		{
			var input = new[]
			{
				"[1247] first",
				"[8392] second",
			};
			var result = ContentQualityTriage.RenumberCluster(
				input,
				getDetail: s => s,
				extractPosition: ContentQualityTriage.ExtractLeadingBracketPosition);

			Assert.Equal("[01] first", result[0].Detail);
			Assert.Equal("[02] second", result[1].Detail);
		}

		// ── RenumberCluster: larger cluster (6-sub-row case) ─

		[Fact]
		public void Renumber_SixEntries_AllRenumbered()
		{
			// Six findings, arbitrary input order; positions ascending in source
			// at 100, 200, 300, 400, 500, 600. Shuffled here on input.
			var input = new[]
			{
				"[400] d",
				"[100] a",
				"[600] f",
				"[200] b",
				"[500] e",
				"[300] c",
			};
			var result = ContentQualityTriage.RenumberCluster(
				input,
				getDetail: s => s,
				extractPosition: ContentQualityTriage.ExtractLeadingBracketPosition);

			Assert.Equal(6, result.Count);
			Assert.Equal("[01] a", result[0].Detail);
			Assert.Equal("[02] b", result[1].Detail);
			Assert.Equal("[03] c", result[2].Detail);
			Assert.Equal("[04] d", result[3].Detail);
			Assert.Equal("[05] e", result[4].Detail);
			Assert.Equal("[06] f", result[5].Detail);
		}

		// ── RenumberCluster: empty input ─────────────────────────────────

		[Fact]
		public void Renumber_Empty_ReturnsEmpty()
		{
			var result = ContentQualityTriage.RenumberCluster(
				new string[] { },
				getDetail: s => s,
				extractPosition: ContentQualityTriage.ExtractLeadingBracketPosition);
			Assert.Empty(result);
		}

		// ── RenumberCluster: defensive — missing position info ──────────

		[Fact]
		public void Renumber_MissingPosition_SortsLast()
		{
			// One entry has no [N] prefix — extractor returns null → sorts last.
			var input = new[]
			{
				"[200] has-position",
				"no-position",
				"[100] also-has-position",
			};
			var result = ContentQualityTriage.RenumberCluster(
				input,
				getDetail: s => s,
				extractPosition: ContentQualityTriage.ExtractLeadingBracketPosition);

			Assert.Equal(3, result.Count);
			// Sort: 100 → 200 → null (last)
			Assert.Equal("[01] also-has-position", result[0].Detail);
			Assert.Equal("[02] has-position", result[1].Detail);
			Assert.Equal("[03] no-position", result[2].Detail);
		}

		// ── RenumberCluster: generic over T (source object retained) ─────

		[Fact]
		public void Renumber_SourceObjectRetained_AlongsideDetail()
		{
			// Confirm the helper returns (Detail, Source) pairs so callers can
			// pair the renumbered Detail with the original entry's other fields
			// (e.g., Context for HTML display).
			var sources = new[]
			{
				new TestEntry(Detail: "[200] second", Tag: "B"),
				new TestEntry(Detail: "[100] first",  Tag: "A"),
			};
			var result = ContentQualityTriage.RenumberCluster(
				sources,
				getDetail: e => e.Detail,
				extractPosition: ContentQualityTriage.ExtractLeadingBracketPosition);

			Assert.Equal("[01] first", result[0].Detail);
			Assert.Equal("A", result[0].Source.Tag);
			Assert.Equal("[02] second", result[1].Detail);
			Assert.Equal("B", result[1].Source.Tag);
		}

		private sealed record TestEntry(string Detail, string Tag);
	}
}
