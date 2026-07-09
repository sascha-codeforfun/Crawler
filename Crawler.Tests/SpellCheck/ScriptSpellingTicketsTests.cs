using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// 661 — pins <see cref="ScriptSpellingTickets.FromBundleFindings"/>: the file scan's emitted findings
	/// become WordTickets so they merge into the existing spell triage. Routing decides the Url — BULK →
	/// the stable bundle key, CLEAR → each reached page — and reach rides in the occurrence label.
	/// </summary>
	public class ScriptSpellingTicketsTests
	{
		private static ScriptBundleFindings Bundle(
			string path, string key, int reach, bool isBulk, IReadOnlyList<string> pages, params (string w, string ex)[] words) =>
			new ScriptBundleFindings(path, key, "https://site/" + path, reach, isBulk, pages,
				words.Select(t => new ScriptWordHit(t.w, t.ex)).ToList());

		[Fact]
		public void Bulk_PointsAtStableKey_OneTicketPerWord()
		{
			var t = ScriptSpellingTickets.FromBundleFindings(new[]
			{
				Bundle("v/ima.js", "ima-js", 42, isBulk: true, new List<string>(), ("typo", "a typo here"), ("misspeld", "x")),
			});

			Assert.Equal(2, t.Count);
			Assert.All(t, x => Assert.Equal("ima-js", x.Url));            // stable key, not a page
			Assert.Contains(t, x => x.Word == "typo");
			Assert.Contains("(bulk)", t[0].Occurrences[0].SourcePath);   // reach/bulk surfaced in label
			Assert.Contains("reach 42", t[0].Occurrences[0].SourcePath);
		}

		[Fact]
		public void Clear_PointsAtEachReachedPage()
		{
			var pages = new List<string> { "https://site/a", "https://site/b" };
			var t = ScriptSpellingTickets.FromBundleFindings(new[]
			{
				Bundle("app/main.js", "main-js", 2, isBulk: false, pages, ("rigth", "the rigth way")),
			});

			Assert.Equal(2, t.Count);                                    // one word × two pages
			Assert.Equal(new[] { "https://site/a", "https://site/b" }, t.Select(x => x.Url));
			Assert.All(t, x => Assert.Equal("rigth", x.Word));
			Assert.DoesNotContain("(bulk)", t[0].Occurrences[0].SourcePath);
		}

		[Fact]
		public void Clear_NoPagesResolved_FallsBackToStableKey()
		{
			var t = ScriptSpellingTickets.FromBundleFindings(new[]
			{
				Bundle("orphan.js", "orphan-js", 0, isBulk: false, new List<string>(), ("x", "c")),
			});

			var ticket = Assert.Single(t);
			Assert.Equal("orphan-js", ticket.Url);
		}

		[Fact]
		public void ExcerptIsCarried()
		{
			var t = ScriptSpellingTickets.FromBundleFindings(new[]
			{
				Bundle("b.js", "b-js", 1, isBulk: false, new List<string> { "https://site/p" }, ("w", "the excerpt text")),
			});
			Assert.Equal("the excerpt text", t[0].Occurrences[0].Excerpt);
		}

		[Fact]
		public void EmptyInput_EmptyOutput()
		{
			Assert.Empty(ScriptSpellingTickets.FromBundleFindings(new List<ScriptBundleFindings>()));
		}
	}
}
