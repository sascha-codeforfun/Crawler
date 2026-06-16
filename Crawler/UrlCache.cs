namespace Crawler
{
	using System.Collections.Concurrent;
	using System.Text;

	public static class UrlCache
	{
		private static readonly ConcurrentDictionary<string, string> cache = new();
		private static readonly ConcurrentDictionary<string, string> sourceCache = new();

		public static void LoadCache(string lookupFile)
		{
			if (!File.Exists(lookupFile))
			{
				Logger.LogError($"Lookup file not found: {lookupFile}");
				return;
			}

			int count = 0;
			try
			{
				foreach (var line in File.ReadLines(lookupFile, Encoding.UTF8))
				{
					if (string.IsNullOrWhiteSpace(line))
					{
						continue;
					}

					var parts = line.Split('|', StringSplitOptions.TrimEntries);
					if (parts.Length < 2)
					{
						continue;
					}

					// [KEEP] Column format: filename|url|source (source is optional).
					// source = "discovery" (normal crawl) or "list" (post-crawl pass).
					// parts[1] is always the URL — parts[2] is the optional source.
					var url = parts[1];

					cache[parts[0]] = url;
					if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
					{
						sourceCache[parts[0]] = parts[2].Trim();
					}

					count++;
				}

				if (count == 0)
				{
					Logger.LogError($"Lookup file contains no valid entries: {lookupFile}");
					return;
				}

				Logger.LogInfo($"Cache loaded with {cache.Count} entries.");
				ConsoleUi.WriteStepRow("URL cache", $"{cache.Count} entries");
			}
			catch (Exception ex)
			{
				Logger.LogError($"Failed to load cache from {lookupFile}: {ex}");
			}
		}

		public static string LookUpUrl(string filenameToSearch)
		{
			if (cache.TryGetValue(filenameToSearch, out var url))
			{
				return url;
			}

			// [KEEP] Miss sentinel — the filename has no entry in the URL cache (built
			// from the crawl log's "saved" rows), so it can't be resolved to a crawled
			// URL. The crawler-index integrity check is the front-line guard for
			// unindexed files (orphans); "error" is the LAST line of defense, for a file
			// that reaches a lookup despite that gate — e.g. download/ changing mid-run,
			// after the orphan snapshot but before analysis enumerates (a file copied or
			// renamed into place). We return "error" rather than abort the whole snapshot:
			// callers treat it as "no URL available" (substitute the filename, or skip),
			// and the miss is recorded here to application.log. Do not "simplify" this
			// away — it guards a real, hard-to-reproduce mid-run desync.
			Logger.LogError($"URL not found for '{filenameToSearch}'.");
			return "error";
		}

		/// <summary>
		/// Returns the crawl source for the given filename:
		/// "discovery" (found via link following) or "list" (post-crawl pass from log 05).
		/// Returns an empty string if the source was not recorded (old index format).
		/// </summary>
		public static string LookUpSource(string filenameToSearch)
		{
			if (sourceCache.TryGetValue(filenameToSearch, out var source))
			{
				return source;
			}

			return string.Empty;
		}

		/// <summary>
		/// Reverse lookup — finds the filename for a given URL.
		/// Returns null if not found. Used by spell triage [M] to locate raw HTML.
		/// </summary>
		public static string? GetFilenameForUrl(string url)
		{
			foreach (var kvp in cache)
			{
				if (kvp.Value.Equals(url, StringComparison.OrdinalIgnoreCase))
				{
					return kvp.Key;
				}
			}

			return null;
		}
	}

}
