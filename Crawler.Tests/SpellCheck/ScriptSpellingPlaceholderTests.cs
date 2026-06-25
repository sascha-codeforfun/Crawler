using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// 662/663 — pins the lorem-ipsum PLACEHOLDER collapse in <see cref="ScriptSpellingTickets"/>. 663:
	/// detection keys on the markers in a finding's EXCERPT, because the common Latin roots (ipsum, dolor,
	/// amet…) match a loaded dictionary and never flag — so requiring them as flagged words missed real
	/// placeholder blocks. When "lorem"+"ipsum" appear in the surrounding text the distinctive Latin filler
	/// is absorbed into ONE placeholder finding; real (non-Latin) typos still ticket individually.
	/// </summary>
	public class ScriptSpellingPlaceholderTests
	{
		// A real lorem block as it appears in an excerpt. Note: ipsum/dolor/amet are NOT flagged words here.
		private const string LoremExcerpt =
			"Beratung: Lorem ipsum dolor sit amet, consectetuer adipiscing elit. " +
			"Aenean commodo ligula eget dolor. Cum sociis natoque penatibus.";

		private static ScriptWordHit Hit(string word, string excerpt) => new ScriptWordHit(word, excerpt);

		private static ScriptBundleFindings Bundle(bool isBulk, IReadOnlyList<string> pages, params ScriptWordHit[] words) =>
			new ScriptBundleFindings("biz/check.min.js", "check-js", "https://site/biz/check.min.js",
				isBulk ? 9 : 1, isBulk, pages, words.ToList());

		private static readonly IReadOnlyList<string> OnePage = new List<string> { "https://site/check.html" };

		[Fact]
		public void LoremBlock_CollapsesLatinToOnePlaceholder()
		{
			// The six flagged Latin words all carry the lorem-ipsum excerpt → one row.
			var t = ScriptSpellingTickets.FromBundleFindings(new[]
			{
				Bundle(false, OnePage,
					Hit("Lorem", LoremExcerpt), Hit("consectetuer", LoremExcerpt), Hit("adipiscing", LoremExcerpt),
					Hit("natoque", LoremExcerpt), Hit("penatibus", LoremExcerpt), Hit("sociis", LoremExcerpt)),
			});

			var ticket = Assert.Single(t);
			Assert.Equal(ScriptSpellingTickets.PlaceholderWord, ticket.Word);
		}

		[Fact]
		public void IpsumNotFlagged_StillDetectedViaExcerpt()
		{
			// The real-world 663 bug: ipsum/lorem are dictionary-matched and never flag, only consectetuer
			// does — but its excerpt carries the markers, so the placeholder is still detected and collapsed.
			var t = ScriptSpellingTickets.FromBundleFindings(new[]
			{
				Bundle(false, OnePage, Hit("consectetuer", LoremExcerpt)),
			});

			var ticket = Assert.Single(t);
			Assert.Equal(ScriptSpellingTickets.PlaceholderWord, ticket.Word);
		}

		[Fact]
		public void RealTyposSurviveAlongsideThePlaceholder()
		{
			var t = ScriptSpellingTickets.FromBundleFindings(new[]
			{
				Bundle(false, OnePage,
					Hit("consectetuer", LoremExcerpt),
					Hit("Maximalendite", "header:\"Maximalendite p.a.\""),
					Hit("sencond", "throw new Error(\"sencond key\")")),
			});

			Assert.Equal(3, t.Count);
			Assert.Contains(t, x => x.Word == ScriptSpellingTickets.PlaceholderWord);
			Assert.Contains(t, x => x.Word == "Maximalendite");
			Assert.Contains(t, x => x.Word == "sencond");
		}

		[Fact]
		public void StrayLatin_NoMarkers_SurfacesNormally()
		{
			// 'consectetuer' with no lorem+ipsum in its excerpt is NOT absorbed — a stray Latin token shows.
			var t = ScriptSpellingTickets.FromBundleFindings(new[]
			{
				Bundle(false, OnePage, Hit("consectetuer", "var consectetuer = 1;")),
			});

			var ticket = Assert.Single(t);
			Assert.Equal("consectetuer", ticket.Word);
		}

		[Fact]
		public void OnlyOneMarkerInExcerpt_NoCollapse()
		{
			var t = ScriptSpellingTickets.FromBundleFindings(new[]
			{
				Bundle(false, OnePage, Hit("consectetuer", "the word lorem appears but not the other marker")),
			});
			Assert.DoesNotContain(t, x => x.Word == ScriptSpellingTickets.PlaceholderWord);
		}

		[Fact]
		public void Bulk_OnePlaceholderAtStableKey()
		{
			var t = ScriptSpellingTickets.FromBundleFindings(new[]
			{
				Bundle(true, new List<string>(), Hit("adipiscing", LoremExcerpt)),
			});
			var ticket = Assert.Single(t);
			Assert.Equal(ScriptSpellingTickets.PlaceholderWord, ticket.Word);
			Assert.Equal("check-js", ticket.Url);
		}

		[Fact]
		public void Helpers_ExcerptDetectionAndFiller()
		{
			Assert.True(ScriptSpellingTickets.ExcerptIsPlaceholder("x Lorem ipsum y"));
			Assert.False(ScriptSpellingTickets.ExcerptIsPlaceholder("just lorem here"));
			Assert.False(ScriptSpellingTickets.ExcerptIsPlaceholder(null));
			Assert.True(ScriptSpellingTickets.IsLatinFiller("consectetuer"));
			Assert.False(ScriptSpellingTickets.IsLatinFiller("Maximalendite"));
		}

		[Fact]
		public void LocateFillerRun_SpansFirstToLastFiller_OverInterspersedNonFiller()
		{
			var (start, length) = ScriptSpellingTickets.LocateFillerRun(LoremExcerpt);

			Assert.True(start >= 0);
			var hit = LoremExcerpt.Substring(start, length);

			// Run starts at the first filler ("Lorem") and ends at the last ("penatibus"); the leading
			// prose ("Beratung: ") and the trailing "." are outside it.
			Assert.Equal("Beratung: ", LoremExcerpt[..start]);
			Assert.StartsWith("Lorem", hit);
			Assert.EndsWith("penatibus", hit);
			Assert.Equal(".", LoremExcerpt[(start + length)..]);

			// One contiguous span: short ambiguous Latin excluded from the filler set ("sit", "Cum") sits
			// INSIDE the run, not breaking it.
			Assert.Contains("sit", hit);
			Assert.Contains("Cum", hit);
		}

		[Fact]
		public void LocateFillerRun_SingleFillerToken_IsItsOwnSpan()
		{
			var (start, length) = ScriptSpellingTickets.LocateFillerRun("x consectetuer y");
			Assert.Equal("consectetuer", "x consectetuer y".Substring(start, length));
		}

		[Theory]
		[InlineData("nur deutscher Fließtext ohne Füllwörter")]
		[InlineData("")]
		[InlineData(null)]
		public void LocateFillerRun_NoFiller_ReturnsNone(string? excerpt)
		{
			var (start, length) = ScriptSpellingTickets.LocateFillerRun(excerpt);
			Assert.Equal(-1, start);
			Assert.Equal(0, length);
		}
	}
}
