using System.Linq;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery: the parallel store writer used for old-vs-new oracle comparison. All fixtures
	/// are synthetic and neutral — no real URLs, words, or content. Tests assert the emit SHAPE
	/// of each view, not any real-site data.
	/// </summary>
	public class SpellCheckParallelStoreWriterTests
	{
		private static TicketOccurrence Occ(RunSource source, string path, string excerpt) =>
			new(source, path, excerpt);

		private static WordTicket Ticket(string word, string url, string langs, params TicketOccurrence[] occ) =>
			new(word, url, langs, occ.ToList());

		[Fact]
		public void SourcesView_LinePerPage_WordWithLang()
		{
			var tickets = new[]
			{
				Ticket("Wortx", "http://x/a", "de", Occ(RunSource.TextNode, "p[#text]", "ctx")),
			};

			var output = ParallelStoreWriter.SourcesView(tickets);

			Assert.Equal("http://x/a|Wortx (de)\n", output);
		}

		[Fact]
		public void SourcesView_AddsMetaLabel_OnlyForMetaSource()
		{
			var meta = new[]
			{
				Ticket("Keywx", "http://x/a", "de", Occ(RunSource.Meta, "meta[@name=keywords]", "ctx")),
			};
			var text = new[]
			{
				Ticket("Bodyx", "http://x/a", "de", Occ(RunSource.TextNode, "p[#text]", "ctx")),
			};
			var attr = new[]
			{
				Ticket("Altx", "http://x/a", "de", Occ(RunSource.Attribute, "img[@alt]", "ctx")),
			};

			Assert.Equal("http://x/a|Keywx (de) (meta[@name=keywords])\n", ParallelStoreWriter.SourcesView(meta));
			Assert.Equal("http://x/a|Bodyx (de)\n", ParallelStoreWriter.SourcesView(text));
			// Ordinary attribute carries NO suffix (mirrors the old log, which labels only meta).
			Assert.Equal("http://x/a|Altx (de)\n", ParallelStoreWriter.SourcesView(attr));
		}

		[Fact]
		public void SourcesView_MultipleWordsOnPage_PipeJoined()
		{
			// Two distinct words on one page -> two tickets, two pipe segments on (logically) the
			// page. Writer emits one line per ticket; both share the url.
			var tickets = new[]
			{
				Ticket("Erstx", "http://x/a", "en", Occ(RunSource.TextNode, "p[#text]", "c1")),
				Ticket("Zweitx", "http://x/a", "en", Occ(RunSource.TextNode, "p[#text]", "c2")),
			};

			var output = ParallelStoreWriter.SourcesView(tickets);

			Assert.Contains("http://x/a|Erstx (en)\n", output);
			Assert.Contains("http://x/a|Zweitx (en)\n", output);
		}

		[Fact]
		public void UniqueView_GloballyUnique_ByWordPlusLang_Sorted()
		{
			var tickets = new[]
			{
				Ticket("Bword", "http://x/a", "de", Occ(RunSource.TextNode, "p[#text]", "c")),
				Ticket("Aword", "http://x/b", "en", Occ(RunSource.TextNode, "p[#text]", "c")),
				Ticket("Bword", "http://x/c", "de", Occ(RunSource.TextNode, "p[#text]", "c")), // dup word+lang
			};

			var output = ParallelStoreWriter.UniqueView(tickets);
			var lines = output.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

			Assert.Equal(2, lines.Length);            // Bword(de) deduped
			Assert.Equal("Aword (en)", lines[0]);     // sorted
			Assert.Equal("Bword (de)", lines[1]);
		}

		[Fact]
		public void UniqueView_SameWordDifferentLang_KeptSeparate()
		{
			var tickets = new[]
			{
				Ticket("Appx", "http://x/a", "de", Occ(RunSource.TextNode, "p[#text]", "c")),
				Ticket("Appx", "http://x/b", "all", Occ(RunSource.TextNode, "p[#text]", "c")),
			};

			var lines = ParallelStoreWriter.UniqueView(tickets).Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

			Assert.Equal(2, lines.Length);
			Assert.Contains("Appx (de)", lines);
			Assert.Contains("Appx (all)", lines);
		}

		[Fact]
		public void LocatedView_ListsEveryOccurrence_WithLabelAndExcerpt()
		{
			var tickets = new[]
			{
				Ticket("Dupx", "http://x/a", "de",
					Occ(RunSource.TextNode, "p[#text]", "first para"),
					Occ(RunSource.TextNode, "p[#text]", "second para"),
					Occ(RunSource.Attribute, "img[@alt]", "alt text")),
			};

			var output = ParallelStoreWriter.LocatedView(tickets);

			Assert.Contains("Dupx (de) @ http://x/a", output);
			Assert.Contains("p[#text]  first para", output);
			Assert.Contains("p[#text]  second para", output);
			Assert.Contains("img[@alt]  alt text", output);
			// all three occurrences present
			Assert.Equal(2, output.Split("p[#text]").Length - 1);
		}

		[Fact]
		public void EmptyTickets_EmptyViews()
		{
			var none = System.Array.Empty<WordTicket>();
			Assert.Equal(string.Empty, ParallelStoreWriter.SourcesView(none));
			Assert.Equal(string.Empty, ParallelStoreWriter.UniqueView(none));
			Assert.Equal(string.Empty, ParallelStoreWriter.LocatedView(none));
		}
	}
}
