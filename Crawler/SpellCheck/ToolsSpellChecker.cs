namespace Crawler.SpellCheck
{
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>
	/// Production adapter: exposes the existing <c>Tools.CheckSpelling</c> as a
	/// <see cref="RunCheck"/> delegate, VERBATIM. No spell logic is reimplemented here — the
	/// LibreOffice dic/aff + user dictionary + site dictionary + prefix-strip + fugenelemente all
	/// come from the same call the current pipeline makes.
	///
	/// This is the single thin seam coupling the new module to the existing checker. When the
	/// spell members eventually leave Tools.cs for their proper home, only this adapter is
	/// repointed; the rest of the new module is unaffected.
	///
	/// Cross-language handling is NOT delegated to any region-based mechanism: the new module's
	/// union over a page's declared PageLanguageOverrides languages (see RunChecker) is the sole
	/// cross-language mechanism by design. The regions an "accept against any loaded dictionary"
	/// path once covered are now either boilerplate-pruned (chrome/cookie banners) or handled by
	/// declaring the page's languages.
	///
	/// The bundles, prefixes and fugenelemente are supplied by the caller (sourced exactly as the
	/// pipeline sources them) — this adapter does not load or invent dictionary data.
	/// </summary>
	public sealed class ToolsSpellChecker
	{
		private readonly DictionaryBundle _bundle;
		private readonly Dictionary<string, DictionaryBundle> _allBundles;
		private readonly IReadOnlyList<string> _prefixesToStrip;
		private readonly IReadOnlyList<string> _fugenelemente;

		public ToolsSpellChecker(
			DictionaryBundle bundle,
			Dictionary<string, DictionaryBundle> allBundles,
			IReadOnlyList<string> prefixesToStrip,
			IReadOnlyList<string> fugenelemente)
		{
			_bundle = bundle;
			_allBundles = allBundles;
			_prefixesToStrip = prefixesToStrip;
			_fugenelemente = fugenelemente;
		}

		/// <summary>The <see cref="RunCheck"/> delegate to hand to <see cref="RunChecker"/>.</summary>
		public IEnumerable<CheckMiss> Check(string canonicalRunText, string language)
		{
			// Use the requested language's bundle as the primary. For a single-language page this is
			// the same bundle passed at construction; for a multi-language (union) page RunChecker calls
			// this once per language, so the primary must follow the language argument rather than the
			// fixed constructor bundle. Falls back to the constructor bundle if the language is absent.
			DictionaryBundle primary = _allBundles.TryGetValue(language, out var b) ? b : _bundle;

			return Tools.CheckSpelling(
					canonicalRunText,
					primary,
					language,
					_prefixesToStrip,
					_fugenelemente)
				.Select(kvp => new CheckMiss(kvp.Key, string.Join(",", kvp.Value)));
		}
	}
}
