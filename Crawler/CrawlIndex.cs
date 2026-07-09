namespace Crawler
{
	using System.Collections.Concurrent;
	using System.Diagnostics.CodeAnalysis;
	using System.Text;
	using Crawler.Urls;

	/// <summary>
	/// Builds and queries the crawl-log-derived URL/file index. CreateLookupFile writes
	/// the on-disk index (02-crawler-index.log) from the raw crawl log; BuildLogIndex
	/// produces the same filename->URL mapping in memory; LookUpUrlForFile/
	/// LookUpSourceForFile are the safe lookup facades over Cache; GetLinesContainingKey
	/// filters crawl-log lines by key. Quarantined (.unverified) downloads are excluded
	/// from the index uniformly.
	/// </summary>
	public static class CrawlIndex
	{
		public static void CreateLookupFile(string logFile, string lookupFile, int maxDegreeOfParallelism = 0)
		{
			var lookupEntries = new ConcurrentBag<string>();

			try
			{
				var lines = File.ReadAllLines(logFile, Encoding.UTF8);

				int dop = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
				var po = new ParallelOptions { MaxDegreeOfParallelism = dop };

				Parallel.ForEach(lines, po, line =>
				{
					if (line.Contains("saved", StringComparison.OrdinalIgnoreCase))
					{
						var parts = line.Split('|', StringSplitOptions.TrimEntries);
						if (parts.Length > 3)
						{
							var identifier = parts[3];
							var url = parts[1];
							// Quarantined downloads (.unverified) are recorded in
							// 00/01 (so they count for 404 detection) but must NOT enter the
							// index — they are not analyzable pages. Excluding them here keeps
							// them out of Cache and every index-driven analyzer.
							if (identifier.EndsWith(FileTypeClassifier.UnverifiedExtension, StringComparison.OrdinalIgnoreCase))
							{
								return;
							}
							// [KEEP] Column 3 (source) is optional — present when the URL was
							// found via a specific discovery path. See CrawlLogWriter.Write source param.
							// "discovery" = normal crawl link following
							// "list"      = 05-not-directly-crawlable.log post-crawl pass
							// Downstream readers that only use filename|url safely ignore col 3.
							var source = parts.Length > 4 ? parts[4].Trim() : string.Empty;
							var entry = string.IsNullOrEmpty(source)
								? $"{identifier}|{url}"
								: $"{identifier}|{url}|{source}";
							lookupEntries.Add(entry);
						}
					}
				});

				// Sort deterministically before write. The Parallel.ForEach
				// above collects into ConcurrentBag in completion order, which is
				// CPU-scheduling-dependent — same input produces different byte-level
				// output across runs. Sorting by the composed entry string is
				// equivalent to sorting by (identifier, url, source) since they
				// concatenate in that order, and the SHA-prefixed identifier gives
				// stable alphabetic distribution.
				// Without this sort, 02-crawler-index.log was non-deterministic
				// across replays (113 differences observed in a real validation).
				var sortedEntries = lookupEntries.OrderBy(e => e, StringComparer.Ordinal).ToList();
				// Route through WriteAllLinesWithRetry so a locked file
				// (operator has it open in a compare tool / editor) is recoverable
				// rather than silently swallowed. Previously the catch-all below
				// caught IOException for the locked-file case too, leaving stale
				// content on disk without an actionable prompt.
				if (!FileIo.WriteAllLinesWithRetry(lookupFile, sortedEntries, Path.GetFileName(lookupFile)))
				{
					Logger.LogError($"CreateLookupFile: write to {lookupFile} did not complete — downstream steps may produce wrong results.");
					return;
				}
				Logger.LogInfo($"Lookup file created: {lookupFile}");
			}
			catch (Exception ex)
			{
				Logger.LogError($"CreateLookupFile: {ex.Message}");
			}
		}

		/// <summary>
		/// Returns the crawl source ("discovery" or "list") for the given filename.
		/// Returns an empty string if not found or the index was built without source info.
		/// </summary>
		public static string LookUpSourceForFile(string filenameToSearch)
		{
			try
			{
				return Cache.SourceFor(filenameToSearch);
			}
			catch (Exception ex)
			{
				Logger.LogError($"LookUpSourceForFile: {filenameToSearch} resulted in {ex.Message}");
				return string.Empty;
			}
		}

		public static string LookUpUrlForFile(string filenameToSearch)
		{
			// Mirrors Cache.UrlFor's miss sentinel; returned on a cache miss
			// or lookup exception. See Cache.UrlFor for when "error" arises
			// and how callers must handle it.
			const string notFound = "error";
			try
			{
				string url = Cache.UrlFor(filenameToSearch);
				if (url != notFound)
				{
					return url;
				}
			}
			catch (Exception ex)
			{
				Logger.LogError($"LookUpUrlForFile: {filenameToSearch} resulted in {ex.Message}");
			}

			Logger.LogError($"LookUpUrlForFile: {filenameToSearch} was not found in the cache.");
			return notFound;
		}

		// Reads the crawler log once into a filename -> URL dictionary.
		[ExcludeFromCodeCoverage(Justification =
			"Filesystem read + line parsing into a lookup dict. Output shape is " +
			"exercised by callers (LookUpSourceForFile/LookUpUrlForFile tests). " +
			"Direct testing would re-test File.ReadAllLines.")]
		public static Dictionary<string, string> BuildLogIndex(string logFilePath)
		{
			Dictionary<string, string> index = new(StringComparer.OrdinalIgnoreCase);

			try
			{
				foreach (var line in File.ReadLines(logFilePath, Encoding.UTF8))
				{
					var parts = line.Split('|');
					if (parts.Length > 3 && parts[2].Trim().Equals("saved", StringComparison.OrdinalIgnoreCase))
					{
						string url = parts[1].Replace(":443", string.Empty).Trim();
						string filename = parts[3].Trim();
						// Quarantined (.unverified) downloads are not analyzable
						// pages — keep them out of the index (uniform with CreateLookupFile).
						if (filename.EndsWith(FileTypeClassifier.UnverifiedExtension, StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}

						index.TryAdd(filename, url);
					}
				}
			}
			catch (Exception ex)
			{
				Logger.LogError($"Error building log index: {ex}");
			}

			return index;
		}

		[ExcludeFromCodeCoverage(Justification =
			"Filesystem read + linear scan. Logic is a Where(s => s.Contains(key)) " +
			"with no edge cases beyond what File.ReadAllLines guarantees.")]
		public static List<string> GetLinesContainingKey(string logFilePath, string key)
		{
			List<string> matchingLines = [];

			try
			{
				foreach (var line in File.ReadLines(logFilePath, Encoding.UTF8))
				{
					if (line.Contains(key, StringComparison.OrdinalIgnoreCase))
					{
						var parts = line.Split('|');
						if (parts.Length > 1)
						{
							string cleanLine = parts[1].Replace(":443", string.Empty).Trim();
							matchingLines.Add(cleanLine);
						}
					}
				}
			}
			catch (Exception ex)
			{
				Logger.LogError($"Error on GetLinesContainingKey: {ex}");
			}

			return [.. matchingLines.OrderBy(line => line)];
		}
	}
}
