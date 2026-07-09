namespace Crawler.Boilerplate
{
	using System;
	using System.Collections.Generic;
	using System.Runtime.CompilerServices;
	using HtmlAgilityPack;

	/// <summary>
	/// One operator-declared boilerplate selector. The <see cref="Type"/> tag keeps each match
	/// rule unambiguous and footgun-free per case (see <see cref="BoilerplateMatcher"/>). A
	/// general tool cannot assume class is the marker — other sites use id, data-*, role, tag, or
	/// structure — so the typed form is axis-agnostic via the xpath escape hatch.
	/// </summary>
	public sealed record BoilerplateSelector(string Type, string Value);

	/// <summary>
	/// Decides whether a DOM node is (or is inside) operator-declared boilerplate, so the
	/// traversal can prune it on non-entry pages. This is a BLACKLIST by deliberate design (§4):
	/// the default is to check everything; only declared selectors are suppressed; anything
	/// undeclared stays checked and surfaces as visible noise rather than being silently
	/// swallowed.
	///
	/// Supported selector types:
	///   * "class" — value is one or more whitespace-separated class tokens. Matches iff EVERY
	///               token is present in the element's class set (subset / ".foo.bar" semantics),
	///               WHOLE-TOKEN and CASE-SENSITIVE (ordinal). Never substring (so "nav_footer"
	///               does not match "nav_footer2"), never exact-string (so multi-class
	///               "block_outer nav_footer" is matched correctly). Uses HtmlNode.GetClasses().
	///   * "xpath" — HAP-native SelectNodes; the axis-agnostic escape hatch for id / data-* /
	///               role / tag / structural boilerplate.
	///
	/// More types (id, role, data, css) can be added later behind the Type tag without disturbing
	/// existing config; an unknown type matches nothing (and should be flagged by the parked
	/// config-validation refinement).
	/// </summary>
	public sealed class BoilerplateMatcher
	{
		private readonly List<(string[] Tokens, bool IsClass)> _classSelectors = new();
		private readonly List<string> _xpathSelectors = new();

		// Per-document cache of resolved xpath matches. ResolveXpath runs a full-document
		// SelectNodes per selector; without this it was recomputed on every IsBoilerplate(node)
		// call (i.e. per node), making each page O(nodes²). Keyed by HtmlDocument so the work
		// happens once per page. ConditionalWeakTable is thread-safe and lets a document be
		// collected once its page is done, so the shared global-ignore matcher is safe across
		// the parallel page loop (each thread checks a distinct document → distinct key).
		private readonly ConditionalWeakTable<HtmlDocument, HashSet<HtmlNode>> _xpathCache = new();

		public BoilerplateMatcher(IEnumerable<BoilerplateSelector>? selectors)
		{
			if (selectors == null)
			{
				return;
			}

			foreach (var sel in selectors)
			{
				if (sel == null || string.IsNullOrWhiteSpace(sel.Value))
				{
					continue;
				}

				switch (sel.Type?.Trim().ToLowerInvariant())
				{
					case "class":
						var tokens = sel.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
						if (tokens.Length > 0)
						{
							_classSelectors.Add((tokens, true));
						}

						break;

					case "xpath":
						_xpathSelectors.Add(sel.Value.Trim());
						break;

					// Unknown type: matches nothing (kept deliberately silent here; surfacing
					// unknown/empty-match selectors is the parked config-validation refinement).
				}
			}
		}

		/// <summary>True if no selectors are declared — caller checks everything (fail-loud default).</summary>
		public bool IsEmpty => _classSelectors.Count == 0 && _xpathSelectors.Count == 0;

		/// <summary>
		/// True if <paramref name="node"/> itself, or any ancestor, matches a declared selector.
		/// A node inside a boilerplate subtree is boilerplate. xpath matches are resolved against
		/// the owning document once and cached as a node set per document.
		/// </summary>
		public bool IsBoilerplate(HtmlNode node)
		{
			HtmlNode? current = node;
			HashSet<HtmlNode>? xpathMatches = null;

			while (current != null && current.NodeType != HtmlNodeType.Document)
			{
				if (current.NodeType == HtmlNodeType.Element)
				{
					if (MatchesClass(current))
					{
						return true;
					}

					if (_xpathSelectors.Count > 0)
					{
						xpathMatches ??= ResolveXpath(current.OwnerDocument);
						if (xpathMatches.Contains(current))
						{
							return true;
						}
					}
				}

				current = current.ParentNode;
			}

			return false;
		}

		private bool MatchesClass(HtmlNode element)
		{
			if (_classSelectors.Count == 0)
			{
				return false;
			}

			// HtmlNode.GetClasses() returns the whitespace-split class tokens (case-sensitive).
			var classSet = new HashSet<string>(element.GetClasses(), StringComparer.Ordinal);
			if (classSet.Count == 0)
			{
				return false;
			}

			foreach (var (tokens, _) in _classSelectors)
			{
				bool all = true;
				foreach (var t in tokens)
				{
					if (!classSet.Contains(t))
					{
						all = false;
						break;
					}
				}

				if (all)
				{
					return true;
				}
			}

			return false;
		}

		private HashSet<HtmlNode> ResolveXpath(HtmlDocument? doc)
		{
			if (doc == null)
			{
				return new HashSet<HtmlNode>();
			}

			// Computed once per document, then reused across every node of that page.
			return _xpathCache.GetValue(doc, ComputeXpathMatches);
		}

		private HashSet<HtmlNode> ComputeXpathMatches(HtmlDocument doc)
		{
			var set = new HashSet<HtmlNode>();

			foreach (var xpath in _xpathSelectors)
			{
				HtmlNodeCollection? matches = null;
				try
				{
					matches = doc.DocumentNode.SelectNodes(xpath);
				}
				catch
				{
					// Malformed xpath matches nothing (would be surfaced by config validation).
				}

				if (matches != null)
				{
					foreach (var m in matches)
					{
						set.Add(m);
					}
				}
			}

			return set;
		}
	}
}
