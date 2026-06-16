using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// [#581] Pure-helper contract for spelling triage now that decisions live in
	/// IssueTracking (not SpellTracking). Covers the identity key, the SPELLING
	/// predicate, gone-is-gone reconciliation against this run's detected keys, the
	/// ticket→IssueRecord decision mapping, the upsert-by-Key, and the SPELLING-exempt
	/// end-of-run Merge. The interactive loop (RunSpellCheckTriage) reads the keyboard
	/// and is not unit-tested; the logic it relies on is extracted here and pinned.
	/// </summary>
	public class SpellTriageLedgerTests
	{
		private static TicketOccurrence Occ(
			string path = "p[#text]", string excerpt = "ex", RunSource src = RunSource.TextNode)
			=> new(src, path, excerpt);

		private static WordTicket Ticket(
			string word, string url, string lang = "de", params TicketOccurrence[] occ)
			=> new(word, url, lang, occ);

		private static IssueTracking.IssueRecord Rec(
			string type, string url, string word, string status = "pending")
			=> new() { Type = type, Url = url, Word = word, Status = status };

		// ── SpellingKey ────────────────────────────────────────────────────────────

		[Fact]
		public void SpellingKey_FormatsAsTypeUrlWord()
		{
			Assert.Equal("SPELLING|https://x/p|Wort", SpellTriage.SpellingKey("https://x/p", "Wort"));
		}

		[Fact]
		public void SpellingKey_MatchesIssueRecordKey()
		{
			var rec = Rec("SPELLING", "https://x/p", "Wort");
			Assert.Equal(rec.Key, SpellTriage.SpellingKey("https://x/p", "Wort"));
		}

		// ── IsSpelling ─────────────────────────────────────────────────────────────

		[Fact]
		public void IsSpelling_TrueForSpellingType()
		{
			Assert.True(SpellTriage.IsSpelling(Rec("SPELLING", "u", "w")));
		}

		[Fact]
		public void IsSpelling_FalseForOtherType()
		{
			Assert.False(SpellTriage.IsSpelling(Rec("404", "u", "")));
		}

		[Fact]
		public void IsSpelling_CaseInsensitive()
		{
			Assert.True(SpellTriage.IsSpelling(Rec("spelling", "u", "w")));
		}

		// ── ReconcileSpelling ────────────────────────────────────────────────────────

		[Fact]
		public void ReconcileSpelling_DropsSpellingNotInDetected()
		{
			var ledger = new List<IssueTracking.IssueRecord> { Rec("SPELLING", "u", "gone") };
			var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			var result = SpellTriage.ReconcileSpelling(ledger, detected);

			Assert.Empty(result);
		}

		[Fact]
		public void ReconcileSpelling_KeepsSpellingInDetected()
		{
			var ledger = new List<IssueTracking.IssueRecord> { Rec("SPELLING", "u", "stay") };
			var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				SpellTriage.SpellingKey("u", "stay")
			};

			var result = SpellTriage.ReconcileSpelling(ledger, detected);

			Assert.Single(result);
			Assert.Equal("stay", result[0].Word);
		}

		[Fact]
		public void ReconcileSpelling_KeepsAllNonSpellingRegardlessOfDetected()
		{
			var ledger = new List<IssueTracking.IssueRecord>
			{
				Rec("404", "u1", ""),
				Rec("SEO_TITLE", "u2", ""),
			};
			var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			var result = SpellTriage.ReconcileSpelling(ledger, detected);

			Assert.Equal(2, result.Count);
		}

		[Fact]
		public void ReconcileSpelling_MixedKeepsNonSpellingAndDetectedSpellingOnly()
		{
			var ledger = new List<IssueTracking.IssueRecord>
			{
				Rec("404", "u1", ""),
				Rec("SPELLING", "u2", "stay"),
				Rec("SPELLING", "u3", "gone"),
			};
			var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				SpellTriage.SpellingKey("u2", "stay")
			};

			var result = SpellTriage.ReconcileSpelling(ledger, detected);

			Assert.Equal(2, result.Count);
			Assert.Contains(result, r => r.Type == "404");
			Assert.Contains(result, r => r.Word == "stay");
			Assert.DoesNotContain(result, r => r.Word == "gone");
		}

		[Fact]
		public void ReconcileSpelling_DoesNotMutateInput()
		{
			var ledger = new List<IssueTracking.IssueRecord> { Rec("SPELLING", "u", "gone") };
			var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			SpellTriage.ReconcileSpelling(ledger, detected);

			Assert.Single(ledger);
		}

		// ── BuildDecision ──────────────────────────────────────────────────────────

		[Fact]
		public void BuildDecision_MapsTicketFields()
		{
			var ticket = Ticket("Wort", "https://x/p", "en", Occ());

			var d = SpellTriage.BuildDecision(ticket, "pending", "TCK-9", "reason", "2026-06-08");

			Assert.Equal("triage", d.Source);
			Assert.Equal("SPELLING", d.Type);
			Assert.Equal("https://x/p", d.Url);
			Assert.Equal("Wort", d.Word);
			Assert.Equal("pending", d.Status);
			Assert.Equal("TCK-9", d.Ticket);
			Assert.Equal("reason", d.Comment);
			Assert.Equal("en", d.Language);
		}

		[Fact]
		public void BuildDecision_UsesFirstOccurrenceForSourceLabelAndExcerpt()
		{
			var ticket = Ticket("Wort", "u", "de",
				Occ("img[@alt]", "first-excerpt"),
				Occ("p[#text]", "second-excerpt"));

			var d = SpellTriage.BuildDecision(ticket, "pending", "", "", "2026-06-08");

			Assert.Equal("img[@alt]", d.SourceLabel);
			Assert.Equal("first-excerpt", d.Excerpt);
		}

		[Fact]
		public void BuildDecision_NoOccurrences_EmptySourceLabelAndExcerpt()
		{
			var ticket = Ticket("Wort", "u");

			var d = SpellTriage.BuildDecision(ticket, "wontfix", "", "", "2026-06-08");

			Assert.Equal(string.Empty, d.SourceLabel);
			Assert.Equal(string.Empty, d.Excerpt);
		}

		[Fact]
		public void BuildDecision_SetsDatesToSuppliedToday()
		{
			var ticket = Ticket("Wort", "u", "de", Occ());

			var d = SpellTriage.BuildDecision(ticket, "pending", "", "", "2026-06-08");

			Assert.Equal("2026-06-08", d.DateFound);
			Assert.Equal("2026-06-08", d.DateReported);
			Assert.Equal("2026-06-08", d.DateLastSeen);
		}

		[Fact]
		public void BuildDecision_NullTicketRef_EmptyTicket()
		{
			var ticket = Ticket("Wort", "u", "de", Occ());

			var d = SpellTriage.BuildDecision(ticket, "pending", null!, "c", "2026-06-08");

			Assert.Equal(string.Empty, d.Ticket);
		}

		[Fact]
		public void BuildDecision_WontfixStatusPreserved()
		{
			var ticket = Ticket("Wort", "u", "de", Occ());

			var d = SpellTriage.BuildDecision(ticket, "wontfix", "", "Intentional", "2026-06-08");

			Assert.Equal("wontfix", d.Status);
		}

		// ── UpsertSpelling ─────────────────────────────────────────────────────────

		[Fact]
		public void UpsertSpelling_AddsWhenAbsent()
		{
			var ledger = new List<IssueTracking.IssueRecord>();

			SpellTriage.UpsertSpelling(ledger, Rec("SPELLING", "u", "w"));

			Assert.Single(ledger);
		}

		[Fact]
		public void UpsertSpelling_ReplacesWhenKeyPresent()
		{
			var ledger = new List<IssueTracking.IssueRecord> { Rec("SPELLING", "u", "w", "pending") };

			SpellTriage.UpsertSpelling(ledger, Rec("SPELLING", "u", "w", "wontfix"));

			Assert.Single(ledger);
			Assert.Equal("wontfix", ledger[0].Status);
		}

		[Fact]
		public void UpsertSpelling_DistinctKeysCoexist()
		{
			var ledger = new List<IssueTracking.IssueRecord> { Rec("SPELLING", "u", "w1") };

			SpellTriage.UpsertSpelling(ledger, Rec("SPELLING", "u", "w2"));

			Assert.Equal(2, ledger.Count);
		}

		// ── MergeExempt ──────────────────────────────────────────────────────────────

		[Fact]
		public void MergeExempt_SpellingSurvivesWhenAbsentFromDetected()
		{
			var existing = new List<IssueTracking.IssueRecord> { Rec("SPELLING", "u", "w") };
			var detected = new List<IssueTracking.IssueRecord>();

			var result = IssueTracking.MergeExempt(existing, detected, "SPELLING");

			Assert.Single(result);
			Assert.Equal("SPELLING", result[0].Type);
		}

		[Fact]
		public void MergeExempt_NonExemptGoneIsGoneStillDrops()
		{
			var existing = new List<IssueTracking.IssueRecord>
			{
				Rec("404", "u1", ""),
				Rec("SPELLING", "u2", "w"),
			};
			var detected = new List<IssueTracking.IssueRecord>();

			var result = IssueTracking.MergeExempt(existing, detected, "SPELLING");

			Assert.Single(result);
			Assert.Equal("SPELLING", result[0].Type);
		}

		[Fact]
		public void MergeExempt_AddsNewlyDetectedNonExempt()
		{
			var existing = new List<IssueTracking.IssueRecord>();
			var detected = new List<IssueTracking.IssueRecord> { Rec("404", "u", "") };

			var result = IssueTracking.MergeExempt(existing, detected, "SPELLING");

			Assert.Single(result);
			Assert.Equal("404", result[0].Type);
		}

		[Fact]
		public void MergeExempt_MultipleSpellingRowsAllSurvive()
		{
			var existing = new List<IssueTracking.IssueRecord>
			{
				Rec("SPELLING", "u1", "w1"),
				Rec("SPELLING", "u2", "w2"),
			};
			var detected = new List<IssueTracking.IssueRecord>();

			var result = IssueTracking.MergeExempt(existing, detected, "SPELLING");

			Assert.Equal(2, result.Count);
			Assert.All(result, r => Assert.Equal("SPELLING", r.Type));
		}
	}
}
