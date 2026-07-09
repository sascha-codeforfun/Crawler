using HtmlAgilityPack;

namespace Crawler.Quality
{
	internal static class LanguageMismatch
	{
		internal static IEnumerable<QualityIssue> Check(
			string filename, HtmlDocument doc)
		{
			var htmlNode = doc.DocumentNode.SelectSingleNode("//html");
			var htmlLang = htmlNode?.GetAttributeValue("lang", string.Empty)?.Trim();
			var metaNode = doc.DocumentNode.SelectSingleNode("//meta[@name='language']");
			var metaLang = metaNode?.GetAttributeValue("content", string.Empty)?.Trim();

			if (string.IsNullOrEmpty(htmlLang) || string.IsNullOrEmpty(metaLang))
			{
				yield break;
			}

			// Normalise to base language code (de-DE → de).
			var htmlCode = htmlLang.Split('-')[0].ToLowerInvariant();
			var metaCode = metaLang.Split('-')[0].ToLowerInvariant();

			if (!htmlCode.Equals(metaCode, StringComparison.OrdinalIgnoreCase))
			{
				yield return new QualityIssue(
					filename,
					"LANGUAGE_MISMATCH",
					$"<html lang=\"{htmlLang}\"> conflicts with <meta name=\"language\" content=\"{metaLang}\">",
					$"html lang wins for spell-checking — meta tag should be corrected to \"{htmlCode}\"");
			}
		}
	}
}
