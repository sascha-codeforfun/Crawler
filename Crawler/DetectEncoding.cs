namespace Crawler
{
	using System.Text;
	using System.Text.RegularExpressions;

	/// <summary>
	/// Byte-level character-encoding detection for HTML content, extracted from Tools.
	/// Detection order: UTF-8 BOM → UTF-16 BE BOM → UTF-16 LE BOM → meta-charset
	/// declaration in the first 4 KB → Windows-1252 fallback. Used across the pipeline
	/// (crawl, SEO extraction, sitemap, simplification) for consistent decoding.
	/// </summary>
	public static partial class DetectEncoding
	{
		// Extracts the value of a <meta charset=...> declaration from the head of an HTML document.
		// Used once per file by FromBytes — compile-time-generated for analyzer cleanliness
		// (SYSLIB1045) rather than runtime perf, since this is not a hot path.
		[GeneratedRegex(@"<meta[^>]*charset\s*=\s*[""']?(?<enc>[^'""\s;/>]+)", RegexOptions.IgnoreCase)]
		private static partial Regex MetaCharsetPattern();

		public static Encoding FromBytes(byte[] bytes)
		{
			if (bytes == null || bytes.Length == 0)
			{
				return Encoding.UTF8;
			}

			// BOM checks
			if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
			{
				return Encoding.UTF8;
			}

			if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
			{
				return Encoding.BigEndianUnicode;
			}

			if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
			{
				return Encoding.Unicode;
			}

			// Try to find meta charset in the first few KB
			string head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
			var m = MetaCharsetPattern().Match(head);
			if (m.Success)
			{
				try { return Encoding.GetEncoding(m.Groups["enc"].Value.Trim()); } catch { }
			}

			// Final fallback: Windows-1252 (covers common legacy pages)
			return Encoding.GetEncoding(1252);
		}
	}
}
