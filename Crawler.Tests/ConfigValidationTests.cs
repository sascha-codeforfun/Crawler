using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for Config.LoadFromJson — covers the comment-stripping behaviour of
	/// FilterComments and the throw paths of ValidateConfig. Both are private;
	/// they are exercised indirectly through the public LoadFromJson surface.
	///
	/// Each test writes a minimal JSON file to a temp dir, loads it, and asserts
	/// either success (returning a populated Config) or an InvalidOperationException
	/// whose message names the failing rule.
	/// </summary>
	[Collection("Logger")]
	public class ConfigValidationTests : IDisposable
	{
		private readonly string _tempDir;

		public ConfigValidationTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"cfg-val-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
			Logger.Initialize(Path.Combine(_tempDir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		private string WriteJson(string content)
		{
			var path = Path.Combine(_tempDir, $"cfg-{Guid.NewGuid():N}.json");
			File.WriteAllText(path, content, Encoding.UTF8);
			return path;
		}

		// Builds a valid single-site Config, attaches a CmsContentList with the given
		// Path, sets the site's PostCrawlPass, then runs the projection + the
		// post-resolution validation (where the CMS cascade now lives, having moved out
		// of load-time ValidateConfig once PostCrawlPass became per-site). Returns the
		// resolved config; throws InvalidOperationException if the cascade halts.
		private static Config ResolveWithCms(string cmsPath, bool postCrawlPass)
		{
			var site = new SiteConfig
			{
				Name = "Site1",
				Tenant = "tenant001",
				IsPrimary = true,
				Url = "https://example.com",
				UrlSubdomainsAllowed = [],
				PostCrawlPass = postCrawlPass,
			};
			var config = new Config
			{
				Sites = [site],
				BaseDirectory = "C:/temp",
				FilePattern = "*.html",
				CustomDictionaryFile = "custom.dic",
				CmsContentList = new CmsContentListConfig { Path = cmsPath },
			};
			config.ResolveForSite(site);
			Config.ValidateResolvedSite(config);
			return config;
		}

		// A JSON body that satisfies every required ValidateConfig rule. Tests
		// that want to trigger a single failure mode mutate one field of this
		// shape and assert the resulting error message.
		private static string ValidConfigJson => """
			{
				"Sites": [
					{
						"Name": "Site1",
						"Tenant": "tenant001",
						"IsPrimary": true,
						"Url": "https://example.com",
						"UrlSubdomainsAllowed": [],
						"PostCrawlPass": false
					}
				],
				"BaseDirectory": "C:/temp",
				"FilePattern": "*.html",
				"CustomDictionaryFile": "custom.dic",
				"TagsToRemoveBeforeSpellCheck": [ "script" ],
				"AttributesToRemoveBeforeSpellCheck": [ "style" ],
				"MaxDegreeOfParallelism": 0,
				"MaxConcurrentPageDownloads": 1,
				"MaxConcurrentAssetDownloads": 1
			}
			""";

		// ── Comment stripping (FilterComments) ──────────────────────────────

		[Fact]
		public void LoadFromJson_StripsDoubleSlashLineComments()
		{
			// Lines whose first non-whitespace chars are // are stripped before
			// JSON parsing. Lines containing trailing // mid-line are NOT stripped
			// (the implementation only filters whole-line comments).
			var path = WriteJson("""
				{
					// Top-level comment — should be removed.
					"Sites": [
						{
							"Name": "Site1",
							"Tenant": "tenant001",
							"IsPrimary": true,
							"Url": "https://example.com",
							"UrlSubdomainsAllowed": [],
							"PostCrawlPass": false
						}
					],
					"BaseDirectory": "C:/temp",
					"FilePattern": "*.html",
					"CustomDictionaryFile": "custom.dic",
					"TagsToRemoveBeforeSpellCheck": [ "script" ],
					"AttributesToRemoveBeforeSpellCheck": [ "style" ],
					"MaxConcurrentPageDownloads": 1,
					"MaxConcurrentAssetDownloads": 1
				}
				""");

			var config = Config.LoadFromJson(path);

			Assert.Equal("https://example.com", config.Sites[0].Url);
		}

		[Fact]
		public void LoadFromJson_AcceptsIndentedComments()
		{
			// A // comment with leading whitespace is also stripped — FilterComments
			// uses TrimStart() before checking the comment marker.
			var path = WriteJson("""
				{
					"Sites": [
						{
							"Name": "Site1",
							"Tenant": "tenant001",
								// Indented comment.
							"IsPrimary": true,
							"Url": "https://example.com",
							"UrlSubdomainsAllowed": [],
							"PostCrawlPass": false
						}
					],
					"BaseDirectory": "C:/temp",
					"FilePattern": "*.html",
					"CustomDictionaryFile": "custom.dic",
					"TagsToRemoveBeforeSpellCheck": [ "script" ],
					"AttributesToRemoveBeforeSpellCheck": [ "style" ],
					"MaxConcurrentPageDownloads": 1,
					"MaxConcurrentAssetDownloads": 1
				}
				""");

			var config = Config.LoadFromJson(path);

			Assert.Equal("C:/temp", config.BaseDirectory);
		}

		// ── ValidateConfig — individual rule failures ───────────────────────

		[Fact]
		public void LoadFromJson_ThrowsWhenSiteUrlEmpty()
		{
			// A site with an empty Url is a definite config error, caught at load
			// (independent of which site is later selected).
			var path = WriteJson(ValidConfigJson.Replace(
				"\"Url\": \"https://example.com\"",
				"\"Url\": \"\""));

			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));

			Assert.Contains("has an empty Url", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenBaseDirectoryWhitespace()
		{
			var path = WriteJson(ValidConfigJson.Replace(
				"\"BaseDirectory\": \"C:/temp\"",
				"\"BaseDirectory\": \"   \""));

			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));

			Assert.Contains("BaseDirectory is required.", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenFilePatternEmpty()
		{
			var path = WriteJson(ValidConfigJson.Replace(
				"\"FilePattern\": \"*.html\"",
				"\"FilePattern\": \"\""));

			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));

			Assert.Contains("FilePattern is required.", ex.Message);
		}

		[Theory]
		[InlineData("html")]        // no "*." prefix — GetFiles would treat as literal filename
		[InlineData("*")]           // bare wildcard, no extension
		[InlineData("*.*")]         // matches everything, including .header sidecars / .bak
		[InlineData("*.ht ml")]    // space — invalid
		[InlineData("*.markuppage")] // 10-char extension exceeds the 8 cap
		public void LoadFromJson_ThrowsWhenFilePatternMalformed(string badPattern)
		{
			var path = WriteJson(ValidConfigJson.Replace(
				"\"FilePattern\": \"*.html\"",
				$"\"FilePattern\": \"{badPattern}\""));

			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));

			Assert.Contains("FilePattern must be a glob", ex.Message);
		}

		// ── ContentUnwantedPatterns validation (envelope Reference integrity) ──

		// Injects a ContentUnwantedPatterns array into the valid config, using the same
		// stable anchor as WithUrlHighlight.
		private static string WithUnwantedPatterns(string arrayBody) =>
			ValidConfigJson.Replace(
				"\"MaxConcurrentAssetDownloads\": 1",
				"\"MaxConcurrentAssetDownloads\": 1,\n\t\t\t\t\"ContentUnwantedPatterns\": " + arrayBody);

		[Fact]
		public void LoadFromJson_AcceptsValidEnvelopeReference()
		{
			var body = "[ { \"Category\": \"Security\", \"Name\": \"CMS-Parameter-Leak\", "
				+ "\"GroupPatterns\": true, \"Reference\": \"CMS-Editor-Error\", \"CaseSensitive\": true, "
				+ "\"Patterns\": [ \"%(\", \")%\" ] }, "
				+ "{ \"Category\": \"Security\", \"Name\": \"CMS-Editor-Error\", \"GroupPatterns\": false, "
				+ "\"CaseSensitive\": true, \"Patterns\": [ \"produkt.\", \"p_name\" ] } ]";
			var cfg = Config.LoadFromJson(WriteJson(WithUnwantedPatterns(body)));
			Assert.Equal(2, cfg.ContentUnwantedPatterns.Count);
			Assert.Equal("CMS-Editor-Error", cfg.ContentUnwantedPatterns[0].Reference);
		}

		[Fact]
		public void LoadFromJson_AcceptsSetsWithoutAnyReference()
		{
			var body = "[ { \"Category\": \"Test\", \"Name\": \"A\", \"GroupPatterns\": false, \"Patterns\": [ \"x\" ] }, "
				+ "{ \"Category\": \"Test\", \"Name\": \"B\", \"GroupPatterns\": false, \"Patterns\": [ \"y\" ] } ]";
			var cfg = Config.LoadFromJson(WriteJson(WithUnwantedPatterns(body)));
			Assert.Equal(2, cfg.ContentUnwantedPatterns.Count);
		}

		[Fact]
		public void LoadFromJson_AcceptsOnlyFlagUnbalancedOnEnvelope()
		{
			var body = "[ { \"Category\": \"Security\", \"Name\": \"Mustache\", \"GroupPatterns\": true, "
				+ "\"OnlyFlagUnbalanced\": true, \"CaseSensitive\": true, \"Patterns\": [ \"{{\", \"}}\" ] } ]";
			var cfg = Config.LoadFromJson(WriteJson(WithUnwantedPatterns(body)));
			Assert.True(cfg.ContentUnwantedPatterns[0].OnlyFlagUnbalanced);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenOnlyFlagUnbalancedOnNonEnvelope()
		{
			// OnlyFlagUnbalanced on a single-pattern (non-envelope) set is inert → rejected (rule D).
			var body = "[ { \"Category\": \"Security\", \"Name\": \"Bad\", \"GroupPatterns\": false, "
				+ "\"OnlyFlagUnbalanced\": true, \"Patterns\": [ \"{{\" ] } ]";
			var ex = Assert.Throws<InvalidOperationException>(
				() => Config.LoadFromJson(WriteJson(WithUnwantedPatterns(body))));
			Assert.Contains("OnlyFlagUnbalanced", ex.Message);
			Assert.Contains("not an envelope", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsOnDuplicateUnwantedPatternName()
		{
			var body = "[ { \"Category\": \"Test\", \"Name\": \"Dup\", \"GroupPatterns\": false, \"Patterns\": [ \"x\" ] }, "
				+ "{ \"Category\": \"Test\", \"Name\": \"Dup\", \"GroupPatterns\": false, \"Patterns\": [ \"y\" ] } ]";
			var ex = Assert.Throws<InvalidOperationException>(
				() => Config.LoadFromJson(WriteJson(WithUnwantedPatterns(body))));
			Assert.Contains("duplicate Name", ex.Message);
			Assert.Contains("'Dup'", ex.Message);
		}

		[Fact]
		public void LoadFromJson_AcceptsDuplicateEmptyUnwantedPatternNames()
		{
			// Empty-Name entries are unconfigured placeholders — uniqueness is scoped to
			// non-empty Names, so two of them must NOT trip rule A.
			var body = "[ { \"Category\": \"Test\", \"Name\": \"\", \"GroupPatterns\": false, \"Patterns\": [ \"x\" ] }, "
				+ "{ \"Category\": \"Test\", \"Name\": \"\", \"GroupPatterns\": false, \"Patterns\": [ \"y\" ] } ]";
			var cfg = Config.LoadFromJson(WriteJson(WithUnwantedPatterns(body)));
			Assert.Equal(2, cfg.ContentUnwantedPatterns.Count);
		}

		[Fact]
		public void LoadFromJson_ThrowsOnDanglingUnwantedPatternReference()
		{
			var body = "[ { \"Category\": \"Security\", \"Name\": \"Leak\", \"GroupPatterns\": true, "
				+ "\"Reference\": \"Nope\", \"CaseSensitive\": true, \"Patterns\": [ \"%(\", \")%\" ] } ]";
			var ex = Assert.Throws<InvalidOperationException>(
				() => Config.LoadFromJson(WriteJson(WithUnwantedPatterns(body))));
			Assert.Contains("names no configured set", ex.Message);
			Assert.Contains("Nope", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenReferenceOnNonEnvelope()
		{
			// The operator's mistake: Reference on an ungrouped set — the fold can never fire.
			var body = "[ { \"Category\": \"Security\", \"Name\": \"Leak\", \"GroupPatterns\": false, "
				+ "\"Reference\": \"Editor\", \"CaseSensitive\": true, \"Patterns\": [ \"%(\", \")%\" ] }, "
				+ "{ \"Category\": \"Security\", \"Name\": \"Editor\", \"GroupPatterns\": false, "
				+ "\"CaseSensitive\": true, \"Patterns\": [ \"produkt.\" ] } ]";
			var ex = Assert.Throws<InvalidOperationException>(
				() => Config.LoadFromJson(WriteJson(WithUnwantedPatterns(body))));
			Assert.Contains("is not an envelope", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenReferenceOnGroupedSetWithWrongPatternCount()
		{
			var body = "[ { \"Category\": \"Security\", \"Name\": \"Leak\", \"GroupPatterns\": true, "
				+ "\"Reference\": \"Editor\", \"CaseSensitive\": true, \"Patterns\": [ \"%(\", \")%\", \"%}\" ] }, "
				+ "{ \"Category\": \"Security\", \"Name\": \"Editor\", \"GroupPatterns\": false, "
				+ "\"CaseSensitive\": true, \"Patterns\": [ \"produkt.\" ] } ]";
			var ex = Assert.Throws<InvalidOperationException>(
				() => Config.LoadFromJson(WriteJson(WithUnwantedPatterns(body))));
			Assert.Contains("is not an envelope", ex.Message);
		}

		// ── TriageUrlHighlight validation ────────────────────────────────────

		// Injects a TriageUrlHighlight array into the valid config by appending it
		// after the last property line (a stable anchor in ValidConfigJson).
		private static string WithUrlHighlight(string arrayBody) =>
			ValidConfigJson.Replace(
				"\"MaxConcurrentAssetDownloads\": 1",
				"\"MaxConcurrentAssetDownloads\": 1,\n\t\t\t\t\"TriageUrlHighlight\": " + arrayBody);

		[Fact]
		public void LoadFromJson_AcceptsValidUrlHighlightRule()
		{
			var path = WriteJson(WithUrlHighlight(
				"[ { \"Values\": [ \"/en/\", \"/de/\" ], \"Highlight\": 1 } ]"));

			var config = Config.LoadFromJson(path);

			var rule = Assert.Single(config.TriageUrlHighlight);
			Assert.Equal(["/en/", "/de/"], rule.Values);
			Assert.Equal(1, rule.Highlight);
		}

		[Fact]
		public void LoadFromJson_AcceptsEmptyUrlHighlightList()
		{
			// Feature off (the default) must remain valid.
			var path = WriteJson(WithUrlHighlight("[]"));
			var config = Config.LoadFromJson(path);
			Assert.Empty(config.TriageUrlHighlight);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenUrlHighlightValuesEmpty()
		{
			// A rule with no fragments is a config mistake — caught at load.
			var path = WriteJson(WithUrlHighlight(
				"[ { \"Values\": [], \"Highlight\": 1 } ]"));

			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));

			Assert.Contains("must contain at least one path fragment", ex.Message);
		}

		[Theory]
		[InlineData("en")]    // no anchors at all
		[InlineData("/en")]   // missing trailing slash
		[InlineData("en/")]   // missing leading slash
		[InlineData("/")]     // length < 2
		public void LoadFromJson_ThrowsWhenUrlHighlightValueNotSlashBounded(string badValue)
		{
			var path = WriteJson(WithUrlHighlight(
				$"[ {{ \"Values\": [ \"{badValue}\" ], \"Highlight\": 1 }} ]"));

			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));

			Assert.Contains("must be a slash-bounded", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenOneFragmentInGroupMalformed()
		{
			// A bad fragment anywhere in the list fails the whole load — the error
			// names the offending index so the operator can find it.
			var path = WriteJson(WithUrlHighlight(
				"[ { \"Values\": [ \"/en/\", \"bad\" ], \"Highlight\": 1 } ]"));

			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));

			Assert.Contains("must be a slash-bounded", ex.Message);
		}

		[Theory]
		[InlineData(0)]
		[InlineData(6)]
		[InlineData(-1)]
		public void LoadFromJson_ThrowsWhenUrlHighlightSlotOutOfRange(int badSlot)
		{
			var path = WriteJson(WithUrlHighlight(
				$"[ {{ \"Values\": [ \"/en/\" ], \"Highlight\": {badSlot} }} ]"));

			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));

			Assert.Contains("out of range", ex.Message);
		}

		// ── SEO config validation ────────────────────────────────────────────

		private static string WithSeo(string seoJsonBody) =>
			ValidConfigJson.Replace(
				"\"FilePattern\": \"*.html\",",
				"\"FilePattern\": \"*.html\",\n\t\t\t\t\"Seo\": { " + seoJsonBody + " },");

		[Theory]
		[InlineData("no placeholder here")]              // zero {title}
		[InlineData("{title} | {title} | Brand")]        // two {title}
		public void LoadFromJson_ThrowsWhenTitleTemplatePlaceholderCountWrong(string template)
		{
			var path = WriteJson(WithSeo($"\"TitleTemplates\": [\"{template}\"]"));
			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));
			Assert.Contains("exactly one", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenAnyTitleTemplateEntryPlaceholderCountWrong()
		{
			// First entry is valid, second has zero placeholders → still halts, and the
			// error names the offending index.
			var path = WriteJson(WithSeo("\"TitleTemplates\": [\"{title} | Brand\", \"no placeholder\"]"));
			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));
			Assert.Contains("exactly one", ex.Message);
			Assert.Contains("TitleTemplates[1]", ex.Message);
		}

		[Fact]
		public void LoadFromJson_AcceptsValidTitleTemplate()
		{
			var path = WriteJson(WithSeo("\"TitleTemplates\": [\"{title} | Brand\"]"));
			var config = Config.LoadFromJson(path);   // must not throw
			Assert.Equal("{title} | Brand", Assert.Single(config.Seo.TitleTemplates));
		}

		[Fact]
		public void LoadFromJson_AcceptsMultipleValidTitleTemplates()
		{
			var path = WriteJson(WithSeo(
				"\"TitleTemplates\": [\"{title} | Company\", \"{title} | Second Company\"]"));
			var config = Config.LoadFromJson(path);   // must not throw
			Assert.Equal(2, config.Seo.TitleTemplates.Count);
			Assert.Equal("{title} | Company", config.Seo.TitleTemplates[0]);
			Assert.Equal("{title} | Second Company", config.Seo.TitleTemplates[1]);
		}

		[Fact]
		public void LoadFromJson_AcceptsEmptyTitleTemplatesList()
		{
			var path = WriteJson(WithSeo("\"TitleTemplates\": []"));
			var config = Config.LoadFromJson(path);   // must not throw
			Assert.Empty(config.Seo.TitleTemplates);
		}

		[Fact]
		public void LoadFromJson_AcceptsNonLatinBrandInTitleTemplate()
		{
			// A Cyrillic brand name must be permitted — only invisible chars are barred.
			var path = WriteJson(WithSeo("\"TitleTemplates\": [\"{title} | \\u041F\\u0440\\u0430\\u0432\\u0434\\u0430\"]"));
			var config = Config.LoadFromJson(path);   // must not throw
			Assert.Contains("\u041F\u0440\u0430\u0432\u0434\u0430", config.Seo.TitleTemplates[0]);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenTitleTemplateHasInvisibleChar()
		{
			// Zero-width space embedded in a template → halt.
			var path = WriteJson(WithSeo("\"TitleTemplates\": [\"{title} | Brand\\u200B\"]"));
			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));
			Assert.Contains("invisible character", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenTitleMinNotLessThanMax()
		{
			var path = WriteJson(WithSeo("\"TitleMinLength\": 60, \"TitleMaxLength\": 60"));
			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));
			Assert.Contains("TitleMinLength", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenDescriptionMinNotLessThanMax()
		{
			var path = WriteJson(WithSeo("\"DescriptionMinLength\": 200, \"DescriptionMaxLength\": 160"));
			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));
			Assert.Contains("DescriptionMinLength", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenCustomDictionaryFileEmpty()
		{
			var path = WriteJson(ValidConfigJson.Replace(
				"\"CustomDictionaryFile\": \"custom.dic\"",
				"\"CustomDictionaryFile\": \"\""));

			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));

			Assert.Contains("CustomDictionaryFile is required.", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenMaxDegreeOfParallelismNegative()
		{
			var path = WriteJson(ValidConfigJson.Replace(
				"\"MaxDegreeOfParallelism\": 0",
				"\"MaxDegreeOfParallelism\": -1"));

			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));

			Assert.Contains("MaxDegreeOfParallelism must be 0 (auto) or a positive integer.", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenMaxConcurrentPageDownloadsZero()
		{
			var path = WriteJson(ValidConfigJson.Replace(
				"\"MaxConcurrentPageDownloads\": 1",
				"\"MaxConcurrentPageDownloads\": 0"));

			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));

			Assert.Contains("MaxConcurrentPageDownloads must be at least 1.", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenMaxConcurrentAssetDownloadsZero()
		{
			var path = WriteJson(ValidConfigJson.Replace(
				"\"MaxConcurrentAssetDownloads\": 1",
				"\"MaxConcurrentAssetDownloads\": 0"));

			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));

			Assert.Contains("MaxConcurrentAssetDownloads must be at least 1.", ex.Message);
		}

		// ── ValidateConfig — multi-error aggregation ────────────────────────

		[Fact]
		public void LoadFromJson_AggregatesAllErrorsInSingleException()
		{
			// Multiple validation failures must all be reported in one exception
			// message rather than only the first being surfaced. Uses current-schema
			// error sources that accumulate in ValidateConfig's error list: an empty
			// Sites collection AND an empty BaseDirectory.
			var path = WriteJson("""
				{
					"Sites": [],
					"BaseDirectory": "",
					"FilePattern": "*.html",
					"CustomDictionaryFile": "custom.dic",
					"TagsToRemoveBeforeSpellCheck": [ "script" ],
					"AttributesToRemoveBeforeSpellCheck": [ "style" ],
					"MaxConcurrentPageDownloads": 1,
					"MaxConcurrentAssetDownloads": 1
				}
				""");

			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));

			Assert.Contains("Sites must contain at least one site", ex.Message);
			Assert.Contains("BaseDirectory is required.", ex.Message);
		}

		// ── LoadFromJson — file-not-found ───────────────────────────────────

		[Fact]
		public void LoadFromJson_ThrowsFileNotFound_WhenPathDoesNotExist()
		{
			var missingPath = Path.Combine(_tempDir, "nonexistent.json");

			Assert.Throws<FileNotFoundException>(() => Config.LoadFromJson(missingPath));
		}

		// ── LoadFromJson — CmsContentList object schema ─────────────────────

		[Fact]
		public void LoadFromJson_CmsContentList_OmittedEntirely_RemainsNull()
		{
			// Backward-incompatible schema change: the legacy form
			// "CmsContentList": "path/to/file.csv" is no longer supported.
			// Omission should deserialize cleanly to null and have all
			// downstream features behave as disabled.
			var path = WriteJson("""
				{
					"Sites": [
						{
							"Name": "Site1",
							"Tenant": "tenant001",
							"IsPrimary": true,
							"Url": "https://example.com",
							"UrlSubdomainsAllowed": [],
							"PostCrawlPass": false
						}
					],
					"BaseDirectory": "C:/temp",
					"FilePattern": "*.html",
					"CustomDictionaryFile": "custom.dic",
					"TagsToRemoveBeforeSpellCheck": [ "script" ],
					"AttributesToRemoveBeforeSpellCheck": [ "style" ],
					"MaxConcurrentPageDownloads": 1,
					"MaxConcurrentAssetDownloads": 1
				}
				""");

			var config = Config.LoadFromJson(path);

			Assert.Null(config.CmsContentList);
		}

		[Fact]
		public void LoadFromJson_CmsContentList_FullSchema_PopulatesAllFields()
		{
			var path = WriteJson("""
				{
					"Sites": [
						{
							"Name": "Site1",
							"Tenant": "tenant001",
							"IsPrimary": true,
							"Url": "https://example.com",
							"UrlSubdomainsAllowed": [],
							"PostCrawlPass": false
						}
					],
					"BaseDirectory": "C:/temp",
					"FilePattern": "*.html",
					"CustomDictionaryFile": "custom.dic",
					"TagsToRemoveBeforeSpellCheck": [ "script" ],
					"AttributesToRemoveBeforeSpellCheck": [ "style" ],
					"MaxConcurrentPageDownloads": 1,
					"MaxConcurrentAssetDownloads": 1,
					"CmsContentList": {
						"Path": "C:/data/list.csv",
						"MaxAgeDays": 14,
						"Comment": "Refresh weekly from CMS XYZ."
					}
				}
				""");

			var config = Config.LoadFromJson(path);

			Assert.NotNull(config.CmsContentList);
			Assert.Equal("C:/data/list.csv", config.CmsContentList!.Path);
			Assert.Equal(14, config.CmsContentList.MaxAgeDays);
			Assert.Equal("Refresh weekly from CMS XYZ.", config.CmsContentList.Comment);
		}

		[Fact]
		public void LoadFromJson_CmsContentList_PartialSchema_AppliesDefaults()
		{
			// Path-only — MaxAgeDays defaults to 7, Comment to empty string.
			var path = WriteJson("""
				{
					"Sites": [
						{
							"Name": "Site1",
							"Tenant": "tenant001",
							"IsPrimary": true,
							"Url": "https://example.com",
							"UrlSubdomainsAllowed": [],
							"PostCrawlPass": false
						}
					],
					"BaseDirectory": "C:/temp",
					"FilePattern": "*.html",
					"CustomDictionaryFile": "custom.dic",
					"TagsToRemoveBeforeSpellCheck": [ "script" ],
					"AttributesToRemoveBeforeSpellCheck": [ "style" ],
					"MaxConcurrentPageDownloads": 1,
					"MaxConcurrentAssetDownloads": 1,
					"CmsContentList": {
						"Path": "C:/data/list.csv"
					}
				}
				""");

			var config = Config.LoadFromJson(path);

			Assert.NotNull(config.CmsContentList);
			Assert.Equal("C:/data/list.csv", config.CmsContentList!.Path);
			Assert.Equal(7, config.CmsContentList.MaxAgeDays);
			Assert.Equal(string.Empty, config.CmsContentList.Comment);
		}

		[Fact]
		public void LoadFromJson_CmsContentList_LegacyStringForm_ThrowsJsonException()
		{
			// The legacy form "CmsContentList": "path" must produce a clear
			// JSON deserialization error rather than silently deserializing
			// to null (which would look like "feature disabled" and hide the
			// real config error).
			var path = WriteJson("""
				{
					"Sites": [
						{
							"Name": "Site1",
							"Tenant": "tenant001",
							"IsPrimary": true,
							"Url": "https://example.com",
							"UrlSubdomainsAllowed": [],
							"PostCrawlPass": false
						}
					],
					"BaseDirectory": "C:/temp",
					"FilePattern": "*.html",
					"CustomDictionaryFile": "custom.dic",
					"TagsToRemoveBeforeSpellCheck": [ "script" ],
					"AttributesToRemoveBeforeSpellCheck": [ "style" ],
					"MaxConcurrentPageDownloads": 1,
					"MaxConcurrentAssetDownloads": 1,
					"CmsContentList": "C:/data/list.csv"
				}
				""");

			Assert.Throws<System.Text.Json.JsonException>(() => Config.LoadFromJson(path));
		}

		// ── LoadFromJson — CmsContentList validation cascade (fileset #313) ─

		// The validation runs as a strict cascade — fail fast on the first
		// condition that's actually broken. Tests below cover each path
		// through the decision table; paths are Windows-shape to match the
		// operator's build environment (per CLAUDE.md).

		[Fact]
		public void LoadFromJson_CmsContentList_EmptyPath_NoCrawl_AcceptsAsDisabled()
		{
			// Path empty + PostCrawlPass=false → OK. Optional feature, not configured.
			// (Cascade now runs post-resolution; PostCrawlPass is per-site.)
			var config = ResolveWithCms(cmsPath: "", postCrawlPass: false);
			Assert.NotNull(config);
		}

		[Fact]
		public void LoadFromJson_CmsContentList_EmptyPath_CrawlOn_Halts()
		{
			// Path empty + PostCrawlPass=true → HALT. Operator asked for the pass but
			// gave nothing to download from.
			var ex = Assert.Throws<InvalidOperationException>(
				() => ResolveWithCms(cmsPath: "", postCrawlPass: true));
			Assert.Contains("CmsContentList.PostCrawlPass is true", ex.Message);
			Assert.Contains("CmsContentList.Path is empty", ex.Message);
		}

		[Fact]
		public void LoadFromJson_CmsContentList_MalformedPath_Halts()
		{
			// Path non-empty but not fully qualified → HALT regardless of PostCrawlPass.
			// ':\\Crawler\\content.csv' is the exact typo shape that motivated this.
			var ex = Assert.Throws<InvalidOperationException>(
				() => ResolveWithCms(cmsPath: ":\\Crawler\\content.csv", postCrawlPass: false));
			Assert.Contains("not a fully-qualified absolute path", ex.Message);
			Assert.Contains(":\\Crawler\\content.csv", ex.Message);
		}

		[Fact]
		public void LoadFromJson_CmsContentList_RelativePath_Halts()
		{
			// A relative path is another well-formedness failure — reject explicitly.
			var ex = Assert.Throws<InvalidOperationException>(
				() => ResolveWithCms(cmsPath: "data/list.csv", postCrawlPass: false));
			Assert.Contains("not a fully-qualified absolute path", ex.Message);
		}

		[Fact]
		public void LoadFromJson_CmsContentList_WellFormedMissingFile_NoCrawl_AcceptsGracefully()
		{
			// Path well-formed but file missing + PostCrawlPass=false → OK. Graceful:
			// ticket-metadata lookup tolerates absence.
			var missingPath = Path.Combine(_tempDir, "does-not-exist.csv");
			var config = ResolveWithCms(cmsPath: missingPath, postCrawlPass: false);
			Assert.NotNull(config);
		}

		[Fact]
		public void LoadFromJson_CmsContentList_WellFormedMissingFile_CrawlOn_Halts()
		{
			// Path well-formed but file missing + PostCrawlPass=true → HALT. The pass
			// cannot run without the file.
			var missingPath = Path.Combine(_tempDir, "does-not-exist.csv");
			var ex = Assert.Throws<InvalidOperationException>(
				() => ResolveWithCms(cmsPath: missingPath, postCrawlPass: true));
			Assert.Contains("does not exist", ex.Message);
			Assert.Contains("CmsContentList.PostCrawlPass is true", ex.Message);
		}

		[Fact]
		public void LoadFromJson_CmsContentList_WellFormedPresentFile_CrawlOn_Accepts()
		{
			// Path well-formed + file present + PostCrawlPass=true → OK (success path).
			var existingPath = Path.Combine(_tempDir, "list.csv");
			File.WriteAllText(existingPath, "Path;Module\n/home;X\n", Encoding.UTF8);

			var config = ResolveWithCms(cmsPath: existingPath, postCrawlPass: true);
			Assert.NotNull(config);
			Assert.Equal(existingPath, config.CmsContentList!.Path);
		}

		[Fact]
		public void LoadFromJson_NewSchemaOnly_NoLegacyDetected()
		{
			// Sanity: a clean new-schema config loads and maps CmsContentList.
			var path = WriteJson("""
				{
					"Sites": [
						{
							"Name": "Site1",
							"Tenant": "tenant001",
							"IsPrimary": true,
							"Url": "https://example.com",
							"UrlSubdomainsAllowed": [],
							"PostCrawlPass": false
						}
					],
					"BaseDirectory": "C:/temp",
					"FilePattern": "*.html",
					"CustomDictionaryFile": "custom.dic",
					"TagsToRemoveBeforeSpellCheck": [ "script" ],
					"AttributesToRemoveBeforeSpellCheck": [ "style" ],
					"MaxConcurrentPageDownloads": 1,
					"MaxConcurrentAssetDownloads": 1,
					"CmsContentList": {
						"Path": "",
						"ColumnDelimiter": ";",
						"SkipRows": 0
					}
				}
				""");

			var config = Config.LoadFromJson(path);
			Assert.NotNull(config);
			Assert.NotNull(config.CmsContentList);
			Assert.Equal(";", config.CmsContentList!.ColumnDelimiter);
		}

		[Fact]
		public void LoadFromJson_NewTicketGenerationSchema_NoLegacyDetected()
		{
			// Sanity: a clean new-schema config loads. OverdueAfterDays inside
			// TicketGeneration is the correct shape.
			var path = WriteJson("""
				{
					"Sites": [
						{
							"Name": "Site1",
							"Tenant": "tenant001",
							"IsPrimary": true,
							"Url": "https://example.com",
							"UrlSubdomainsAllowed": [],
							"PostCrawlPass": false
						}
					],
					"BaseDirectory": "C:/temp",
					"FilePattern": "*.html",
					"CustomDictionaryFile": "custom.dic",
					"TagsToRemoveBeforeSpellCheck": [ "script" ],
					"AttributesToRemoveBeforeSpellCheck": [ "style" ],
					"MaxConcurrentPageDownloads": 1,
					"MaxConcurrentAssetDownloads": 1,
					"TicketGeneration": {
						"OverdueAfterDays": 60
					}
				}
				""");

			var config = Config.LoadFromJson(path);
			Assert.NotNull(config);
			Assert.Equal(60, config.TicketGeneration.OverdueAfterDays);
		}

		[Fact]
		public void LoadFromJson_DefaultOverdueAfterDays_IsThirty()
		{
			// When neither legacy nor new field is present, the default of
			// 30 days applies. Locks the default value as part of the
			// public contract.
			var path = WriteJson("""
				{
					"Sites": [
						{
							"Name": "Site1",
							"Tenant": "tenant001",
							"IsPrimary": true,
							"Url": "https://example.com",
							"UrlSubdomainsAllowed": [],
							"PostCrawlPass": false
						}
					],
					"BaseDirectory": "C:/temp",
					"FilePattern": "*.html",
					"CustomDictionaryFile": "custom.dic",
					"TagsToRemoveBeforeSpellCheck": [ "script" ],
					"AttributesToRemoveBeforeSpellCheck": [ "style" ],
					"MaxConcurrentPageDownloads": 1,
					"MaxConcurrentAssetDownloads": 1
				}
				""");

			var config = Config.LoadFromJson(path);
			Assert.Equal(30, config.TicketGeneration.OverdueAfterDays);
		}

		// ── #320: PathShortenSegments validation ───────────────────────────

		[Fact]
		public void LoadFromJson_PathShortenSegmentsAllValid_LoadsCleanly()
		{
			// All entries 4+ chars — no validation issue.
			var path = WriteJson("""
				{
					"Sites": [
						{
							"Name": "Site1",
							"Tenant": "tenant001",
							"IsPrimary": true,
							"Url": "https://example.com",
							"UrlSubdomainsAllowed": [],
							"PostCrawlPass": false
						}
					],
					"BaseDirectory": "C:/temp",
					"FilePattern": "*.html",
					"CustomDictionaryFile": "custom.dic",
					"TagsToRemoveBeforeSpellCheck": [ "script" ],
					"AttributesToRemoveBeforeSpellCheck": [ "style" ],
					"MaxConcurrentPageDownloads": 1,
					"MaxConcurrentAssetDownloads": 1,
					"TicketGeneration": {
						"PathShortenSegments": [ "privatkunden", "altersvorsorge" ]
					}
				}
				""");
			var config = Config.LoadFromJson(path);
			Assert.Equal(2, config.TicketGeneration.PathShortenSegments.Count);
		}

		[Fact]
		public void LoadFromJson_PathShortenSegmentsShortEntry_Interactive_Halts()
		{
			// Interactive (default — not silent): halt with a message listing
			// every bad entry so the operator fixes them all in one pass.
			var path = WriteJson("""
				{
					"Sites": [
						{
							"Name": "Site1",
							"Tenant": "tenant001",
							"IsPrimary": true,
							"Url": "https://example.com",
							"UrlSubdomainsAllowed": [],
							"PostCrawlPass": false
						}
					],
					"BaseDirectory": "C:/temp",
					"FilePattern": "*.html",
					"CustomDictionaryFile": "custom.dic",
					"TagsToRemoveBeforeSpellCheck": [ "script" ],
					"AttributesToRemoveBeforeSpellCheck": [ "style" ],
					"MaxConcurrentPageDownloads": 1,
					"MaxConcurrentAssetDownloads": 1,
					"TicketGeneration": {
						"PathShortenSegments": [ "de", "abc", "privatkunden" ]
					}
				}
				""");
			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));
			// Both bad entries enumerated.
			Assert.Contains("\"de\"", ex.Message);
			Assert.Contains("\"abc\"", ex.Message);
			// Valid entry not flagged.
			Assert.DoesNotContain("\"privatkunden\"", ex.Message);
			Assert.Contains("4 or more characters", ex.Message);
		}

		[Fact]
		public void LoadFromJson_PathShortenSegmentsShortEntry_Silent_WarnsAndSkips()
		{
			// Silent mode: warn (logged but doesn't throw), remove bad entries,
			// keep the run going. The valid entries still apply.
			var originalSilent = CrawlerContext.Silent;
			try
			{
				CrawlerContext.Silent = true;

				var path = WriteJson("""
					{
						"Sites": [
							{
								"Name": "Site1",
								"Tenant": "tenant001",
								"IsPrimary": true,
								"Url": "https://example.com",
								"UrlSubdomainsAllowed": [],
								"PostCrawlPass": false
							}
						],
						"BaseDirectory": "C:/temp",
						"FilePattern": "*.html",
						"CustomDictionaryFile": "custom.dic",
						"TagsToRemoveBeforeSpellCheck": [ "script" ],
						"AttributesToRemoveBeforeSpellCheck": [ "style" ],
						"MaxConcurrentPageDownloads": 1,
						"MaxConcurrentAssetDownloads": 1,
						"TicketGeneration": {
							"PathShortenSegments": [ "de", "abc", "privatkunden", "altersvorsorge" ]
						}
					}
					""");
				var config = Config.LoadFromJson(path);
				// Bad entries removed, valid entries preserved.
				Assert.Equal(2, config.TicketGeneration.PathShortenSegments.Count);
				Assert.Contains("privatkunden", config.TicketGeneration.PathShortenSegments);
				Assert.Contains("altersvorsorge", config.TicketGeneration.PathShortenSegments);
				Assert.DoesNotContain("de", config.TicketGeneration.PathShortenSegments);
				Assert.DoesNotContain("abc", config.TicketGeneration.PathShortenSegments);
			}
			finally
			{
				CrawlerContext.Silent = originalSilent;
			}
		}

		[Fact]
		public void LoadFromJson_PathShortenSegmentsEmpty_NoValidationIssue()
		{
			// Empty list = feature disabled, no validation noise.
			var path = WriteJson("""
				{
					"Sites": [
						{
							"Name": "Site1",
							"Tenant": "tenant001",
							"IsPrimary": true,
							"Url": "https://example.com",
							"UrlSubdomainsAllowed": [],
							"PostCrawlPass": false
						}
					],
					"BaseDirectory": "C:/temp",
					"FilePattern": "*.html",
					"CustomDictionaryFile": "custom.dic",
					"TagsToRemoveBeforeSpellCheck": [ "script" ],
					"AttributesToRemoveBeforeSpellCheck": [ "style" ],
					"MaxConcurrentPageDownloads": 1,
					"MaxConcurrentAssetDownloads": 1,
					"TicketGeneration": {
						"PathShortenSegments": []
					}
				}
				""");
			var config = Config.LoadFromJson(path);
			Assert.Empty(config.TicketGeneration.PathShortenSegments);
		}

		[Fact]
		public void LoadFromJson_PathShortenSegmentsExactlyFourChars_AcceptedAsMinimum()
		{
			// Boundary: 4 chars is the minimum that actually saves chars
			// (replacing /abcd/ with /.../ saves 1 char). Should pass.
			var path = WriteJson("""
				{
					"Sites": [
						{
							"Name": "Site1",
							"Tenant": "tenant001",
							"IsPrimary": true,
							"Url": "https://example.com",
							"UrlSubdomainsAllowed": [],
							"PostCrawlPass": false
						}
					],
					"BaseDirectory": "C:/temp",
					"FilePattern": "*.html",
					"CustomDictionaryFile": "custom.dic",
					"TagsToRemoveBeforeSpellCheck": [ "script" ],
					"AttributesToRemoveBeforeSpellCheck": [ "style" ],
					"MaxConcurrentPageDownloads": 1,
					"MaxConcurrentAssetDownloads": 1,
					"TicketGeneration": {
						"PathShortenSegments": [ "abcd" ]
					}
				}
				""");
			var config = Config.LoadFromJson(path);
			Assert.Single(config.TicketGeneration.PathShortenSegments);
		}

		// ── UnverifiedHtmlPolicy (#336) ─────────────────────────────────────

		[Fact]
		public void LoadFromJson_UnverifiedHtmlPolicy_Omitted_ResolvesToTrustByteSniff()
		{
			var path = WriteJson(ValidConfigJson);
			var config = Config.LoadFromJson(path);
			Assert.Equal(UnverifiedHtmlPolicy.TrustByteSniff, config.ResolvedUnverifiedHtmlPolicy);
		}

		[Theory]
		[InlineData("TrustByteSniff", UnverifiedHtmlPolicy.TrustByteSniff)]
		[InlineData("TrustContentType", UnverifiedHtmlPolicy.TrustContentType)]
		[InlineData("Quarantine", UnverifiedHtmlPolicy.Quarantine)]
		[InlineData("AnalyseBlindly", UnverifiedHtmlPolicy.AnalyseBlindly)]
		[InlineData("trustbytesniff", UnverifiedHtmlPolicy.TrustByteSniff)] // case-insensitive
		public void LoadFromJson_UnverifiedHtmlPolicy_ValidValue_Resolves(
			string value, UnverifiedHtmlPolicy expected)
		{
			var json = ValidConfigJson.TrimEnd().TrimEnd('}')
				+ $", \"UnverifiedHtmlPolicy\": \"{value}\" }}";
			var path = WriteJson(json);
			var config = Config.LoadFromJson(path);
			Assert.Equal(expected, config.ResolvedUnverifiedHtmlPolicy);
		}

		[Fact]
		public void LoadFromJson_UnverifiedHtmlPolicy_UnknownValue_Throws()
		{
			var json = ValidConfigJson.TrimEnd().TrimEnd('}')
				+ ", \"UnverifiedHtmlPolicy\": \"BelieveEverything\" }";
			var path = WriteJson(json);
			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));
			Assert.Contains("UnverifiedHtmlPolicy", ex.Message);
			Assert.Contains("TrustByteSniff", ex.Message); // message lists valid options
		}

		// ── Multi-site: Sites collection + exactly-one-primary ──────────────────

		// A self-contained valid config built from parts, so tests can vary the Sites
		// block precisely without depending on the fixture's exact indentation (a
		// mismatched multi-line .Replace would silently no-op and falsely pass).
		private static string ConfigWithSites(string sitesArrayJson) => $$"""
			{
				"Sites": {{sitesArrayJson}},
				"BaseDirectory": "C:/temp",
				"FilePattern": "*.html",
				"CustomDictionaryFile": "custom.dic",
				"TagsToRemoveBeforeSpellCheck": [ "script" ],
				"AttributesToRemoveBeforeSpellCheck": [ "style" ],
				"MaxDegreeOfParallelism": 0,
				"MaxConcurrentPageDownloads": 1,
				"MaxConcurrentAssetDownloads": 1
			}
			""";

		private const string OneSiteJson =
			"[ { \"Name\": \"Site1\", \"Tenant\": \"t1\", \"IsPrimary\": true, \"Url\": \"https://a.example.com\", \"UrlSubdomainsAllowed\": [], \"PostCrawlPass\": false } ]";

		[Fact]
		public void LoadFromJson_ThrowsWhenSitesEmpty()
		{
			var path = WriteJson(ConfigWithSites("[]"));
			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));
			Assert.Contains("Sites must contain at least one site", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenNoPrimary()
		{
			var sites = "[ { \"Name\": \"Site1\", \"Tenant\": \"t1\", \"IsPrimary\": false, \"Url\": \"https://a.example.com\", \"UrlSubdomainsAllowed\": [], \"PostCrawlPass\": false } ]";
			var path = WriteJson(ConfigWithSites(sites));
			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));
			Assert.Contains("Exactly one site must have IsPrimary:true", ex.Message);
			Assert.Contains("found none", ex.Message);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenMultiplePrimary()
		{
			var sites =
				"[ { \"Name\": \"Site1\", \"Tenant\": \"t1\", \"IsPrimary\": true, \"Url\": \"https://a.example.com\", \"UrlSubdomainsAllowed\": [], \"PostCrawlPass\": false }, "
				+ "{ \"Name\": \"Site2\", \"Tenant\": \"t2\", \"IsPrimary\": true, \"Url\": \"https://b.example.com\", \"UrlSubdomainsAllowed\": [], \"PostCrawlPass\": false } ]";
			var path = WriteJson(ConfigWithSites(sites));
			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));
			Assert.Contains("Exactly one site must have IsPrimary:true", ex.Message);
			Assert.Contains("found 2", ex.Message);
			Assert.Contains("'Site1'", ex.Message);
			Assert.Contains("'Site2'", ex.Message);
		}

		[Fact]
		public void LoadFromJson_AcceptsTwoSitesWithOnePrimary()
		{
			var sites =
				"[ { \"Name\": \"Site1\", \"Tenant\": \"t1\", \"IsPrimary\": true, \"Url\": \"https://a.example.com\", \"UrlSubdomainsAllowed\": [], \"PostCrawlPass\": false }, "
				+ "{ \"Name\": \"Site2\", \"Tenant\": \"t2\", \"IsPrimary\": false, \"Url\": \"https://b.example.com\", \"UrlSubdomainsAllowed\": [], \"PostCrawlPass\": true } ]";
			var path = WriteJson(ConfigWithSites(sites));
			var config = Config.LoadFromJson(path);   // must not throw
			Assert.Equal(2, config.Sites.Count);
			Assert.Single(config.Sites, s => s.IsPrimary);
		}

		[Fact]
		public void LoadFromJson_ThrowsWhenSiteUrlEmpty_ViaSitesBuilder()
		{
			var sites = "[ { \"Name\": \"Site1\", \"Tenant\": \"t1\", \"IsPrimary\": true, \"Url\": \"\", \"UrlSubdomainsAllowed\": [], \"PostCrawlPass\": false } ]";
			var path = WriteJson(ConfigWithSites(sites));
			var ex = Assert.Throws<InvalidOperationException>(() => Config.LoadFromJson(path));
			Assert.Contains("has an empty Url", ex.Message);
		}


		// ── IsValidFilePattern ────────────────────────────────────────────────

		[Theory]
		// Valid: "*." + 1-8 alphanumeric chars.
		[InlineData("*.html", true)]
		[InlineData("*.htm", true)]
		[InlineData("*.aspx", true)]
		[InlineData("*.xhtml", true)]
		[InlineData("*.h", true)]            // 1-char extension
		[InlineData("*.HTML", true)]         // uppercase allowed
		[InlineData("*.htm5", true)]         // digits allowed
		[InlineData("*.abcdefgh", true)]     // 8 chars — the cap, allowed
											 // Invalid.
		[InlineData("html", false)]          // no "*." — GetFiles treats as literal filename
		[InlineData(".html", false)]         // missing wildcard
		[InlineData("*", false)]             // bare wildcard, no extension
		[InlineData("*.", false)]            // empty extension
		[InlineData("*.*", false)]           // matches everything
		[InlineData("*.ht ml", false)]      // space
		[InlineData("*.ht.ml", false)]      // dot in extension
		[InlineData("*.abcdefghi", false)]   // 9 chars — exceeds the 8 cap
		[InlineData("*.markuppage", false)]  // 10 chars — implausible
		[InlineData("**.html", false)]       // double wildcard
		[InlineData("", false)]              // empty
		public void IsValidFilePattern_AcceptsOnlyStarDotExtensionGlobs(string pattern, bool expected)
		{
			Assert.Equal(expected, Config.IsValidFilePattern(pattern));
		}
	}
}
