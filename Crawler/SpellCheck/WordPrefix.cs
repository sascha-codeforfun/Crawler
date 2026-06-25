namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>
	/// Strips configured brand/compound prefixes from a hyphenated token before
	/// dictionary lookup, recursively, longest prefix first. Returns null when the
	/// token exactly matches a prefix (caller accepts the word without lookup).
	/// Used by the spell checker and by Audit to identify redundant user
	/// dictionary entries covered by prefix stripping.
	/// Examples with prefixes ["BRAND-Suite", "BRAND", "Suite"]:
	///   "BRAND-Suite-Pro" → "Pro"; "BRAND-Suite" → null; "normalword" → "normalword".
	/// </summary>
	public static class WordPrefix
	{
		// [KEEP] Seam: brand/compound prefixes are an APPLICATION-CONFIG concept, not a
		// dictionary one. Operators declare prefixes (e.g. "BRAND") in config; this strips
		// "BRAND-word" → "word" so the dictionary only ever sees the real word. It is the
		// boundary between operator config and the spelling substrate, and belongs on the
		// spelling side. Do not silently drop this note; it marks a config/substrate boundary.
		public static string? Strip(string word, IReadOnlyList<string> prefixes)
		{
			if (prefixes.Count == 0 || !word.Contains('-'))
			{
				return word;
			}

			// Sort longest prefix first so compound entries like "BRAND-Suite" are
			// tried before their shorter components "BRAND" and "Suite".
			var sorted = prefixes
				.Where(p => !string.IsNullOrEmpty(p))
				.OrderByDescending(p => p.Length);

			foreach (var prefix in sorted)
			{
				// Exact match — the token IS the prefix, nothing left to spell-check.
				if (word.Equals(prefix, StringComparison.OrdinalIgnoreCase))
				{
					return null;
				}

				var candidate = prefix + "-";
				if (word.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
				{
					var remainder = word[candidate.Length..];
					if (string.IsNullOrEmpty(remainder))
					{
						return null; // prefix with trailing hyphen but nothing after
					}

					// Recurse — the remainder may itself start with another prefix.
					return Strip(remainder, prefixes);
				}
			}

			return word;
		}
	}
}
