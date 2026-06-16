using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for IssueTracking: Merge status transitions, Load/Save round-trip,
	/// and the five PromoteFrom* parsers.
	/// No Logger dependency — IssueTracking is pure file I/O + logic.
	/// </summary>
	[Collection("Logger")]
	public class IssueTrackingTests : IDisposable
	{
		private readonly string _tempDir;

		public IssueTrackingTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"it-test-{Guid.NewGuid()}");
			Directory.CreateDirectory(_tempDir);
		}

		public void Dispose()
		{
			if (Directory.Exists(_tempDir))
			{
				Directory.Delete(_tempDir, recursive: true);
			}
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		private string TmpFile(string name, params string[] lines)
		{
			var path = Path.Combine(_tempDir, name);
			File.WriteAllLines(path, lines, Encoding.UTF8);
			return path;
		}

		private static IssueTracking.IssueRecord New(
			string type, string url, string word = "",
			string status = "new", string dateFound = "2026-01-01",
			string dateLastSeen = "2026-01-01", string dateExpiry = "") =>
			new()
			{
				Type = type,
				Url = url,
				Word = word,
				Status = status,
				DateFound = dateFound,
				DateLastSeen = dateLastSeen,
				DateExpiry = dateExpiry,
			};

		// ── Load / Save round-trip ────────────────────────────────────────────

		[Fact]
		public void Load_NonExistentFile_ReturnsEmpty()
		{
			var result = IssueTracking.Load(Path.Combine(_tempDir, "missing.log"));
			Assert.Empty(result);
		}

		[Fact]
		public void LoadSave_RoundTrip_PreservesAllFields()
		{
			var path = Path.Combine(_tempDir, "IssueTracking.log");
			var records = new List<IssueTracking.IssueRecord>
			{
				New("404", "https://example.com/missing", status: "pending",
					dateFound: "2026-01-15", dateLastSeen: "2026-04-01")
			};
			IssueTracking.Save(path, records);
			var loaded = IssueTracking.Load(path);
			Assert.Single(loaded);
			Assert.Equal("404", loaded[0].Type);
			Assert.Equal("https://example.com/missing", loaded[0].Url);
			Assert.Equal("pending", loaded[0].Status);
			Assert.Equal("2026-01-15", loaded[0].DateFound);
			Assert.Equal("2026-04-01", loaded[0].DateLastSeen);
		}

		[Fact]
		public void Load_SkipsHeaderLine()
		{
			var path = TmpFile("it.log",
				"Source|Ticket|DateReported|Type|Url|Status|DateFound|DateLastSeen|DateExpiry|Word|Comment|Language|SourceLabel|Excerpt",
				"auto|2026-01-01|404|https://example.com/missing|new|2026-01-01|2026-01-01|||||||");
			var loaded = IssueTracking.Load(path);
			// Header skipped — only data row loaded
			Assert.Single(loaded);
		}

		// ── Merge — status transitions ────────────────────────────────────────

		[Fact]
		public void Merge_NewIssueNotExisting_Added()
		{
			var detected = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/new") };
			var result = IssueTracking.Merge([], detected);
			Assert.Single(result);
			Assert.Equal("new", result[0].Status);
		}

		[Fact]
		public void Merge_ExistingIssueStillDetected_KeptVerbatim()
		{
			var existing = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old", dateLastSeen: "2026-01-01") };
			var detected = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old") };
			var result = IssueTracking.Merge(existing, detected);
			Assert.Single(result);
			// gone-is-gone keeps the still-detected record verbatim (no rewrite).
			Assert.Equal("2026-01-01", result[0].DateLastSeen);
		}

		[Fact]
		public void Merge_ExistingIssueStillDetected_DateFoundUnchanged()
		{
			var existing = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old", dateFound: "2026-01-01") };
			var detected = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old") };
			var result = IssueTracking.Merge(existing, detected);
			Assert.Equal("2026-01-01", result[0].DateFound);
		}

		[Fact]
		public void Merge_ExistingNewIssueGone_Dropped()
		{
			var existing = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old", status: "new") };
			var result = IssueTracking.Merge(existing, []);
			Assert.Empty(result);
		}

		[Fact]
		public void Merge_ExistingPendingIssueGone_Dropped()
		{
			var existing = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old", status: "pending") };
			var result = IssueTracking.Merge(existing, []);
			Assert.Empty(result);
		}

		[Fact]
		public void Merge_ExistingOverdueIssueGone_Dropped()
		{
			var existing = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old", status: "overdue") };
			var result = IssueTracking.Merge(existing, []);
			Assert.Empty(result);
		}

		[Fact]
		public void Merge_ExistingReopenedIssueGone_Dropped()
		{
			var existing = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old", status: "reopened") };
			var result = IssueTracking.Merge(existing, []);
			Assert.Empty(result);
		}

		[Fact]
		public void Merge_ExistingWontfixIssueGone_Dropped()
		{
			// gone-is-gone drops EVERY disposition once the finding is gone —
			// including a deliberate wontfix (it re-surfaces as new if it returns).
			var existing = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old", status: "wontfix") };
			var result = IssueTracking.Merge(existing, []);
			Assert.Empty(result);
		}

		[Fact]
		public void Merge_ExistingConfigIssueGone_Dropped()
		{
			var existing = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old", status: "config") };
			var result = IssueTracking.Merge(existing, []);
			Assert.Empty(result);
		}

		[Fact]
		public void Merge_ExistingRedetected_StatusUnchanged()
		{
			// Still detected → existing kept verbatim, disposition preserved
			// (no fixed→reopened transition; that lifecycle is retired).
			var existing = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old", status: "wontfix") };
			var detected = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old") };
			var result = IssueTracking.Merge(existing, detected);
			Assert.Single(result);
			Assert.Equal("wontfix", result[0].Status);
		}

		[Fact]
		public void Merge_FixedIssueRedetected_DateFoundUnchanged()
		{
			var existing = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old", status: "fixed", dateFound: "2026-01-01") };
			var detected = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old") };
			var result = IssueTracking.Merge(existing, detected);
			Assert.Equal("2026-01-01", result[0].DateFound);
		}

		[Fact]
		public void Merge_ExistingPendingRedetected_Unchanged()
		{
			// The overdue-by-DateExpiry transition is retired; "overdue" is now a
			// render-time derivation, not a stored status. Still-detected → verbatim.
			var existing = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old", status: "pending",
					dateExpiry: "2020-01-01") };
			var detected = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old") };
			var result = IssueTracking.Merge(existing, detected);
			Assert.Single(result);
			Assert.Equal("pending", result[0].Status);
		}

		[Fact]
		public void Merge_PendingNotExpired_StatusUnchanged()
		{
			var existing = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old", status: "pending",
					dateExpiry: "2099-01-01") };
			var detected = new List<IssueTracking.IssueRecord>
				{ New("404", "https://example.com/old") };
			var result = IssueTracking.Merge(existing, detected);
			Assert.Equal("pending", result[0].Status);
		}

		[Fact]
		public void Merge_SpellingIdentityKeyIncludesWord()
		{
			// Two spelling issues on the same URL but different words are distinct
			// keys (Word is part of the key) — both are added.
			var detected = new List<IssueTracking.IssueRecord>
			{
				New("SPELLING", "https://example.com/page", word: "Werrd"),
				New("SPELLING", "https://example.com/page", word: "Spleling"),
			};
			var result = IssueTracking.Merge([], detected);
			Assert.Equal(2, result.Count);
		}

		// ── PromoteFrom404 ────────────────────────────────────────────────────

		[Fact]
		public void PromoteFrom404_NonExistentFile_ReturnsEmpty()
		{
			var result = IssueTracking.PromoteFrom404(
				Path.Combine(_tempDir, "missing.log"));
			Assert.Empty(result);
		}

		[Fact]
		public void PromoteFrom404_ValidLine_ParsedCorrectly()
		{
			var path = TmpFile("404sources.log",
				"https://example.com/missing|https://example.com/page");
			var result = IssueTracking.PromoteFrom404(path);
			Assert.Single(result);
			Assert.Equal("404", result[0].Type);
			Assert.Equal("https://example.com/missing", result[0].Url);
			Assert.Equal("https://example.com/page", result[0].SourceLabel);
		}

		[Fact]
		public void PromoteFrom404_DuplicateUrls_Deduplicated()
		{
			var path = TmpFile("404sources.log",
				"https://example.com/missing|https://example.com/page1",
				"https://example.com/missing|https://example.com/page2");
			var result = IssueTracking.PromoteFrom404(path);
			Assert.Single(result);
		}

		// ── PromoteFromSelfLink ───────────────────────────────────────────────

		[Fact]
		public void PromoteFromSelfLink_NonExistentFile_ReturnsEmpty()
		{
			var result = IssueTracking.PromoteFromSelfLink(
				Path.Combine(_tempDir, "missing.log"));
			Assert.Empty(result);
		}

		[Fact]
		public void PromoteFromSelfLink_HeaderSkipped()
		{
			var path = TmpFile("selflink.log",
				"File|FileUrl|LinkFound|ContextSnippet",
				"page.html|https://example.com/page|https://example.com/page|Click here");
			var result = IssueTracking.PromoteFromSelfLink(path);
			Assert.Single(result);
		}

		[Fact]
		public void PromoteFromSelfLink_ValidLine_ParsedCorrectly()
		{
			var path = TmpFile("selflink.log",
				"File|FileUrl|LinkFound|ContextSnippet",
				"page.html|https://example.com/page|https://example.com/page|Click here for more");
			var result = IssueTracking.PromoteFromSelfLink(path);
			Assert.Equal("SELFLINK", result[0].Type);
			Assert.Equal("https://example.com/page", result[0].Url);
			Assert.Equal("Click here for more", result[0].Excerpt);
		}

		// ── PromoteFromRedirect ───────────────────────────────────────────────

		[Fact]
		public void PromoteFromRedirect_NonExistentFile_ReturnsEmpty()
		{
			var result = IssueTracking.PromoteFromRedirect(
				Path.Combine(_tempDir, "missing.log"));
			Assert.Empty(result);
		}

		[Fact]
		public void PromoteFromRedirect_SingleFound_NotAnIssue()
		{
			var path = TmpFile("redirect.log",
				"https://example.com/old | Found | https://example.com/new > OK https://example.com/new");
			var result = IssueTracking.PromoteFromRedirect(path);
			Assert.Empty(result);
		}

		[Fact]
		public void PromoteFromRedirect_DoubleFound_IsIssue()
		{
			var path = TmpFile("redirect.log",
				"https://example.com/old | Found | https://example.com/mid > Found https://example.com/new");
			var result = IssueTracking.PromoteFromRedirect(path);
			Assert.Single(result);
			Assert.Equal("REDIRECT", result[0].Type);
			Assert.Equal("https://example.com/old", result[0].Url);
		}

		[Fact]
		public void PromoteFromRedirect_NotFoundAtEnd_NotAnIssue()
		{
			// External URL — out of scope, ignored
			var path = TmpFile("redirect.log",
				"https://example.com/old | Found | https://external.com/page > (NotFound)");
			var result = IssueTracking.PromoteFromRedirect(path);
			Assert.Empty(result);
		}

		[Fact]
		public void PromoteFromRedirect_DoubleFound_ExcerptContainsFullChain()
		{
			var line = "https://example.com/old | Found | https://example.com/mid > Found https://example.com/new";
			var path = TmpFile("redirect.log", line);
			var result = IssueTracking.PromoteFromRedirect(path);
			Assert.Equal(line, result[0].Excerpt);
		}

		// ── PromoteFromQuality ────────────────────────────────────────────────

		[Fact]
		public void PromoteFromQuality_NonExistentFile_ReturnsEmpty()
		{
			var result = IssueTracking.PromoteFromQuality(
				Path.Combine(_tempDir, "missing.log"));
			Assert.Empty(result);
		}

		[Fact]
		public void PromoteFromQuality_HeaderSkipped()
		{
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"page.html|LIGATURE|U+FB01|found in word");
			var result = IssueTracking.PromoteFromQuality(path);
			Assert.Single(result);
		}

		[Fact]
		public void PromoteFromQuality_ValidLine_TypeAndWordParsed()
		{
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"page.html|LIGATURE|U+FB01|found in word");
			var result = IssueTracking.PromoteFromQuality(path);
			Assert.Equal("QUALITY", result[0].Type);
			Assert.Equal("LIGATURE", result[0].Word);
			Assert.Equal("U+FB01", result[0].SourceLabel);
			Assert.Equal("found in word", result[0].Excerpt);
		}

		// ── PromoteFromSpelling ───────────────────────────────────────────────

		[Fact]
		public void PromoteFromSpelling_NonExistentFile_ReturnsEmpty()
		{
			var result = IssueTracking.PromoteFromSpelling(
				Path.Combine(_tempDir, "missing.log"));
			Assert.Empty(result);
		}

		[Fact]
		public void PromoteFromSpelling_ValidLine_OneRecordPerWord()
		{
			var path = TmpFile("spell.log",
				"https://example.com/page|page.html|Werrd (de)|Spleling (en)");
			var result = IssueTracking.PromoteFromSpelling(path);
			Assert.Equal(2, result.Count);
		}

		[Fact]
		public void PromoteFromSpelling_ValidLine_WordAndLangParsed()
		{
			var path = TmpFile("spell.log",
				"https://example.com/page|page.html|Werrd (de)");
			var result = IssueTracking.PromoteFromSpelling(path);
			Assert.Single(result);
			Assert.Equal("SPELLING", result[0].Type);
			Assert.Equal("https://example.com/page", result[0].Url);
			Assert.Equal("Werrd", result[0].Word);
			Assert.Equal("de", result[0].Language);
		}

		[Fact]
		public void PromoteFromSpelling_DuplicateWordOnSamePage_Deduplicated()
		{
			var path = TmpFile("spell.log",
				"https://example.com/page|page.html|Werrd (de)",
				"https://example.com/page|page.html|Werrd (de)");
			var result = IssueTracking.PromoteFromSpelling(path);
			Assert.Single(result);
		}

		// ── BuildSplitAnchorArtifacts ─────────────────────────────────────────────

		[Fact]
		public void BuildSplitAnchorArtifacts_NonExistentFile_ReturnsEmpty()
		{
			var result = IssueTracking.BuildSplitAnchorArtifacts(
				Path.Combine(_tempDir, "missing.log"));
			Assert.Empty(result);
		}

		[Fact]
		public void BuildSplitAnchorArtifacts_SplitAnchorEntry_ExtractsArtifactWord()
		{
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"page.html|SPLIT_WORD_ANCHOR|Anchor closes mid-word — stray character after </a>: 'r'|...navigation=\"de\">Baufinanzierungsrechne</a>r und sichern...");
			var result = IssueTracking.BuildSplitAnchorArtifacts(path);
			Assert.Single(result);
			Assert.True(result.ContainsKey("page.html"));
			Assert.Contains("Baufinanzierungsrechne", result["page.html"]);
		}

		[Fact]
		public void BuildSplitAnchorArtifacts_NonSplitAnchorEntry_Ignored()
		{
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"page.html|QUOTE_SYSTEM_MIX|Multiple systems|...excerpt...");
			var result = IssueTracking.BuildSplitAnchorArtifacts(path);
			Assert.Empty(result);
		}

		[Fact]
		public void BuildSplitAnchorArtifacts_MultiplePagesMultipleWords_AllExtracted()
		{
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"page1.html|SPLIT_WORD_ANCHOR|stray: 'r'|...>Baufinanzierungsrechne</a>r...",
				"page1.html|SPLIT_WORD_ANCHOR|stray: 'r'|...>LinkedIn-Datenformula</a>r...",
				"page2.html|SPLIT_WORD_ANCHOR|stray: 'l'|...>Autofil</a>l form...");
			var result = IssueTracking.BuildSplitAnchorArtifacts(path);
			Assert.Equal(2, result.Count);
			Assert.Equal(2, result["page1.html"].Count);
			Assert.Contains("Baufinanzierungsrechne", result["page1.html"]);
			Assert.Contains("LinkedIn-Datenformula", result["page1.html"]);
			Assert.Contains("Autofil", result["page2.html"]);
		}

		// ── LooksLikeFilename (fileset #286) ─────────────────────────────────
		// Reader-side defence: PromoteFromQuality (and SpellTracking's content-
		// quality consumer) skip log lines whose F(0) doesn't look like a
		// filename. Catches malformed lines from historical / out-of-tree logs.

		[Fact]
		public void LooksLikeFilename_NormalFilename_True()
		{
			Assert.True(IssueTracking.LooksLikeFilename("page.html"));
			Assert.True(IssueTracking.LooksLikeFilename("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefpage.html"));
			Assert.True(IssueTracking.LooksLikeFilename("simple.txt"));
		}

		[Fact]
		public void LooksLikeFilename_TextWithSpaces_False()
		{
			// Hello world isn't a filename — would be content text leaking into F(0).
			Assert.False(IssueTracking.LooksLikeFilename("Hello world"));
			Assert.False(IssueTracking.LooksLikeFilename("For a hassle-free holiday"));
		}

		[Fact]
		public void LooksLikeFilename_LeadingDashSpace_False()
		{
			// The exact symptom from the original Czech-page failure.
			Assert.False(IssueTracking.LooksLikeFilename("- Our Credit Card."));
		}

		[Fact]
		public void LooksLikeFilename_EmptyOrNull_False()
		{
			Assert.False(IssueTracking.LooksLikeFilename(""));
			Assert.False(IssueTracking.LooksLikeFilename(null!));
		}

		[Fact]
		public void LooksLikeFilename_TooLong_False()
		{
			// MAX_PATH on Windows is 260.
			var longName = new string('x', 300) + ".html";
			Assert.False(IssueTracking.LooksLikeFilename(longName));
		}

		[Fact]
		public void LooksLikeFilename_ContainsTabOrControl_False()
		{
			Assert.False(IssueTracking.LooksLikeFilename("page\tnewer.html"));
			Assert.False(IssueTracking.LooksLikeFilename("page\u0001malformed"));
		}

		[Fact]
		public void PromoteFromQuality_SkipsLineWithMalformedFilename()
		{
			// Regression test for the Czech-page scenario: a quality log with one
			// good line followed by what was previously the tail of a split-up
			// translation-issue entry. The good line should produce an issue;
			// the malformed lines should be silently skipped.
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"page.html|LIGATURE|Ligature fi|some content with ﬁ",
				"- Our Credit Card.",
				"and access to a broad range of deals");
			var result = IssueTracking.PromoteFromQuality(path);
			// Only the good line should make it. The malformed lines have F(0)
			// values that don't look like filenames — skipped.
			// (The good line's URL resolution returns the filename itself when
			// the UrlCache lookup fails for the test scenario; we just check
			// that we got ONE record back, with the right IssueType.)
			Assert.Single(result);
			Assert.Equal("QUALITY", result[0].Type);
			Assert.Equal("LIGATURE", result[0].Word);
		}

		// ── Fileset #286b — empty-IssueType rejection ─────────────────────────
		// Lines that pass LooksLikeFilename (filename-shaped F(0)) but have an
		// empty F(1) IssueType are still malformed. They used to be persisted as
		// anonymous Url="error" records. Now rejected at promotion time.

		[Fact]
		public void PromoteFromQuality_SkipsLineWithEmptyIssueType()
		{
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"page.html|LIGATURE|Ligature fi|some content with ﬁ",
				"otherpage.html|||malformed line with empty IssueType");
			var result = IssueTracking.PromoteFromQuality(path);
			// Only the good line should make it.
			Assert.Single(result);
			Assert.Equal("LIGATURE", result[0].Word);
		}

		[Fact]
		public void PromoteFromQuality_FailedUrlLookup_FallsBackToFilename()
		{
			// When LookUpUrlForFile returns "error" (no UrlCache populated),
			// the record's Url falls back to the filename rather than literally
			// "error". Prevents the anonymous "Url=error" residue in IssueTracking.
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"orphan-page.html|LIGATURE|U+FB01|content");
			var result = IssueTracking.PromoteFromQuality(path);
			Assert.Single(result);
			// Url should be "orphan-page.html" — NOT the sentinel "error".
			Assert.NotEqual("error", result[0].Url);
			Assert.Equal("orphan-page.html", result[0].Url);
		}

		// ── Fileset #286b — Load filters historical Url="error" residue ───────

		[Fact]
		public void Load_FiltersHistoricalUrlErrorResidue()
		{
			// Two malformed historical records (Url=="error") plus one good record.
			// Load should drop the malformed ones; only the good record returned.
			var path = TmpFile("IssueTracking.log",
				"Source|Ticket|DateReported|Type|Url|Status|DateFound|DateLastSeen|DateExpiry|Word|Comment|Language|SourceLabel|Excerpt|CrawlSource",
				"auto|||QUALITY|error|fixed|2026-05-15|2026-05-15|||||||",
				"auto|||QUALITY|error|fixed|2026-05-14|2026-05-14|||||||",
				"auto||2026-05-14|QUALITY|https://example.com/page|new|2026-05-14|2026-05-14||LIGATURE||||content|discovery");
			var result = IssueTracking.Load(path);
			Assert.Single(result);
			Assert.Equal("https://example.com/page", result[0].Url);
			Assert.Equal("LIGATURE", result[0].Word);
		}

		// ── Fileset #286d — ApplyTriageDecisions semantics ────────────────────
		// The triage Save path must touch ONLY the triaged records and leave
		// all other existing records completely unchanged. Previously the
		// triage path used Merge, which incorrectly classified non-triaged
		// records as 'fixed' (the bug the fileset fixes).

		[Fact]
		public void ApplyTriageDecisions_NonTriagedRecord_StatusUnchanged()
		{
			// The exact scenario from the field report: a CONTROL_CHARS_IN_CONTENT
			// record is 'new' from an earlier run. Operator triages a DIFFERENT
			// record (a LIGATURE). The control-chars record must stay 'new', not
			// move to 'fixed'.
			var existing = new List<IssueTracking.IssueRecord>
		{
			New("QUALITY", "https://example.com/page1", "CONTROL_CHARS_IN_CONTENT", status: "new"),
			New("QUALITY", "https://example.com/page2", "LIGATURE",                 status: "new"),
		};
			var decisions = new List<IssueTracking.IssueRecord>
		{
			New("QUALITY", "https://example.com/page2", "LIGATURE", status: "wontfix"),
		};

			var result = IssueTracking.ApplyTriageDecisions(existing, decisions);

			var ccic = result.Single(r => r.Word == "CONTROL_CHARS_IN_CONTENT");
			Assert.Equal("new", ccic.Status);
			var ligature = result.Single(r => r.Word == "LIGATURE");
			Assert.Equal("wontfix", ligature.Status);
		}

		[Fact]
		public void ApplyTriageDecisions_TriagedRecord_StatusAndCommentUpdated()
		{
			// Operator presses W (wontfix) with a comment. Existing record's
			// status changes; DateLastSeen updates; DateFound preserved.
			var existing = new List<IssueTracking.IssueRecord>
		{
			New("QUALITY", "https://example.com/page", "QUOTE_WRONG_OPEN",
				status: "new", dateFound: "2026-01-01") with { Comment = "" },
		};
			var decisions = new List<IssueTracking.IssueRecord>
		{
			New("QUALITY", "https://example.com/page", "QUOTE_WRONG_OPEN",
				status: "wontfix") with { Comment = "intentional dialect spelling" },
		};

			var result = IssueTracking.ApplyTriageDecisions(existing, decisions);

			Assert.Single(result);
			Assert.Equal("wontfix", result[0].Status);
			Assert.Equal("intentional dialect spelling", result[0].Comment);
			// DateFound preserved from original.
			Assert.Equal("2026-01-01", result[0].DateFound);
		}

		[Fact]
		public void ApplyTriageDecisions_DecisionWithNoMatchingKey_AddedAsNew()
		{
			// Decisions can include records whose Keys aren't in existing
			// (e.g. a freshly detected issue surfaced in triage). Those add.
			var existing = new List<IssueTracking.IssueRecord>();
			var decisions = new List<IssueTracking.IssueRecord>
		{
			New("QUALITY", "https://example.com/newone", "BARE_TEXT_IN_CONTAINER",
				status: "new"),
		};

			var result = IssueTracking.ApplyTriageDecisions(existing, decisions);

			Assert.Single(result);
			Assert.Equal("BARE_TEXT_IN_CONTAINER", result[0].Word);
		}

		[Fact]
		public void ApplyTriageDecisions_EmptyDecisions_AllExistingPassedThroughUnchanged()
		{
			// Defensive edge case: empty decisions list (e.g. operator entered
			// triage and pressed Q immediately). Existing records pass through
			// completely unchanged — no status mutation, no field updates.
			var existing = new List<IssueTracking.IssueRecord>
		{
			New("QUALITY", "https://example.com/a", "CONTROL_CHARS_IN_CONTENT", status: "new"),
			New("QUALITY", "https://example.com/b", "LIGATURE",                 status: "reopened"),
			New("QUALITY", "https://example.com/c", "QUOTE_UNMATCHED",          status: "fixed"),
		};

			var result = IssueTracking.ApplyTriageDecisions(existing, []);

			Assert.Equal(3, result.Count);
			Assert.Equal("new", result[0].Status);
			Assert.Equal("reopened", result[1].Status);
			Assert.Equal("fixed", result[2].Status);
		}

		[Fact]
		public void ApplyTriageDecisions_ExistingWontfixNotInDecisions_StaysWontfix()
		{
			// A pre-existing wontfix record (suppressed from triage display
			// because operator already decided it) must stay wontfix even when
			// not in the current decisions list.
			var existing = new List<IssueTracking.IssueRecord>
		{
			New("QUALITY", "https://example.com/page", "LIGATURE", status: "wontfix"),
		};
			var decisions = new List<IssueTracking.IssueRecord>();

			var result = IssueTracking.ApplyTriageDecisions(existing, decisions);

			Assert.Single(result);
			Assert.Equal("wontfix", result[0].Status);
		}

		// ── Fileset #286e — UNWANTED_PATTERN composite Word ───────────────────
		// PromoteFromQuality must produce composite Word values for
		// UNWANTED_PATTERN records (Word = "UNWANTED_PATTERN:<detail>") to match
		// the shape ContentQualityTriage uses. Without this, triage decisions
		// create parallel records instead of updating auto-promoted ones.
		// Other QUALITY IssueTypes keep bare-IssueType Word values.

		[Fact]
		public void PromoteFromQuality_UnwantedPattern_WordIsComposite()
		{
			// Auto-promoter must produce Word = "UNWANTED_PATTERN:<detail>"
			// so the Key matches the composite Key triage will build.
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"page.html|UNWANTED_PATTERN|Legacy-Product: Old Product — pattern: kwitt|...content with kwitt...");
			var result = IssueTracking.PromoteFromQuality(path);

			Assert.Single(result);
			Assert.Equal("UNWANTED_PATTERN:Legacy-Product: Old Product — pattern: kwitt",
				result[0].Word);
			// SourceLabel still carries the detail (unchanged behaviour).
			Assert.Equal("Legacy-Product: Old Product — pattern: kwitt",
				result[0].SourceLabel);
		}

		[Fact]
		public void PromoteFromQuality_UnwantedPatternWithEmptyDetail_FallsBackToBareWord()
		{
			// Defensive edge case: malformed log line with empty Detail. The
			// composite Word would otherwise be "UNWANTED_PATTERN:" (trailing
			// colon, no semantic value). Fall back to bare "UNWANTED_PATTERN".
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"page.html|UNWANTED_PATTERN||context");
			var result = IssueTracking.PromoteFromQuality(path);

			Assert.Single(result);
			Assert.Equal("UNWANTED_PATTERN", result[0].Word);
		}

		[Fact]
		public void PromoteFromQuality_NonUnwantedPattern_KeepsBareWord()
		{
			// Other QUALITY IssueTypes (LIGATURE, QUOTE_*, etc.) must keep
			// bare-IssueType Word values — composing with Detail would
			// over-discriminate the Key space.
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"page1.html|LIGATURE|U+FB01 (fi ligature)|some content with ﬁ",
				"page2.html|QUOTE_UNMATCHED|Opener \"„\" has no closer|some quoted text",
				"page3.html|BARE_TEXT_IN_CONTAINER|text in div|...");
			var result = IssueTracking.PromoteFromQuality(path);

			Assert.Equal(3, result.Count);
			Assert.Equal("LIGATURE", result[0].Word);
			Assert.Equal("QUOTE_UNMATCHED", result[1].Word);
			Assert.Equal("BARE_TEXT_IN_CONTAINER", result[2].Word);
		}

		[Fact]
		public void PromoteFromQuality_UnwantedPatternCompositeKey_MatchesTriageKey()
		{
			// Critical regression check: auto-promoted UNWANTED_PATTERN record
			// must have the SAME Key as a triage-produced one for the same
			// underlying issue. This is the property that makes
			// ApplyTriageDecisions reconcile rather than duplicate.
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"page.html|UNWANTED_PATTERN|Legacy-Product: Old Product — pattern: kwitt|content");
			var autoResult = IssueTracking.PromoteFromQuality(path);

			// Simulate a triage decision for the SAME underlying issue. Url
			// resolution in tests falls back to filename, so we use the same
			// filename-as-Url here that PromoteFromQuality will produce.
			var triageDecision = new IssueTracking.IssueRecord
			{
				Type = "QUALITY",
				Url = autoResult[0].Url,
				Word = "UNWANTED_PATTERN:Legacy-Product: Old Product — pattern: kwitt",
				Status = "wontfix",
			};

			// Keys must match. This is what makes ApplyTriageDecisions update
			// the existing auto record instead of adding a parallel one.
			Assert.Equal(autoResult[0].Key, triageDecision.Key);
		}

		// ── Fileset #342 — MALFORMED_HTML composite Word ──────────────────────
		// MALFORMED_HTML carries its sub-defect in Detail (e.g.
		// CONTENT_BEFORE_DOCTYPE), so the auto-promoter must build the same
		// composite Word shape as UNWANTED_PATTERN. It is auto-promoted only
		// (not interactively triaged) — when present it is a server-side bug.

		[Fact]
		public void PromoteFromQuality_MalformedHtml_WordIsComposite()
		{
			// Auto-promoter must produce Word = "MALFORMED_HTML:<detail>" so the
			// Key carries the sub-defect identity and stays stable as further
			// MALFORMED_HTML sub-types are added later (tier 2).
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"page.html|MALFORMED_HTML|CONTENT_BEFORE_DOCTYPE|offset 0: <div>error</div>");
			var result = IssueTracking.PromoteFromQuality(path);

			Assert.Single(result);
			Assert.Equal("QUALITY", result[0].Type);
			Assert.Equal("MALFORMED_HTML:CONTENT_BEFORE_DOCTYPE", result[0].Word);
			// SourceLabel still carries the bare detail (unchanged behaviour).
			Assert.Equal("CONTENT_BEFORE_DOCTYPE", result[0].SourceLabel);
		}

		[Fact]
		public void PromoteFromQuality_MalformedHtmlWithEmptyDetail_FallsBackToBareWord()
		{
			// Defensive edge case mirroring the UNWANTED_PATTERN fallback: an
			// empty Detail must not produce a trailing-colon Word.
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"page.html|MALFORMED_HTML||context");
			var result = IssueTracking.PromoteFromQuality(path);

			Assert.Single(result);
			Assert.Equal("MALFORMED_HTML", result[0].Word);
		}

		[Fact]
		public void PromoteFromQuality_MalformedHtmlCompositeKey_MatchesAutoPromoterKey()
		{
			// The auto-promoted record's Key must round-trip with any record
			// keyed the same way for the same underlying issue, so future
			// merge/reconcile logic matches rather than duplicates.
			var path = TmpFile("quality.log",
				"Filename|IssueType|Detail|Context",
				"page.html|MALFORMED_HTML|CONTENT_BEFORE_DOCTYPE|offset 0: <div>error</div>");
			var autoResult = IssueTracking.PromoteFromQuality(path);

			var sameIssue = new IssueTracking.IssueRecord
			{
				Type = "QUALITY",
				Url = autoResult[0].Url,
				Word = "MALFORMED_HTML:CONTENT_BEFORE_DOCTYPE",
				Status = "new",
			};

			Assert.Equal(autoResult[0].Key, sameIssue.Key);
		}
	}
}
