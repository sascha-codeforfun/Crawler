namespace Crawler.Logging
{
	using System.Collections.Generic;
	using System.Diagnostics.CodeAnalysis;

	/// <summary>
	/// Writes the error-sources log (which pages contained a 404 link), routed
	/// through <see cref="IssueLogWriter"/> for consistent field sanitisation.
	/// Extracted from Tools.
	/// </summary>
	public static class ErrorSourcesLog
	{
		[ExcludeFromCodeCoverage(Justification =
			"I/O writer delegating to IssueLogWriter. Logic is a foreach over the " +
			"404 pages, resolving each source URL via CrawlIndex and collecting one " +
			"record per page; the dual-locale CSV emission, RFC-4180 quoting and BOM " +
			"live in IssueLogWriter.WriteCsvPair and are tested there.")]
		public static void Write(IEnumerable<KeyValuePair<string, string>> pagesContaining404Link, string errorSourcesCsvBasePath)
		{
			// [KEEP] Routed through IssueLogWriter for consistency. The fields
			// are URLs — low risk of containing delimiters in well-formed input,
			// but URL parameters can technically contain unusual characters
			// and the cost of central sanitization is negligible.
			var records = new List<string?[]>
			{
				new string?[] { "404Url", "SourceUrl" }
			};
			foreach (var item in pagesContaining404Link)
			{
				string errorUrl = CrawlIndex.LookUpUrlForFile(item.Value);
				// Columns: {404Url} | {sourcePageUrl}
				records.Add(new string?[] { item.Key, errorUrl });
			}
			IssueLogWriter.WriteCsvPair(errorSourcesCsvBasePath, records);
		}
	}
}
