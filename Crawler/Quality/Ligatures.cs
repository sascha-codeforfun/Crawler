namespace Crawler.Quality
{
	internal static class Ligatures
	{
		private static readonly Dictionary<char, string> Table = new()
		{
			{ '\uFB00', "ff  (U+FB00)" },
			{ '\uFB01', "fi  (U+FB01)" },
			{ '\uFB02', "fl  (U+FB02)" },
			{ '\uFB03', "ffi (U+FB03)" },
			{ '\uFB04', "ffl (U+FB04)" },
			{ '\uFB05', "ſt  (U+FB05)" },
			{ '\uFB06', "st  (U+FB06)" },
		};

		internal static IEnumerable<QualityIssue> Check(
			string filename, string text, ContentQualityConfig config)
		{
			foreach (var (ch, name) in Table)
			{
				int pos = 0;
				while ((pos = text.IndexOf(ch, pos)) >= 0)
				{
					var context = Excerpt.Around(text, pos, config.ContentQualityExcerptRadius);
					yield return new QualityIssue(
						filename,
						"LIGATURE",
						$"Ligature {name}",
						context);
					pos++;
				}
			}
		}
	}
}
