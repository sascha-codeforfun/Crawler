using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for SpellMetadataLookup.BuildMetadataLookup. Synthetic-fixture style:
	/// each test writes a tiny CmsContentList with known structure, constructs a
	/// minimal Config, builds the lookup function, and asserts the resulting
	/// TicketMetadata for representative URLs.
	///
	/// Focus area for fileset #281: the strip-prefix resolution, where an empty
	/// PathStripPrefix must fall back to config.Url. This was a
	/// real bug — empty strip prefix left the full URL in place, which then
	/// could not match any path-shaped row in the CSV, producing empty ticket
	/// metadata silently.
	///
	/// All fixture content uses generic placeholder names (alpha, beta, foo,
	/// /cms/path/, https://example.test) with no resemblance to any real site
	/// or CMS structure.
	/// </summary>
	[Collection("Logger")]
	public class SpellMetadataLookupTests : IDisposable
	{
		private readonly string _tempDir;
		private readonly string _csvPath;

		public SpellMetadataLookupTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"meta-lookup-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
			_csvPath = Path.Combine(_tempDir, "content.csv");
			Logger.Initialize(Path.Combine(_tempDir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		// ── Fixture helpers ─────────────────────────────────────────────────

		private void WriteCsv(params string[] lines)
		{
			File.WriteAllLines(_csvPath, lines, System.Text.Encoding.UTF8);
		}

		/// <summary>
		/// Reads the current log file content for assertions in diagnostic tests.
		/// Logger writes to BaseDirectory + filename; when filename is rooted
		/// (our case — _tempDir/test.log), the rooted path wins so the log
		/// ends up in _tempDir.
		/// </summary>
		private string ReadLog()
		{
			var logPath = Path.Combine(_tempDir, "test.log");
			if (!File.Exists(logPath))
			{
				return string.Empty;
			}

			return File.ReadAllText(logPath);
		}

		/// <summary>
		/// Builds a minimal Config wired to invoke the path-construction logic
		/// in BuildMetadataLookup. The TicketGeneration section is set up to
		/// drive Location/Package/CmsLink resolution against the synthetic CSV.
		/// </summary>
		private Config BuildConfig(string url, string stripPrefix)
		{
			return new Config
			{
				Url = url,
				CmsContentList = new CmsContentListConfig
				{
					Path = _csvPath,
					ColumnDelimiter = ";",
					SkipRows = 0,
					RowFilter = "/cms/path",
					ValuePrefixReplace = true,
					ValueSuffix = ".html",
					PathStripPrefix = stripPrefix,
				},
				TicketGeneration = new TicketGenerationConfig
				{
					UrlSourceColumn = "Source",
					UrlSourceLocalName = "local",
					UrlSourceExternalName = "inherited",
					PackageColumn = "Module",
					CmsPathColumn = "Path",
					CmsEditorBaseUrl = "https://editor.example.test/cf#",
					CmsEditorBaseUrlSuffix = ".html",
					PathColumn = "Path",
				}
			};
		}

		// ── Scenario 1: Empty strip prefix falls back to config.Url ─────────

		[Fact]
		public void BuildMetadataLookup_EmptyStripPrefix_FallsBackToConfigUrl()
		{
			// CSV row matches the path constructed from a URL after config.Url is stripped.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""  // empty — should fall back to config.Url
			);

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);
			var meta = lookup("https://example.test/alpha/page-one.html");

			Assert.Equal("local", meta.Location);
			Assert.Equal("ModuleAlpha", meta.Package);
			Assert.Equal("https://editor.example.test/cf#/cms/path/alpha/page-one.html", meta.CmsLink);
		}

		// ── Scenario 2: Explicit strip prefix overrides config.Url ──────────

		[Fact]
		public void BuildMetadataLookup_ExplicitStripPrefix_OverridesConfigUrl()
		{
			// In some CMSes the export URL differs from the public site URL.
			// An explicit PathStripPrefix must take precedence.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/beta/page-two;inherited;ModuleBeta"
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: "https://export.example.test"  // explicit — different from config.Url
			);

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);

			// Input URL uses the export host — the strip prefix removes it cleanly.
			var meta = lookup("https://export.example.test/beta/page-two.html");

			Assert.Equal("inherited", meta.Location);
			Assert.Equal("ModuleBeta", meta.Package);
		}

		// ── Scenario 3: The original bug — empty strip prefix would fail ────

		[Fact]
		public void BuildMetadataLookup_EmptyStripPrefixWithNoConfigUrl_BehavesGracefully()
		{
			// Edge case: both config.Url AND PathStripPrefix empty.
			// The lookup uses the URL as-is, which won't match any path-shaped
			// CSV row → returns empty metadata. No exception, just empty fields.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);

			var config = BuildConfig(
				url: "",  // both empty — defensive fallback only
				stripPrefix: ""
			);

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);
			var meta = lookup("https://example.test/alpha/page-one.html");

			// Empty metadata returned without crashing.
			Assert.Equal("", meta.Location);
			Assert.Equal("", meta.Package);
			Assert.Equal("", meta.CmsLink);
		}

		// ── Scenario 4: URL with no match in CSV ─────────────────────────────

		[Fact]
		public void BuildMetadataLookup_UrlNotInCsv_ReturnsEmptyMetadata()
		{
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);

			// URL exists on the site but no CSV row matches.
			var meta = lookup("https://example.test/gamma/never-listed.html");

			Assert.Equal("", meta.Location);
			Assert.Equal("", meta.Package);
			Assert.Equal("", meta.CmsLink);
		}

		// ── Scenario 5: Configured fields populated correctly ────────────────

		[Fact]
		public void BuildMetadataLookup_AllFieldsPopulated_RendersCompleteMetadata()
		{
			// Confirms full path-construction-and-column-lookup pipeline works
			// end-to-end after the fix. Tests Location, Package, CmsLink in one go.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/foo/sub/page-three;local;ModuleFoo"
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);
			var meta = lookup("https://example.test/foo/sub/page-three.html");

			Assert.Equal("local", meta.Location);
			Assert.Equal("ModuleFoo", meta.Package);
			Assert.Equal("https://editor.example.test/cf#/cms/path/foo/sub/page-three.html", meta.CmsLink);
		}

		// ── Scenario 6: Suffix stripping when ValuePrefixReplace=true ─

		[Fact]
		public void BuildMetadataLookup_StripsSuffixBeforePrefixing()
		{
			// The .html extension is stripped from the URL before prefixing with
			// RowFilter, because the CSV's path column never has extensions.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);

			// URL has .html, CSV row does not. The match must still succeed.
			var meta = lookup("https://example.test/alpha/page-one.html");

			Assert.Equal("ModuleAlpha", meta.Package);
		}

		// ── Scenario 7: Case-insensitive strip ───────────────────────────────

		[Fact]
		public void BuildMetadataLookup_StripPrefixIsCaseInsensitive()
		{
			// pageUrl.Replace uses OrdinalIgnoreCase in the production code.
			// A page URL with mixed-case host should still be stripped correctly.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);
			var meta = lookup("HTTPS://Example.Test/alpha/page-one.html");

			Assert.Equal("ModuleAlpha", meta.Package);
		}

		// ── Scenario 8: Blank line in preamble counts toward skip ───────────

		[Fact]
		public void BuildMetadataLookup_BlankLineInPreamble_CountsTowardSkip()
		{
			// Mirrors Power Query's Table.Skip(table, N) semantic: blank rows
			// occupy a row number and consume a skip slot. The user-configured
			// SkipRows matches the row count visible in any editor (Excel,
			// Power Query, plain text).
			//
			// File layout: 3 metadata lines + 1 blank + header + 1 data row.
			// SkipRows = 4 must skip exactly those 4 rows so the header is
			// captured from row 5.
			WriteCsv(
				"Foo = some value",                                // row 1
				"Bar = another value",                             // row 2
				"Baz = yet another",                               // row 3
				"",                                                // row 4 — blank
				"Path;Source;Module",                              // row 5 — header
				"/cms/path/alpha/page-one;local;ModuleAlpha"       // row 6 — data
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);
			config.CmsContentList!.SkipRows = 4;

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);
			var meta = lookup("https://example.test/alpha/page-one.html");

			Assert.Equal("local", meta.Location);
			Assert.Equal("ModuleAlpha", meta.Package);
		}

		// ── Scenario 9: No blanks in preamble — baseline check ──────────────

		[Fact]
		public void BuildMetadataLookup_NoBlanksInPreamble_SkipsExactRowCount()
		{
			// Regression guard: the all-non-blank preamble case must still work
			// correctly after the loop reorder.
			WriteCsv(
				"line one of preamble",                            // row 1
				"line two of preamble",                            // row 2
				"line three of preamble",                          // row 3
				"line four of preamble",                           // row 4
				"Path;Source;Module",                              // row 5 — header
				"/cms/path/alpha/page-one;local;ModuleAlpha"       // row 6 — data
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);
			config.CmsContentList!.SkipRows = 4;

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);
			var meta = lookup("https://example.test/alpha/page-one.html");

			Assert.Equal("local", meta.Location);
			Assert.Equal("ModuleAlpha", meta.Package);
		}

		// ── Scenario 10: Blank rows after preamble are filtered ─────────────

		[Fact]
		public void BuildMetadataLookup_BlankDataRows_FilteredSilently()
		{
			// Blanks AFTER the preamble (in the data area) are still filtered
			// silently — common with CSV exports that include a trailing empty
			// row. Affects only post-preamble lines; preamble blanks still count.
			WriteCsv(
				"Path;Source;Module",                              // row 1 — header
				"/cms/path/alpha/page-one;local;ModuleAlpha",      // row 2 — data
				"",                                                // row 3 — blank
				"/cms/path/beta/page-two;inherited;ModuleBeta"     // row 4 — data
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);
			config.CmsContentList!.SkipRows = 0;

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);

			// Both data rows resolve successfully; the blank does not break parsing.
			var metaAlpha = lookup("https://example.test/alpha/page-one.html");
			var metaBeta = lookup("https://example.test/beta/page-two.html");

			Assert.Equal("ModuleAlpha", metaAlpha.Package);
			Assert.Equal("ModuleBeta", metaBeta.Package);
		}
		// ── Scenario 11: Template blank-line separators are preserved ───────

		[Fact]
		public void WriteTicketText_TemplateWithBlankLineSeparators_PreservesBlanks()
		{
			// Templates may include [LF][LF] sequences as intentional blank-line
			// separators between sections. The renderer must NOT collapse these —
			// the readability of the final ticket depends on those separators.
			//
			// Mirrors a real-world template structure: section labels separated
			// by blank lines for easy human reading and copy-paste into ticket
			// systems.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);
			// Blank-line separators now live in the page shell (#462). The shell
			// renders once per ticket; its [LF][LF] sequences become single blank
			// lines (collapse only kicks in at 3+ consecutive blanks).
			config.TicketGeneration.TicketShellTemplate =
				"Page:[LF]{Url}[LF][LF]Source: {Location}[LF][LF]Module: {Package}";
			config.TicketGeneration.TicketIssueTypes =
				[new TicketIssueTypeEntry { Type = "SPELLING", Label = "Spelling" }];
			config.TicketGeneration.TicketSectionIntros =
				[new TicketSectionIntro { Type = "SPELLING", Text = "Errors:" }];

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);
			var entries = new List<IssueTracking.IssueRecord>
			{
				new() { Type = "SPELLING", Word = "foo", Url = "https://example.test/alpha/page-one.html", Status = "pending", DateFound = "2026-05-14", Language = "en", SourceLabel = "p[#text]" }
			};

			var outputPath = Path.Combine(_tempDir, "ticket.log");
			TicketRenderer.WriteTicketText(outputPath, entries, config.TicketGeneration, config.CmsContentList, lookup);

			var content = File.ReadAllText(outputPath);

			// Blank lines between sections must be present (template's [LF][LF]
			// becomes one blank line via [LF] → Environment.NewLine substitution).
			// Under #315 the {Errors} block now opens with a dash separator,
			// the word line, the Context line, and a closing dash separator.
			// ContentExcerpt was not set on this entry, so Context renders as
			// "(none)" per the empty-handling convention from #315.
			// Shell renders once with its [LF][LF] preserved as single blank
			// lines; the SPELLING section ("Errors:") and its bullet follow.
			var nl = Environment.NewLine;
			Assert.Contains($"Page:{nl}https://example.test/alpha/page-one.html{nl}{nl}Source: local{nl}{nl}Module: ModuleAlpha", content);
			Assert.Contains($"Errors:{nl}", content);
			Assert.Contains("* foo [p[#text]]", content);
			Assert.Contains("Context: (none)", content);
			Assert.Contains(new string('-', 60), content);
		}

		// ── Scenario 12: Non-empty SpecialInfo renders cleanly ──────────────

		[Fact]
		public void WriteTicketText_NonEmptySpecialInfo_RendersWithItsOwnSeparator()
		{
			// SpecialInfo labels typically include their own [LF][LF] trailer to
			// produce a blank line after the label. This pattern relies on the
			// renderer NOT collapsing consecutive [LF] tokens.
			WriteCsv(
				"Path;Source;Module;Status",
				"/cms/path/alpha/page-one;local;ModuleAlpha;Locked-Pending"
			);

			var config = BuildConfig(
				url: "https://example.test",
				stripPrefix: ""
			);
			config.TicketGeneration.SpecialInfoMappings.Add(new SpecialInfoMapping
			{
				Column = "Status",
				Pattern = "Locked*",
				Label = "(locked)"
			});
			// SpecialInfo renders inline in the shell (#462). Place it on its own
			// shell line here so the test isolates SpecialInfo rendering.
			config.TicketGeneration.TicketShellTemplate =
				"Page:[LF]{Url}[LF][LF]{SpecialInfo}";
			config.TicketGeneration.TicketIssueTypes =
				[new TicketIssueTypeEntry { Type = "SPELLING", Label = "Spelling" }];
			config.TicketGeneration.TicketSectionIntros =
				[new TicketSectionIntro { Type = "SPELLING", Text = "Errors:" }];

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);
			var entries = new List<IssueTracking.IssueRecord>
			{
				new() { Type = "SPELLING", Word = "foo", Url = "https://example.test/alpha/page-one.html", Status = "pending", DateFound = "2026-05-14", Language = "en", SourceLabel = "p[#text]" }
			};

			var outputPath = Path.Combine(_tempDir, "ticket.log");
			TicketRenderer.WriteTicketText(outputPath, entries, config.TicketGeneration, config.CmsContentList, lookup);

			var content = File.ReadAllText(outputPath);

			// "(locked)" label followed by its own blank line, then "Errors:".
			// Under #315 the error block following "Errors:" opens with a dash
			// separator, the word line, the Context line ("(none)" when the
			// entry has no excerpt), and a closing dash.
			// "(locked)" renders inline in the shell; the SPELLING section
			// ("Errors:") and bullet follow as separate sections.
			Assert.Contains("(locked)", content);
			Assert.Contains("Errors:", content);
			Assert.Contains("* foo [p[#text]]", content);
			Assert.Contains("Context: (none)", content);
		}
		// ── Scenario 13: Diagnostic logs file presence + headers ────────────

		[Fact]
		public void LogMetadataLookupDiagnostics_ValidConfig_LogsHeaderAndSample()
		{
			WriteCsv(
				"preamble row 1",
				"preamble row 2",
				"Path;Source;Module",                                  // header
				"/cms/path/alpha/page-one;local;ModuleAlpha"           // first data row
			);

			var config = BuildConfig(url: "https://example.test", stripPrefix: "");
			config.CmsContentList!.SkipRows = 2;
			// The startup preview renders the shell + spelling section intro (#462).
			config.TicketGeneration.TicketShellTemplate =
				"Page: {Url}[LF]Source: {Location}[LF]Module: {Package}";
			config.TicketGeneration.TicketIssueTypes =
				[new TicketIssueTypeEntry { Type = "SPELLING", Label = "Spelling" }];
			config.TicketGeneration.TicketSectionIntros =
				[new TicketSectionIntro { Type = "SPELLING", Text = "Errors:" }];

			SpellMetadataLookup.LogMetadataLookupDiagnostics(config);

			var log = ReadLog();
			// File presence reported.
			Assert.Contains("File present: yes", log);
			// Header captured correctly.
			Assert.Contains("Path | Source | Module", log);
			// Sample ticket includes derived URL — last segment is "sample"
			// (synthetic-by-design) but the rest of the path is preserved so
			// URL derivation is verifiable.
			Assert.Contains("https://example.test/alpha/sample.html", log);
			// Sample ticket shows populated Location and Package.
			Assert.Contains("Source: local", log);
			Assert.Contains("Module: ModuleAlpha", log);
		}

		// ── Scenario 14: Missing CSV file warns ─────────────────────────────

		[Fact]
		public void LogMetadataLookupDiagnostics_MissingFile_Warns()
		{
			// Don't write the CSV — point at a nonexistent path.
			var config = BuildConfig(url: "https://example.test", stripPrefix: "");
			config.CmsContentList!.Path = Path.Combine(_tempDir, "does-not-exist.csv");

			SpellMetadataLookup.LogMetadataLookupDiagnostics(config);

			var log = ReadLog();
			Assert.Contains("[WARNING]", log);
			Assert.Contains("file not found", log);
		}

		// ── Scenario 15: Empty CmsContentList treated as disabled ───────────

		[Fact]
		public void LogMetadataLookupDiagnostics_EmptyContentListCsv_DisablesGracefully()
		{
			var config = BuildConfig(url: "https://example.test", stripPrefix: "");
			config.CmsContentList!.Path = "";

			SpellMetadataLookup.LogMetadataLookupDiagnostics(config);

			var log = ReadLog();
			// Informational only — no warning.
			Assert.Contains("disabled", log);
			Assert.DoesNotContain("[WARNING] Ticket metadata lookup", log);
		}

		// ── Scenario 16: Configured column missing in header warns ──────────

		[Fact]
		public void LogMetadataLookupDiagnostics_ConfiguredColumnNotInHeader_Warns()
		{
			// This is the failure mode that hit fs281a — SkipRows off by one
			// causes the captured "header" to actually be data values, so
			// configured column names won't match.
			WriteCsv(
				"preamble row 1",
				"Path;Source;Module",                                  // would-be header at row 2
				"some-value;local;ModuleAlpha"                         // captured AS header due to off-by-one
			);

			var config = BuildConfig(url: "https://example.test", stripPrefix: "");
			config.CmsContentList!.SkipRows = 2;  // off by one — skips both preamble AND real header
			config.TicketGeneration.PackageColumn = "Module";          // won't be in captured header

			SpellMetadataLookup.LogMetadataLookupDiagnostics(config);

			var log = ReadLog();
			Assert.Contains("[WARNING]", log);
			// Warning identifies the missing column by name.
			Assert.Contains("PackageColumn", log);
			Assert.Contains("Module", log);
		}

		// ── Scenario 17: Empty data area produces no sample ─────────────────

		[Fact]
		public void LogMetadataLookupDiagnostics_NoDataRows_InformsButDoesNotWarn()
		{
			// Header only, no data rows below it.
			WriteCsv(
				"Path;Source;Module"
			);

			var config = BuildConfig(url: "https://example.test", stripPrefix: "");
			config.CmsContentList!.SkipRows = 0;

			SpellMetadataLookup.LogMetadataLookupDiagnostics(config);

			var log = ReadLog();
			Assert.Contains("Header captured", log);
			Assert.Contains("No data rows found", log);
		}

		// ── #315: Context line / dash separators / sanitization / empty ─────

		[Fact]
		public void WriteTicketText_ContentExcerptPopulated_RendersContextLine()
		{
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);
			var config = BuildConfig(url: "https://example.test", stripPrefix: "");
			config.TicketGeneration.TicketShellTemplate = "{Url}";
			config.TicketGeneration.TicketIssueTypes = [new TicketIssueTypeEntry { Type = "SPELLING", Label = "Spelling" }];
			config.TicketGeneration.TicketSectionIntros = [new TicketSectionIntro { Type = "SPELLING", Text = "Errors:" }];

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);
			var entries = new List<IssueTracking.IssueRecord>
			{
				new() { Type = "SPELLING", Word = "Fehlr", Url = "https://example.test/alpha/page-one.html", Status = "pending", DateFound = "2026-05-18", Language = "de", SourceLabel = "p[#text]", Excerpt = "ein kurzer Satz mit Fehlr darin" }
			};

			var outputPath = Path.Combine(_tempDir, "ticket.log");
			TicketRenderer.WriteTicketText(outputPath, entries, config.TicketGeneration, config.CmsContentList, lookup);

			var text = File.ReadAllText(outputPath);
			Assert.Contains("* Fehlr [p[#text]]", text);
			Assert.Contains("Context: ein kurzer Satz mit Fehlr darin", text);
			Assert.Contains(new string('-', 60), text);
		}

		[Fact]
		public void WriteTicketText_EmptyContentExcerpt_RendersContextNone()
		{
			// Empty ContentExcerpt → "Context: (none)" per the #315 convention.
			// Surfaces detection-time bugs where the excerpt failed to be
			// captured, rather than silently omitting the line.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);
			var config = BuildConfig(url: "https://example.test", stripPrefix: "");
			config.TicketGeneration.TicketShellTemplate = "{Url}";
			config.TicketGeneration.TicketIssueTypes = [new TicketIssueTypeEntry { Type = "SPELLING", Label = "Spelling" }];
			config.TicketGeneration.TicketSectionIntros = [new TicketSectionIntro { Type = "SPELLING", Text = "Errors:" }];

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);
			var entries = new List<IssueTracking.IssueRecord>
			{
				new() { Type = "SPELLING", Word = "Fehlr", Url = "https://example.test/alpha/page-one.html", Status = "pending", DateFound = "2026-05-18", Language = "de", SourceLabel = "p[#text]", Excerpt = "" }
			};

			var outputPath = Path.Combine(_tempDir, "ticket.log");
			TicketRenderer.WriteTicketText(outputPath, entries, config.TicketGeneration, config.CmsContentList, lookup);

			var text = File.ReadAllText(outputPath);
			Assert.Contains("Context: (none)", text);
		}

		[Fact]
		public void WriteTicketText_MultipleErrors_DashSeparatesEachBlock()
		{
			// Two errors on the same page: dash separator opens the first
			// block, sits between the two errors, and closes the last. The
			// separator MUST appear between consecutive errors to prevent
			// one error's excerpt from visually merging into the next
			// error's word — the failure mode that motivated #315's dash
			// design choice.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);
			var config = BuildConfig(url: "https://example.test", stripPrefix: "");
			config.TicketGeneration.TicketShellTemplate = "{Url}";
			config.TicketGeneration.TicketIssueTypes = [new TicketIssueTypeEntry { Type = "SPELLING", Label = "Spelling" }];
			config.TicketGeneration.TicketSectionIntros = [
				new TicketSectionIntro { Type = "SPELLING", Text = "Errors:" },
				new TicketSectionIntro { Type = "SPELLING_PLURAL", Text = "Errors:" } ];

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);
			var url = "https://example.test/alpha/page-one.html";
			var entries = new List<IssueTracking.IssueRecord>
			{
				new() { Type = "SPELLING", Word = "Fehlr", Url = url, Status = "pending", DateFound = "2026-05-18", Language = "de", SourceLabel = "p[#text]", Excerpt = "first excerpt" },
				new() { Type = "SPELLING", Word = "Andror", Url = url, Status = "pending", DateFound = "2026-05-18", Language = "de", SourceLabel = "h2[#text]", Excerpt = "second excerpt" },
			};

			var outputPath = Path.Combine(_tempDir, "ticket.log");
			TicketRenderer.WriteTicketText(outputPath, entries, config.TicketGeneration, config.CmsContentList, lookup);

			var text = File.ReadAllText(outputPath);
			var dash = new string('-', 60);
			// Three dash separators expected: opening, between, closing.
			var dashCount = System.Text.RegularExpressions.Regex.Matches(text, dash).Count;
			Assert.Equal(3, dashCount);

			// Both errors and their excerpts present.
			Assert.Contains("* Fehlr [p[#text]]", text);
			Assert.Contains("Context: first excerpt", text);
			Assert.Contains("* Andror [h2[#text]]", text);
			Assert.Contains("Context: second excerpt", text);
		}

		[Fact]
		public void WriteTicketText_ExcerptWithTabAndLineBreak_SanitizedToSpaces()
		{
			// CMS-pasted content sometimes carries TAB or embedded line
			// breaks inside text nodes. Routed through
			// IssueLogWriter.SanitizeField, these become single spaces so
			// the Context: line stays on one physical row and TABs don't
			// jump to tab stops in the ticket system's rendering.
			WriteCsv(
				"Path;Source;Module",
				"/cms/path/alpha/page-one;local;ModuleAlpha"
			);
			var config = BuildConfig(url: "https://example.test", stripPrefix: "");
			config.TicketGeneration.TicketShellTemplate = "{Url}";
			config.TicketGeneration.TicketIssueTypes = [new TicketIssueTypeEntry { Type = "SPELLING", Label = "Spelling" }];
			config.TicketGeneration.TicketSectionIntros = [new TicketSectionIntro { Type = "SPELLING", Text = "Errors:" }];

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config, silent: true);
			var entries = new List<IssueTracking.IssueRecord>
			{
				new() { Type = "SPELLING", Word = "Fehlr", Url = "https://example.test/alpha/page-one.html", Status = "pending", DateFound = "2026-05-18", Language = "de", SourceLabel = "p[#text]", Excerpt = "before\ttab\rcr\nlf\u2028ls\u2029ps after" }
			};

			var outputPath = Path.Combine(_tempDir, "ticket.log");
			TicketRenderer.WriteTicketText(outputPath, entries, config.TicketGeneration, config.CmsContentList, lookup);

			var text = File.ReadAllText(outputPath);
			// All five whitespace hazards collapse to single spaces.
			Assert.Contains("Context: before tab cr lf ls ps after", text);
			// The original characters must not survive into the rendered text.
			Assert.DoesNotContain("\t", text);
			Assert.DoesNotContain("\u2028", text);
			Assert.DoesNotContain("\u2029", text);
		}

		// ── BuildMetadataLookup resolver-creation guards ────────────────────
		// Relocated from the retired SpellTrackingTests when SpellTracking was
		// deleted; these cover the resolver itself (no CSV / missing file /
		// valid match / unknown URL), complementing the field-extraction cases above.

		[Fact]
		public void BuildMetadataLookup_NoCsvConfigured_ReturnsEmptyResolver()
		{
			var config = new Config { CmsContentList = new CmsContentListConfig { Path = "" } };
			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			var result = lookup("https://example.com/page");
			Assert.Equal("", result.Location);
			Assert.Equal("", result.Package);
		}

		[Fact]
		public void BuildMetadataLookup_CsvNotFound_ReturnsEmptyResolver()
		{
			var config = new Config
			{
				CmsContentList = new CmsContentListConfig
				{
					Path = Path.Combine(_tempDir, "missing.csv")
				}
			};
			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			var result = lookup("https://example.com/page");
			Assert.Equal("", result.Location);
		}

		[Fact]
		public void BuildMetadataLookup_ValidCsv_ReturnsLocationAndModule()
		{
			var csvPath = Path.Combine(_tempDir, "content.csv");
			File.WriteAllLines(csvPath, [
				"Path;Location;Module",
				"https://example.com/de/home/page;RegionA;ModuleB",
			]);

			var config = new Config
			{
				CmsContentList = new CmsContentListConfig
				{
					Path = csvPath,
					ColumnDelimiter = ";",
				},
				TicketGeneration = new TicketGenerationConfig
				{
					PathColumn = "Path",
					UrlSourceColumn = "Location",
					PackageColumn = "Module",
				}
			};

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			var result = lookup("https://example.com/de/home/page");
			Assert.Equal("RegionA", result.Location);
			Assert.Equal("ModuleB", result.Package);
		}

		[Fact]
		public void BuildMetadataLookup_UnknownUrl_ReturnsEmpty()
		{
			var csvPath = Path.Combine(_tempDir, "content.csv");
			File.WriteAllLines(csvPath, [
				"Path;Location;Module",
				"https://example.com/de/home/page;RegionA;ModuleB",
			]);

			var config = new Config
			{
				CmsContentList = new CmsContentListConfig
				{
					Path = csvPath,
					ColumnDelimiter = ";",
				},
				TicketGeneration = new TicketGenerationConfig
				{
					PathColumn = "Path",
					UrlSourceColumn = "Location",
					PackageColumn = "Module",
				}
			};

			var lookup = SpellMetadataLookup.BuildMetadataLookup(config);
			var result = lookup("https://example.com/unknown/page");
			Assert.Equal("", result.Location);
			Assert.Equal("", result.Package);
		}
	}
}
