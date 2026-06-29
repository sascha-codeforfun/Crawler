namespace Crawler.Html
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using HtmlAgilityPack;

	/// <summary>
	/// The single source of truth for "which language(s) is this page declared to be in".
	/// Composes the two signals that used to be stitched separately at each call site
	/// (spelling, content-quality detection, content-quality triage), which had drifted
	/// out of sync — CQ resolved <c>&lt;html lang&gt;</c> alone and never applied the
	/// overrides. Resolution order:
	///
	///   1. <b>PageLanguageOverrides</b> — operator ground truth, by URL PATH PREFIX,
	///      longest key wins (lex specialis). Corrects pages whose markup lies about
	///      their language (e.g. a German page served under a Finnish path) and declares
	///      genuine multi-language pages.
	///   2. <b>&lt;html lang&gt;</b> then <b>&lt;meta name="language"&gt;</b> — the page's
	///      own declaration (via <see cref="Language.FromMeta"/>), html wins on conflict.
	///   3. <b>defaultLanguage</b> — the caller's floor.
	///
	/// Returns a SET. An empty set means "nothing declared" (no override, no html/meta,
	/// and an empty default) — callers that need a single language anchor (the quote
	/// system check) treat both the empty set and a multi-element set as "no single
	/// anchor" and fall back to structure-only checks. Spelling passes a real default so
	/// it always gets at least one language to pick a dictionary; the quote path passes
	/// an empty default so undeclared pages resolve to the empty set.
	///
	/// Pure: resolves, never raises defects. The html-vs-meta disagreement is a separate
	/// finding owned by <c>LanguageMismatch</c>.
	/// </summary>
	public static class PageLanguageSet
	{
		/// <summary>
		/// Core resolution from a pre-resolved branch language. Override (longest-prefix)
		/// wins; otherwise the branch language as a single-element set, or the empty set
		/// when the branch is blank (nothing declared and an empty default).
		/// </summary>
		public static IReadOnlyList<string> Resolve(
			string url,
			string branchLanguage,
			IReadOnlyDictionary<string, List<string>>? overrides)
		{
			if (overrides is { Count: > 0 })
			{
				string path = PathOf(url);
				string? bestKey = null;
				foreach (var key in overrides.Keys)
				{
					if (path.StartsWith(key, StringComparison.OrdinalIgnoreCase)
						&& (bestKey == null || key.Length > bestKey.Length))
					{
						bestKey = key;
					}
				}

				if (bestKey != null && overrides[bestKey] is { Count: > 0 } languages)
				{
					return languages.ToList();
				}
			}

			return string.IsNullOrWhiteSpace(branchLanguage)
				? Array.Empty<string>()
				: new[] { branchLanguage };
		}

		/// <summary>
		/// Resolves from a parsed document — branch language via <see cref="Language.FromMeta"/>
		/// (&lt;html lang&gt; → &lt;meta language&gt; → <paramref name="defaultLanguage"/>),
		/// then overrides.
		/// </summary>
		public static IReadOnlyList<string> Resolve(
			string url,
			HtmlDocument doc,
			IReadOnlyDictionary<string, List<string>>? overrides,
			string defaultLanguage)
			=> Resolve(url, Language.FromMeta(doc, defaultLanguage), overrides);

		/// <summary>
		/// Resolves from a downloaded HTML file (reads + parses the file, then as above).
		/// </summary>
		public static IReadOnlyList<string> Resolve(
			string url,
			string filename,
			string fileDownloadDirectory,
			IReadOnlyDictionary<string, List<string>>? overrides,
			string defaultLanguage)
			=> Resolve(url, Language.FromHtmlFile(filename, fileDownloadDirectory, defaultLanguage), overrides);

		/// <summary>
		/// True iff a PageLanguageOverrides prefix governs this URL — i.e. <see cref="Resolve(string,
		/// string, IReadOnlyDictionary{string, List{string}})"/> would return an override set rather
		/// than the branch language. Mirrors that method's branch exactly: longest matching key wins,
		/// and that key's language list must be non-empty. Callers use it to decide whether per-element
		/// lang resolution applies (it does not when an override governs — the operator's declaration
		/// is ground truth for the whole page).
		/// </summary>
		public static bool HasOverride(
			string url,
			IReadOnlyDictionary<string, List<string>>? overrides)
		{
			if (overrides is not { Count: > 0 })
			{
				return false;
			}

			string path = PathOf(url);
			string? bestKey = null;
			foreach (var key in overrides.Keys)
			{
				if (path.StartsWith(key, StringComparison.OrdinalIgnoreCase)
					&& (bestKey == null || key.Length > bestKey.Length))
				{
					bestKey = key;
				}
			}

			return bestKey != null && overrides[bestKey] is { Count: > 0 };
		}

		/// <summary>Strip scheme + host and query/fragment, leaving the path from the first '/'.</summary>
		private static string PathOf(string url)
		{
			if (string.IsNullOrEmpty(url))
			{
				return string.Empty;
			}

			int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
			int start = 0;
			if (schemeEnd >= 0)
			{
				int firstSlash = url.IndexOf('/', schemeEnd + 3);
				start = firstSlash >= 0 ? firstSlash : url.Length;
			}

			int end = url.Length;
			int q = url.IndexOf('?', start);
			int h = url.IndexOf('#', start);
			if (q >= 0)
			{
				end = q;
			}

			if (h >= 0 && h < end)
			{
				end = h;
			}

			return url.Substring(start, end - start);
		}
	}
}
