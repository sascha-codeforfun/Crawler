using System.Linq;
using Crawler;
using Crawler.Quality;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.Quality
{
	/// <summary>
	/// Tests for the decomposition (non-NFC) content-quality detector. SYNTHETIC
	/// fixtures only — umlauts written explicitly as composed (U+00FC) vs decomposed
	/// (u + U+0308) so the byte form is unambiguous in source.
	/// </summary>
	public class DecompositionTests
	{
		private static HtmlDocument Doc(string html)
		{
			var d = new HtmlDocument();
			d.LoadHtml(html);
			return d;
		}

		private static ContentQualityConfig Cfg(string mode) =>
			new() { CheckDecomposition = mode };

		// ── FindFirstDecomposition ─────────────────────────────────────────

		[Fact]
		public void FindFirstDecomposition_DecomposedUmlaut_ReturnsComposed()
		{
			var hit = Decomposition.FindFirstDecomposition("fu\u0308r"); // f u ◌̈ r
			Assert.NotNull(hit);
			Assert.Equal("\u00FC", hit.Value.Composed); // ü
			Assert.Equal('u', hit.Value.BaseChar);
			Assert.Equal(0x0308, hit.Value.Mark);
		}

		[Fact]
		public void FindFirstDecomposition_ComposedText_ReturnsNull()
		{
			Assert.Null(Decomposition.FindFirstDecomposition("f\u00FCr")); // precomposed ü
		}

		[Fact]
		public void FindFirstDecomposition_NonComposableMark_ReturnsNull()
		{
			// x + combining acute has no precomposed form, so NFC does not shorten it
			// → exempt (mirrors Arabic/Hebrew/Indic marks with no precomposed form).
			Assert.Null(Decomposition.FindFirstDecomposition("ax\u0301b"));
		}

		// ── Check: presence (FlagAll) ──────────────────────────────────────

		[Fact]
		public void Check_FlagAll_DecomposedParagraph_FlagsPresence()
		{
			var doc = Doc("<html><body><p>fu\u0308r die App</p></body></html>");
			var issues = Decomposition.Check("f.html", doc, Cfg("FlagAll")).ToList();
			var presence = issues.Where(i => i.IssueType == "DECOMPOSED_TEXT_IN_CONTENT").ToList();
			Assert.Single(presence);
			Assert.Contains("U+0308", presence[0].Detail);
		}

		[Fact]
		public void Check_FlagAll_CleanComposedText_Empty()
		{
			var doc = Doc("<html><body><p>f\u00FCr die App</p></body></html>");
			Assert.Empty(Decomposition.Check("f.html", doc, Cfg("FlagAll")));
		}

		// ── Check: mixed ───────────────────────────────────────────────────

		[Fact]
		public void Check_FlagAll_SameWordTwoForms_FlagsMixed()
		{
			// "für" composed in one paragraph, decomposed in another.
			var doc = Doc("<html><body><p>f\u00FCr</p><p>fu\u0308r</p></body></html>");
			var mixed = Decomposition.Check("f.html", doc, Cfg("FlagAll"))
				.Where(i => i.IssueType == "MIXED_NORMALIZATION_IN_CONTENT")
				.ToList();
			Assert.Single(mixed);
			Assert.Contains("U+0308", mixed[0].Context); // decomposed form annotated
		}

		[Fact]
		public void Check_FlagMixed_SameWordTwoForms_OnlyMixedNoPresence()
		{
			var doc = Doc("<html><body><p>f\u00FCr</p><p>fu\u0308r</p></body></html>");
			var issues = Decomposition.Check("f.html", doc, Cfg("FlagMixed")).ToList();
			Assert.Single(issues);
			Assert.Equal("MIXED_NORMALIZATION_IN_CONTENT", issues[0].IssueType);
		}

		[Fact]
		public void Check_FlagMixed_ConsistentDecomposition_Empty()
		{
			// Uniformly decomposed, no second form → not a mixed page, and FlagMixed
			// does not flag mere presence.
			var doc = Doc("<html><body><p>fu\u0308r</p><p>k\u00F6nnen</p></body></html>");
			Assert.Empty(Decomposition.Check("f.html", doc, Cfg("FlagMixed")));
		}

		// ── Check: off ─────────────────────────────────────────────────────

		[Fact]
		public void Check_Off_Empty()
		{
			var doc = Doc("<html><body><p>fu\u0308r</p><p>f\u00FCr</p></body></html>");
			Assert.Empty(Decomposition.Check("f.html", doc, Cfg("off")));
		}

		// ── Config resolution ──────────────────────────────────────────────

		[Fact]
		public void FindAllDecompositions_TwoLetters_ReturnsBothDistinct()
		{
			var all = Decomposition.FindAllDecompositions("fu\u0308r ko\u0308nnen");
			Assert.Equal(2, all.Count);
			Assert.Equal("\u00FC", all[0].Composed);
			Assert.Equal("\u00F6", all[1].Composed);
		}

		[Fact]
		public void FindAllDecompositions_RepeatedLetter_DedupedToOne()
		{
			var all = Decomposition.FindAllDecompositions("fu\u0308r u\u0308ber");
			Assert.Single(all);
			Assert.Equal("\u00FC", all[0].Composed);
		}

		[Fact]
		public void FindAllDecompositions_NonComposable_Empty()
		{
			Assert.Empty(Decomposition.FindAllDecompositions("ax\u0301b"));
		}

		[Fact]
		public void Check_FlagAll_ElementWithTwoLetters_DetailNamesBoth()
		{
			var doc = Doc("<html><body><p>fu\u0308r ko\u0308nnen</p></body></html>");
			var presence = Decomposition.Check("f.html", doc, Cfg("FlagAll"))
				.Where(i => i.IssueType == "DECOMPOSED_TEXT_IN_CONTENT").ToList();
			Assert.Single(presence);
			Assert.Contains("\u00FC", presence[0].Detail);
			Assert.Contains("\u00F6", presence[0].Detail);
		}

		[Fact]
		public void ResolvedCheckDecomposition_EmptyDefaultsToFlagAll()
		{
			Assert.Equal(DecompositionMode.FlagAll, new ContentQualityConfig().ResolvedCheckDecomposition);
		}

		[Theory]
		[InlineData("off", DecompositionMode.Off)]
		[InlineData("FlagMixed", DecompositionMode.FlagMixed)]
		[InlineData("flagall", DecompositionMode.FlagAll)] // case-insensitive
		public void ResolvedCheckDecomposition_ParsesValues(string raw, DecompositionMode expected)
		{
			Assert.Equal(expected, new ContentQualityConfig { CheckDecomposition = raw }.ResolvedCheckDecomposition);
		}
	}
}
