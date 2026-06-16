using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for the SEO derived-issue checks in <see cref="IssueTracking.PromoteFromSeo"/>:
	/// title/description length, meta-keywords policy, H1 presence/uniqueness, and the
	/// {title} template strip + conformance logic. PromoteFromSeo is pure (reads a log,
	/// returns records), so these feed synthetic 08-seo-data.log lines and assert findings.
	/// </summary>
	[Collection("Logger")]
	public class SeoPromoteTests : IDisposable
	{
		private readonly string _tempDir;

		public SeoPromoteTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"seo-test-{Guid.NewGuid()}");
			Directory.CreateDirectory(_tempDir);
		}

		public void Dispose()
		{
			if (Directory.Exists(_tempDir))
			{
				Directory.Delete(_tempDir, recursive: true);
			}

			GC.SuppressFinalize(this);
		}

		// Column order: url robots title titleLen desc descLen keywords source h1Count
		private static string Row(string url, string robots, string title, string desc,
			string keywords, string source, int h1Count)
			=> string.Join("@@@", url, robots, title, title.Length.ToString(),
				desc, desc.Length.ToString(), keywords, source, h1Count.ToString());

		private string WriteLog(params string[] dataRows)
		{
			var path = Path.Combine(_tempDir, "08-seo-data.log");
			var lines = new List<string>
			{
				"url@@@robotsValue@@@titleValue@@@titleLength@@@descriptionValue@@@" +
				"descriptionLength@@@keywordsValue@@@source@@@h1Count"
			};
			lines.AddRange(dataRows);
			File.WriteAllText(path, string.Join("\n", lines), new UTF8Encoding(false));
			return path;
		}

		private static SeoConfig DefaultSeo() => new();   // factory defaults

		private static IReadOnlyList<string> Words(IEnumerable<IssueTracking.IssueRecord> r, string url)
			=> r.Where(x => x.Url == url).Select(x => x.Word).ToList();

		// ── Title length ─────────────────────────────────────────────────────

		[Fact]
		public void MissingTitle_WhenTitleEmpty()
		{
			var seo = DefaultSeo();
			var log = WriteLog(Row("u1", "index", "", "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("MissingTitle", Words(r, "u1"));
		}

		[Fact]
		public void TitleTooShort_BelowMin()
		{
			var seo = DefaultSeo();   // TitleMinLength = 30
			var log = WriteLog(Row("u1", "index", "Short title", "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("TitleTooShort", Words(r, "u1"));
		}

		[Fact]
		public void TitleTooLong_AboveMax()
		{
			var seo = DefaultSeo();   // TitleMaxLength = 60
			var longTitle = new string('x', 80);
			var log = WriteLog(Row("u1", "index", longTitle, "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("TitleTooLong", Words(r, "u1"));
		}

		[Fact]
		public void TitleInRange_NoTitleFinding()
		{
			var seo = DefaultSeo();
			var title = new string('x', 45);   // between 30 and 60
			var log = WriteLog(Row("u1", "index", title, "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.DoesNotContain("TitleTooShort", Words(r, "u1"));
			Assert.DoesNotContain("TitleTooLong", Words(r, "u1"));
			Assert.DoesNotContain("MissingTitle", Words(r, "u1"));
		}

		// ── Title template: strip framing for length, flag mismatch ──────────

		[Fact]
		public void Template_SuffixStripped_BeforeLengthCheck()
		{
			var seo = DefaultSeo();
			seo.TitleTemplates = ["{title} | Brand"];
			// Core "About Our Company Services Here" is 31 chars (in range);
			// whole title with " | Brand" is 39 — also in range, so use a case
			// where the suffix would push it over if NOT stripped.
			var core = new string('x', 55);            // in range alone
			var title = core + " | Brand";             // 63 with suffix → would be TooLong if not stripped
			var log = WriteLog(Row("u1", "index", title, "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.DoesNotContain("TitleTooLong", Words(r, "u1"));      // suffix stripped → core 55 ok
			Assert.DoesNotContain("InconsistentTitleFormat", Words(r, "u1"));
		}

		[Fact]
		public void Template_ShortCoreLongBrand_NotFlaggedTooShort()
		{
			// The asymmetry: a short {title} core is fine when the brand framing
			// fills out the whole title. "Our Cars | A Long Brand Name Here" should
			// NOT flag TitleTooShort even though the core "Our Cars" is only 8 chars,
			// because too-short measures the WHOLE title.
			var seo = DefaultSeo();          // TitleMinLength = 30
			seo.TitleTemplates = ["{title} | A Long Brand Name Here"];
			var title = "Our Cars | A Long Brand Name Here";   // 33 chars total; core "Our Cars" = 8
			Assert.True(title.Length >= 30);
			var log = WriteLog(Row("u1", "index", title, "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.DoesNotContain("TitleTooShort", Words(r, "u1"));  // full title (33) >= 30
			Assert.DoesNotContain("InconsistentTitleFormat", Words(r, "u1"));
		}

		[Fact]
		public void Template_TooShort_MeasuresWholeTitle()
		{
			// When even the whole title is under the minimum, TooShort still fires.
			var seo = DefaultSeo();          // TitleMinLength = 30
			seo.TitleTemplates = ["{title} | Br"];
			var title = "Hi | Br";           // 7 chars total < 30
			var log = WriteLog(Row("u1", "index", title, "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("TitleTooShort", Words(r, "u1"));
		}

		[Fact]
		public void Template_PrefixForm_Works()
		{
			var seo = DefaultSeo();
			seo.TitleTemplates = ["Brand | {title}"];
			var core = new string('x', 40);
			var title = "Brand | " + core;
			var log = WriteLog(Row("u1", "index", title, "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.DoesNotContain("InconsistentTitleFormat", Words(r, "u1"));
			Assert.DoesNotContain("TitleTooLong", Words(r, "u1"));
		}

		[Fact]
		public void Template_FramingMismatch_FlagsInconsistent()
		{
			var seo = DefaultSeo();
			seo.TitleTemplates = ["{title} | Brand"];
			// Wrong suffix (typo'd brand) → framing mismatch.
			var title = new string('x', 40) + " | Brnd";
			var log = WriteLog(Row("u1", "index", title, "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("InconsistentTitleFormat", Words(r, "u1"));
		}

		[Fact]
		public void Template_MissingFraming_FlagsInconsistentAndMeasuresWhole()
		{
			var seo = DefaultSeo();
			seo.TitleTemplates = ["{title} | Brand"];
			var title = "Homepage";   // no framing at all; 8 chars
			var log = WriteLog(Row("u1", "index", title, "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			var words = Words(r, "u1");
			Assert.Contains("InconsistentTitleFormat", words);   // framing missing
			Assert.Contains("TitleTooShort", words);              // whole title measured (8 < 30)
		}

		// ── Title templates: any-of, first-match-wins ───────────────────────

		[Fact]
		public void Templates_MatchesSecondEntry_NoInconsistent()
		{
			// The backlog example: title matches the SECOND template, not the first.
			var seo = DefaultSeo();
			seo.TitleTemplates = ["{title} | Company", "{title} | Second Company"];
			var title = "Our new product line | Second Company";
			var log = WriteLog(Row("u1", "index", title, "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.DoesNotContain("InconsistentTitleFormat", Words(r, "u1"));
		}

		[Fact]
		public void Templates_TooLong_SubtractsMatchedEntrysFraming()
		{
			// TooLong must use the core of the MATCHED template. Title matches the
			// second entry; its core ("x"×55) is in range, so NO TooLong — even though
			// the whole title (55 + " | Second Company" = 72) exceeds the max of 60.
			var seo = DefaultSeo();   // TitleMaxLength = 60
			seo.TitleTemplates = ["{title} | Company", "{title} | Second Company"];
			var core = new string('x', 55);
			var title = core + " | Second Company";   // 72 total; matched core = 55
			Assert.True(title.Length > seo.TitleMaxLength);
			var log = WriteLog(Row("u1", "index", title, "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.DoesNotContain("TitleTooLong", Words(r, "u1"));        // matched core stripped → 55 ok
			Assert.DoesNotContain("InconsistentTitleFormat", Words(r, "u1"));
		}

		[Fact]
		public void Templates_MatchesNone_FlagsInconsistentAndMeasuresWhole()
		{
			// Matches neither template → InconsistentTitleFormat, and length falls back
			// to the whole title (here over max → TitleTooLong on the full string).
			var seo = DefaultSeo();   // TitleMaxLength = 60
			seo.TitleTemplates = ["{title} | Company", "{title} | Second Company"];
			var title = new string('x', 70) + " | Third Company";   // matches no entry
			var log = WriteLog(Row("u1", "index", title, "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			var words = Words(r, "u1");
			Assert.Contains("InconsistentTitleFormat", words);
			Assert.Contains("TitleTooLong", words);   // whole title measured (no match → no strip)
		}

		[Fact]
		public void Templates_FirstMatchWins_WhenSeveralCouldMatch()
		{
			// Both templates would match this title (empty-prefix "{title}" matches
			// anything ending in " | Brand"; the more specific one also matches). The
			// FIRST entry in list order wins — its core drives the length check. Here
			// the first entry strips only " | Brand" leaving a 55-char core (ok); if the
			// second (more aggressive) entry had won, the core would differ. We assert
			// the framing is accepted and no TooLong fires under the first match.
			var seo = DefaultSeo();   // TitleMaxLength = 60
			var core = new string('x', 55);
			var title = core + " | Brand";   // 63 total
			seo.TitleTemplates = ["{title} | Brand", "{title}"];
			var log = WriteLog(Row("u1", "index", title, "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.DoesNotContain("InconsistentTitleFormat", Words(r, "u1"));
			Assert.DoesNotContain("TitleTooLong", Words(r, "u1"));   // first entry: core 55 ≤ 60
		}

		[Fact]
		public void Templates_Empty_NoConformanceCheck_WholeTitleLength()
		{
			// Empty list = no template: whole-title length, never InconsistentTitleFormat.
			var seo = DefaultSeo();
			seo.TitleTemplates = [];
			var title = new string('x', 45);   // in range
			var log = WriteLog(Row("u1", "index", title, "a good long description that exceeds seventy characters for the test ok", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			var words = Words(r, "u1");
			Assert.DoesNotContain("InconsistentTitleFormat", words);
			Assert.DoesNotContain("TitleTooLong", words);
			Assert.DoesNotContain("TitleTooShort", words);
		}

		// ── Description ──────────────────────────────────────────────────────

		[Fact]
		public void MissingDescription_WhenEmpty()
		{
			var seo = DefaultSeo();
			var log = WriteLog(Row("u1", "index", new string('x', 45), "", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("MissingDescription", Words(r, "u1"));
		}

		[Fact]
		public void DescriptionTooShort_BelowMin()
		{
			var seo = DefaultSeo();   // DescriptionMinLength = 70
			var log = WriteLog(Row("u1", "index", new string('x', 45), "too short desc", "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("DescriptionTooShort", Words(r, "u1"));
		}

		[Fact]
		public void DescriptionTooLong_AboveMax()
		{
			var seo = DefaultSeo();   // DescriptionMaxLength = 160
			var log = WriteLog(Row("u1", "index", new string('x', 45), new string('d', 200), "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("DescriptionTooLong", Words(r, "u1"));
		}

		// ── Meta keywords ────────────────────────────────────────────────────

		[Fact]
		public void MetaKeywordsPresent_WhenFlaggedAndPresent()
		{
			var seo = DefaultSeo();   // MetaKeywordsFlagAsError = true
			var log = WriteLog(Row("u1", "index", new string('x', 45), new string('d', 100), "buy,cheap,seo", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("MetaKeywordsPresent", Words(r, "u1"));
		}

		[Fact]
		public void MetaKeywords_NotFlagged_WhenDisabled()
		{
			var seo = DefaultSeo();
			seo.MetaKeywordsFlagAsError = false;
			var log = WriteLog(Row("u1", "index", new string('x', 45), new string('d', 100), "buy,cheap,seo", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.DoesNotContain("MetaKeywordsPresent", Words(r, "u1"));
		}

		// ── H1 ───────────────────────────────────────────────────────────────

		[Fact]
		public void MissingH1_WhenZeroAndFlagged()
		{
			var seo = DefaultSeo();   // MissingH1FlagAsError = true
			var log = WriteLog(Row("u1", "index", new string('x', 45), new string('d', 100), "", "crawl", 0));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("MissingH1", Words(r, "u1"));
		}

		[Fact]
		public void MultipleH1_NotFlaggedByDefault()
		{
			var seo = DefaultSeo();   // MultipleH1FlagAsError = false
			var log = WriteLog(Row("u1", "index", new string('x', 45), new string('d', 100), "", "crawl", 3));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.DoesNotContain("MultipleH1", Words(r, "u1"));
		}

		[Fact]
		public void MultipleH1_FlaggedWhenEnabled()
		{
			var seo = DefaultSeo();
			seo.MultipleH1FlagAsError = true;
			var log = WriteLog(Row("u1", "index", new string('x', 45), new string('d', 100), "", "crawl", 3));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("MultipleH1", Words(r, "u1"));
		}

		[Fact]
		public void SingleH1_NoH1Finding()
		{
			var seo = DefaultSeo();
			seo.MultipleH1FlagAsError = true;
			var log = WriteLog(Row("u1", "index", new string('x', 45), new string('d', 100), "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.DoesNotContain("MissingH1", Words(r, "u1"));
			Assert.DoesNotContain("MultipleH1", Words(r, "u1"));
		}

		// ── Existing IndexableButNotCrawlable preserved ──────────────────────

		[Fact]
		public void IndexableButNotCrawlable_StillFires_ForListSource()
		{
			var seo = DefaultSeo();
			var log = WriteLog(Row("u1", "index", new string('x', 45), new string('d', 100), "", "list", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("IndexableButNotCrawlable", r.Where(x => x.Url == "u1").Select(x => x.Comment));
		}

		[Fact]
		public void IndexableButNotCrawlable_Suppressed_WhenNoindex()
		{
			var seo = DefaultSeo();
			var log = WriteLog(Row("u1", "noindex", new string('x', 45), new string('d', 100), "", "list", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.DoesNotContain("IndexableButNotCrawlable", r.Where(x => x.Url == "u1").Select(x => x.Comment));
		}

		// ── Multiple findings coexist on one page (Key uniqueness) ───────────

		[Fact]
		public void MultipleFindings_CoexistOnOnePage()
		{
			var seo = DefaultSeo();
			// Empty title + empty description + zero H1 → three distinct findings, one URL.
			var log = WriteLog(Row("u1", "index", "", "", "", "crawl", 0));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			var words = Words(r, "u1");
			Assert.Contains("MissingTitle", words);
			Assert.Contains("MissingDescription", words);
			Assert.Contains("MissingH1", words);
		}

		// ── Robots indexability gating ───────────────────────────────────────

		[Fact]
		public void Noindex_SuppressesContentFindings()
		{
			var seo = DefaultSeo();   // default allow-list does NOT include noindex
									  // Empty title/desc/h1 would normally flag three findings — but noindex suppresses.
			var log = WriteLog(Row("u1", "noindex,follow", "", "", "", "crawl", 0));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Empty(Words(r, "u1"));
		}

		[Fact]
		public void Index_AllowsContentFindings()
		{
			var seo = DefaultSeo();
			var log = WriteLog(Row("u1", "index,follow", "", "", "", "crawl", 0));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("MissingTitle", Words(r, "u1"));
		}

		[Fact]
		public void EmptyRobots_CheckedByDefault()
		{
			// Default list contains "" → a page with no robots meta is an SEO target.
			var seo = DefaultSeo();
			var log = WriteLog(Row("u1", "", "", "", "", "crawl", 0));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("MissingTitle", Words(r, "u1"));
		}

		[Theory]
		[InlineData("index, follow")]   // space after comma
		[InlineData("INDEX,FOLLOW")]    // uppercase
		[InlineData("follow,index")]    // reversed order
		[InlineData("  index ,follow ")]// stray spaces
		public void RobotsNormalization_MatchesVariants(string robots)
		{
			// All of these normalise to "follow,index" and must match the default
			// allow-list entry "index,follow".
			var seo = DefaultSeo();
			var log = WriteLog(Row("u1", robots, "", "", "", "crawl", 0));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("MissingTitle", Words(r, "u1"));
		}

		[Fact]
		public void CustomNarrowList_SuppressesEmptyRobots()
		{
			// Operator narrows to explicit index only — empty robots no longer checked.
			var seo = DefaultSeo();
			seo.IndexableRobotsValues = ["index", "index,follow"];
			var logEmpty = WriteLog(Row("u1", "", "", "", "", "crawl", 0));
			Assert.Empty(Words(IssueTracking.PromoteFromSeo(logEmpty, seo), "u1"));

			var logIndex = WriteLog(Row("u2", "index", "", "", "", "crawl", 0));
			Assert.Contains("MissingTitle", Words(IssueTracking.PromoteFromSeo(logIndex, seo), "u2"));
		}

		[Fact]
		public void NofollowOnly_CheckedByDefault()
		{
			// Google indexes nofollow-only pages; default list includes "nofollow".
			var seo = DefaultSeo();
			var log = WriteLog(Row("u1", "nofollow", "", "", "", "crawl", 0));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Contains("MissingTitle", Words(r, "u1"));
		}

		// ── Excerpt population ───────────────────────────────────────────────

		private static string Excerpt(IEnumerable<IssueTracking.IssueRecord> r, string word)
			=> r.First(x => x.Word == word).Excerpt;

		[Fact]
		public void TitleTooLong_ExcerptHoldsTheTitle()
		{
			var seo = DefaultSeo();
			var title = new string('x', 80);
			var log = WriteLog(Row("u1", "index", title, new string('d', 100), "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Equal(title, Excerpt(r, "TitleTooLong"));
		}

		[Fact]
		public void TitleTooShort_ExcerptHoldsTheTitle()
		{
			var seo = DefaultSeo();
			var title = "Short one";
			var log = WriteLog(Row("u1", "index", title, new string('d', 100), "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Equal(title, Excerpt(r, "TitleTooShort"));
		}

		[Fact]
		public void InconsistentTitleFormat_ExcerptHoldsTheTitle()
		{
			var seo = DefaultSeo();
			seo.TitleTemplates = ["{title} | Brand"];
			var title = new string('x', 40) + " | Wrong";
			var log = WriteLog(Row("u1", "index", title, new string('d', 100), "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Equal(title, Excerpt(r, "InconsistentTitleFormat"));
		}

		[Fact]
		public void DescriptionTooLong_ExcerptHoldsTheDescription()
		{
			var seo = DefaultSeo();
			var desc = new string('d', 200);
			var log = WriteLog(Row("u1", "index", new string('x', 45), desc, "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			Assert.Equal(desc, Excerpt(r, "DescriptionTooLong"));
		}

		[Fact]
		public void TitleExcerptWithPipe_SanitizedByComposeLine()
		{
			// The raw record carries the pipe; sanitization happens at write time.
			// Verify ComposeLine turns the delimiter in the excerpt into '/', so the
			// composed line keeps exactly the expected field count (no injected columns).
			var seo = DefaultSeo();
			var title = "Our Cars | Brand Name Here That Is Quite Long Indeed For The Test";  // core > 60
			var log = WriteLog(Row("u1", "index", title, new string('d', 100), "", "crawl", 1));
			var r = IssueTracking.PromoteFromSeo(log, seo);
			var rec = r.First(x => x.Word == "TitleTooLong");
			Assert.Contains("|", rec.Excerpt);   // raw record still has the pipe

			// Compose a 4-field line; the excerpt's pipe must not add a 5th field.
			var line = IssueLogWriter.ComposeLine(IssueLogWriter.PipeDelimiter,
				rec.Type, rec.Url, rec.Word, rec.Excerpt);
			Assert.Equal(4, line.Split(IssueLogWriter.PipeDelimiter).Length);   // still 4 fields
			Assert.Contains("/", line.Split(IssueLogWriter.PipeDelimiter)[3]);  // pipe became '/'
		}
	}
}
