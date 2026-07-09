using System.Text;
using Xunit;
using Crawler.Lexicon;

namespace Crawler.Tests.Lexicon
{
	/// <summary>
	/// Tests for Audit: SortDictionary, BackupDictionary, CleanDictionary,
	/// and the internal AnalyseDictionary method.
	/// All tests use temp directories and a Logger initialised to a temp log file.
	/// </summary>
	[Collection("Logger")]
	public class AuditTests : IDisposable
	{
		private readonly string _tempDir;
		private readonly string _tempLog;

		public AuditTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"dic-test-{Guid.NewGuid()}");
			Directory.CreateDirectory(_tempDir);
			_tempLog = Path.Combine(_tempDir, "test.log");
			Logger.Initialize(_tempLog, silent: true);
		}

		public void Dispose()
		{
			if (Directory.Exists(_tempDir))
			{
				Directory.Delete(_tempDir, recursive: true);
			}
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		private string DicFile(string name, params string[] lines)
		{
			var path = Path.Combine(_tempDir, name);
			File.WriteAllLines(path, lines, Encoding.UTF8);
			return path;
		}

		private string TxtFile(string name, string content)
		{
			var path = Path.Combine(_tempDir, name);
			File.WriteAllText(path, content, Encoding.UTF8);
			return path;
		}

		// ── BackupDictionary ──────────────────────────────────────────────────

		[Fact]
		public void BackupDictionary_NonExistentFile_NoOp()
		{
			var ex = Record.Exception(() =>
				Audit.BackupDictionary(Path.Combine(_tempDir, "missing.dic")));
			Assert.Null(ex);
		}

		[Fact]
		public void BackupDictionary_CreatesNumberedBackup()
		{
			var dic = DicFile("test.dic", "word1", "word2");
			Audit.BackupDictionary(dic);
			Assert.True(File.Exists(dic + ".1"));
		}

		[Fact]
		public void BackupDictionary_SecondCall_IncrementsNumber()
		{
			var dic = DicFile("test.dic", "word1");
			Audit.BackupDictionary(dic);
			Audit.BackupDictionary(dic);
			Assert.True(File.Exists(dic + ".1"));
			Assert.True(File.Exists(dic + ".2"));
		}

		[Fact]
		public void BackupDictionary_PreservesOriginal()
		{
			var dic = DicFile("test.dic", "word1", "word2");
			Audit.BackupDictionary(dic);
			Assert.True(File.Exists(dic));
			var lines = File.ReadAllLines(dic);
			Assert.Contains("word1", lines);
		}

		// ── SortDictionary ────────────────────────────────────────────────────

		[Fact]
		public void SortDictionary_NonExistentFile_NoOp()
		{
			var ex = Record.Exception(() =>
				Audit.SortDictionary(Path.Combine(_tempDir, "missing.dic")));
			Assert.Null(ex);
		}

		[Fact]
		public void SortDictionary_AlreadySorted_NoBackupCreated()
		{
			var dic = DicFile("sorted.dic", "apple", "banana", "cherry");
			Audit.SortDictionary(dic);
			Assert.False(File.Exists(dic + ".1"));
		}

		[Fact]
		public void SortDictionary_Unsorted_SortsAlphabetically()
		{
			var dic = DicFile("unsorted.dic", "cherry", "apple", "banana");
			Audit.SortDictionary(dic);
			var lines = File.ReadAllLines(dic).Where(l => l.Trim().Length > 0).ToList();
			Assert.Equal("apple", lines[0]);
			Assert.Equal("banana", lines[1]);
			Assert.Equal("cherry", lines[2]);
		}

		[Fact]
		public void SortDictionary_Unsorted_CreatesBackup()
		{
			var dic = DicFile("unsorted.dic", "cherry", "apple", "banana");
			Audit.SortDictionary(dic);
			Assert.True(File.Exists(dic + ".1"));
		}

		[Fact]
		public void SortDictionary_PinMarkerPreservedInOutput()
		{
			// ! prefix preserved in output but stripped for sort key
			var dic = DicFile("pinned.dic", "cherry", "!apple", "banana");
			Audit.SortDictionary(dic);
			var lines = File.ReadAllLines(dic).Where(l => l.Trim().Length > 0).ToList();
			Assert.Contains("!apple", lines);
		}

		[Fact]
		public void SortDictionary_PinMarkerStrippedForSortKey()
		{
			// !banana should sort between apple and cherry, not at end
			var dic = DicFile("pinned.dic", "cherry", "apple", "!banana");
			Audit.SortDictionary(dic);
			var lines = File.ReadAllLines(dic).Where(l => l.Trim().Length > 0).ToList();
			var appleIdx = lines.IndexOf("apple");
			var bananaIdx = lines.IndexOf("!banana");
			var cherryIdx = lines.IndexOf("cherry");
			Assert.True(appleIdx < bananaIdx && bananaIdx < cherryIdx);
		}

		[Fact]
		public void SortDictionary_CommentsWrittenFirst()
		{
			var dic = DicFile("commented.dic",
				"cherry",
				"// This is a comment",
				"apple");
			Audit.SortDictionary(dic);
			var lines = File.ReadAllLines(dic).Where(l => l.Trim().Length > 0).ToList();
			Assert.StartsWith("//", lines[0]);
		}

		[Fact]
		public void SortDictionary_HunspellFlagLinesPreservedAsComments()
		{
			// Lines containing '/' are Hunspell flag lines — treated as comments
			var dic = DicFile("flags.dic", "cherry", "word/FLAG", "apple");
			Audit.SortDictionary(dic);
			var lines = File.ReadAllLines(dic).Where(l => l.Trim().Length > 0).ToList();
			Assert.Contains("word/FLAG", lines);
			// Flag line should come before word entries
			Assert.True(lines.IndexOf("word/FLAG") < lines.IndexOf("apple"));
		}

		// ── CleanDictionary ───────────────────────────────────────────────────

		[Fact]
		public void CleanDictionary_NonExistentFile_NoOp()
		{
			var ex = Record.Exception(() =>
				Audit.CleanDictionary(
					Path.Combine(_tempDir, "missing.dic"),
					new Dictionary<string, string> { ["word"] = "orphan" }));
			Assert.Null(ex);
		}

		[Fact]
		public void CleanDictionary_EmptyToRemove_NoBackupCreated()
		{
			var dic = DicFile("clean.dic", "word1", "word2");
			Audit.CleanDictionary(dic, new Dictionary<string, string>());
			Assert.False(File.Exists(dic + ".1"));
		}

		[Fact]
		public void CleanDictionary_RemovesFlaggedEntry()
		{
			var dic = DicFile("clean.dic", "word1", "word2", "word3");
			Audit.CleanDictionary(dic,
				new Dictionary<string, string> { ["word2"] = "orphan" });
			var lines = File.ReadAllLines(dic);
			Assert.DoesNotContain("word2", lines);
			Assert.Contains("word1", lines);
			Assert.Contains("word3", lines);
		}

		[Fact]
		public void CleanDictionary_CreatesBackupBeforeRemoving()
		{
			var dic = DicFile("clean.dic", "word1", "word2");
			Audit.CleanDictionary(dic,
				new Dictionary<string, string> { ["word2"] = "orphan" });
			Assert.True(File.Exists(dic + ".1"));
		}

		[Fact]
		public void CleanDictionary_PreservesComments()
		{
			var dic = DicFile("clean.dic",
				"// keep this comment",
				"word1",
				"word2");
			Audit.CleanDictionary(dic,
				new Dictionary<string, string> { ["word2"] = "orphan" });
			var lines = File.ReadAllLines(dic);
			Assert.Contains("// keep this comment", lines);
		}

		[Fact]
		public void CleanDictionary_RemovesDuplicates()
		{
			var dic = DicFile("clean.dic", "word1", "word1", "word2");
			Audit.CleanDictionary(dic, new Dictionary<string, string>());
			// Even with empty toRemove, duplicates should be removed — but wait,
			// empty toRemove returns early. Let's pass one entry to trigger processing.
			var dic2 = DicFile("clean2.dic", "word1", "word1", "word2");
			Audit.CleanDictionary(dic2,
				new Dictionary<string, string> { ["word3"] = "orphan" });
			var lines = File.ReadAllLines(dic2);
			Assert.Equal(1, lines.Count(l => l.Trim() == "word1"));
		}

		[Fact]
		public void CleanDictionary_PinMarkerStrippedForComparison()
		{
			// !word1 in file, toRemove has "word1" (without !) — should still be removed
			var dic = DicFile("clean.dic", "!word1", "word2");
			Audit.CleanDictionary(dic,
				new Dictionary<string, string> { ["word1"] = "orphan" });
			var lines = File.ReadAllLines(dic);
			Assert.DoesNotContain("!word1", lines);
		}

		// ── PinWords ───────────────────────────────────────────────────────────────

		[Fact]
		public void PinWords_NonExistentFile_NoOp()
		{
			var ex = Record.Exception(() =>
				Audit.PinWords(
					Path.Combine(_tempDir, "missing.dic"),
					new[] { "word1" }));
			Assert.Null(ex);
		}

		[Fact]
		public void PinWords_EmptyList_NoBackupCreated()
		{
			var dic = DicFile("pin.dic", "word1", "word2");
			Audit.PinWords(dic, Array.Empty<string>());
			Assert.False(File.Exists(dic + ".1"));
		}

		[Fact]
		public void PinWords_MatchingWord_PrependsBang()
		{
			var dic = DicFile("pin.dic", "word1", "word2");
			Audit.PinWords(dic, new[] { "word1" });
			var lines = File.ReadAllLines(dic);
			Assert.Contains("!word1", lines);
		}

		[Fact]
		public void PinWords_MatchingWord_CreatesBackup()
		{
			var dic = DicFile("pin.dic", "word1", "word2");
			Audit.PinWords(dic, new[] { "word1" });
			Assert.True(File.Exists(dic + ".1"));
		}

		[Fact]
		public void PinWords_NonMatchingWord_Untouched()
		{
			var dic = DicFile("pin.dic", "word1", "word2");
			Audit.PinWords(dic, new[] { "word1" });
			var lines = File.ReadAllLines(dic);
			Assert.Contains("word2", lines);
			Assert.DoesNotContain("!word2", lines);
		}

		[Fact]
		public void PinWords_AlreadyPinned_NotDoublePinned()
		{
			var dic = DicFile("pin.dic", "!word1", "word2");
			Audit.PinWords(dic, new[] { "word1" });
			var lines = File.ReadAllLines(dic);
			Assert.DoesNotContain("!!word1", lines);
			Assert.Contains("!word1", lines);
		}

		[Fact]
		public void PinWords_CommentLines_Untouched()
		{
			var dic = DicFile("pin.dic", "// comment", "word1");
			Audit.PinWords(dic, new[] { "word1" });
			var lines = File.ReadAllLines(dic);
			Assert.Contains("// comment", lines);
		}

		[Fact]
		public void PinWords_HunspellFlagLines_Untouched()
		{
			var dic = DicFile("pin.dic", "word/FLAG", "word1");
			Audit.PinWords(dic, new[] { "word1" });
			var lines = File.ReadAllLines(dic);
			Assert.Contains("word/FLAG", lines);
			Assert.DoesNotContain("!word/FLAG", lines);
		}

		[Fact]
		public void PinWords_NoMatchFound_NoBackupCreated()
		{
			var dic = DicFile("pin.dic", "word1", "word2");
			Audit.PinWords(dic, new[] { "nonexistent" });
			Assert.False(File.Exists(dic + ".1"));
		}

		[Fact]
		public void PinWords_MultipleWords_AllPinned()
		{
			var dic = DicFile("pin.dic", "word1", "word2", "word3");
			Audit.PinWords(dic, new[] { "word1", "word3" });
			var lines = File.ReadAllLines(dic);
			Assert.Contains("!word1", lines);
			Assert.Contains("!word3", lines);
			Assert.Contains("word2", lines);
		}

		// ── AnalyseDictionary ─────────────────────────────────────────────────

		[Fact]
		public void AnalyseDictionary_WordNotInCorpus_Orphaned()
		{
			var dic = DicFile("user.dic", "missingword");
			TxtFile("page.txt", "completely different content here");
			var result = Audit.AnalyseDictionary(dic, _tempDir, null, null);
			Assert.Contains("missingword", result.Orphaned);
		}

		[Fact]
		public void AnalyseDictionary_WordFoundInCorpus_NotOrphaned()
		{
			var dic = DicFile("user.dic", "foundword");
			TxtFile("page.txt", "This page contains foundword in its text.");
			var result = Audit.AnalyseDictionary(dic, _tempDir, null, null);
			Assert.DoesNotContain("foundword", result.Orphaned);
		}

		[Fact]
		public void AnalyseDictionary_PinnedWord_ExcludedFromOrphanAnalysis()
		{
			var dic = DicFile("user.dic", "!pinnedword");
			TxtFile("page.txt", "no matching content here at all");
			var result = Audit.AnalyseDictionary(dic, _tempDir, null, null);
			Assert.DoesNotContain("pinnedword", result.Orphaned);
		}

		[Fact]
		public void AnalyseDictionary_WordFoundViaPrefix_NotOrphaned()
		{
			// "Wort" found as "Bindestrich-Wort" with prefix "Bindestrich"
			var dic = DicFile("user.dic", "Wort");
			TxtFile("page.txt", "Das ist ein Bindestrich-Wort im Text.");
			var result = Audit.AnalyseDictionary(
				dic, _tempDir, ["Bindestrich"], null);
			Assert.DoesNotContain("Wort", result.Orphaned);
		}

		[Fact]
		public void AnalyseDictionary_EmptyDictionary_ReturnsEmpty()
		{
			var dic = DicFile("user.dic");
			TxtFile("page.txt", "some content");
			var result = Audit.AnalyseDictionary(dic, _tempDir, null, null);
			Assert.Empty(result.Orphaned);
			Assert.Empty(result.Redundant);
		}

		[Fact]
		public void AnalyseDictionary_HunspellFlagLines_Ignored()
		{
			// Lines with '/' are Hunspell flags — not analysed as words
			var dic = DicFile("user.dic", "realword", "word/FLAG");
			TxtFile("page.txt", "no matching content");
			var result = Audit.AnalyseDictionary(dic, _tempDir, null, null);
			Assert.Contains("realword", result.Orphaned);
			// word/FLAG should not appear as an orphan
			Assert.DoesNotContain("word/FLAG", result.Orphaned);
		}

		[Fact]
		public void AnalyseDictionary_MultipleCorpusFiles_AllSearched()
		{
			var dic = DicFile("user.dic", "wordA", "wordB");
			TxtFile("page1.txt", "This text has wordA in it.");
			TxtFile("page2.txt", "This text has wordB in it.");
			var result = Audit.AnalyseDictionary(dic, _tempDir, null, null);
			Assert.DoesNotContain("wordA", result.Orphaned);
			Assert.DoesNotContain("wordB", result.Orphaned);
		}
	}
}
