using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests verifying that CrawlIndex.CreateLookupFile (writer for 02-crawler-index.log)
	/// produces deterministic byte-identical output across runs, regardless of
	/// parallel-iteration completion order.
	///
	/// Pre-#294 the writer used a ConcurrentBag from Parallel.ForEach and wrote
	/// entries in completion order — same input produced different byte-level
	/// output across runs (113 differences observed in real-world validation).
	///
	/// Post-#294 the writer sorts entries before writing.
	/// </summary>
	[Collection("Logger")]
	public class CrawlIndexOrderingTests
	{
		// Required by CrawlIndex.CreateLookupFile → Logger.LogInfo/Logger.LogError.
		public CrawlIndexOrderingTests()
		{
			var tempLog = Path.Combine(Path.GetTempPath(), $"createlookup-test-logger-{Guid.NewGuid()}.log");
			Logger.Initialize(tempLog, silent: true);
		}

		private static string CreateFakeCrawlerLog(int recordCount)
		{
			// Build a fake 01-crawler.log shape — lines containing "saved" with
			// at least 4 |-delimited fields. The parser uses field index 3
			// (identifier — the file hash+name) and field 1 (URL).
			var path = Path.Combine(Path.GetTempPath(), $"fake-01-crawler-{Guid.NewGuid()}.log");
			var lines = new List<string>();
			for (int i = 0; i < recordCount; i++)
			{
				// Use spaced-out hash prefixes so sort order is unambiguous.
				var hash = $"{i:x16}aabbccddeeff0011{i:x16}";
				var url = $"https://example.com/page{i}.html";
				// Field shape: timestamp | url | status | identifier | source
				lines.Add($"2026-05-16T00:00:00Z | {url} | saved | {hash}page{i}.html | discovery");
			}
			File.WriteAllLines(path, lines);
			return path;
		}

		// ── Determinism across two runs ──────────────────────────────────

		[Fact]
		public void CreateLookupFile_TwoRuns_ProduceByteIdenticalOutput()
		{
			// The critical regression guard for #294: run the writer twice on
			// the same input and assert the two outputs are byte-identical.
			// Pre-#294 this assertion would fail intermittently due to
			// Parallel.ForEach completion-order non-determinism.
			var fakeCrawlerLog = CreateFakeCrawlerLog(200);
			var out1 = Path.Combine(Path.GetTempPath(), $"index-run1-{Guid.NewGuid()}.log");
			var out2 = Path.Combine(Path.GetTempPath(), $"index-run2-{Guid.NewGuid()}.log");

			try
			{
				CrawlIndex.CreateLookupFile(fakeCrawlerLog, out1);
				CrawlIndex.CreateLookupFile(fakeCrawlerLog, out2);

				var bytes1 = File.ReadAllBytes(out1);
				var bytes2 = File.ReadAllBytes(out2);

				Assert.Equal(bytes1, bytes2);
			}
			finally
			{
				if (File.Exists(fakeCrawlerLog))
				{
					File.Delete(fakeCrawlerLog);
				}

				if (File.Exists(out1))
				{
					File.Delete(out1);
				}

				if (File.Exists(out2))
				{
					File.Delete(out2);
				}
			}
		}

		[Fact]
		public void CreateLookupFile_ManyRecords_StillDeterministic()
		{
			// Stress: with a larger record set, parallelism has more opportunity
			// to scramble ordering. The sort must hold.
			var fakeCrawlerLog = CreateFakeCrawlerLog(2000);
			var out1 = Path.Combine(Path.GetTempPath(), $"index-large-run1-{Guid.NewGuid()}.log");
			var out2 = Path.Combine(Path.GetTempPath(), $"index-large-run2-{Guid.NewGuid()}.log");

			try
			{
				CrawlIndex.CreateLookupFile(fakeCrawlerLog, out1);
				CrawlIndex.CreateLookupFile(fakeCrawlerLog, out2);

				Assert.Equal(File.ReadAllBytes(out1), File.ReadAllBytes(out2));
			}
			finally
			{
				if (File.Exists(fakeCrawlerLog))
				{
					File.Delete(fakeCrawlerLog);
				}

				if (File.Exists(out1))
				{
					File.Delete(out1);
				}

				if (File.Exists(out2))
				{
					File.Delete(out2);
				}
			}
		}

		// ── Ordering is alphabetic (operator-readable) ───────────────────

		[Fact]
		public void CreateLookupFile_Output_IsSortedAlphabetically()
		{
			var fakeCrawlerLog = CreateFakeCrawlerLog(50);
			var outPath = Path.Combine(Path.GetTempPath(), $"index-sort-{Guid.NewGuid()}.log");

			try
			{
				CrawlIndex.CreateLookupFile(fakeCrawlerLog, outPath);

				var lines = File.ReadAllLines(outPath);
				Assert.NotEmpty(lines);

				// Each line should compare ≤ the next using ordinal string comparison.
				for (int i = 1; i < lines.Length; i++)
				{
					Assert.True(StringComparer.Ordinal.Compare(lines[i - 1], lines[i]) <= 0,
						$"Line {i - 1} ('{lines[i - 1]}') should sort before line {i} ('{lines[i]}')");
				}
			}
			finally
			{
				if (File.Exists(fakeCrawlerLog))
				{
					File.Delete(fakeCrawlerLog);
				}

				if (File.Exists(outPath))
				{
					File.Delete(outPath);
				}
			}
		}

		// ── Output preserves all records ─────────────────────────────────

		[Fact]
		public void CreateLookupFile_Output_PreservesAllRecords()
		{
			// Sort must not drop or duplicate entries.
			const int recordCount = 100;
			var fakeCrawlerLog = CreateFakeCrawlerLog(recordCount);
			var outPath = Path.Combine(Path.GetTempPath(), $"index-preserve-{Guid.NewGuid()}.log");

			try
			{
				CrawlIndex.CreateLookupFile(fakeCrawlerLog, outPath);

				var lines = File.ReadAllLines(outPath);
				Assert.Equal(recordCount, lines.Length);
			}
			finally
			{
				if (File.Exists(fakeCrawlerLog))
				{
					File.Delete(fakeCrawlerLog);
				}

				if (File.Exists(outPath))
				{
					File.Delete(outPath);
				}
			}
		}
	}
}
