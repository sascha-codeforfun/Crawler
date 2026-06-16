using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for SpellMetadataLookup.BuildMetadataLookup new fields (Package, CmsLink,
	/// SpecialInfo) and MatchesWildcard pattern matching.
	/// </summary>
	[Collection("Logger")]
	public class SpellMetadataTests : IDisposable
	{
		private readonly string _tempDir;

		public SpellMetadataTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"st-meta-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
			Logger.Initialize(Path.Combine(_tempDir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		private string WriteCsv(params string[] lines)
		{
			var path = Path.Combine(_tempDir, $"content-{Guid.NewGuid():N}.csv");
			File.WriteAllLines(path, lines, Encoding.UTF8);
			return path;
		}

		private static Config MakeConfig(string csvPath, TicketGenerationConfig tg) =>
			new()
			{
				CmsContentList = new CmsContentListConfig
				{
					Path = csvPath,
					ColumnDelimiter = ";",
				},
				TicketGeneration = tg,
			};

		// ── Path suffix stripping ───────────────────────────────────────────

		[Fact]
		public void BuildMetadataLookup_UrlWithHtmlSuffix_MatchesPfadWithoutSuffix()
		{
			// PFAD column has no .html — URL has .html — must still match.
			// Strip domain via PathStripPrefix, strip .html via
			// ValueSuffix removal, prepend RowFilter → matches PFAD.
			var csv = WriteCsv(
				"Path;Package",
				"/content/mysite/de/home/page;TestPackage");
			var config = new Config
			{
				CmsContentList = new CmsContentListConfig
				{
					Path = csv,
					ColumnDelimiter = ";",
					PathStripPrefix = "https://www.example.com",
					RowFilter = "/content/mysite",
					ValuePrefixReplace = true,
					ValueSuffix = ".html",
				},
				TicketGeneration = new TicketGenerationConfig
				{
					PathColumn = "Path",
					PackageColumn = "Package",
				}
			};
			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			// Strip domain  → /de/home/page.html
			// Strip .html   → /de/home/page
			// Prepend filter → /content/mysite/de/home/page  ← matches PathColumn
			var result = lookup("https://www.example.com/de/home/page.html");
			Assert.Equal("TestPackage", result.Package);
		}

		// ── SkipRows ─────────────────────────────────────────────────────

		[Fact]
		public void BuildMetadataLookup_CsvSkipRows_SkipsPreambleRows()
		{
			var csv = WriteCsv(
				"Preamble line 1",
				"Preamble line 2",
				"Preamble line 3",
				"Preamble line 4",
				"Path;Package",
				"https://example.com/page;TestPackage");
			var config = new Config
			{
				CmsContentList = new CmsContentListConfig
				{
					Path = csv,
					ColumnDelimiter = ";",
					SkipRows = 4,
				},
				TicketGeneration = new TicketGenerationConfig
				{
					PathColumn = "Path",
					PackageColumn = "Package",
				}
			};
			var result = SpellMetadataLookup.BuildMetadataLookup(config)("https://example.com/page");
			Assert.Equal("TestPackage", result.Package);
		}

		[Fact]
		public void BuildMetadataLookup_CsvSkipRowsZero_ReadsFirstRowAsHeader()
		{
			var csv = WriteCsv(
				"Path;Package",
				"https://example.com/page;TestPackage");
			var config = new Config
			{
				CmsContentList = new CmsContentListConfig
				{
					Path = csv,
					ColumnDelimiter = ";",
					SkipRows = 0,
				},
				TicketGeneration = new TicketGenerationConfig
				{
					PathColumn = "Path",
					PackageColumn = "Package",
				}
			};
			var result = SpellMetadataLookup.BuildMetadataLookup(config)("https://example.com/page");
			Assert.Equal("TestPackage", result.Package);
		}

		// ── Package field ─────────────────────────────────────────────────────

		[Fact]
		public void BuildMetadataLookup_PackageColumn_ReturnsPackage()
		{
			var csv = WriteCsv(
				"Path;Package",
				"https://example.com/page;TestPackage");
			var config = MakeConfig(csv, new TicketGenerationConfig
			{
				PathColumn = "Path",
				PackageColumn = "Package",
			});
			var result = SpellMetadataLookup.BuildMetadataLookup(config)("https://example.com/page");
			Assert.Equal("TestPackage", result.Package);
		}

		[Fact]
		public void BuildMetadataLookup_PackageColumnMissing_ReturnsEmpty()
		{
			var csv = WriteCsv(
				"Path;Other",
				"https://example.com/page;value");
			var config = MakeConfig(csv, new TicketGenerationConfig
			{
				PathColumn = "Path",
				PackageColumn = "Package", // column doesn't exist in CSV
			});
			var result = SpellMetadataLookup.BuildMetadataLookup(config)("https://example.com/page");
			Assert.Equal("", result.Package);
		}

		// ── CmsLink field ─────────────────────────────────────────────────────

		[Fact]
		public void BuildMetadataLookup_CmsLink_BuildsFromBaseUrlAndPath()
		{
			var csv = WriteCsv(
				"Path;PFAD",
				"https://example.com/page;/content/mysite/de/home/page");
			var config = MakeConfig(csv, new TicketGenerationConfig
			{
				PathColumn = "Path",
				CmsPathColumn = "PFAD",
				CmsEditorBaseUrl = "https://cms.example.com/editor.html",
				CmsEditorBaseUrlSuffix = ".html",
			});
			var result = SpellMetadataLookup.BuildMetadataLookup(config)("https://example.com/page");
			Assert.Equal("https://cms.example.com/editor.html/content/mysite/de/home/page.html",
				result.CmsLink);
		}

		[Fact]
		public void BuildMetadataLookup_CmsLink_NoSuffix_NoExtensionAppended()
		{
			var csv = WriteCsv(
				"Path;PFAD",
				"https://example.com/page;/content/mysite/de/home/page.html");
			var config = MakeConfig(csv, new TicketGenerationConfig
			{
				PathColumn = "Path",
				CmsPathColumn = "PFAD",
				CmsEditorBaseUrl = "https://cms.example.com/editor.html",
				CmsEditorBaseUrlSuffix = "",
			});
			var result = SpellMetadataLookup.BuildMetadataLookup(config)("https://example.com/page");
			Assert.Equal("https://cms.example.com/editor.html/content/mysite/de/home/page.html",
				result.CmsLink);
		}

		[Fact]
		public void BuildMetadataLookup_CmsLinkNoBaseUrl_ReturnsEmpty()
		{
			var csv = WriteCsv(
				"Path;PFAD",
				"https://example.com/page;/content/mysite/de/home/page");
			var config = MakeConfig(csv, new TicketGenerationConfig
			{
				PathColumn = "Path",
				CmsPathColumn = "PFAD",
				CmsEditorBaseUrl = "", // not configured
			});
			var result = SpellMetadataLookup.BuildMetadataLookup(config)("https://example.com/page");
			Assert.Equal("", result.CmsLink);
		}

		[Fact]
		public void BuildMetadataLookup_CmsLinkBaseUrlTrailingSlash_NoDuplicateSlash()
		{
			var csv = WriteCsv(
				"Path;PFAD",
				"https://example.com/page;/content/mysite/de/home/page");
			var config = MakeConfig(csv, new TicketGenerationConfig
			{
				PathColumn = "Path",
				CmsPathColumn = "PFAD",
				CmsEditorBaseUrl = "https://cms.example.com/editor.html/", // trailing slash
			});
			var result = SpellMetadataLookup.BuildMetadataLookup(config)("https://example.com/page");
			Assert.Equal("https://cms.example.com/editor.html/content/mysite/de/home/page",
				result.CmsLink);
		}

		// ── SpecialInfo field ─────────────────────────────────────────────────

		[Fact]
		public void BuildMetadataLookup_SpecialInfo_ExactPatternMatch()
		{
			var csv = WriteCsv(
				"Path;Status",
				"https://example.com/page;Auto-Publizierung (Mandant A)");
			var config = MakeConfig(csv, new TicketGenerationConfig
			{
				PathColumn = "Path",
				SpecialInfoMappings =
				[
					new SpecialInfoMapping
					{
						Column  = "Status",
						Pattern = "Auto-Publizierung*",
						Label   = "(auto-publiziert)",
					}
				],
			});
			var result = SpellMetadataLookup.BuildMetadataLookup(config)("https://example.com/page");
			Assert.Equal("(auto-publiziert)", result.SpecialInfo);
		}

		[Fact]
		public void BuildMetadataLookup_SpecialInfo_NoMatch_ReturnsEmpty()
		{
			var csv = WriteCsv(
				"Path;Status",
				"https://example.com/page;nicht gesperrt");
			var config = MakeConfig(csv, new TicketGenerationConfig
			{
				PathColumn = "Path",
				SpecialInfoMappings =
				[
					new SpecialInfoMapping
					{
						Column  = "Status",
						Pattern = "Auto-Publizierung*",
						Label   = "(auto-publiziert)",
					}
				],
			});
			var result = SpellMetadataLookup.BuildMetadataLookup(config)("https://example.com/page");
			Assert.Equal("", result.SpecialInfo);
		}

		[Fact]
		public void BuildMetadataLookup_SpecialInfo_FirstMatchWins()
		{
			var csv = WriteCsv(
				"Path;Status",
				"https://example.com/page;Auto-Publizierung (Mandant A)");
			var config = MakeConfig(csv, new TicketGenerationConfig
			{
				PathColumn = "Path",
				SpecialInfoMappings =
				[
					new SpecialInfoMapping { Column = "Status", Pattern = "Auto-*", Label = "first" },
					new SpecialInfoMapping { Column = "Status", Pattern = "*",       Label = "second" },
				],
			});
			var result = SpellMetadataLookup.BuildMetadataLookup(config)("https://example.com/page");
			Assert.Equal("first", result.SpecialInfo);
		}

		[Fact]
		public void BuildMetadataLookup_SpecialInfo_MultiplePatterns_SecondMatches()
		{
			var csv = WriteCsv(
				"Path;Status",
				"https://example.com/page;gesperrt");
			var config = MakeConfig(csv, new TicketGenerationConfig
			{
				PathColumn = "Path",
				SpecialInfoMappings =
				[
					new SpecialInfoMapping { Column = "Status", Pattern = "Auto-*",    Label = "auto" },
					new SpecialInfoMapping { Column = "Status", Pattern = "gesperrt*", Label = "locked" },
				],
			});
			var result = SpellMetadataLookup.BuildMetadataLookup(config)("https://example.com/page");
			Assert.Equal("locked", result.SpecialInfo);
		}

		// ── MatchesWildcard ───────────────────────────────────────────────────

		[Fact]
		public void MatchesWildcard_ExactMatch_ReturnsTrue()
			=> Assert.True(SpellMetadataLookup.MatchesWildcard("Auto-Publizierung", "Auto-Publizierung"));

		[Fact]
		public void MatchesWildcard_TrailingWildcard_MatchesPrefix()
			=> Assert.True(SpellMetadataLookup.MatchesWildcard("Auto-Publizierung (Mandant A)", "Auto-Publizierung*"));

		[Fact]
		public void MatchesWildcard_TrailingWildcard_DoesNotMatchDifferentPrefix()
			=> Assert.False(SpellMetadataLookup.MatchesWildcard("nicht gesperrt", "Auto-Publizierung*"));

		[Fact]
		public void MatchesWildcard_StarAlone_MatchesAnything()
			=> Assert.True(SpellMetadataLookup.MatchesWildcard("anything at all", "*"));

		[Fact]
		public void MatchesWildcard_StarAlone_MatchesEmpty()
			=> Assert.True(SpellMetadataLookup.MatchesWildcard("", "*"));

		[Fact]
		public void MatchesWildcard_CaseInsensitive()
			=> Assert.True(SpellMetadataLookup.MatchesWildcard("auto-publizierung", "Auto-*"));

		[Fact]
		public void MatchesWildcard_WildcardInMiddle_Matches()
			=> Assert.True(SpellMetadataLookup.MatchesWildcard("Auto-XYZ-Publizierung", "Auto-*-Publizierung"));

		[Fact]
		public void MatchesWildcard_NichtGesperrt_NotMatchedByGesperrt()
			=> Assert.False(SpellMetadataLookup.MatchesWildcard("nicht gesperrt", "gesperrt"));

		[Fact]
		public void MatchesWildcard_NichtGesperrt_MatchedByNichtStar()
			=> Assert.True(SpellMetadataLookup.MatchesWildcard("nicht gesperrt", "nicht*"));

		// ── [LF] collapse in WriteTicketText ─────────────────────────────────

		[Fact]
		public void WriteTicketText_EmptySpecialInfo_NoDoubleBlankLine()
		{
			var outputPath = Path.Combine(_tempDir, "ticket.log");

			var entries = new List<IssueTracking.IssueRecord>
			{
				new() { Type = "SPELLING", Word = "Fehlr", Url = "https://example.com/page",
					Status = "pending", DateFound = "2026-01-01", Language = "de" }
			};

			var csv = WriteCsv("Path;Package", "https://example.com/page;TestPackage");
			var config = MakeConfig(csv, new TicketGenerationConfig
			{
				PathColumn = "Path",
				PackageColumn = "Package",
				TicketShellTemplate = "Seite: {Url}[LF][LF]{SpecialInfo}",
				TicketIssueTypes = [new TicketIssueTypeEntry { Type = "SPELLING", Label = "Spelling" }],
				TicketSectionIntros = [new TicketSectionIntro { Type = "SPELLING", Text = "Fehler:" }],
			});

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			TicketRenderer.WriteTicketText(outputPath, entries, config.TicketGeneration, config.CmsContentList, lookup);

			var text = File.ReadAllText(outputPath);
			// Empty SpecialInfo — should not produce double blank line
			Assert.DoesNotContain("\n\n\n", text);
			Assert.Contains("Fehler:", text);
		}

		[Fact]
		public void WriteTicketText_FilledSpecialInfo_AppearsInOutput()
		{
			var outputPath = Path.Combine(_tempDir, "ticket2.log");

			var entries = new List<IssueTracking.IssueRecord>
			{
				new() { Type = "SPELLING", Word = "Fehlr", Url = "https://example.com/page",
					Status = "pending", DateFound = "2026-01-01", Language = "de" }
			};

			var csv = WriteCsv(
				"Path;Status",
				"https://example.com/page;Auto-Publizierung (Mandant A)");
			var config = MakeConfig(csv, new TicketGenerationConfig
			{
				PathColumn = "Path",
				TicketShellTemplate = "Seite: {Url}[LF]{SpecialInfo}",
				TicketIssueTypes = [new TicketIssueTypeEntry { Type = "SPELLING", Label = "Spelling" }],
				TicketSectionIntros = [new TicketSectionIntro { Type = "SPELLING", Text = "Fehler:" }],
				SpecialInfoMappings =
				[
					new SpecialInfoMapping
					{
						Column  = "Status",
						Pattern = "Auto-Publizierung*",
						Label   = "(auto-publiziert)",
					}
				],
			});

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			TicketRenderer.WriteTicketText(outputPath, entries, config.TicketGeneration, config.CmsContentList, lookup);

			var text = File.ReadAllText(outputPath);
			Assert.Contains("(auto-publiziert)", text);
			Assert.Contains("Fehler:", text);
		}

		// ── Location field three-way branch (notes-for-later #4) ────────────
		//
		// Inside BuildMetadataLookup, the Location field has three branches:
		//   1. sourceVal equals UrlSourceLocalName    → location = UrlSourceLocalName (canonical casing)
		//   2. sourceVal equals UrlSourceExternalName → location = UrlSourceExternalName (canonical casing)
		//   3. neither matches                        → location = sourceVal (fallback, raw)
		// Each branch was previously untested.

		[Fact]
		public void BuildMetadataLookup_Location_LocalNameMatchReturnsCanonicalLocalName()
		{
			var csv = WriteCsv(
				"Path;Source",
				"/page;local");
			var config = new Config
			{
				CmsContentList = new CmsContentListConfig
				{
					Path = csv,
					ColumnDelimiter = ";",
				},
				TicketGeneration = new TicketGenerationConfig
				{
					PathColumn = "Path",
					UrlSourceColumn = "Source",
					UrlSourceLocalName = "Local",
					UrlSourceExternalName = "External",
				}
			};

			var result = SpellMetadataLookup.BuildMetadataLookup(config)("/page");

			// Case-insensitive match — the canonical casing "Local" is returned,
			// not the raw "local" from the CSV.
			Assert.Equal("Local", result.Location);
		}

		[Fact]
		public void BuildMetadataLookup_Location_ExternalNameMatchReturnsCanonicalExternalName()
		{
			var csv = WriteCsv(
				"Path;Source",
				"/page;EXTERNAL");
			var config = new Config
			{
				CmsContentList = new CmsContentListConfig
				{
					Path = csv,
					ColumnDelimiter = ";",
				},
				TicketGeneration = new TicketGenerationConfig
				{
					PathColumn = "Path",
					UrlSourceColumn = "Source",
					UrlSourceLocalName = "Local",
					UrlSourceExternalName = "External",
				}
			};

			var result = SpellMetadataLookup.BuildMetadataLookup(config)("/page");

			Assert.Equal("External", result.Location);
		}

		[Fact]
		public void BuildMetadataLookup_Location_UnknownValueFallsBackToRawSourceValue()
		{
			// Source value matches neither configured canonical name → returned as-is.
			var csv = WriteCsv(
				"Path;Source",
				"/page;UnclassifiedValue");
			var config = new Config
			{
				CmsContentList = new CmsContentListConfig
				{
					Path = csv,
					ColumnDelimiter = ";",
				},
				TicketGeneration = new TicketGenerationConfig
				{
					PathColumn = "Path",
					UrlSourceColumn = "Source",
					UrlSourceLocalName = "Local",
					UrlSourceExternalName = "External",
				}
			};

			var result = SpellMetadataLookup.BuildMetadataLookup(config)("/page");

			Assert.Equal("UnclassifiedValue", result.Location);
		}

		[Fact]
		public void BuildMetadataLookup_Location_EmptyWhenUrlSourceColumnNotConfigured()
		{
			// When UrlSourceColumn is unset, the Location branch is skipped
			// entirely and the field defaults to empty.
			var csv = WriteCsv(
				"Path;Source",
				"/page;local");
			var config = new Config
			{
				CmsContentList = new CmsContentListConfig
				{
					Path = csv,
					ColumnDelimiter = ";",
				},
				TicketGeneration = new TicketGenerationConfig
				{
					PathColumn = "Path",
					// UrlSourceColumn intentionally not set
					PackageColumn = "Source", // give the lookup *something* to do
				}
			};

			var result = SpellMetadataLookup.BuildMetadataLookup(config)("/page");

			Assert.Equal(string.Empty, result.Location);
		}

	}
}
