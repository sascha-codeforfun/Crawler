namespace Crawler.SpellCheck
{
	using System.Collections.Generic;
	using System.Text;

	/// <summary>
	/// Verbatim diagnostic dump of the spell engine's in-memory <see cref="WordTicket"/>s — the
	/// "what was flagged and where" paper trail (log 14). Written on EVERY harvest run so that when
	/// a spell result looks wrong there is a faithful record of exactly what the engine produced,
	/// independent of whether anyone triages.
	///
	/// Deliberately dumb: it prints every field of every ticket and every occurrence exactly as the
	/// engine produced them — no joining, no windowing, no selection, and NO sanitisation. In
	/// particular embedded delimiter-like characters in an excerpt (a '|', a slash) are written
	/// through unchanged; this is a record to read, never a feed anything parses back, so there is
	/// nothing to recover and nothing to corrupt. (This is the opposite of a lossy pipe-flattened
	/// view: faithfulness is the whole point of a diagnostic.)
	///
	/// Excerpts are single-line by construction (Canonicalizer collapses all whitespace to single
	/// spaces and trims), so one occurrence is one line with no ambiguity.
	///
	/// Layout, per ticket:
	///   Word (Languages) @ Url
	///     [Source] SourcePath  Excerpt
	///     [Source] SourcePath  Excerpt
	/// with a blank line between tickets. Empty input → empty string (matching the sibling views).
	/// </summary>
	public static class WordTicketsDiagnostic
	{
		public static string Compose(IReadOnlyList<WordTicket> tickets)
		{
			if (tickets == null || tickets.Count == 0)
			{
				return string.Empty;
			}

			var sb = new StringBuilder();
			bool first = true;
			foreach (var t in tickets)
			{
				// Blank line between tickets (the previous ticket's last line already ended in '\n',
				// so one extra '\n' before a subsequent header yields a single separating blank line).
				if (!first)
				{
					sb.Append('\n');
				}

				first = false;

				sb.Append(t.Word).Append(" (").Append(t.Languages).Append(") @ ").Append(t.Url).Append('\n');

				foreach (var o in t.Occurrences)
				{
					sb.Append("  [").Append(o.Source.ToString()).Append("] ")
						.Append(o.SourcePath).Append("  ").Append(o.Excerpt).Append('\n');
				}
			}

			return sb.ToString();
		}
	}
}
