namespace Crawler.SpellCheck
{
	using System;
	using Crawler.Lexicon;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>
	/// Resolves which language(s) a page is spell-checked against, applying the operator-declared
	/// <see cref="SpellCheckEngineConfig.PageLanguageOverrides"/> on top of the single branch-detected
	/// language. The override exists because some pages mix languages (or carry a branch language that
	/// is wrong for their content) and the site's markup cannot be trusted to declare it (no reliable
	/// lang attribute) — so the operator declares ground truth by URL path prefix.
	///
	/// Matching: the key is a URL PATH PREFIX (from the first slash, domain stripped), compared with
	/// <see cref="string.StartsWith(string, StringComparison)"/> using ordinal-ignore-case. When more
	/// than one key matches a path, the LONGEST key wins (lex specialis derogat legi generali) so a
	/// specific page override beats a section-wide one regardless of declaration order. A page that
	/// matches no key resolves to its single branch language.
	///
	/// The resolved set is checked as a UNION by <see cref="RunChecker"/>: a word is a miss only if
	/// every listed dictionary misses it. This is intentionally more permissive than a single-language
	/// check, so overrides should be declared narrowly — only for genuinely mixed pages.
	/// </summary>
	public static class PageLanguageResolver
	{
		/// <summary>
		/// Resolve the language set for <paramref name="url"/>. If a <paramref name="overrides"/> key is
		/// a prefix of the URL's path, the longest matching key's language list is returned (order
		/// preserved); otherwise a single-element list holding <paramref name="branchLanguage"/>.
		/// </summary>
		// Resolution moved to the Html layer (Crawler.Html.PageLanguageSet) so spelling
		// and content-quality share one resolver and cannot drift again. This forwards;
		// behaviour is identical for spelling (it always passes a non-empty branch
		// language, so the override-or-branch result is unchanged). ValidateBundles
		// stays here because it is spell-config-specific (needs loaded dictionary bundles).
		public static IReadOnlyList<string> Resolve(
			string url,
			string branchLanguage,
			IReadOnlyDictionary<string, List<string>>? overrides)
			=> Crawler.Html.PageLanguageSet.Resolve(url, branchLanguage, overrides);

		/// <summary>
		/// Fail-fast configuration check: every language named in any override entry MUST have a loaded
		/// dictionary bundle. A missing bundle is a misconfiguration (the operator declared a language
		/// the run cannot honour) and throws here — BEFORE any page is processed — rather than silently
		/// checking fewer languages than declared. Call this right after dictionaries are loaded.
		/// </summary>
		public static void ValidateBundles(
			IReadOnlyDictionary<string, List<string>>? overrides,
			IReadOnlyDictionary<string, Bundle> loadedBundles)
		{
			if (overrides == null || overrides.Count == 0)
			{
				return;
			}

			var missing = new List<string>();
			foreach (var (key, languages) in overrides)
			{
				foreach (var language in languages)
				{
					if (!loadedBundles.ContainsKey(language))
					{
						missing.Add($"'{language}' (declared for path '{key}')");
					}
				}
			}

			if (missing.Count > 0)
			{
				throw new InvalidOperationException(
					"PageLanguageOverrides references languages with no loaded dictionary bundle: "
					+ string.Join(", ", missing)
					+ ". Add the dictionary or correct the override.");
			}
		}

	}
}
