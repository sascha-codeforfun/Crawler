namespace Crawler.Snapshots
{
	using System;
	using System.IO;
	using System.Linq;

	/// <summary>
	/// The snapshot-folder identifier. Each crawl run lives in a folder named
	/// yyyy-MM-dd-HH-mm-ss; <see cref="NewName"/> produces that name for a run
	/// (with a debug "latest"/explicit override), and <see cref="Matches"/>
	/// recognises such folders on disk. Extracted from Tools.
	/// </summary>
	public static class SnapshotFolder
	{
		/// <summary>
		/// Returns the timestamp to use for the current run.
		/// When DebugDisableCrawl is false, generates a fresh timestamp.
		/// When DebugDisableCrawl is true:
		///   - "latest" (case-insensitive) → finds the most recently created subfolder
		///     under <paramref name="sessionParentDirectory"/> and uses its name.
		///   - Any other value → used as-is.
		/// </summary>
		public static string NewName(
			bool debugDisableCrawl,
			string debugTimeStamp,
			string? sessionParentDirectory = null)
		{
			if (!debugDisableCrawl)
			{
				return $"{DateTime.Now:yyyy-MM-dd-HH-mm-ss}";
			}

			if (debugTimeStamp.Equals("latest", StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrEmpty(sessionParentDirectory)
				&& Directory.Exists(sessionParentDirectory))
			{
				// Only consider folders whose names look like a crawler timestamp
				// (yyyy-MM-dd-HH-mm-ss — 19 chars, digits and hyphens only).
				var latest = Directory
					.EnumerateDirectories(sessionParentDirectory)
					.Select(d => new DirectoryInfo(d))
					.Where(d => Matches(d.Name))
					.OrderByDescending(d => d.CreationTimeUtc)
					.FirstOrDefault();

				if (latest != null)
				{
					Logger.LogInfo($"DebugTimeStamp \"latest\" resolved to: {latest.Name}");
					return latest.Name;
				}

				Logger.LogWarning(
					$"DebugTimeStamp is \"latest\" but no valid timestamp subfolders found " +
					$"in {sessionParentDirectory}. A fresh timestamp will be used — " +
					$"run a full crawl first to create a snapshot.");
				return $"{DateTime.Now:yyyy-MM-dd-HH-mm-ss}";
			}

			return debugTimeStamp;
		}

		/// <summary>
		/// Returns true when the folder name matches the crawler timestamp format
		/// yyyy-MM-dd-HH-mm-ss (e.g. "2026-04-25-14-30-00").
		/// </summary>
		public static bool Matches(string name) =>
			name.Length == 19
			&& name[4] == '-' && name[7] == '-' && name[10] == '-'
			&& name[13] == '-' && name[16] == '-'
			&& name.Replace("-", "").All(char.IsDigit);
	}
}
