namespace Crawler.Html
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics.CodeAnalysis;
	using System.IO;
	using System.Linq;
	using System.Text;

	/// <summary>
	/// Searches a directory of files for a set of strings, returning the first file
	/// (by enumeration order) containing each. Generic string-hunting over file
	/// contents — used to locate which page links a given 404 URL; not HTML-aware.
	/// Extracted from Tools.
	/// </summary>
	public static class HtmlSearch
	{
		[ExcludeFromCodeCoverage(Justification =
			"Filesystem enumeration + per-file string scan. Logic is a foreach " +
			"over directory contents; no decidable behaviour beyond the I/O.")]
		public static Dictionary<string, string> FindFilesContaining(List<string> strings, string directory, string filePattern)
		{
			if (strings == null || strings.Count == 0)
			{
				throw new ArgumentException("The list of strings cannot be null or empty.", nameof(strings));
			}

			if (!Directory.Exists(directory))
			{
				throw new DirectoryNotFoundException($"The provided directory '{directory}' does not exist.");
			}

			// Track unfound strings so we can stop scanning once all are matched
			HashSet<string> remaining = new(strings, StringComparer.OrdinalIgnoreCase);
			Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

			foreach (var filePath in Directory.EnumerateFiles(directory, filePattern))
			{
				if (remaining.Count == 0)
				{
					break;
				}

				var content = File.ReadAllText(filePath, Encoding.UTF8);
				var fileName = Path.GetFileName(filePath);

				foreach (var str in remaining.ToList())
				{
					if (content.Contains(str, StringComparison.OrdinalIgnoreCase))
					{
						result[str] = fileName;
						remaining.Remove(str);
					}
				}
			}

			return result;
		}
	}
}
