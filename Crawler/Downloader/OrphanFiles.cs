namespace Crawler.Downloader
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics.CodeAnalysis;
	using System.IO;
	using System.Linq;
	using System.Text;

	/// <summary>
	/// Crawler-index integrity primitives over the downloaded corpus: detect files
	/// on disk that the crawl index does not account for ("orphans"), and quarantine
	/// a chosen orphan by renaming it to a .bak sibling for safe recovery. Extracted
	/// from Tools. The interactive resolution flow stays in InteractiveTriage /
	/// CrawlOrchestrator; these are just the mechanics. <see cref="Quarantine"/> is
	/// [ExcludeFromCodeCoverage] — a File.Move wrapper.
	/// </summary>
	public static class OrphanFiles
	{
		/// <summary>
		/// Returns the list of HTML files in <paramref name="downloadDirectory"/>
		/// whose filename is not present in the crawler index. Comparison is
		/// case-insensitive on the filename only (no path component).
		/// Returns an empty list if the directory or index is missing.
		/// </summary>
		public static List<string> Detect(
			string downloadDirectory, string indexPath, string filePattern)
		{
			if (!Directory.Exists(downloadDirectory) || !File.Exists(indexPath))
			{
				return [];
			}

			// Build a set of all filenames known to the index.
			var indexedFiles = File.ReadAllLines(indexPath, Encoding.UTF8)
				.Select(l => l.Split('|')[0].Trim())
				.Where(f => f.Length > 0)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			return
			[
				.. Directory
					.EnumerateFiles(downloadDirectory, filePattern)
					.Select(Path.GetFileName)
					.Where(f => !string.IsNullOrEmpty(f) && !indexedFiles.Contains(f!))
					.Select(f => f!)
					.OrderBy(f => f)
			];
		}

		/// <summary>
		/// Renames an orphan file in place by appending <paramref name="bakExt"/>
		/// (e.g. ".html.bak"). Overwrites any existing .bak with the same name.
		/// Logs an error if the rename fails — does not throw.
		/// </summary>
		[ExcludeFromCodeCoverage(Justification =
			"Filesystem rename with collision handling. Behaviour is a File.Move " +
			"wrapper; testing would re-test File.Move.")]
		public static void Quarantine(string directory, string filename, string bakExt)
		{
			var src = Path.Combine(directory, filename);
			var dest = Path.Combine(directory, filename + bakExt);
			try { File.Move(src, dest, overwrite: true); }
			catch (Exception ex) { Logger.LogError($"Could not rename {filename}: {ex.Message}"); }
		}
	}
}
