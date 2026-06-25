using Xunit;

using Crawler.Lexicon;
namespace Crawler.Tests.Lexicon
{
	/// <summary>
	/// Synthetic-fixture tests for Audit's usage-driven and wrapper paths,
	/// complementing DictionaryCheckAnalysisTests (which covers the file-scan core
	/// AnalyseDictionary):
	///
	///   • AnalyseFromUsage   — orphan/redundancy derived from a used-words set
	///                          (missing file, all-pinned, orphan, used, pinned-exempt,
	///                          redundant, orphan-skipped-in-redundancy, slash-filtered).
	///   • AnalyseDictionaries — the user+site wrapper and its combined log.
	///   • WriteAnalysisReport — the pre-computed report path and "in BOTH" sections.
	///   • CountDictionaryWords — entry counting incl. the Hunspell count-line.
	///   • RunRemovalTriage     — the no-candidates early return (the interactive
	///                            R/P/S/Q loop needs console-key input — left uncovered).
	///
	/// All tokens are generic ASCII with no relation to any real site. Bundles use
	/// SharedSite (Bundle.Check hits the shared sets before the null System
	/// WordList), mirroring the sibling test's BundleWith.
	/// </summary>
	[Collection("Logger")]
	public class AuditUsageTests : IDisposable
	{
		private readonly string _tempDir;
		private readonly string _corpusDir;

		public AuditUsageTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"dictusage-{Guid.NewGuid():N}");
			_corpusDir = Path.Combine(_tempDir, "corpus");
			Directory.CreateDirectory(_corpusDir);
			Logger.Initialize(Path.Combine(_tempDir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── helpers ─────────────────────────────────────────────────────────

		private string WriteDictionary(params string[] lines)
		{
			var path = Path.Combine(_tempDir, $"dict-{Guid.NewGuid():N}.dic");
			File.WriteAllLines(path, lines);
			return path;
		}

		private void WriteCorpus(string name, string content) =>
			File.WriteAllText(Path.Combine(_corpusDir, name), content);

		private static Bundle BundleWith(params string[] systemAcceptedWords)
		{
			var bundle = new Bundle();
			foreach (var w in systemAcceptedWords)
			{
				bundle.SharedSite.Add(w);
			}
			return bundle;
		}

		private static HashSet<string> Set(params string[] words) =>
			new(words, StringComparer.Ordinal);

		// ── AnalyseFromUsage ────────────────────────────────────────────────

		[Fact]
		public void AnalyseFromUsage_FileMissing_ReturnsEmpty()
		{
			var r = Audit.AnalyseFromUsage(
				Path.Combine(_tempDir, "nope.dic"), Array.Empty<string>(), null, null);

			Assert.Empty(r.Orphaned);
			Assert.Empty(r.Redundant);
		}

		[Fact]
		public void AnalyseFromUsage_OnlyPinnedWords_ReturnsEmpty()
		{
			var dict = WriteDictionary("!pinned");
			var r = Audit.AnalyseFromUsage(dict, Array.Empty<string>(), null, null);

			Assert.Empty(r.Orphaned);
			Assert.Empty(r.Redundant);
		}

		[Fact]
		public void AnalyseFromUsage_UnusedWord_FlaggedOrphan()
		{
			var dict = WriteDictionary("ghost");
			var r = Audit.AnalyseFromUsage(dict, Array.Empty<string>(), null, null);

			Assert.Contains("ghost", r.Orphaned);
		}

		[Fact]
		public void AnalyseFromUsage_UsedWord_NotOrphan()
		{
			var dict = WriteDictionary("seen");
			var r = Audit.AnalyseFromUsage(dict, new[] { "seen" }, null, null);

			Assert.DoesNotContain("seen", r.Orphaned);
		}

		[Fact]
		public void AnalyseFromUsage_UsedCaseInsensitively_NotOrphan()
		{
			var dict = WriteDictionary("Seen");
			var r = Audit.AnalyseFromUsage(dict, new[] { "seen" }, null, null);

			Assert.DoesNotContain("Seen", r.Orphaned); // membership is case-insensitive
		}

		[Fact]
		public void AnalyseFromUsage_PinnedWord_ExemptFromOrphanCheck()
		{
			var dict = WriteDictionary("!pinned", "ghost");
			var r = Audit.AnalyseFromUsage(dict, Array.Empty<string>(), null, null);

			Assert.Contains("ghost", r.Orphaned);
			Assert.DoesNotContain("pinned", r.Orphaned);
		}

		[Fact]
		public void AnalyseFromUsage_PrefixStrippedRemainderAccepted_FlaggedRedundant()
		{
			var dict = WriteDictionary("PFX-widget");
			var r = Audit.AnalyseFromUsage(
				dict, new[] { "PFX-widget" }, new[] { "PFX" }, new[] { BundleWith("widget") });

			Assert.Contains("PFX-widget", r.Redundant);
			Assert.DoesNotContain("PFX-widget", r.Orphaned);
		}

		[Fact]
		public void AnalyseFromUsage_OrphanWord_SkippedInRedundancyCheck()
		{
			var dict = WriteDictionary("PFX-ghost");
			var r = Audit.AnalyseFromUsage(
				dict, Array.Empty<string>(), new[] { "PFX" }, new[] { BundleWith("ghost") });

			Assert.Contains("PFX-ghost", r.Orphaned);
			Assert.DoesNotContain("PFX-ghost", r.Redundant);
		}

		[Fact]
		public void AnalyseFromUsage_SlashLine_Ignored()
		{
			var dict = WriteDictionary("with/slash", "ghost");
			var r = Audit.AnalyseFromUsage(dict, Array.Empty<string>(), null, null);

			Assert.Contains("ghost", r.Orphaned);
			Assert.DoesNotContain("with/slash", r.Orphaned);
		}

		// ── CountDictionaryWords ────────────────────────────────────────────

		[Fact]
		public void CountDictionaryWords_MissingOrEmptyPath_ReturnsZero()
		{
			Assert.Equal(0, Audit.CountDictionaryWords(Path.Combine(_tempDir, "nope.dic")));
			Assert.Equal(0, Audit.CountDictionaryWords(string.Empty));
		}

		[Fact]
		public void CountDictionaryWords_CountsNonBlankLines()
		{
			var dict = WriteDictionary("alpha", "beta", "gamma");
			Assert.Equal(3, Audit.CountDictionaryWords(dict));
		}

		[Fact]
		public void CountDictionaryWords_ExcludesLeadingHunspellCountLine()
		{
			var dict = WriteDictionary("2", "foo", "bar"); // line 1 is a bare integer
			Assert.Equal(2, Audit.CountDictionaryWords(dict));
		}

		// ── AnalyseDictionaries (wrapper + combined log) ────────────────────

		[Fact]
		public void AnalyseDictionaries_BothFilesAnalysed_WritesLog()
		{
			var userDict = WriteDictionary("uorphan");
			var siteDict = WriteDictionary("sorphan");
			WriteCorpus("page.txt", "unrelated filler content");
			var outLog = Path.Combine(_tempDir, "log15.txt");

			var (user, site) = Audit.AnalyseDictionaries(userDict, siteDict, _corpusDir, outLog);

			Assert.Contains("uorphan", user.Orphaned);
			Assert.Contains("sorphan", site.Orphaned);
			Assert.True(File.Exists(outLog));
			var log = File.ReadAllText(outLog);
			Assert.Contains("uorphan", log);
			Assert.Contains("sorphan", log);
		}

		[Fact]
		public void AnalyseDictionaries_UserFileMissing_UserAnalysisEmpty()
		{
			var siteDict = WriteDictionary("sorphan");
			WriteCorpus("page.txt", "filler");
			var outLog = Path.Combine(_tempDir, "log15b.txt");

			var (user, site) = Audit.AnalyseDictionaries(
				Path.Combine(_tempDir, "no-user.dic"), siteDict, _corpusDir, outLog);

			Assert.Empty(user.Orphaned);
			Assert.Contains("sorphan", site.Orphaned);
		}

		// ── WriteAnalysisReport (in-both intersection) ──────────────────────

		[Fact]
		public void WriteAnalysisReport_SharedOrphan_EmitsBothSection()
		{
			var user = new Audit.Analysis(Set("shared", "uonly"), Set());
			var site = new Audit.Analysis(Set("shared", "sonly"), Set());
			var outLog = Path.Combine(_tempDir, "report.txt");

			Audit.WriteAnalysisReport(outLog, "user.dic", user, "site.dic", site);

			var log = File.ReadAllText(outLog);
			Assert.Contains("BOTH", log);   // the shared-entry section header
			Assert.Contains("shared", log);
			Assert.Contains("uonly", log);  // per-dict sections still list their own
			Assert.Contains("sonly", log);
		}

		// ── RunRemovalTriage (no-candidates early return) ───────────────────

		[Fact]
		public void RunRemovalTriage_NoCandidates_ReturnsEmptyWithoutPrompting()
		{
			var dict = WriteDictionary("alpha", "beta");
			var analysis = new Audit.Analysis(Set(), Set());

			var (toRemove, toPin) = Audit.RunRemovalTriage(dict, analysis);

			Assert.Empty(toRemove);
			Assert.Empty(toPin);
		}
	}
}
