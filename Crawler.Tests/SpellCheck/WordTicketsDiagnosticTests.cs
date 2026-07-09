using System.Collections.Generic;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the log-14 verbatim word-tickets diagnostic format. The diagnostic must dump every
	/// ticket/occurrence field exactly as produced — no sanitisation, no selection — so a flagged
	/// result can always be traced to what the engine actually emitted. The embedded-delimiter case
	/// is the explicit guard against re-introducing a lossy pipe-flattened view.
	/// </summary>
	public class WordTicketsDiagnosticTests
	{
		private static WordTicket Ticket(string word, string url, string languages, params TicketOccurrence[] occ)
			=> new(word, url, languages, occ);

		[Fact]
		public void Compose_EmptyOrNull_ReturnsEmptyString()
		{
			Assert.Equal(string.Empty, WordTicketsDiagnostic.Compose(new List<WordTicket>()));
			Assert.Equal(string.Empty, WordTicketsDiagnostic.Compose(null!));
		}

		[Fact]
		public void Compose_SingleOccurrence_DumpsAllFieldsVerbatim()
		{
			var tickets = new List<WordTicket>
			{
				Ticket("Adress", "https://site/a", "de",
					new TicketOccurrence(RunSource.TextNode, "p[#text]", "Die Adress ist hier")),
			};

			var output = WordTicketsDiagnostic.Compose(tickets);

			Assert.Equal(
				"Adress (de) @ https://site/a\n  [TextNode] p[#text]  Die Adress ist hier\n",
				output);
		}

		[Fact]
		public void Compose_MultipleOccurrences_ListsEachInOrder()
		{
			var tickets = new List<WordTicket>
			{
				Ticket("checkbox", "https://site/b", "en",
					new TicketOccurrence(RunSource.Attribute, "img[@alt]", "checkbox"),
					new TicketOccurrence(RunSource.TextNode, "li[#text]", "a checkbox item")),
			};

			var output = WordTicketsDiagnostic.Compose(tickets);

			Assert.Equal(
				"checkbox (en) @ https://site/b\n"
				+ "  [Attribute] img[@alt]  checkbox\n"
				+ "  [TextNode] li[#text]  a checkbox item\n",
				output);
		}

		[Fact]
		public void Compose_PreservesEmbeddedDelimitersVerbatim()
		{
			// An excerpt containing pipe/slash must survive UNCHANGED — the diagnostic never
			// sanitises (the anti-pattern that a lossy pipe view would introduce).
			var tickets = new List<WordTicket>
			{
				Ticket("teh", "https://site/c", "en",
					new TicketOccurrence(RunSource.TextNode, "p[#text]", "teh a|b / c value")),
			};

			var output = WordTicketsDiagnostic.Compose(tickets);

			Assert.Contains("teh a|b / c value", output);
		}

		[Fact]
		public void Compose_SeparatesTicketsWithBlankLine()
		{
			var tickets = new List<WordTicket>
			{
				Ticket("one", "https://site/x", "en",
					new TicketOccurrence(RunSource.TextNode, "p[#text]", "one here")),
				Ticket("two", "https://site/y", "en",
					new TicketOccurrence(RunSource.TextNode, "p[#text]", "two here")),
			};

			var output = WordTicketsDiagnostic.Compose(tickets);

			Assert.Equal(
				"one (en) @ https://site/x\n  [TextNode] p[#text]  one here\n"
				+ "\n"
				+ "two (en) @ https://site/y\n  [TextNode] p[#text]  two here\n",
				output);
		}
	}
}
