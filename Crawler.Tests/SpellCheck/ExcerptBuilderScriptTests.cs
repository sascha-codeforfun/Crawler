using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the <see cref="RunSource.Script"/> branch of <see cref="ExcerptBuilder"/>: the excerpt is
	/// the context carried on the finding (<c>ExcerptText</c>) — a raw-source window built upstream at
	/// extraction time — returned AS-IS, never re-derived from the DOM node (which holds the raw,
	/// undecoded script body) and never re-windowed here. The windowing itself is the traverser's job
	/// and is covered by <see cref="DomTraverserScriptContextTests"/>.
	///
	/// Fixtures are invented.
	/// </summary>
	public class ExcerptBuilderScriptTests
	{
		private static SpellFinding ScriptFinding(string word, string? excerptText) =>
			new(word, "suggestion", "de", RunSource.Script, "script[L1:1]",
				HtmlNode.CreateNode("<p>x</p>"), 0, word.Length)
			{
				ExcerptText = excerptText,
			};

		[Fact]
		public void Excerpt_IsTheCarriedContext_NotTheNodeContent()
		{
			// The node deliberately holds unrelated text; the excerpt must come from ExcerptText.
			var f = ScriptFinding("wolrd", "Finder.create(\"wolrd\", cfg);");
			Assert.Equal("Finder.create(\"wolrd\", cfg);", ExcerptBuilder.Build(f));
		}

		[Fact]
		public void Excerpt_IsReturnedVerbatim_NotReWindowed()
		{
			// The carried window is already bounded and centred upstream; ExcerptBuilder returns it
			// unchanged — no truncation, no added ellipsis — even when it is long. (Re-windowing here
			// would re-centre on the decoded word and could not align with the raw source.)
			string window = "var x = \"" + string.Concat(System.Linq.Enumerable.Repeat("lang ", 40)) + "wolrd\";";
			string excerpt = ExcerptBuilder.Build(ScriptFinding("wolrd", window));

			Assert.Equal(window, excerpt);
		}

		[Fact]
		public void Excerpt_PreservesEllipsisWindow()
		{
			// A window the traverser truncated by the radius carries ellipses; they survive verbatim.
			const string window = "…finderId: \"wolrd\"}…";
			Assert.Equal(window, ExcerptBuilder.Build(ScriptFinding("wolrd", window)));
		}

		[Fact]
		public void Excerpt_NullExcerptText_IsEmpty()
		{
			// Defensive: a Script finding with no carried context yields an empty excerpt, never a throw.
			Assert.Equal(string.Empty, ExcerptBuilder.Build(ScriptFinding("wolrd", null)));
		}
	}
}
