using System.Collections.ObjectModel;
using System.Text;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Batch A — core orchestration of NewSpellEngineRunner.Run plus the cheap
	/// branch toggles (no JavaScript path; that is Batch B). Drives the runner
	/// end-to-end over temp HTML files with in-memory dictionary bundles, lambda
	/// language/url resolvers and a near-default engine config, then asserts on
	/// the returned WordTickets.
	///
	/// All collaborators (DomTraverser, RunChecker, BoilerplateResolver,
	/// FindingAggregator, ParallelStoreWriter, …) are real and separately tested;
	/// these tests verify the runner wires them together correctly.
	///
	/// Misspelling fixtures use tokens that RunCheckerTests already proves get
	/// flagged in prose ("Fehla", "Bidl"); an empty bundle accepts nothing, so
	/// every identified word becomes a finding. SYNTHETIC fixtures throughout.
	///
	/// In the Logger collection: Run logs progress via the static Logger.
	/// </summary>
	[Collection("Logger")]
	public class SpellCheckNewSpellEngineRunnerTests : IDisposable
	{
		private readonly string _dir;
		private readonly string _uniqueView;
		private readonly string _sourcesView;
		private readonly string _locatedView;
		private readonly string _diagView;
		private readonly string _suppressView;

		public SpellCheckNewSpellEngineRunnerTests()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"NewSpellEngine_{Guid.NewGuid():N}");
			Directory.CreateDirectory(_dir);
			Logger.Initialize(Path.Combine(_dir, "test.log"), silent: true);

			_uniqueView = Path.Combine(_dir, "11_unique.log");
			_sourcesView = Path.Combine(_dir, "12_sources.log");
			_locatedView = Path.Combine(_dir, "13_located.log");
			_diagView = Path.Combine(_dir, "14_diag.log");
			_suppressView = Path.Combine(_dir, "27_suppress.log");
		}

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── helpers ───────────────────────────────────────────────────────────

		private void WriteHtml(string filename, string html) =>
			File.WriteAllText(Path.Combine(_dir, filename), html,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

		private static NewSpellEngineRunner.FileInput File_(string filename) =>
			new() { Filename = filename };

		// In-memory bundle: supplied words accepted (SharedUser), System null so
		// everything else is a miss.
		private static DictionaryBundle Bundle(params string[] acceptedWords)
		{
			var bundle = new DictionaryBundle();
			foreach (var w in acceptedWords)
			{
				bundle.SharedUser.Add(w);
			}
			return bundle;
		}

		private static Func<string, HtmlDocument, string> Lang(string code) => (_, _) => code;

		private IReadOnlyList<WordTicket> RunEngine(
			IEnumerable<NewSpellEngineRunner.FileInput> files,
			Dictionary<string, DictionaryBundle> bundles,
			Func<string, HtmlDocument, string> resolveLanguage,
			SpellCheckEngineConfig config,
			int maxDop = 1)
			=> RunRaw(files, bundles, resolveLanguage, config, maxDop, f => "https://example.test/" + f);

		// Lower-level entry exposing the knobs the edge-case tests vary: a url
		// resolver that can return null, a bundle dictionary that may not be a
		// concrete Dictionary, and a file sequence that may not be an IReadOnlyList.
		private IReadOnlyList<WordTicket> RunRaw(
			IEnumerable<NewSpellEngineRunner.FileInput> files,
			IReadOnlyDictionary<string, DictionaryBundle> bundles,
			Func<string, HtmlDocument, string> resolveLanguage,
			SpellCheckEngineConfig config,
			int maxDop,
			Func<string, string> lookUpUrlForFile)
			=> NewSpellEngineRunner.Run(
				files,
				_dir,
				_uniqueView,
				_sourcesView,
				_locatedView,
				_diagView,
				_suppressView,
				config,
				bundles,
				resolveLanguage,
				Array.Empty<string>(),
				Array.Empty<string>(),
				lookUpUrlForFile,
				maxDop,
				null);

		// ── core path ─────────────────────────────────────────────────────────

		[Fact]
		public void Run_PageWithMisspelling_ReturnsTicketAndWritesViews()
		{
			WriteHtml("p1.html", "<html><body><p>Fehla</p></body></html>");

			var tickets = RunEngine(
				new[] { File_("p1.html") },
				new() { ["en"] = Bundle() },
				Lang("en"),
				new SpellCheckEngineConfig());

			var ticket = Assert.Single(tickets);
			Assert.Equal("Fehla", ticket.Word);
			Assert.Equal("https://example.test/p1.html", ticket.Url);

			// The four standard views are written for a run with findings.
			Assert.True(File.Exists(_uniqueView));
			Assert.True(File.Exists(_sourcesView));
			Assert.True(File.Exists(_locatedView));
			Assert.True(File.Exists(_diagView));
		}

		[Fact]
		public void Run_CleanPage_NoTickets()
		{
			// Bundle accepts the only word → no findings → empty views written
			// (exercises WriteView's empty-content path).
			WriteHtml("p1.html", "<html><body><p>hello</p></body></html>");

			var tickets = RunEngine(
				new[] { File_("p1.html") },
				new() { ["en"] = Bundle("hello") },
				Lang("en"),
				new SpellCheckEngineConfig());

			Assert.Empty(tickets);
		}

		// ── skip branches ───────────────────────────────────────────────────

		[Fact]
		public void Run_UnreadableFile_SkippedNoThrow()
		{
			// FileInput names a file that is not on disk: the read throws and the
			// file is counted unreadable and skipped — no exception escapes Run.
			var tickets = RunEngine(
				new[] { File_("missing.html") },
				new() { ["en"] = Bundle() },
				Lang("en"),
				new SpellCheckEngineConfig());

			Assert.Empty(tickets);
		}

		[Fact]
		public void Run_LanguageWithoutBundle_FileSkipped()
		{
			WriteHtml("p1.html", "<html><body><p>Fehla</p></body></html>");

			// Resolver names "xx", but no bundle exists for it → page skipped.
			var tickets = RunEngine(
				new[] { File_("p1.html") },
				new() { ["en"] = Bundle() },
				Lang("xx"),
				new SpellCheckEngineConfig());

			Assert.Empty(tickets);
		}

		// ── parallelism & pruning toggles ───────────────────────────────────

		[Fact]
		public void Run_MaxDegreeOfParallelismZero_UsesProcessorCount()
		{
			WriteHtml("p1.html", "<html><body><p>Fehla</p></body></html>");

			// maxDop = 0 takes the Environment.ProcessorCount branch; the page
			// must still be processed.
			var tickets = RunEngine(
				new[] { File_("p1.html") },
				new() { ["en"] = Bundle() },
				Lang("en"),
				new SpellCheckEngineConfig(),
				maxDop: 0);

			var ticket = Assert.Single(tickets);
			Assert.Equal("Fehla", ticket.Word);
		}

		[Fact]
		public void Run_GlobalXpathToIgnore_PrunesMatchedSubtree()
		{
			WriteHtml("p1.html",
				"<html><body><div id=\"tracker\"><p>Fehla</p></div><p>Bidl</p></body></html>");

			var config = new SpellCheckEngineConfig();
			config.GlobalXpathToIgnore.Add("//div[@id='tracker']"); // globalIgnore non-null

			var tickets = RunEngine(
				new[] { File_("p1.html") },
				new() { ["en"] = Bundle() },
				Lang("en"),
				config);

			// Misspelling inside the ignored subtree is pruned; the one outside stays.
			Assert.DoesNotContain(tickets, t => t.Word == "Fehla");
			Assert.Contains(tickets, t => t.Word == "Bidl");
		}

		[Fact]
		public void Run_MultipleFiles_TicketsInDocumentOrder()
		{
			WriteHtml("a.html", "<html><body><p>Fehla</p></body></html>");
			WriteHtml("b.html", "<html><body><p>Bidl</p></body></html>");

			// Two files, parallel: results are slotted by input position, so the
			// flattened order follows the input order regardless of thread timing.
			var tickets = RunEngine(
				new[] { File_("a.html"), File_("b.html") },
				new() { ["en"] = Bundle() },
				Lang("en"),
				new SpellCheckEngineConfig(),
				maxDop: 2);

			Assert.Equal(2, tickets.Count);
			Assert.Equal("Fehla", tickets[0].Word);
			Assert.Equal("Bidl", tickets[1].Word);
		}

		// ── Batch B: JavaScript / Slot-27 + config overrides ────────────────

		[Fact]
		public void Run_JsEnabledWithScriptFinding_WritesSuppressionSuggestions()
		{
			// Inline <script> literal "hallo wolrd": "hallo" is accepted, "wolrd"
			// is the lone miss (1-of-2 ratio — the proven flaggable shape). With
			// JavaScript checking on, the Script finding drives the slot-27
			// suppression-suggestions output.
			WriteHtml("p1.html",
				"<html><body><script>var x = 'hallo wolrd';</script></body></html>");

			var config = new SpellCheckEngineConfig();
			config.SpellCheckJavaScript.Enabled = true;

			var tickets = RunEngine(
				new[] { File_("p1.html") },
				new() { ["en"] = Bundle("hallo") },
				Lang("en"),
				config);

			Assert.Contains(tickets, t => t.Word == "wolrd");
			Assert.True(File.Exists(_suppressView)); // slot-27 file written when JS on
		}

		[Fact]
		public void Run_ConfigOverrideLists_NonNull_ExercisedOverDefaults()
		{
			// The three lists that default to null take their "?? default" / "is {}"
			// fallback side in the other tests; declaring them non-null takes the
			// declared side instead.
			WriteHtml("p1.html",
				"<html><head><meta name=\"description\" content=\"Fehla\"></head><body><p>Bidl</p></body></html>");

			var config = new SpellCheckEngineConfig
			{
				GlobalNonProseHtmlAttributesThatWillBeIgnored = new List<string> { "data-x" },
				GlobalBooleanHtmlAttributesThatWillBeIgnored = new List<string> { "hidden" },
				MetaContentNamesToSpellCheck = new List<string> { "description" },
			};

			var tickets = RunEngine(
				new[] { File_("p1.html") },
				new() { ["en"] = Bundle() },
				Lang("en"),
				config);

			Assert.Contains(tickets, t => t.Word == "Bidl");
		}

		[Fact]
		public void Run_JsEnabledProseOnly_NoScriptInputs_ValidScriptFallback()
		{
			// JavaScript on but the page has only a prose finding (no Script-source
			// findings) → the per-file scriptInputs set stays empty. ScriptFallbackDictionary
			// names a loaded bundle ("en"), exercising the fallback guard's valid-key side.
			WriteHtml("p1.html", "<html><body><p>Fehla</p></body></html>");

			var config = new SpellCheckEngineConfig();
			config.SpellCheckJavaScript.Enabled = true;
			config.SpellCheckJavaScript.ScriptFallbackDictionary = "en";

			var tickets = RunEngine(
				new[] { File_("p1.html") },
				new() { ["en"] = Bundle() },
				Lang("en"),
				config);

			var ticket = Assert.Single(tickets);
			Assert.Equal("Fehla", ticket.Word);
		}

		// ── defensive fallbacks (cheap as/?? right-sides) ───────────────────

		[Fact]
		public void Run_LookUpUrlReturnsNull_FallsBackToFilename()
		{
			WriteHtml("p1.html", "<html><body><p>Fehla</p></body></html>");

			// url resolver returns null → "?? file.Filename" supplies the url.
			var tickets = RunRaw(
				new[] { File_("p1.html") },
				new Dictionary<string, DictionaryBundle> { ["en"] = Bundle() },
				Lang("en"),
				new SpellCheckEngineConfig(),
				1,
				_ => null!);

			var ticket = Assert.Single(tickets);
			Assert.Equal("Fehla", ticket.Word);
			Assert.Equal("p1.html", ticket.Url);
		}

		[Fact]
		public void Run_BundlesNotConcreteDictionary_ClonedIntoConcrete()
		{
			WriteHtml("p1.html", "<html><body><p>Fehla</p></body></html>");

			// A read-only dictionary is IReadOnlyDictionary but not Dictionary, so the
			// "as Dictionary ?? new Dictionary(clone)" fallback materializes a copy.
			var bundles = new ReadOnlyDictionary<string, DictionaryBundle>(
				new Dictionary<string, DictionaryBundle> { ["en"] = Bundle() });

			var tickets = RunRaw(
				new[] { File_("p1.html") },
				bundles,
				Lang("en"),
				new SpellCheckEngineConfig(),
				1,
				f => "https://example.test/" + f);

			Assert.Equal("Fehla", Assert.Single(tickets).Word);
		}

		[Fact]
		public void Run_FilesNotReadOnlyList_MaterializedToList()
		{
			WriteHtml("p1.html", "<html><body><p>Fehla</p></body></html>");

			// A lazy Where(...) sequence is not an IReadOnlyList<FileInput>, so the
			// "as IReadOnlyList ?? files.ToList()" fallback materializes it.
			IEnumerable<NewSpellEngineRunner.FileInput> lazy =
				new[] { File_("p1.html") }.Where(_ => true);

			var tickets = RunRaw(
				lazy,
				new Dictionary<string, DictionaryBundle> { ["en"] = Bundle() },
				Lang("en"),
				new SpellCheckEngineConfig(),
				1,
				f => "https://example.test/" + f);

			Assert.Equal("Fehla", Assert.Single(tickets).Word);
		}
	}
}
