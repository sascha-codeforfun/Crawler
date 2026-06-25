namespace Crawler.Snapshots
{
	using System;
	using System.Diagnostics.CodeAnalysis;
	using System.IO;
	using System.Linq;
	using System.Text;

	/// <summary>
	/// Reads and interprets a snapshot's crawler log (01-crawler.log).
	/// <see cref="Analyse"/> decides whether a previous crawl completed and how
	/// long it took; <see cref="Summarise"/> adds file-count/size for the
	/// keep/delete dialog; <see cref="ParseTimestamp"/> (internal) extracts the
	/// timestamp from one log line. Extracted from Tools.
	/// </summary>
	public static class CrawlLog
	{
		/// <summary>
		/// Analyses a previous snapshot's 01-crawler.log to determine if the
		/// crawl completed normally and how long it took.
		/// Returns (isComplete, duration, lastEntry) where duration is null if
		/// incomplete or if timestamps could not be parsed.
		/// Snapshot health is determined by checking only the LAST non-empty
		/// line for the word "completed". When CmsContentList.PostCrawlPass is enabled
		/// there are two "completed" markers — the post-crawl one is final and
		/// is the one this method validates against.
		/// </summary>
		public static (bool IsComplete, TimeSpan? Duration, string LastEntry) Analyse(
			string crawlerLogPath, string baseUrl)
		{
			if (!File.Exists(crawlerLogPath))
			{
				return (false, null, "");
			}

			var lines = File.ReadAllLines(crawlerLogPath, Encoding.UTF8)
				.Where(l => l.Trim().Length > 0).ToList();

			if (lines.Count == 0)
			{
				return (false, null, "");
			}

			var lastLine = lines[^1];
			bool isComplete = lastLine.Contains("completed", StringComparison.OrdinalIgnoreCase);

			if (!isComplete)
			{
				return (false, null, lastLine);
			}

			// Parse duration: first line matching base URL + "crawled" → last line "completed"
			TimeSpan? duration = null;
			var startLine = lines.FirstOrDefault(l =>
				l.Contains(baseUrl, StringComparison.OrdinalIgnoreCase) &&
				l.Contains("crawled", StringComparison.OrdinalIgnoreCase));
			if (startLine != null)
			{
				var startTs = ParseTimestamp(startLine);
				var endTs = ParseTimestamp(lastLine);
				if (startTs.HasValue && endTs.HasValue)
				{
					duration = endTs.Value - startTs.Value;
				}
			}

			return (true, duration, lastLine);
		}

		/// <summary>
		/// Parses a crawler log line timestamp ("yyyy-MM-dd HH:mm:ss | ...").
		/// Returns null if the leading token doesn't parse.
		/// </summary>
		internal static DateTime? ParseTimestamp(string logLine)
		{
			// Crawler log format: "2026-04-25 12:07:22 | url | status | message"
			var pipeIdx = logLine.IndexOf('|');
			var raw = pipeIdx > 0 ? logLine[..pipeIdx].Trim() : logLine.Trim();
			if (DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss",
				System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.None, out var dt))
			{
				return dt;
			}

			if (DateTime.TryParse(raw, out var dt2))
			{
				return dt2;
			}

			return null;
		}

		/// <summary>
		/// Pure half of the snapshot integrity check. Returns whether the
		/// snapshot's crawler log shows a completed marker, plus the snapshot's
		/// file count and total size in MB (used by the interactive dialog to
		/// inform the delete/keep/abort decision).
		/// The interactive dialog lives in InteractiveTriage.CheckSnapshotIntegrity;
		/// silent-mode callers can use this method and warn-and-proceed.
		/// </summary>
		[ExcludeFromCodeCoverage(Justification =
			"Filesystem enumeration + completion-marker detection. Marker logic is " +
			"exercised by Analyse tests (which run at 100% coverage).")]
		public static (bool IsComplete, int FileCount, double TotalMb) Summarise(
			DirectoryInfo snapshot, string baseUrl)
		{
			var crawlerLog = Path.Combine(snapshot.FullName, LogFileNames.Crawler);
			var (isComplete, _, _) = Analyse(crawlerLog, baseUrl);
			if (isComplete)
			{
				return (true, 0, 0);
			}

			var allFiles = snapshot.Exists
				? snapshot.GetFiles("*", SearchOption.AllDirectories) : [];
			var fileCount = allFiles.Length;
			var totalMb = allFiles.Sum(f => f.Length) / 1_048_576.0;
			return (false, fileCount, totalMb);
		}
	}
}
