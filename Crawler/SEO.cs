namespace Crawler
{
	using HtmlAgilityPack;

	public class SEO
	{
		public static async Task ExtractDataAsync(string directoryPath, string outputFilePath, string filePattern)
		{
			var htmlFiles = Directory.GetFiles(directoryPath, filePattern);

			// Route through IssueLogWriter so the '@@@' delimiter is
			// enforced and each crawled field (title / description / robots / keywords)
			// is sanitized for CR / LF / control chars / bidi controls / the
			// delimiter itself.
			// [KEEP] Sanitization-through-IssueLogWriter.ComposeLine is mandatory —
			// previously a local Escape() handled CR/LF/TAB only; the centralised
			// path is strictly more defensive.
			// Use IssueLogWriter.Utf8WithBom (BOM-emitting) for consistency
			// with the rest of the log corpus. Earlier the encoding was
			// `new UTF8Encoding(false)` (no BOM), producing inconsistent output
			// vs. the BOM-prefixed logs written via IssueLogWriter's non-streaming
			// helpers (10, 16, 17, etc.).
			using var writer = new StreamWriter(outputFilePath, append: false,
				IssueLogWriter.Utf8WithBom);
			await writer.WriteLineAsync(IssueLogWriter.ComposeLine(IssueLogWriter.SeoDelimiter,
				"url", "robotsValue", "titleValue", "titleLength",
				"descriptionValue", "descriptionLength", "keywordsValue", "source", "h1Count"));

			foreach (var htmlFile in htmlFiles)
			{
				// Use the same byte-level encoding detection as the rest of the
				// pipeline (DetectEncoding.FromBytes) instead of letting
				// HtmlAgilityPack's doc.Load(path) apply its own detection, which can
				// produce different results for non-UTF-8 pages.
				var rawBytes = await File.ReadAllBytesAsync(htmlFile);
				var encoding = DetectEncoding.FromBytes(rawBytes);
				var htmlContent = encoding.GetString(rawBytes);

				var doc = new HtmlDocument();
				doc.LoadHtml(htmlContent);

				var titleNode = doc.DocumentNode.SelectSingleNode("//title");
				string titleValue = titleNode?.InnerText ?? string.Empty;
				int titleLength = titleValue.Length;

				var descriptionNode = doc.DocumentNode.SelectSingleNode("//meta[@name='description']");
				string descriptionValue = descriptionNode?.GetAttributeValue("content", string.Empty) ?? string.Empty;
				int descriptionLength = descriptionValue.Length;

				var robotsNode = doc.DocumentNode.SelectSingleNode("//meta[@name='robots']");
				string robotsValue = robotsNode?.GetAttributeValue("content", string.Empty) ?? string.Empty;

				var keywordsNode = doc.DocumentNode.SelectSingleNode("//meta[@name='keywords']");
				string keywordsValue = keywordsNode?.GetAttributeValue("content", string.Empty) ?? string.Empty;

				// H1 count (document-wide). Captured as raw integer so the findings
				// layer can distinguish 0 (missing → real structural defect) from >1
				// (multiple → template/structure signal; not a Google SEO error, but a
				// reliable tell for CMS/templating defects). SelectNodes returns null
				// when there are no matches, hence the ?? 0.
				int h1Count = doc.DocumentNode.SelectNodes("//h1")?.Count ?? 0;

				string filename = Path.GetFileName(htmlFile);
				string url = CrawlIndex.LookUpUrlForFile(filename);
				string source = CrawlIndex.LookUpSourceForFile(filename);

				await writer.WriteLineAsync(IssueLogWriter.ComposeLine(IssueLogWriter.SeoDelimiter,
					url, robotsValue, titleValue, titleLength.ToString(),
					descriptionValue, descriptionLength.ToString(), keywordsValue, source, h1Count.ToString()));
			}
		}
	}
}
