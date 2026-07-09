namespace Crawler.Lexicon
{
	using System.Collections.Generic;
	using WeCantSpell.Hunspell;

	public class Bundle
	{
		public WordList System { get; set; } = null!;
		public HashSet<string> SharedSite { get; set; } = new(StringComparer.OrdinalIgnoreCase);
		public HashSet<string> SharedUser { get; set; } = new(StringComparer.OrdinalIgnoreCase);

		// Foreign-language allow-list (user_foreign_languages.dic). A researched,
		// comment-justified archive of legitimate non-Latin-script / diacritic words that
		// the strict user/site policy rejects. CONSULT-ONLY: words here are accepted by
		// Check but are deliberately exempt from ALL maintenance — never recorded in
		// UsageTracker, never passed to orphan/prune analysis, never written from triage.
		// A legitimately rare foreign word must not be nagged for removal, which is why it
		// lives outside the cross-off machinery entirely.
		public HashSet<string> SharedForeign { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

			// D112: NFC-normalise the lookup query. The dictionaries (Hunspell System
			// plus the SharedUser/SharedSite lists) hold composed forms; page/script
			// content can arrive decomposed — e.g. "für" as u + U+0308 combining
			// diaeresis — which renders identically but mismatches a composed entry
			// byte-for-byte and would false-flag a valid word. Query-only: callers keep
			// the original token for the displayed miss/excerpt, so offsets are
			// untouched. IsNormalized() guards the hot path — already-composed tokens
			// (the vast majority) skip the allocation; only decomposed ones pay it.
			// (Parameterless Normalize()/IsNormalized() default to Form C, and avoid
			// the System.Text namespace clashing with this class's System member.)
			if (!word.IsNormalized())
			{
				word = word.Normalize();
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

			// Foreign-language allow-list: consult-only. Accepted like the other tiers but
			// NEVER recorded in UsageTracker — the foreign dictionary is exempt from
			// orphan/prune maintenance by design (see SharedForeign).
			if (SharedForeign.Contains(word))
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
