namespace Crawler.Lexicon
{
	using System;
	using System.Diagnostics.CodeAnalysis;
	using System.IO;
	using System.Text;
	using WeCantSpell.Hunspell;

	/// <summary>
	/// Loads the three dictionary tiers — the system .dic/.aff via Hunspell, plus the
	/// site and user plain-word files — into a populated <see cref="Bundle"/>
	/// so Check() works across all tiers. Seed of the Lexicon module: the dictionary
	/// substrate lives here so a future format change (Hunspell today) stays localised.
	/// Extracted from Tools.
	/// </summary>
	public static class Loader
	{
		[ExcludeFromCodeCoverage(Justification =
			"Filesystem read + Hunspell WordList construction. Testing requires real " +
			"Hunspell dictionary fixtures and primarily exercises Hunspell, not Tools.")]
		public static Bundle Load(
			string dictionaryFileName,
			string affFileName,
			string customDictionaryFile,
			string customSiteDictionaryFile,
			string foreignDictionaryFile = "",
			bool silent = false)
		{
			var systemWordList = WordList.CreateFromFiles(dictionaryFileName, affFileName);
			var bundle = new Bundle { System = systemWordList };

			if (!string.IsNullOrEmpty(customDictionaryFile) && File.Exists(customDictionaryFile))
			{
				CharacterValidator.ValidateDictionaryFileHalt(customDictionaryFile, silent);

				foreach (var raw in File.ReadLines(customDictionaryFile, Encoding.UTF8))
				{
					if (string.IsNullOrWhiteSpace(raw))
					{
						continue;
					}

					var line = raw.Trim().TrimStart('!'); // strip pin marker before spell-check
					if (line.Contains('/'))
					{
						continue;
					}

					bundle.SharedUser.Add(line);
				}
			}

			if (!string.IsNullOrEmpty(customSiteDictionaryFile) && File.Exists(customSiteDictionaryFile))
			{
				CharacterValidator.ValidateDictionaryFileHalt(customSiteDictionaryFile, silent);

				foreach (var raw in File.ReadLines(customSiteDictionaryFile, Encoding.UTF8))
				{
					if (string.IsNullOrWhiteSpace(raw))
					{
						continue;
					}

					var line = raw.Trim().TrimStart('!'); // strip pin marker before spell-check
					if (line.Contains('/'))
					{
						continue;
					}

					bundle.SharedSite.Add(line);
				}
			}

			if (!string.IsNullOrEmpty(foreignDictionaryFile) && File.Exists(foreignDictionaryFile))
			{
				CharacterValidator.ValidateForeignDictionaryFileHalt(foreignDictionaryFile, silent);

				foreach (var word in ReadForeignDictionaryWords(foreignDictionaryFile))
				{
					bundle.SharedForeign.Add(word);
				}
			}

			return bundle;
		}

		// Parse a foreign-dictionary file into its bare words. Strips a // comment
		// (whole-line or trailing) and a leading ! pin via CharacterValidator, then skips
		// pure-comment / blank / affix-flag (/) lines. The // strip runs BEFORE the /-skip
		// so a commented word isn't mistaken for an affix line and dropped. Extracted from
		// Load so the parse is testable without a System dictionary.
		internal static List<string> ReadForeignDictionaryWords(string filePath)
		{
			var words = new List<string>();
			if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
			{
				return words;
			}

			foreach (var raw in File.ReadLines(filePath, Encoding.UTF8))
			{
				var word = CharacterValidator.ForeignDictionaryWord(raw);
				if (string.IsNullOrEmpty(word) || word.Contains('/'))
				{
					continue;
				}

				words.Add(word);
			}

			return words;
		}
	}
}
