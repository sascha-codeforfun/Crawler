namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;

	/// <summary>
	/// 647 — the reverse index that makes a JS-file finding LOCATABLE: which pages load a given
	/// script bundle. A finding's bundle is the artifact; its actionability is "and that bundle is
	/// on these N pages" — one page is a ten-minute fix, all pages is a priority conversation, and
	/// only the reach tells them apart. The crawler already saw every page's &lt;script src&gt; when
	/// it downloaded; this inverts that page→scripts relation into script→pages.
	///
	/// Keys are <see cref="ScriptUrlKey.StableKey"/> (hash-stripped path), so a bundle re-deployed
	/// under a new fingerprint still maps to the same key and the same page set.
	///
	/// REACH/FAN-OUT vs DRIFT — deliberate scope boundary: this index answers "how many pages load
	/// this bundle" (reach) and could answer "does this word span multiple bundles" (fan-out). It
	/// does NOT attempt to detect cross-copy DRIFT — the same component embedded as independent
	/// copies on many pages, one updated and the others not. Drift is a similarity/diff problem
	/// between copies, not a reach problem, and it must NOT be inferred from spelling asymmetry (a
	/// word flagging in one copy but not another); that inference is unsound and would manufacture
	/// false "you forgot to update the other copy" findings. Drift, if ever pursued, is a separate
	/// near-duplicate-bundle pass — not this index.
	/// </summary>
	public sealed class ScriptPageIndex
	{
		private readonly IReadOnlyDictionary<string, HashSet<string>> keyToPages;

		private ScriptPageIndex(IReadOnlyDictionary<string, HashSet<string>> keyToPages)
		{
			this.keyToPages = keyToPages;
		}

		/// <summary>Distinct pages that load the bundle identified by <paramref name="stableKey"/>.</summary>
		public int Reach(string stableKey) =>
			keyToPages.TryGetValue(stableKey, out var pages) ? pages.Count : 0;

		/// <summary>The page URLs loading the bundle, sorted for stable output. Empty if none/unknown.</summary>
		public IReadOnlyList<string> Pages(string stableKey) =>
			keyToPages.TryGetValue(stableKey, out var pages)
				? pages.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
				: Array.Empty<string>();

		/// <summary>Number of distinct bundle keys referenced across all pages (diagnostics).</summary>
		public int KeyCount => keyToPages.Count;

		/// <summary>
		/// Testable core: build from already-resolved (pageUrl, html-bytes) pairs. Each page's
		/// &lt;script src&gt; is resolved relative to its own URL, reduced to a stable key, and
		/// inverted to key→{pages}. Operates on already-DECODED HTML, so it carries no dependency on
		/// byte→encoding detection (an app-global concern) — that lives only in the disk path below.
		/// Pure; no disk, no Cache.
		/// </summary>
		public static ScriptPageIndex Build(IEnumerable<(string PageUrl, string Html)> pages)
		{
			var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

			foreach (var (pageUrl, html) in pages)
			{
				if (string.IsNullOrWhiteSpace(pageUrl) || string.IsNullOrWhiteSpace(html))
				{
					continue;
				}

				if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri))
				{
					continue; // a page we cannot resolve a base URL for cannot anchor relative srcs
				}

				HtmlAgilityPack.HtmlDocument doc;
				try
				{
					doc = new HtmlAgilityPack.HtmlDocument();
					doc.LoadHtml(html);
				}
				catch
				{
					continue; // unparseable page — skip, never abort the index build
				}

				AddScriptsFromDoc(doc, pageUrl, baseUri, map);
			}

			return new ScriptPageIndex(map);
		}

		/// <summary>
		/// Shared core: pull every external &lt;script src&gt; from a parsed page, resolve each against
		/// the page's base URL, reduce to a stable key, and record page→key in the inverted map.
		/// </summary>
		private static void AddScriptsFromDoc(
			HtmlAgilityPack.HtmlDocument doc, string pageUrl, Uri baseUri,
			Dictionary<string, HashSet<string>> map)
		{
			var scripts = doc.DocumentNode.SelectNodes("//script");
			if (scripts == null)
			{
				return;
			}

			foreach (var s in scripts)
			{
				string src = s.GetAttributeValue("src", string.Empty);
				if (string.IsNullOrWhiteSpace(src))
				{
					continue; // inline <script> has no src — not a file reference
				}

				// Resolve the (possibly relative) src against the page URL, then reduce to the
				// hash-stripped stable key.
				string abs;
				try
				{
					abs = new Uri(baseUri, src).ToString();
				}
				catch
				{
					continue;
				}

				string key = ScriptUrlKey.StableKey(abs);
				if (key.Length == 0)
				{
					continue;
				}

				if (!map.TryGetValue(key, out var set))
				{
					set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					map[key] = set;
				}

				set.Add(pageUrl);
			}
		}

		/// <summary>
		/// Disk convenience used by the pipeline: enumerate saved page documents, resolve each to its
		/// page URL via <paramref name="fileToUrl"/> (the authoritative 02-crawler-index lookup), and
		/// parse via the canonical <see cref="DomTraverser.Parse(byte[])"/> (byte→encoding detection
		/// lives here, the app's domain). A page whose URL cannot be resolved or whose bytes cannot be
		/// read/parsed is skipped; the count of such skips is reported so a gap is visible, not silent.
		/// </summary>
		public static ScriptPageIndex BuildFromDownload(
			string downloadDirectory,
			string pagePattern,
			Func<string, string> fileToUrl,
			out int pagesIndexed,
			out int pagesUnresolved)
		{
			pagesIndexed = 0;
			pagesUnresolved = 0;

			var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

			if (string.IsNullOrEmpty(downloadDirectory) || !Directory.Exists(downloadDirectory)
				|| string.IsNullOrEmpty(pagePattern))
			{
				return new ScriptPageIndex(map);
			}

			int indexed = 0;
			int unresolved = 0;

			foreach (var file in Directory.EnumerateFiles(downloadDirectory, pagePattern, SearchOption.AllDirectories))
			{
				string pageUrl;
				try
				{
					pageUrl = fileToUrl(Path.GetFileName(file));
				}
				catch
				{
					pageUrl = string.Empty;
				}

				if (string.IsNullOrWhiteSpace(pageUrl) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri))
				{
					unresolved++;
					continue;
				}

				HtmlAgilityPack.HtmlDocument doc;
				try
				{
					doc = DomTraverser.Parse(File.ReadAllBytes(file));
				}
				catch
				{
					unresolved++;
					continue;
				}

				AddScriptsFromDoc(doc, pageUrl, baseUri, map);
				indexed++;
			}

			pagesIndexed = indexed;
			pagesUnresolved = unresolved;
			return new ScriptPageIndex(map);
		}
	}
}
