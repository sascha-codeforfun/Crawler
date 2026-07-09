using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Crawler;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Pins the CONTENT_BEFORE_DOCTYPE bag's pure logic: the non-word-char predicate, the
	/// sub-code URL gate, the partition, sidecar parsing (Date/cookie-names/extractors), and the
	/// grouped ticket text.
	/// </summary>
	public class ContentBeforeDoctypeBagTests
	{
		// ── ExcerptHasReplacementChar ───────────────────────────────────

		[Theory]
		[InlineData("a perfectly clean excerpt", false)] // no U+FFFD → not bagged
		[InlineData("state-of-the-art wording", false)]  // ordinary punctuation, no U+FFFD
		[InlineData("prefix \uFFFDtail", true)]          // U+FFFD severed the left → bag
		[InlineData("head\uFFFD suffix", true)]          // U+FFFD severed the right → bag
		[InlineData("mid\uFFFDword", true)]              // U+FFFD inside → bag
		public void ExcerptHasReplacementChar_Predicate(string excerpt, bool expected)
		{
			var ticket = T("word", "U", excerpt);
			Assert.Equal(expected, ContentBeforeDoctypeBag.ExcerptHasReplacementChar(ticket));
		}

		[Fact]
		public void ExcerptHasReplacementChar_AnyOccurrenceCounts()
		{
			var ticket = new WordTicket("w", "U", "de", new[]
			{
				new TicketOccurrence(RunSource.TextNode, "s1", "clean one"),
				new TicketOccurrence(RunSource.Script, "s2", "bad\uFFFDone"),
			});
			Assert.True(ContentBeforeDoctypeBag.ExcerptHasReplacementChar(ticket));
		}

		// ── BuildContentBeforeDoctypeUrls (sub-code gate) ───────────────

		[Fact]
		public void BuildUrls_OnlyContentBeforeDoctype()
		{
			var lines = new[]
			{
				"Filename|IssueType|Detail|Context",
				"fileA|MALFORMED_HTML|CONTENT_BEFORE_DOCTYPE|x",
				"fileB|MALFORMED_HTML|TagNotOpened|y",     // wrong sub-code
				"fileC|SPLIT_WORD_ANCHOR|whatever|z",       // wrong type
				"fileD|MALFORMED_HTML|CONTENT_BEFORE_DOCTYPE|w",
			};

			var urls = ContentBeforeDoctypeBag.BuildContentBeforeDoctypeUrls(lines, f => "u:" + f);

			Assert.Contains("u:fileA", urls);
			Assert.Contains("u:fileD", urls);
			Assert.DoesNotContain("u:fileB", urls);
			Assert.DoesNotContain("u:fileC", urls);
			Assert.Equal(2, urls.Count);
		}

		[Fact]
		public void BuildUrls_DropsUnmappable()
		{
			var lines = new[] { "f|MALFORMED_HTML|CONTENT_BEFORE_DOCTYPE|x" };
			Assert.Empty(ContentBeforeDoctypeBag.BuildContentBeforeDoctypeUrls(lines, _ => "error"));
		}

		// ── Partition ───────────────────────────────────────────────────

		private static WordTicket T(string word, string url, string excerpt) =>
			new(word, url, "de", new[] { new TicketOccurrence(RunSource.TextNode, "src", excerpt) });

		[Fact]
		public void Partition_BagsOnlyCbdPageWithReplacementCharExcerpt()
		{
			var cbd = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "U1" };
			var tickets = new[]
			{
				T("alpha", "U1", "prefix \uFFFDtail"),  // CBD page + U+FFFD in excerpt → bag
				T("beta", "U1", "fully clean text"),     // CBD page but clean excerpt → rest (lossless)
				T("gamma", "U2", "other \uFFFDjunk"),    // U+FFFD but not a CBD page → rest
				T("delta", "U2", "clean"),               // neither → rest
			};

			var (bag, rest) = ContentBeforeDoctypeBag.Partition(tickets, cbd);

			Assert.Single(bag);
			Assert.Equal("alpha", bag[0].Word);
			Assert.Equal(3, rest.Count);
			Assert.Equal(tickets.Length, bag.Count + rest.Count); // lossless
		}

		// ── ParseHttpDate ───────────────────────────────────────────────

		[Fact]
		public void ParseHttpDate_ValidImfFixdate()
		{
			var dto = ContentBeforeDoctypeBag.ParseHttpDate("Tue, 04 Feb 2020 09:15:00 GMT");
			Assert.NotNull(dto);
			Assert.Equal(2020, dto!.Value.Year);
			Assert.Equal(9, dto.Value.UtcDateTime.Hour);
		}

		[Theory]
		[InlineData("")]
		[InlineData("not a date")]
		public void ParseHttpDate_BadInput_Null(string s)
		{
			Assert.Null(ContentBeforeDoctypeBag.ParseHttpDate(s));
		}

		// ── Sidecar parsing ─────────────────────────────────────────────

		private const string Sidecar =
			"=== REQUEST ===\n" +
			"GET http://www.example.com/page.html\n" +
			"=== RESPONSE ===\n" +
			"HTTP/1.1 200 OK\n" +
			"Date: Tue, 04 Feb 2020 09:15:00 GMT\n" +
			"Set-Cookie: SESS=aaa; Path=/; HttpOnly\n" +
			"Set-Cookie: TRACK=bbb; Path=/\n" +
			"Affinity: node7\n" +
			"Content-Type: text/html\n";

		[Fact]
		public void CookieNames_NamesOnly_Sorted()
		{
			Assert.Equal(new[] { "SESS", "TRACK" }, ContentBeforeDoctypeBag.CookieNamesFromSidecar(Sidecar).ToArray());
		}

		[Fact]
		public void CookieNames_NoneWhenAbsent()
		{
			Assert.Empty(ContentBeforeDoctypeBag.CookieNamesFromSidecar("=== RESPONSE ===\nDate: x\n"));
		}

		[Fact]
		public void ExtractTokens_AppliesPatterns()
		{
			var ex = new List<(string, Regex)> { ("Aff", new Regex(@"Affinity:\s*(\S+)")) };
			var got = ContentBeforeDoctypeBag.ExtractTokens(Sidecar, ex);
			Assert.Single(got);
			Assert.Equal(("Aff", "node7"), got[0]);
		}

		[Fact]
		public void DiagnosticsFromSidecarText_AssemblesAll()
		{
			var pf = ContentBeforeDoctypeBag.DiagnosticsFromSidecarText("U", Sidecar, Array.Empty<(string, Regex)>());
			Assert.True(pf.SidecarFound);
			Assert.NotNull(pf.DateParsed);
			Assert.Equal(new[] { "SESS", "TRACK" }, pf.CookieNames.ToArray());
		}

		// ── Ticket text ─────────────────────────────────────────────────

		private static ContentBeforeDoctypeBag.PageDiagnostics PF(
			string url, DateTimeOffset? date, string[] cookies, (string, string)[] tokens, bool found = true) =>
			new(url, date?.ToString(), date, cookies, tokens, found);

		[Fact]
		public void TicketText_GroupsByExtractor_WhenPresent()
		{
			var t = new DateTimeOffset(2020, 2, 4, 9, 15, 0, TimeSpan.Zero);
			var pages = new[]
			{
				PF("http://www.example.com/a", t, new[] { "SESS" }, new[] { ("Aff", "node7") }),
				PF("http://www.example.com/b", t.AddSeconds(2), new[] { "SESS" }, new[] { ("Aff", "node7") }),
			};

			var text = ContentBeforeDoctypeBag.BuildTicketText(pages, findingCount: 5);

			Assert.Contains(ContentBeforeDoctypeBag.Marker, text);
			Assert.Contains("Aff=node7", text);          // grouped by extractor token
			Assert.Contains("http://www.example.com/a", text);
			Assert.Contains("earliest:", text);
			Assert.Contains("does not imply causality", text);
		}

		[Fact]
		public void TicketText_FallsBackToCookieNames_WhenNoExtractor()
		{
			var t = new DateTimeOffset(2020, 2, 4, 9, 15, 0, TimeSpan.Zero);
			var pages = new[]
			{
				PF("http://www.example.com/a", t, new[] { "SESS", "TRACK" }, Array.Empty<(string, string)>()),
				PF("http://www.example.com/b", null, Array.Empty<string>(), Array.Empty<(string, string)>(), found: false),
			};

			var text = ContentBeforeDoctypeBag.BuildTicketText(pages, findingCount: 2);

			Assert.Contains("cookies: SESS, TRACK", text);            // fallback shows names
			Assert.Contains("(sidecar unavailable)", text);          // missing sidecar surfaced
			Assert.Contains("Date header missing or unparseable", text); // undated counted
		}
	}
}
