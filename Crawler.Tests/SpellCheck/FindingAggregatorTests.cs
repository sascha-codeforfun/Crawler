using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery: aggregation of per-occurrence findings into per-(word,url) tickets. The defining
	/// behaviour: every occurrence is kept — multiple fragments with the same typo each appear as
	/// their own located occurrence, fully verbose, never collapsed to a single label.
	/// </summary>
	public class SpellCheckFindingAggregatorTests
	{
		private static SpellFinding F(string word, RunSource source, string path, string lang = "de") =>
			new(word, "", lang, source, path, HtmlNode.CreateNode("<p>x</p>"), 0, word.Length);

		// Excerpt stand-in: echo the source path so occurrences are distinguishable in asserts.
		private static string Excerpt(SpellFinding f) => $"ctx:{f.SourcePath}";

		[Fact]
		public void GroupsByWord_OnePerDistinctWord()
		{
			var findings = new[]
			{
				F("Fehla", RunSource.TextNode, "p[#text]"),
				F("Bidl", RunSource.Attribute, "img[@alt]"),
			};

			var tickets = FindingAggregator.Aggregate("http://x/p", findings, Excerpt);

			Assert.Equal(2, tickets.Count);
			Assert.Contains(tickets, t => t.Word == "Fehla");
			Assert.Contains(tickets, t => t.Word == "Bidl");
		}

		[Fact]
		public void SameWordMultipleFragments_KeepsAllOccurrences()
		{
			// The whole point: a typo in two <p>'s and one alt = ONE ticket, THREE occurrences,
			// each with its own location and excerpt. Nothing collapsed.
			var findings = new[]
			{
				F("Aktivitaeten", RunSource.TextNode, "p[#text]"),
				F("Aktivitaeten", RunSource.TextNode, "p[#text]"),
				F("Aktivitaeten", RunSource.Attribute, "img[@alt]"),
			};

			var tickets = FindingAggregator.Aggregate("http://x/p", findings, Excerpt);

			var t = Assert.Single(tickets);
			Assert.Equal("Aktivitaeten", t.Word);
			Assert.Equal(3, t.Occurrences.Count);
			Assert.Equal(2, t.Occurrences.Count(o => o.SourcePath == "p[#text]"));
			Assert.Equal(1, t.Occurrences.Count(o => o.SourcePath == "img[@alt]"));
		}

		[Fact]
		public void OccurrenceCarriesItsOwnExcerpt()
		{
			var findings = new[]
			{
				F("Wort", RunSource.TextNode, "p[#text]"),
				F("Wort", RunSource.Meta, "meta[@name=description]"),
			};

			var tickets = FindingAggregator.Aggregate("http://x/p", findings, Excerpt);

			var t = Assert.Single(tickets);
			Assert.Contains(t.Occurrences, o => o.Excerpt == "ctx:p[#text]");
			Assert.Contains(t.Occurrences, o => o.Excerpt == "ctx:meta[@name=description]");
		}

		[Fact]
		public void JoinsDistinctLanguages_PerWord()
		{
			var findings = new[]
			{
				F("Wort", RunSource.TextNode, "p[#text]", "de"),
				F("Wort", RunSource.TextNode, "p[#text]", "en"),
				F("Wort", RunSource.TextNode, "p[#text]", "de"),
			};

			var tickets = FindingAggregator.Aggregate("http://x/p", findings, Excerpt);

			var t = Assert.Single(tickets);
			Assert.Contains("de", t.Languages);
			Assert.Contains("en", t.Languages);
			// distinct — "de" not repeated
			Assert.Equal(t.Languages, string.Join(",", t.Languages.Split(',').Distinct()));
		}

		[Fact]
		public void GroupingIsCaseInsensitive_ButKeepsFirstCasing()
		{
			var findings = new[]
			{
				F("Wort", RunSource.TextNode, "p[#text]"),
				F("wort", RunSource.TextNode, "li[#text]"),
			};

			var tickets = FindingAggregator.Aggregate("http://x/p", findings, Excerpt);

			var t = Assert.Single(tickets);
			Assert.Equal("Wort", t.Word);          // first casing wins
			Assert.Equal(2, t.Occurrences.Count);  // both occurrences kept
		}

		[Fact]
		public void CarriesTheSuppliedUrl()
		{
			var tickets = FindingAggregator.Aggregate("http://x/page.html",
				new[] { F("Wort", RunSource.TextNode, "p[#text]") }, Excerpt);

			Assert.Equal("http://x/page.html", Assert.Single(tickets).Url);
		}

		[Fact]
		public void NoFindings_NoTickets()
		{
			var tickets = FindingAggregator.Aggregate("http://x/p", System.Array.Empty<SpellFinding>(), Excerpt);
			Assert.Empty(tickets);
		}

		[Fact]
		public void PreservesDocumentOrderOfWords()
		{
			var findings = new[]
			{
				F("erste", RunSource.TextNode, "p[#text]"),
				F("zweite", RunSource.TextNode, "p[#text]"),
			};

			var tickets = FindingAggregator.Aggregate("http://x/p", findings, Excerpt);

			Assert.Equal("erste", tickets[0].Word);
			Assert.Equal("zweite", tickets[1].Word);
		}
	}
}
