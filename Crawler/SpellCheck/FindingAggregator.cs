namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>One located occurrence of a misspelled word, as it will appear in a ticket.</summary>
	public sealed record TicketOccurrence(
		RunSource Source,
		string SourcePath,
		string Excerpt);

	/// <summary>
	/// The triage/persistence unit: one misspelled word on one page (url), carrying EVERY
	/// occurrence of it on that page — each with its own location label and excerpt. This is the
	/// deliberate replacement for the old single-label/single-excerpt entry whose excerpt was
	/// whatever a first-wins race happened to keep. Here nothing is collapsed away: if the word
	/// is wrong in two paragraphs and one image alt, the ticket lists all three, each separately
	/// discoverable. Verbosity is full and uncapped — pruning, if any, is a later human act, not
	/// an automatic truncation.
	///
	/// Languages are joined (distinct) to match the existing per-(word,url) grouping shape so the
	/// old-vs-new comparison lines up on that dimension.
	/// </summary>
	public sealed record WordTicket(
		string Word,
		string Url,
		string Languages,
		IReadOnlyList<TicketOccurrence> Occurrences);

	/// <summary>
	/// Groups per-occurrence <see cref="SpellFinding"/>s for a single page into
	/// <see cref="WordTicket"/>s. Pure transformation: findings in, tickets out. The url is
	/// supplied by the caller (sourced exactly as the old pipeline sources it), so this stays
	/// independent of how files map to urls.
	///
	/// Grouping key is the word, case-insensitive (matching the old grouping). Within a ticket,
	/// occurrences are kept in the order encountered — i.e. document order from the run stream —
	/// so the listing is stable and reproducible. Excerpt selection is NOT done here: every
	/// occurrence is retained; any "best excerpt" choice is a presentation concern layered on top
	/// of the full list, never a discard.
	/// </summary>
	public static class FindingAggregator
	{
		public static IReadOnlyList<WordTicket> Aggregate(
			string url,
			IEnumerable<SpellFinding> findings,
			Func<SpellFinding, string> excerptFor)
		{
			var byWord = new Dictionary<string, List<SpellFinding>>(StringComparer.OrdinalIgnoreCase);
			var order = new List<string>();

			foreach (var f in findings)
			{
				if (!byWord.TryGetValue(f.Word, out var list))
				{
					list = new List<SpellFinding>();
					byWord[f.Word] = list;
					order.Add(f.Word);
				}

				list.Add(f);
			}

			var tickets = new List<WordTicket>(order.Count);
			foreach (var word in order)
			{
				var group = byWord[word];

				string languages = string.Join(
					",",
					group.Select(g => g.Language)
						.Where(l => !string.IsNullOrEmpty(l))
						.Distinct(StringComparer.OrdinalIgnoreCase));

				var occurrences = group
					.Select(g => new TicketOccurrence(g.Source, g.SourcePath, excerptFor(g)))
					.ToList();

				// Use the casing of the first occurrence as the ticket's word form.
				tickets.Add(new WordTicket(group[0].Word, url, languages, occurrences));
			}

			return tickets;
		}
	}
}
