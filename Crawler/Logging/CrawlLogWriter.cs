namespace Crawler.Logging
{
	using System;
	using System.Diagnostics.CodeAnalysis;
	using System.IO;
	using System.Text;

	/// <summary>
	/// Appends rows to the raw crawl logs. <see cref="Write"/> records crawl
	/// activity (timestamp | url | status | message | source); <see cref="WriteSaved"/>
	/// records the 00-crawler.log "saved" row with a trailing Content-Type column.
	/// Both serialise through a shared lock. Extracted from Tools.
	/// </summary>
	public static class CrawlLogWriter
	{
		private static readonly object logLock = new();

		[ExcludeFromCodeCoverage(Justification =
			"Locking + filesystem append. Logging output is operator-visible at " +
			"runtime; format regressions surface immediately.")]
		public static void Write(string url, string? httpStatusCode, string message, string logFile,
			string source = "")
		{
			if (string.IsNullOrEmpty(httpStatusCode))
			{
				httpStatusCode = "n/a";
			}

			// [KEEP] The source column records how the URL was discovered.
			// "discovery" = found via normal link crawling (<a href>, <img src>, etc.)
			// "list"      = found via 05-not-directly-crawlable.log post-crawl pass
			// Empty       = status/info entries (crawled, completed, errors) — not data rows
			// CreateLookupFile reads this column to populate 02-crawler-index.log column 3.
			var sourceSuffix = string.IsNullOrEmpty(source) ? string.Empty : $" | {source}";
			var logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {url} | {httpStatusCode} | {message}{sourceSuffix}";

			lock (logLock)
			{
				File.AppendAllText(logFile, logEntry + Environment.NewLine, Encoding.UTF8);
			}
		}

		/// <summary>
		/// Writes a "saved" row to the raw crawl log (00-crawler.log), carrying the
		/// response Content-Type as a trailing column so the settle phase can read it
		/// back without the live HTTP response. Format:
		///   timestamp | url | status | filename | source | contentType
		/// The Content-Type column exists ONLY in 00; settle drops it when projecting
		/// 00 → 01, so 01 keeps its historical 5-field shape and downstream readers are
		/// untouched. <paramref name="contentType"/> may be null/empty (server omitted
		/// it) — recorded as "n/a" so the column position is always present.
		/// </summary>
		public static void WriteSaved(
			string url, string fileName, string logFile, string source, string? contentType)
		{
			var ct = string.IsNullOrEmpty(contentType) ? "n/a" : contentType;
			var src = string.IsNullOrEmpty(source) ? "discovery" : source;
			var logEntry =
				$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {url} | saved | {fileName} | {src} | {ct}";

			lock (logLock)
			{
				File.AppendAllText(logFile, logEntry + Environment.NewLine, Encoding.UTF8);
			}
		}
	}
}
