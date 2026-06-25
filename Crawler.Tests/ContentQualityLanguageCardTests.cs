using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// D050 — the QUOTE card surfaces the resolved declared-language set (what the
	/// finding was judged against). The interactive cards are keyboard-rendered and
	/// not unit-tested; this pins the data wiring they read: a quote group persists its
	/// language set as the joined IssueRecord.Language (so review shows it), and a
	/// non-quote group persists nothing.
	/// </summary>
	public class ContentQualityLanguageCardTests
	{
		[Fact]
		public void QuoteGroup_PersistsResolvedLanguageSet_AsJoinedRecordLanguage()
		{
			var g = new ContentQualityTriage.TriageGroup(
				DisplayType: "QUOTE ISSUES",
				Url: "https://x/de/home/service.html",
				Word: "QUOTE_SYSTEM_MIX",
				Comment: "",
				Excerpt: "…",
				IsTranslation: false,
				DisplayLines: new List<string> { "Excerpt : …" },
				TrackingWords: new List<string> { "QUOTE_SYSTEM_MIX", "QUOTE_UNMATCHED" },
				QuoteTriggerPositions: null,
				Languages: new[] { "de", "en" });

			var records = g.ToIssueRecords("new", "").ToList();

			Assert.Equal(2, records.Count);                                  // one per tracking word
			Assert.All(records, r => Assert.Equal("de, en", r.Language));    // joined set on each
		}

		[Fact]
		public void NonQuoteGroup_PersistsEmptyLanguage()
		{
			var g = new ContentQualityTriage.TriageGroup(
				DisplayType: "WORD_COLLISION",
				Url: "https://x/p",
				Word: "WORD_COLLISION",
				Comment: "",
				Excerpt: "…",
				IsTranslation: false,
				DisplayLines: new List<string> { "HTML    : …" });
			// Languages defaults to null → empty persisted

			var record = g.ToIssueRecords("new", "").Single();

			Assert.Equal(string.Empty, record.Language);
		}
	}
}
