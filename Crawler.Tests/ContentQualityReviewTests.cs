using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// ContentQualityTriage.DiscardToNew resets a triaged issue record to its
	/// detection state — Status back to "new", all triage-decision fields
	/// cleared, detection facts preserved. The interactive review walk itself
	/// (ReviewTriagedQualityItems) reads the keyboard and isn't unit-tested; the
	/// reset logic it relies on is extracted here and pinned. Mirrors
	/// SpellTrackingReviewTests for the spelling side.
	/// </summary>
	public class ContentQualityReviewTests
	{
		private static IssueTracking.IssueRecord Triaged(
			string status, string comment = "some decision", string ticket = "TCK-1")
			=> new()
			{
				Source = "triage",
				Ticket = ticket,
				DateReported = "2026-05-12",
				Type = "QUALITY",
				Url = "https://x/de/home/page.html",
				Status = status,
				DateFound = "2026-05-10",
				DateLastSeen = "2026-05-20",
				Word = "UNWANTED_PATTERN:kwitt",
				Comment = comment,
				Language = "de",
				SourceLabel = "span[#text]",
				Excerpt = "Jetzt mit kwitt bezahlen",
			};

		[Fact]
		public void DiscardToNew_SetsStatusNew()
		{
			var result = ContentQualityTriage.DiscardToNew(Triaged("pending"));
			Assert.Equal("new", result.Status);
		}

		[Fact]
		public void DiscardToNew_ClearsAllTriageDecisionFields()
		{
			var result = ContentQualityTriage.DiscardToNew(Triaged("pending"));
			Assert.Equal(string.Empty, result.Comment);
			Assert.Equal(string.Empty, result.Ticket);
			Assert.Equal(string.Empty, result.DateReported);
		}

		[Fact]
		public void DiscardToNew_PreservesDetectionFacts()
		{
			var original = Triaged("wontfix");
			var result = ContentQualityTriage.DiscardToNew(original);

			// Detection facts must survive the reset — they describe WHAT was
			// found and WHERE, independent of any triage decision.
			Assert.Equal(original.Type, result.Type);
			Assert.Equal(original.Url, result.Url);
			Assert.Equal(original.Word, result.Word);
			Assert.Equal(original.Language, result.Language);
			Assert.Equal(original.SourceLabel, result.SourceLabel);
			Assert.Equal(original.Excerpt, result.Excerpt);
			Assert.Equal(original.DateFound, result.DateFound);
		}

		[Theory]
		[InlineData("pending")]
		[InlineData("wontfix")]
		public void DiscardToNew_WorksForBothReviewableStatuses(string status)
		{
			// The review pass walks both pending and wontfix records; discard
			// must reset either to the same clean "new" state.
			var result = ContentQualityTriage.DiscardToNew(Triaged(status));
			Assert.Equal("new", result.Status);
			Assert.Equal(string.Empty, result.Comment);
			Assert.Equal(string.Empty, result.Ticket);
		}

		[Fact]
		public void DiscardToNew_DiscardedRecordSurvivesSuppressionFilter()
		{
			// The suppression filter drops records whose status is one of
			// wontfix/config/fixed/pending. After discard the status is "new",
			// so the record must NOT be classified as suppressed — this is what
			// makes a discarded item reappear in the live triage walk.
			var discarded = ContentQualityTriage.DiscardToNew(Triaged("wontfix"));
			var suppressedStatuses = new[] { "wontfix", "config", "fixed", "pending" };
			Assert.DoesNotContain(discarded.Status, suppressedStatuses);
		}

		// ── ExtractHighlightPatterns ──────────────────────────────────────────
		// Shared by triage (red) and review (amber) so both mark identical spans.
		// Pinned here so the two highlight paths can never drift on what to mark.

		[Fact]
		public void ExtractHighlightPatterns_SingularMarker_ReturnsOnePattern()
		{
			var result = ContentQualityTriage.ExtractHighlightPatterns(
				"UNWANTED_PATTERN:Legacy: Old Name — pattern: kwitt");
			Assert.Equal(new[] { "kwitt" }, result);
		}

		[Fact]
		public void ExtractHighlightPatterns_PluralMarker_SplitsOnCommaSpace()
		{
			var result = ContentQualityTriage.ExtractHighlightPatterns(
				"UNWANTED_PATTERN:Legacy: Old Name — patterns: kwitt, paydirekt");
			Assert.Equal(new[] { "kwitt", "paydirekt" }, result);
		}

		[Fact]
		public void ExtractHighlightPatterns_NoMarker_ReturnsAfterColon()
		{
			var result = ContentQualityTriage.ExtractHighlightPatterns("UNWANTED_PATTERN:kwitt");
			Assert.Equal(new[] { "kwitt" }, result);
		}

		[Fact]
		public void ExtractHighlightPatterns_Empty_ReturnsEmpty()
		{
			Assert.Empty(ContentQualityTriage.ExtractHighlightPatterns(string.Empty));
		}

		// —— ComputeReviewQuoteTriggers (D105/D106) ——————————————
		// Review recomputes the quote offender offsets the in-memory TriageGroup
		// carries but the review pass never sees. Pinned so the recompute can never
		// silently drift from live triage. A German „…“ pair (closer U+201C) next to
		// an English “…” pair mixes two double-quote systems on one block →
		// QUOTE_SYSTEM_MIX, which LocateFlags evaluates unconditionally (no config
		// flag, no declared language needed) — a stable trigger to pin against.
		private const string MixExcerpt =
			"Ein \u201Edeutsches\u201C Paar und ein \u201Cenglisches\u201D Paar.";

		private static IssueTracking.IssueRecord QuoteRec(
			string word, string excerpt, string language = "", string sourceLabel = "QUOTE ISSUES")
			=> new()
			{
				Source = "triage",
				Type = "QUALITY",
				Url = "https://x/de/home/page.html",
				Status = "pending",
				Word = word,
				SourceLabel = sourceLabel,
				Excerpt = excerpt,
				Language = language,
			};

		[Fact]
		public void ComputeReviewQuoteTriggers_SystemMix_MarksAQuoteGlyph()
		{
			var pos = ContentQualityTriage.ComputeReviewQuoteTriggers(
				QuoteRec("QUOTE_SYSTEM_MIX", MixExcerpt), new ContentQualityConfig());
			Assert.NotEmpty(pos);
			// every marked offset is in range and lands on a typographic quote glyph
			Assert.All(pos, i =>
			{
				Assert.InRange(i, 0, MixExcerpt.Length - 1);
				Assert.Contains(MixExcerpt[i], "\u201E\u201C\u201D\u00AB\u00BB\u2018\u2019");
			});
		}

		[Fact]
		public void ComputeReviewQuoteTriggers_TypeFilter_ExcludesOtherTypes()
		{
			// The excerpt is all-typographic, so it raises QUOTE_SYSTEM_MIX but never
			// QUOTE_MIXED_KIND (straight/typographic mix). Asking for the type the
			// excerpt does not raise must yield no offsets (per-record-type filter).
			var pos = ContentQualityTriage.ComputeReviewQuoteTriggers(
				QuoteRec("QUOTE_MIXED_KIND", MixExcerpt), new ContentQualityConfig());
			Assert.Empty(pos);
		}

		[Fact]
		public void ComputeReviewQuoteTriggers_NonQuoteRecord_ReturnsEmpty()
		{
			var pos = ContentQualityTriage.ComputeReviewQuoteTriggers(
				QuoteRec("QUOTE_SYSTEM_MIX", MixExcerpt, sourceLabel: "LIGATURE"),
				new ContentQualityConfig());
			Assert.Empty(pos);
		}

		[Fact]
		public void ComputeReviewQuoteTriggers_EmptyExcerpt_ReturnsEmpty()
		{
			var pos = ContentQualityTriage.ComputeReviewQuoteTriggers(
				QuoteRec("QUOTE_SYSTEM_MIX", string.Empty), new ContentQualityConfig());
			Assert.Empty(pos);
		}
	}
}
