namespace Crawler.Lexicon
{
	using System.Collections.Concurrent;
	using System.Collections.Generic;

	/// <summary>
	/// Run-wide recorder for the cross-off dictionary-maintenance feature.
	/// <see cref="Bundle.Check"/> records every consultation that hits the
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
	public static class UsageTracker
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
