namespace Crawler
{
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using WeCantSpell.Hunspell;

	public class DictionaryBundle
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
		/// <see cref="DictionaryUsageTracker"/>. This is a pure side effect — the return
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
				DictionaryUsageTracker.RecordUser(word);
			}

			if (inSite)
			{
				DictionaryUsageTracker.RecordSite(word);
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
		public static bool CheckAny(string word, IEnumerable<DictionaryBundle> bundles)
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

	/// <summary>
	/// Run-wide recorder for the cross-off dictionary-maintenance feature.
	/// <see cref="DictionaryBundle.Check"/> records every consultation that hits the
	/// SharedUser / SharedSite tiers here; at end of run a user/site dictionary entry
	/// that was never recorded is an orphan (never used on any crawled page).
	///
	/// Thread-safe: Check runs under the parallel spell scan, so hits are stored in
	/// ConcurrentDictionary-backed sets (used as sets; the value byte is ignored).
	/// Sets are OrdinalIgnoreCase so a token hit crosses off the
	/// dictionary entry regardless of case. <see cref="Reset"/> is called
	/// at the start of each run's spell-check so the recorder reflects only that run
	/// (and so per-site runs in one process do not leak usage between sites).
	/// </summary>
	public static class DictionaryUsageTracker
	{
		private static readonly ConcurrentDictionary<string, byte> _usedUser =
			new(StringComparer.OrdinalIgnoreCase);

		private static readonly ConcurrentDictionary<string, byte> _usedSite =
			new(StringComparer.OrdinalIgnoreCase);

		public static void RecordUser(string word) => _usedUser[word] = 0;

		public static void RecordSite(string word) => _usedSite[word] = 0;

		public static void Reset()
		{
			_usedUser.Clear();
			_usedSite.Clear();
		}

		/// <summary>Snapshot of user-dictionary words consulted so far (case-insensitive).</summary>
		public static HashSet<string> SnapshotUser() =>
			new(_usedUser.Keys, StringComparer.OrdinalIgnoreCase);

		/// <summary>Snapshot of site-dictionary words consulted so far (case-insensitive).</summary>
		public static HashSet<string> SnapshotSite() =>
			new(_usedSite.Keys, StringComparer.OrdinalIgnoreCase);
	}
}
