namespace Crawler
{
	using System.Diagnostics.CodeAnalysis;
	using System.IO;
	using System.Text;
	using HtmlAgilityPack;

	/// <summary>
	/// Resolves the page language for a downloaded HTML file or a parsed document,
	/// extracted from Tools. GetLanguageFromHtmlFile is the file-reading wrapper;
	/// GetLanguageFromMeta carries the resolution logic: &lt;html lang&gt; attribute
	/// first, then &lt;meta name="language"&gt;, then the fallback language.
	/// </summary>
	public static class HtmlLanguage
	{
		[ExcludeFromCodeCoverage(Justification =
			"Thin wrapper: reads file → calls GetLanguageFromMeta. GetLanguageFromMeta " +
			"carries the real logic and is tested directly.")]
		public static string GetLanguageFromHtmlFile(string filename, string fileDownloadDirectory, string fallbackLanguage)
		{
			var source = Path.Combine(fileDownloadDirectory, filename);
			var doc = new HtmlDocument();
			doc.LoadHtml(File.ReadAllText(source, Encoding.UTF8));
			return GetLanguageFromMeta(doc, fallbackLanguage);
		}

		/// <summary>
		/// Resolves the page language from the HTML document.
		/// Priority:
		///   1. &lt;html lang="..."&gt; — most authoritative, used by browsers and screen readers
		///   2. &lt;meta name="language" content="..."&gt; — CMS-set, sometimes incorrect
		///   3. fallback language
		/// When both are present and disagree, the html lang attribute wins. The mismatch
		/// is detected separately by ContentQuality.Analyse and reported as LANGUAGE_MISMATCH.
		/// </summary>
		public static string GetLanguageFromMeta(HtmlDocument doc, string fallback)
		{
			// Prefer <html lang="..."> — most reliable signal.
			var htmlNode = doc.DocumentNode.SelectSingleNode("//html");
			var htmlLang = htmlNode?.GetAttributeValue("lang", string.Empty)?.Trim();
			if (!string.IsNullOrWhiteSpace(htmlLang))
			{
				// Normalise "de-DE" → "de" etc.
				var code = htmlLang.Split('-')[0].ToLowerInvariant();
				if (RegExPatterns.IsISO6391(code))
				{
					return code;
				}
			}

			// Fall back to <meta name="language">.
			var langNode = doc.DocumentNode.SelectSingleNode("//meta[@name='language']");
			if (langNode != null)
			{
				var metaLang = langNode.GetAttributeValue("content", string.Empty);
				if (!string.IsNullOrWhiteSpace(metaLang) && RegExPatterns.IsISO6391(metaLang))
				{
					return metaLang;
				}
			}

			return fallback;
		}
	}
}
