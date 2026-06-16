namespace Crawler
{
	using System.Text.RegularExpressions;

	public static partial class RegExPatterns
	{
		public static string NormalizeHtmlTags(string html)
		{
			return HtmlNormalize().Replace(html.Replace("\t", " "), "> <");
		}

		[GeneratedRegex(@"><\s*(?!=)", RegexOptions.Compiled)]
		private static partial Regex HtmlNormalize();

		// ----------

		public static IEnumerable<string> TokenizeText(string text)
		{
			return Tokenizer().Matches(text).Cast<Match>().Select(m => m.Value);
		}

		[GeneratedRegex(@"\b[\w'äöüßÄÖÜ-]+(?:\s*-\s*[\w'äöüßÄÖÜ]+)*\b|[.,!?;:()""[\]<>]", RegexOptions.Compiled)]
		private static partial Regex Tokenizer();

		// ----------

		public static string RemoveUrls(string text)
		{
			return Url().Replace(text, string.Empty);
		}

		[GeneratedRegex(@"https?://[^\s]*|www\.[^\s]+", RegexOptions.Compiled)]
		private static partial Regex Url();

		// ----------

		public static bool IdentifyWord(string token)
		{
			return WordIdentifier().IsMatch(token);
		}

		[GeneratedRegex(@"^[a-zA-ZäöüßÄÖÜ-]+(-[a-zA-ZäöüßÄÖÜ]+)*$", RegexOptions.Compiled)]
		private static partial Regex WordIdentifier();

		// ----------

		public static bool IsISO6391(string language)
		{
			return ISO6391().IsMatch(language);
		}

		[GeneratedRegex(@"^[a-z]{2}$")]
		private static partial Regex ISO6391();

		// ----------

		/// <summary>
		/// Validates a crawl FilePattern: must be a glob of the form "*.ext" where
		/// ext is 1-8 letters/digits (e.g. "*.html", "*.htm", "*.aspx"). The tool
		/// is generic across sites whose pages may use different extensions, so the
		/// extension comes from config — but it flows straight into
		/// Directory.GetFiles/EnumerateFiles at many sites, so a malformed value
		/// (no "*.", a bare "*", path characters, or an implausibly long extension)
		/// must be rejected at config validation rather than silently matching
		/// nothing (a non-glob like "html" is treated by GetFiles as a literal
		/// filename and matches no files, with no error).
		/// </summary>
		public static bool IsValidFilePattern(string pattern)
		{
			return FilePatternGlob().IsMatch(pattern);
		}

		[GeneratedRegex(@"^\*\.[A-Za-z0-9]{1,8}$")]
		private static partial Regex FilePatternGlob();
	}
}
