using System;
using System.Collections.Generic;
using Crawler;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery 634: the CONFIGURATION panel is display-only (validated visually on a real run), but its
	/// non-trivial value formatters are pure and tested here — the by-exception CQ summary, per-category
	/// unwanted-pattern counts, malformed/asset descriptions, and the colour-state decisions for the two
	/// red cases (bulk scan with no dictionaries, stale CMS list). Fixtures are synthetic.
	/// </summary>
	public class ConfigSummaryTests
	{
		[Fact]
		public void UnwantedPatterns_TotalsAndPerCategory()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "filetypes", Name = "ext", Patterns = new() { ".docx", ".pdf", ".zip" } },
				new() { Category = "tracking", Name = "utm", Patterns = new() { "utm_", "gclid" } },
				new() { Category = "filetypes", Name = "more", Patterns = new() { ".xls" } },
			};

			// total = 6 patterns; filetypes = 4, tracking = 2; categories alphabetical.
			Assert.Equal("6 total (filetypes 4 · tracking 2)", ConfigSummary.DescribeUnwantedPatterns(patterns));
		}

		[Fact]
		public void ContentQuality_AllOn_ReadsAllChecksOn()
		{
			var cq = new ContentQualityConfig(); // defaults: every check on, malformed doctype+parse, anchors off
			string s = ConfigSummary.DescribeContentQualityChecks(cq);
			Assert.StartsWith("all checks on", s);
			Assert.Contains("malformed: doctype + parse", s);
		}

		[Fact]
		public void ContentQuality_SomeOff_NamesOnlyTheDisabled()
		{
			var cq = new ContentQualityConfig { CheckLigatures = false, CheckQuoteSystemMixing = false };
			string s = ConfigSummary.DescribeContentQualityChecks(cq);
			Assert.Contains("off: ligatures, quote-mixing", s);
			Assert.DoesNotContain("all checks on", s);
		}

		[Fact]
		public void Malformed_DescribesActiveDetectorsAndSuppressedCount()
		{
			var m = new MalformedHtmlConfig(); // doctype + parse on, 1 suppressed code by default
			Assert.Equal("doctype + parse (1 code(s) suppressed)", ConfigSummary.DescribeMalformed(m));
		}

		[Fact]
		public void AssetQuality_ChecksAndLimits_OrOff()
		{
			Assert.Equal("metadata · dimensions · size · ≤1 MB · ≤5000 px", ConfigSummary.DescribeAssetQuality(new AssetQualityConfig()));

			var off = new AssetQualityConfig { CheckMetadataLeakage = false, CheckDimensions = false, CheckSize = false };
			Assert.Equal("off", ConfigSummary.DescribeAssetQuality(off));
		}

		[Fact]
		public void BulkScan_NoDictionaries_IsRedAlert()
		{
			Assert.Equal(("off", false, true), ConfigSummary.DescribeBulkScan(new JavaScriptSpellCheckOptions()));

			var onEmpty = new JavaScriptSpellCheckOptions { BulkScanPageScript = true };
			Assert.Equal(("on — NO DICTIONARIES", true, false), ConfigSummary.DescribeBulkScan(onEmpty));

			var onSet = new JavaScriptSpellCheckOptions { BulkScanPageScript = true, ScriptBulkScanDictionaries = new() { "de", "en" } };
			Assert.Equal(("on (de, en)", false, false), ConfigSummary.DescribeBulkScan(onSet));
		}

		[Fact]
		public void CmsFreshness_Current_Older_Missing_MapToGreenAmberRed()
		{
			var notConfigured = new CmsContentListFreshness(false, false, false, false, 0, 0, "", null, "");
			Assert.Equal(("off", (ConsoleColor?)null, true), ConfigSummary.DescribeCmsFreshness(notConfigured, ""));

			// present + within limit → green "current"
			var current = new CmsContentListFreshness(true, true, false, false, 3, 30, "/data/acme.csv", DateTime.Now, "");
			Assert.Equal(("configured · current (3 d)", ConsoleColor.Green, false), ConfigSummary.DescribeCmsFreshness(current, "/data/acme.csv"));

			// present + over limit → amber "older"
			var older = new CmsContentListFreshness(true, true, false, true, 47, 30, "/data/acme.csv", DateTime.Now, "");
			Assert.Equal(("configured · older (47 d, limit 30 d)", ConsoleColor.DarkYellow, false), ConfigSummary.DescribeCmsFreshness(older, "/data/acme.csv"));

			// configured but absent → red, and the resolved path is shown
			var missing = new CmsContentListFreshness(true, false, false, false, 0, 30, "/data/acme.csv", null, "");
			Assert.Equal(("configured · missing → /data/acme.csv", ConsoleColor.Red, false), ConfigSummary.DescribeCmsFreshness(missing, "/data/acme.csv"));

			// present but freshness check disabled (MaxAgeDays <= 0) → green, no staleness
			var noLimit = new CmsContentListFreshness(true, true, true, false, 10, 0, "/data/acme.csv", DateTime.Now, "");
			Assert.Equal(("configured · present (10 d, no age limit)", ConsoleColor.Green, false), ConfigSummary.DescribeCmsFreshness(noLimit, "/data/acme.csv"));
		}

		[Fact]
		public void DictionaryMaintenance_Off_Report_Interactive_WithTargets()
		{
			Assert.Equal("off", ConfigSummary.DescribeDictionaryMaintenance(new DictionaryMaintenanceConfig()));
			Assert.Equal("report", ConfigSummary.DescribeDictionaryMaintenance(new DictionaryMaintenanceConfig { Mode = "Report" }));
			Assert.Equal("interactive", ConfigSummary.DescribeDictionaryMaintenance(new DictionaryMaintenanceConfig { Mode = "Interactive" }));
			Assert.Equal(
				"interactive (user+site)",
				ConfigSummary.DescribeDictionaryMaintenance(new DictionaryMaintenanceConfig { Mode = "Interactive", UpdateUserDictionary = true, UpdateSiteSpecificDictionary = true }));

			Assert.False(ConfigSummary.IsDictionaryMaintenanceActive(new DictionaryMaintenanceConfig()));
			Assert.True(ConfigSummary.IsDictionaryMaintenanceActive(new DictionaryMaintenanceConfig { Mode = "Report" }));
		}
	}
}
