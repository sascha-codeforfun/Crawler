using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Synthetic-fixture tests for DictionaryCheck.AnalyseDictionary. These tests
	/// exercise the six behavioural branches in isolation against generated
	/// dictionary + corpus content:
	///
	///   1. Plain orphan        — word in dict, absent from every .txt → reported.
	///   2. Plain found         — word in dict, present in some .txt → not reported.
	///   3. Prefix-found        — word not directly present, but "prefix-word" is → not reported.
	///   4. Pinned (!) exemption — pinned word absent from corpus → NOT reported.
	///   5. Redundant           — prefix-stripped remainder accepted by a loaded bundle.
	///   6. Slash-filtered      — dictionary lines containing '/' are ignored.
	///
	/// All inputs are generic ASCII tokens with no relation to any real site.
	/// The synthetic system dictionary is constructed via DictionaryBundle's
	/// SharedSite/SharedUser HashSets — the WordList-backed System field is left
	/// null since DictionaryBundle.Check short-circuits to the shared sets first.
	/// </summary>
	[Collection("Logger")]
	public class DictionaryCheckAnalysisTests : IDisposable
	{
		private readonly string _tempDir;
		private readonly string _corpusDir;

		public DictionaryCheckAnalysisTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"dictcheck-{Guid.NewGuid():N}");
			_corpusDir = Path.Combine(_tempDir, "corpus");
			Directory.CreateDirectory(_corpusDir);
			Logger.Initialize(Path.Combine(_tempDir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		// ── Fixture helpers ─────────────────────────────────────────────────

		private string WriteDictionary(params string[] lines)
		{
			var path = Path.Combine(_tempDir, $"dict-{Guid.NewGuid():N}.dic");
			File.WriteAllLines(path, lines);
			return path;
		}

		private void WriteCorpusFile(string name, string content)
		{
			File.WriteAllText(Path.Combine(_corpusDir, name), content);
		}

		private static DictionaryBundle BundleWith(params string[] systemAcceptedWords)
		{
			// Use SharedSite — DictionaryBundle.Check returns true on SharedUser/SharedSite
			// hits before touching the System WordList, which we leave null.
			var bundle = new DictionaryBundle();
			foreach (var w in systemAcceptedWords)
			{
				bundle.SharedSite.Add(w);
			}

			return bundle;
		}

		// ── Scenario 1: Plain orphan ─────────────────────────────────────────

		[Fact]
		public void AnalyseDictionary_WordNotInCorpus_FlaggedAsOrphan()
		{
			var dict = WriteDictionary("alpha", "beta");
			WriteCorpusFile("page1.txt", "this text contains beta only");

			var result = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir, prefixesToStrip: null, loadedBundles: null);

			Assert.Contains("alpha", result.Orphaned);
			Assert.DoesNotContain("beta", result.Orphaned);
		}

		// ── Scenario 2: Plain found ──────────────────────────────────────────

		[Fact]
		public void AnalyseDictionary_WordPresentInCorpus_NotOrphan()
		{
			var dict = WriteDictionary("alpha");
			WriteCorpusFile("page1.txt", "alpha appears here");

			var result = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir, prefixesToStrip: null, loadedBundles: null);

			Assert.Empty(result.Orphaned);
		}

		[Fact]
		public void AnalyseDictionary_WordBoundaryMatching_RejectsSubstringHits()
		{
			// 'alpha' must NOT be considered found in 'alphabet' (\b word boundary).
			var dict = WriteDictionary("alpha");
			WriteCorpusFile("page1.txt", "the alphabet starts with a");

			var result = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir, prefixesToStrip: null, loadedBundles: null);

			Assert.Contains("alpha", result.Orphaned);
		}

		// ── Scenario 3: Prefix-found ─────────────────────────────────────────

		[Fact]
		public void AnalyseDictionary_PrefixedWordInCorpus_NotOrphan()
		{
			// 'widget' is not present standalone, but 'PFX-widget' is — fallback
			// scan should remove it from notFound.
			var dict = WriteDictionary("widget");
			WriteCorpusFile("page1.txt", "the PFX-widget arrived");

			var result = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir,
				prefixesToStrip: new[] { "PFX" },
				loadedBundles: null);

			Assert.DoesNotContain("widget", result.Orphaned);
		}

		[Fact]
		public void AnalyseDictionary_PrefixMatchIsCaseInsensitive()
		{
			// Current behaviour: text.Contains uses OrdinalIgnoreCase, so
			// 'pfx-widget' in corpus matches prefix 'PFX' in config.
			var dict = WriteDictionary("widget");
			WriteCorpusFile("page1.txt", "the pfx-widget arrived");

			var result = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir,
				prefixesToStrip: new[] { "PFX" },
				loadedBundles: null);

			Assert.DoesNotContain("widget", result.Orphaned);
		}

		// ── Scenario 4: Pinned (!) exemption ─────────────────────────────────

		[Fact]
		public void AnalyseDictionary_PinnedWordAbsent_NotReportedAsOrphan()
		{
			// '!seasonal' is pinned — even though it never appears in corpus,
			// it must not be flagged as orphan.
			var dict = WriteDictionary("!seasonal", "everyday");
			WriteCorpusFile("page1.txt", "everyday content only");

			var result = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir, prefixesToStrip: null, loadedBundles: null);

			// 'seasonal' (with ! stripped for display) must not be in the orphan set.
			Assert.DoesNotContain("seasonal", result.Orphaned);
			Assert.DoesNotContain("!seasonal", result.Orphaned);
			Assert.Empty(result.Orphaned);
		}

		[Fact]
		public void AnalyseDictionary_PinnedWordAlsoExempt_FromRedundancyCheck()
		{
			// '!PFX-widget' pinned + 'widget' loadable in bundle would normally
			// flag PFX-widget as redundant. Pinning suppresses this.
			var dict = WriteDictionary("!PFX-widget");
			var bundle = BundleWith("widget");
			WriteCorpusFile("page1.txt", "irrelevant content");

			var result = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir,
				prefixesToStrip: new[] { "PFX" },
				loadedBundles: new[] { bundle });

			Assert.Empty(result.Redundant);
		}

		// ── Scenario 5: Redundant ────────────────────────────────────────────

		[Fact]
		public void AnalyseDictionary_PrefixStrippedRemainderAcceptedBySystem_FlaggedRedundant()
		{
			// 'PFX-widget' in user dict; 'widget' is accepted by the loaded bundle.
			// The full token 'PFX-widget' is present in corpus so it is NOT an orphan,
			// but it IS redundant because the bundle would have accepted 'widget'.
			var dict = WriteDictionary("PFX-widget");
			var bundle = BundleWith("widget");
			WriteCorpusFile("page1.txt", "PFX-widget here");

			var result = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir,
				prefixesToStrip: new[] { "PFX" },
				loadedBundles: new[] { bundle });

			Assert.Contains("PFX-widget", result.Redundant);
			Assert.DoesNotContain("PFX-widget", result.Orphaned);
		}

		[Fact]
		public void AnalyseDictionary_OrphanWordsSkippedInRedundancyCheck()
		{
			// An orphan must not also be reported as redundant — the orphan check
			// runs first and orphans are skipped in the redundancy pass.
			var dict = WriteDictionary("PFX-ghost");
			var bundle = BundleWith("ghost");
			WriteCorpusFile("page1.txt", "no match here");

			var result = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir,
				prefixesToStrip: new[] { "PFX" },
				loadedBundles: new[] { bundle });

			Assert.Contains("PFX-ghost", result.Orphaned);
			Assert.DoesNotContain("PFX-ghost", result.Redundant);
		}

		// ── Scenario 6: Slash-filtered ───────────────────────────────────────

		[Fact]
		public void AnalyseDictionary_LinesContainingSlash_AreIgnored()
		{
			// Hunspell-style affix flags: 'word/AB' — DictionaryCheck filters these out
			// since they're not analysable as plain words.
			var dict = WriteDictionary("alpha", "beta/XY", "gamma");
			WriteCorpusFile("page1.txt", "only alpha here");

			var result = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir, prefixesToStrip: null, loadedBundles: null);

			// alpha: found — not orphan
			Assert.DoesNotContain("alpha", result.Orphaned);
			// gamma: missing → orphan
			Assert.Contains("gamma", result.Orphaned);
			// beta/XY: filtered → NOT in orphan set (filtered out entirely, not
			// reported under any name).
			Assert.DoesNotContain("beta/XY", result.Orphaned);
			Assert.DoesNotContain("beta", result.Orphaned);
		}

		// ── Edge cases ──────────────────────────────────────────────────────

		[Fact]
		public void AnalyseDictionary_EmptyDictionary_ReturnsEmptyResults()
		{
			var dict = WriteDictionary();
			WriteCorpusFile("page1.txt", "anything");

			var result = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir, prefixesToStrip: null, loadedBundles: null);

			Assert.Empty(result.Orphaned);
			Assert.Empty(result.Redundant);
		}

		[Fact]
		public void AnalyseDictionary_OnlyPinnedWords_NoOrphans()
		{
			var dict = WriteDictionary("!one", "!two", "!three");
			WriteCorpusFile("page1.txt", "no relevant content");

			var result = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir, prefixesToStrip: null, loadedBundles: null);

			Assert.Empty(result.Orphaned);
		}

		[Fact]
		public void AnalyseDictionary_DuplicateDictionaryEntries_DeduplicatedBeforeAnalysis()
		{
			// Trim+Distinct in the load step should collapse duplicates.
			var dict = WriteDictionary("alpha", "alpha", "  alpha  ", "beta");
			WriteCorpusFile("page1.txt", "alpha here");

			var result = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir, prefixesToStrip: null, loadedBundles: null);

			// alpha matched; beta orphan. Count must be exactly 1, not duplicated.
			Assert.Single(result.Orphaned);
			Assert.Contains("beta", result.Orphaned);
		}

		[Fact]
		public void AnalyseDictionary_RespectsExplicitDegreeOfParallelism()
		{
			// DOP=1 forces serial execution; result must match a parallel run.
			var dict = WriteDictionary("alpha", "beta", "gamma", "delta", "epsilon");
			for (int i = 0; i < 10; i++)
			{
				WriteCorpusFile($"page{i}.txt", $"content with beta and delta page {i}");
			}

			var serial = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir,
				prefixesToStrip: null, loadedBundles: null,
				degreeOfParallelism: 1);

			var parallel = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir,
				prefixesToStrip: null, loadedBundles: null,
				degreeOfParallelism: 4);

			Assert.Equal(serial.Orphaned, parallel.Orphaned, HashSet<string>.CreateSetComparer());
			Assert.Equal(serial.Redundant, parallel.Redundant, HashSet<string>.CreateSetComparer());

			// Specifically: alpha, gamma, epsilon are orphans; beta, delta are found.
			Assert.Equal(
				new HashSet<string> { "alpha", "gamma", "epsilon" },
				serial.Orphaned,
				HashSet<string>.CreateSetComparer());
		}

		[Fact]
		public void AnalyseDictionary_ManyFilesAndWords_ProducesConsistentResultUnderParallelism()
		{
			// Larger fixture to stress the parallel path — every word should
			// be found in exactly one specific file. Concurrent removal must
			// remove every word exactly once.
			var words = Enumerable.Range(0, 50).Select(i => $"token{i}").ToArray();
			var dict = WriteDictionary(words);

			for (int i = 0; i < 50; i++)
			{
				WriteCorpusFile($"file{i}.txt", $"this file contains token{i} as its keyword");
			}

			var result = DictionaryCheck.AnalyseDictionary(
				dict, _corpusDir,
				prefixesToStrip: null, loadedBundles: null,
				degreeOfParallelism: 8);

			Assert.Empty(result.Orphaned);
		}
	}
}
