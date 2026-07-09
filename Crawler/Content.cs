namespace Crawler
{
	public class Content
	{
		public static void Listing(
			string outputFile,
			string filePath,
			string rowMustContain,
			string columnDelimiter,
			int columnPosition,
			bool replacePrefix,
			string addSuffix,
			List<string> exclusions,
			string rowNegativeFilter,
			bool silent = false
		)
		{
			List<string> filteredRows = [];

			if (columnPosition < 0)
			{
				Logger.LogError($"Invalid column position configured in CmsContentList.ColumnIndex in config.json.");
				return;
			}

			// Split the negative filter pattern once before the loop instead of
			// re-allocating the array on every line read.
			string[] negativeFilters = rowNegativeFilter.Split(['|'], StringSplitOptions.None);

			if (!FileIo.TryReadCsvLines(filePath, silent, out var csvLines))
			{
				Logger.LogWarning($"Skipping content listing — CSV unavailable: {filePath}");
				return;
			}

			foreach (var line in csvLines)
			{
				if (string.IsNullOrEmpty(line) || !line.Contains(rowMustContain))
				{
					continue;
				}

				bool containsNegativeFilter = negativeFilters.Any(filter => line.Contains(filter));
				if (containsNegativeFilter)
				{
					continue;
				}

				string[] columns = line.Split([columnDelimiter], StringSplitOptions.None);

				if (columns.Length > columnPosition)
				{
					var entry = columns[columnPosition];
					if (replacePrefix)
					{
						entry = entry.Replace(rowMustContain, string.Empty) + addSuffix;
					}

					if (!exclusions.Any(exclusion => entry.Contains(exclusion)))
					{
						if (!string.IsNullOrEmpty(entry) && !entry.Equals(addSuffix))
						{
							filteredRows.Add(entry);
						}
					}
				}
			}

			FileIo.WriteList(outputFile, filteredRows);
		}

		public static void CompareCrawlAndContent(string contentList, string compareList, string outputLog, string prefixToStrip)
		{
			HashSet<string> crawlEntries = [];

			using (StreamReader reader = new(contentList))
			{
				while (!reader.EndOfStream)
				{
					string line = reader.ReadLine() ?? string.Empty;
					if (!string.IsNullOrEmpty(line))
					{
						var parts = line.Split('|');
						// [KEEP] Index format: filename|url|source.
						// Use >= 2 so both old (2-column) and new (3-column) index files are handled.
						if (parts.Length >= 2)
						{
							string remoteFile = parts[1];
							if (remoteFile.StartsWith(prefixToStrip))
							{
								remoteFile = remoteFile[prefixToStrip.Length..];
							}
							crawlEntries.Add(remoteFile);
						}
					}
				}
			}

			List<string> missingPaths = [];

			using (StreamReader reader = new(compareList))
			{
				while (!reader.EndOfStream)
				{
					string relativePath = reader.ReadLine() ?? string.Empty;
					if (!string.IsNullOrEmpty(relativePath) && !crawlEntries.Contains(relativePath))
					{
						missingPaths.Add(relativePath);
					}
				}
			}

			FileIo.WriteAllLinesWithRetry(outputLog, missingPaths, Path.GetFileName(outputLog));
		}
	}
}
