namespace Crawler.Lexicon
{
	using System.Collections.Generic;
	using WeCantSpell.Hunspell;

	public class Bundle
	{
		public WordList System { get; set; } = null!;
		public HashSet<string> SharedSite { get; set; } = new(StringComparer.OrdinalIgnoreCase);
		public HashSet<string> SharedUser { get; set; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Check a word against this bundle's SharedUser, SharedSite, and System dictionaries.
		/// Returns true (accepted) if the word is found in any of the three tiers.
		///
		/// Side effect (cross-off dictionary maintenance): when the word is found in the
		/// SharedUser and/or SharedSite tier, that consultation is recorded in
		/// <see cref="UsageTracker"/>. This is a pure side effect — the return
		/// value is computed exactly as before, so spell-check behaviour and all spell
		/// logs are unchanged. Both tiers are probed for recording even though the
		/// answer short-circuits, so a word present in both dictionaries crosses off
		/// both entries (otherwise the site entry would look orphaned).
		/// </summary>
		public bool Check(string word)
		{
			if (string.IsNullOrWhiteSpace(word))
			{
				return true;
			}

			bool inUser = SharedUser.Contains(word);
			bool inSite = SharedSite.Contains(word);

			if (inUser)
			{
				UsageTracker.RecordUser(word);
			}

			if (inSite)
			{
				UsageTracker.RecordSite(word);
			}

			if (inUser || inSite)
			{
				return true;
			}

			if (System == null)
			{
				return false;
			}

			return System.Check(word);
		}

		/// <summary>
		/// Check a word against a collection of bundles from multiple languages.
		/// Returns true if the word is accepted by ANY of the provided bundles.
		/// Used where a word may legitimately be in any of several languages (e.g. multi-language
		/// page checks and prefix-stripped stem lookups).
		/// </summary>
		public static bool CheckAny(string word, IEnumerable<Bundle> bundles)
		{
			if (string.IsNullOrWhiteSpace(word))
			{
				return true;
			}

			foreach (var bundle in bundles)
			{
				if (bundle.Check(word))
				{
					return true;
				}
			}
			return false;
		}
	}
}
