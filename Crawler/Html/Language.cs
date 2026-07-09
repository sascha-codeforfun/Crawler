namespace Crawler.Html
{
	using System.Diagnostics.CodeAnalysis;
	using System.IO;
	using System.Text;
	using HtmlAgilityPack;
	using System.Text.RegularExpressions;

	/// <summary>
	/// Resolves the page language for a downloaded HTML file or a parsed document,
	/// extracted from Tools. FromHtmlFile is the file-reading wrapper;
	/// FromMeta carries the resolution logic: &lt;html lang&gt; attribute
	/// first, then &lt;meta name="language"&gt;, then the fallback language.
	/// </summary>
	public static partial class Language
	{
		[ExcludeFromCodeCoverage(Justification =
			"Thin wrapper: reads file → calls FromMeta. FromMeta " +
			"carries the real logic and is tested directly.")]
		public static string FromHtmlFile(string filename, string fileDownloadDirectory, string fallbackLanguage)
		{
			var source = Path.Combine(fileDownloadDirectory, filename);
			var doc = new HtmlDocument();
			doc.LoadHtml(File.ReadAllText(source, Encoding.UTF8));
			return FromMeta(doc, fallbackLanguage);
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
		public static string FromMeta(HtmlDocument doc, string fallback)
		{
			// Prefer <html lang="..."> — most reliable signal.
			var htmlNode = doc.DocumentNode.SelectSingleNode("//html");
			var htmlLang = htmlNode?.GetAttributeValue("lang", string.Empty)?.Trim();
			if (!string.IsNullOrWhiteSpace(htmlLang))
			{
				// Normalise "de-DE" → "de" etc.
				var code = htmlLang.Split('-')[0].ToLowerInvariant();
				if (IsISO6391(code))
				{
					return code;
				}
			}

			// Fall back to <meta name="language">.
			var langNode = doc.DocumentNode.SelectSingleNode("//meta[@name='language']");
			if (langNode != null)
			{
				var metaLang = langNode.GetAttributeValue("content", string.Empty);
				if (!string.IsNullOrWhiteSpace(metaLang) && IsISO6391(metaLang))
				{
					return metaLang;
				}
			}

			return fallback;
		}

		internal static bool IsISO6391(string language)
		{
			return ISO6391().IsMatch(language);
		}

		/// <summary>
		/// Resolves the language declared CLOSEST to <paramref name="node"/> by walking up the
		/// ancestor chain and returning the first valid <c>lang</c> attribute — child overrides
		/// ancestor, per HTML inheritance — normalised "de-DE" → "de". Falls back to
		/// <paramref name="fallback"/> when no ancestor declares a usable lang (the page-level
		/// branch language, itself already resolved from &lt;html lang&gt; / meta / default, so a
		/// node with no nearer island still lands on the page language). Honours per-element lang
		/// islands so a word is checked against the dictionary for the language it is written in.
		/// </summary>
		public static string NearestElementLanguage(HtmlNode? node, string fallback)
		{
			for (HtmlNode? current = node;
				current != null && current.NodeType != HtmlNodeType.Document;
				current = current.ParentNode)
			{
				if (current.NodeType != HtmlNodeType.Element)
				{
					continue;
				}

				var raw = current.GetAttributeValue("lang", string.Empty)?.Trim();
				if (!string.IsNullOrWhiteSpace(raw))
				{
					var code = raw.Split('-')[0].ToLowerInvariant();
					if (IsISO6391(code))
					{
						return code;
					}
				}
			}

			return fallback;
		}

		[GeneratedRegex(@"^[a-z]{2}$")]
		private static partial Regex ISO6391();
	}
}
