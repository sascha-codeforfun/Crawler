namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using System.Text;

	/// <summary>
	/// Serializes the new pipeline's <see cref="WordTicket"/>s into text views for SIDE-BY-SIDE
	/// comparison against the preserved old-run logs. This is a bring-up / oracle-diff artifact,
	/// written to a SEPARATE location from the old store so old and new coexist on one crawl. It
	/// is deliberately not the eventual production persistence format — that is decided later,
	/// when the new path becomes primary.
	///
	/// Three views, two axes of comparison plus the payoff:
	///
	///   * SourcesView  — one line per page: "{url}|{word} ({lang})[ ({metaLabel})]|…", one entry
	///                    per DISTINCT word per page, meta-label suffix only for meta-sourced
	///                    words. Shaped to diff against the old per-page sources log. Regression
	///                    guard: real misspellings must persist; words that DISAPPEAR are the
	///                    intended suppressions of technical/attribute leakage and must each be
	///                    audited as correct.
	///
	///   * UniqueView   — globally unique "{word} ({lang})" lines, keyed by word+lang (the old log
	///                    lists the same word under two languages separately), sorted. A set-level
	///                    cross-check.
	///
	///   * LocatedView  — the net-new capability: per (word, url), EVERY occurrence with its
	///                    source label and excerpt. Nothing in the old logs to diff against; this
	///                    is the "where exactly" that ends the human hunt.
	///
	/// The meta-label suffix mirrors the old log, which labels ONLY meta sources (text-node and
	/// ordinary attribute occurrences carry no suffix there).
	/// </summary>
	public static class ParallelStoreWriter
	{
		/// <summary>Old-sources-comparable: per page, distinct words, meta-labelled only.</summary>
		public static string SourcesView(IEnumerable<WordTicket> tickets)
		{
			var sb = new StringBuilder();
			foreach (var t in tickets)
			{
				sb.Append(t.Url);

				// Distinct words already (one ticket per word+url). Within a ticket, surface a
				// meta label iff the word's occurrences include a meta source.
				string langs = string.IsNullOrEmpty(t.Languages) ? string.Empty : t.Languages;
				sb.Append('|').Append(t.Word).Append(" (").Append(langs).Append(')');

				var metaOccurrence = t.Occurrences.FirstOrDefault(o => o.Source == RunSource.Meta);
				if (metaOccurrence != null)
				{
					sb.Append(" (").Append(metaOccurrence.SourcePath).Append(')');
				}

				sb.Append('\n');
			}

			return sb.ToString();
		}

		/// <summary>Old-unique-comparable: globally unique word+lang, sorted.</summary>
		public static string UniqueView(IEnumerable<WordTicket> tickets)
		{
			var seen = new HashSet<string>(StringComparer.Ordinal);
			var lines = new List<string>();
			foreach (var t in tickets)
			{
				string line = $"{t.Word} ({t.Languages})";
				if (seen.Add(line))
				{
					lines.Add(line);
				}
			}

			lines.Sort(StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true));
			return string.Join("\n", lines) + (lines.Count > 0 ? "\n" : string.Empty);
		}

		/// <summary>Net-new located view: every occurrence with label + excerpt.</summary>
		public static string LocatedView(IEnumerable<WordTicket> tickets)
		{
			var sb = new StringBuilder();
			foreach (var t in tickets)
			{
				sb.Append(t.Word).Append(" (").Append(t.Languages).Append(") @ ").Append(t.Url).Append('\n');
				foreach (var o in t.Occurrences)
				{
					sb.Append("    ").Append(o.SourcePath).Append("  ").Append(o.Excerpt).Append('\n');
				}
			}

			return sb.ToString();
		}
	}
}
