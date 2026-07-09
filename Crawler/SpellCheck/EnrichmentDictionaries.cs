namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;

	// Bridge that lets a spell enricher consult a loaded dictionary WITHOUT depending on
	// the pipeline that owns it. AnalysisPipeline registers every loaded bundle here by
	// LanguageCode after load; an enricher asks "is language X's dictionary loaded, and
	// does it accept this token?" via a delegate. This is the standard way a dictionary-
	// dependent enricher reaches the spell engine — the registry stays a stateless static
	// (the enrichers in it are pure functions of context + whatever this holder returns).
	//
	// The PRESENCE of a delegate is itself the runtime "is it configured?" signal: a
	// deployment with an `ar` dictionary gets one back and the Arabic enricher activates;
	// one without gets a miss and the enricher stays silent. No build-time assumption, no
	// human inspecting config — the code answers it per run.
	//
	// Single-writer (AnalysisPipeline at load) then read-only during triage, which is
	// single-threaded — so no synchronisation is needed. (The underlying Bundle.Check /
	// Hunspell is itself not thread-safe; triage consults it serially.)
	public static class EnrichmentDictionaries
	{
		private static readonly Dictionary<string, Func<string, bool>> Accepts =
			new(StringComparer.OrdinalIgnoreCase);

		// Register (or overwrite) the accept-check for a language. Idempotent, so a
		// re-run of the pipeline simply replaces the delegate.
		public static void Register(string languageCode, Func<string, bool> accepts)
		{
			if (!string.IsNullOrWhiteSpace(languageCode) && accepts != null)
			{
				Accepts[languageCode] = accepts;
			}
		}

		// Exact-code lookup (e.g. "ar").
		public static bool TryGet(string languageCode, out Func<string, bool> accepts) =>
			Accepts.TryGetValue(languageCode, out accepts!);

		// Family lookup: exact base (e.g. "ar") OR any regional variant ("ar-SA", …),
		// so the gate is robust to deployments that key dictionaries by locale. Returns
		// the first match; callers use this for "is language X available at all".
		public static bool TryResolveFamily(string baseLanguage, out Func<string, bool> accepts)
		{
			if (Accepts.TryGetValue(baseLanguage, out accepts!))
			{
				return true;
			}

			foreach (var kv in Accepts)
			{
				if (kv.Key.StartsWith(baseLanguage + "-", StringComparison.OrdinalIgnoreCase))
				{
					accepts = kv.Value;
					return true;
				}
			}

			accepts = null!;
			return false;
		}

		// Test-isolation only: drop all registrations.
		public static void Clear() => Accepts.Clear();
	}
}
