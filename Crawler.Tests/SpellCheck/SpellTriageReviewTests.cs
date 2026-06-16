using System.Collections.Generic;
using System.Linq;
using Crawler;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery 639: the spelling review pass (mirror of the content-quality review). The interactive
	/// walk is console I/O (validated on a real run); these cover its pure decisions — which rows are
	/// reviewable + ordering, and the resurrect (remove-by-key). Fixtures use a generic tenant-free site.
	/// </summary>
	public class SpellTriageReviewTests
	{
		private static IssueTracking.IssueRecord Spelling(string url, string word, string status) =>
			new() { Type = "SPELLING", Url = url, Word = word, Status = status };

		[Fact]
		public void SelectReviewable_TakesPendingAndWontfix_PendingFirstThenWord()
		{
			var ledger = new List<IssueTracking.IssueRecord>
			{
				Spelling("https://example.test/a", "zebra", "wontfix"),
				Spelling("https://example.test/a", "alpha", "pending"),
				Spelling("https://example.test/b", "mango", "pending"),
				Spelling("https://example.test/c", "kiwi", "fixed"),            // wrong status → excluded
				new() { Type = "QUALITY", Url = "https://example.test/a", Word = "alpha", Status = "pending" }, // not spelling → excluded
			};

			var got = SpellTriage.SelectReviewableSpelling(ledger).Select(r => $"{r.Status}:{r.Word}").ToList();

			// pending before wontfix; within a status, by Word
			Assert.Equal(new[] { "pending:alpha", "pending:mango", "wontfix:zebra" }, got);
		}

		[Fact]
		public void RemoveSpellingRow_RemovesMatchingKey_LeavesOthers()
		{
			var ledger = new List<IssueTracking.IssueRecord>
			{
				Spelling("https://example.test/a", "alpha", "pending"),
				Spelling("https://example.test/a", "beta", "wontfix"),
				new() { Type = "QUALITY", Url = "https://example.test/a", Word = "alpha", Status = "pending" }, // same url+word, different type
			};

			bool removed = SpellTriage.RemoveSpellingRow(ledger, "SPELLING|https://example.test/a|alpha");

			Assert.True(removed);
			Assert.Equal(2, ledger.Count);
			Assert.DoesNotContain(ledger, r => SpellTriage.IsSpelling(r) && r.Word == "alpha");
			Assert.Contains(ledger, r => SpellTriage.IsSpelling(r) && r.Word == "beta");       // sibling spelling kept
			Assert.Contains(ledger, r => !SpellTriage.IsSpelling(r) && r.Word == "alpha");     // quality row untouched
		}

		[Fact]
		public void RemoveSpellingRow_AbsentKey_IsNoOp()
		{
			var ledger = new List<IssueTracking.IssueRecord> { Spelling("https://example.test/a", "alpha", "pending") };
			bool removed = SpellTriage.RemoveSpellingRow(ledger, "SPELLING|https://example.test/a|missing");
			Assert.False(removed);
			Assert.Single(ledger);
		}
	}
}
