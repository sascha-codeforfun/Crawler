namespace Crawler
{
	using HtmlAgilityPack;

	public static class Spell
	{
		/// <summary>
		/// Removes configured tags and attributes from the document before spell-checking.
		/// The mixed-language whitelist branch (isMixed / TagsWithAttributeToKeepWhenMixed)
		/// has been retired. Use CrossLanguageRegions, SpellCheckExclusionXPaths, or
		/// PageLanguageOverrides instead.
		///
		/// Performance: the previous implementation walked the full
		/// document tree once PER configured attribute (e.g. ~316 walks per file with
		/// the typical configuration). This version builds the attribute set once and
		/// performs a single tree walk, removing every matching attribute on each node
		/// in one visit. The end state is identical because attribute removal is
		/// idempotent and order-independent.
		/// </summary>
		public static HtmlDocument RemoveTagsAndAttributes(
			HtmlDocument doc,
			List<string> tagsToRemoveBeforeSpellCheck,
			List<string> attributesToRemoveBeforeSpellCheck
		)
		{
			foreach (string tag in tagsToRemoveBeforeSpellCheck)
			{
				var nodes = doc.DocumentNode.SelectNodes($"//{tag}");
				if (nodes != null)
				{
					foreach (var node in nodes.ToList())
					{
						node.Remove();
					}
				}
			}

			if (attributesToRemoveBeforeSpellCheck.Count == 0)
			{
				return doc;
			}

			// HAP attribute names are case-insensitive — use ordinal-insensitive lookup.
			var attrsToRemove = new HashSet<string>(
				attributesToRemoveBeforeSpellCheck,
				StringComparer.OrdinalIgnoreCase);

			foreach (var node in doc.DocumentNode.Descendants())
			{
				if (!node.HasAttributes)
				{
					continue;
				}

				// Collect names first; we cannot enumerate and mutate the attribute
				// collection in the same pass. Common case: zero or one attribute
				// removed per node, so the per-node list stays tiny.
				List<string>? toRemove = null;
				foreach (var attr in node.Attributes)
				{
					if (!attrsToRemove.Contains(attr.Name))
					{
						continue;
					}

					// Preserve the "name" attribute on <meta> elements — it is needed by
					// ExtractTextForSpellCheck to decide whether the content value of that
					// meta tag should be spell-checked (MetaContentNamesToSpellCheck whitelist).
					// Stripping it here causes GetAttributeValue("name") to always return
					// empty string, making the whitelist check silently skip all meta content.
					if (attr.Name.Equals("name", StringComparison.OrdinalIgnoreCase)
						&& node.Name.Equals("meta", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					toRemove ??= [];
					toRemove.Add(attr.Name);
				}

				if (toRemove is null)
				{
					continue;
				}

				foreach (var name in toRemove)
				{
					node.Attributes.Remove(name);
				}
			}

			return doc;
		}
	}
}
