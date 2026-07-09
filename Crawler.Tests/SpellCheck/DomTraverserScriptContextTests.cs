using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the Script run's <see cref="TextRun.ScriptContext"/> — the raw-source window carried for
	/// triage so a bare literal (e.g. a technical id) is shown inside its surrounding code (the
	/// assignment, the call), which is the context an operator needs to judge it. The window is up to
	/// 80 chars per side, clipped at newlines so it stays on the literal's own line, with an ellipsis
	/// only where the radius (not a line boundary) did the cutting; indentation is collapsed.
	///
	/// All fixtures are invented, neutral JavaScript — no content from any crawled page.
	/// </summary>
	public class DomTraverserScriptContextTests
	{
		private static HtmlDocument Doc(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		private static System.Collections.Generic.List<TextRun> Script(string html) =>
			DomTraverser.Traverse(Doc(html), skipAttributeNames: null, checkJavaScript: true)
				.Where(r => r.Source == RunSource.Script)
				.ToList();

		[Fact]
		public void TwoOccurrences_GetDistinctContextWindows()
		{
			// The same id appears twice — once assigned into an object, once passed to a call. Each
			// finding must show ITS surrounding line, not the same blob twice. Each literal sits on its
			// own indented line, so the window is that line (no ellipsis), indentation trimmed.
			var html =
				"<div><script>\n" +
				"  let prop = {finderId: \"widgetfinder\"};\n" +
				"  Finder.create(\"widgetfinder\", cfg);\n" +
				"</script></div>";

			var runs = Script(html);
			Assert.Equal(2, runs.Count);
			Assert.Equal("let prop = {finderId: \"widgetfinder\"};", runs[0].ScriptContext);
			Assert.Equal("Finder.create(\"widgetfinder\", cfg);", runs[1].ScriptContext);
		}

		[Fact]
		public void Window_StopsAtNewline_NoEllipsisWhenLineFits()
		{
			// Surrounding lines exist, but the window must not cross into them; the literal's own line
			// fits within the radius, so there is no ellipsis on either side.
			var html =
				"<script>\n" +
				"var before = 1;\n" +
				"var label = \"frei\";\n" +
				"var after = 2;\n" +
				"</script>";

			var run = Assert.Single(Script(html));
			Assert.Equal("var label = \"frei\";", run.ScriptContext);
		}

		[Fact]
		public void Window_AddsEllipsis_WhenRadiusCutsMidLine()
		{
			// One long line (no newlines): the 80-char radius does the cutting on both sides, so both
			// ends carry an ellipsis and the literal stays centred.
			string pad = new string('x', 200);
			var html = "<script>var a=\"" + pad + "\"+\"frei\"+\"" + pad + "\";</script>";

			var run = Script(html).Single(r => r.RawText == "frei");
			Assert.StartsWith("…", run.ScriptContext);
			Assert.EndsWith("…", run.ScriptContext);
			Assert.Contains("\"frei\"", run.ScriptContext);
		}

		[Fact]
		public void Window_NoLeadingEllipsis_WhenLiteralNearBodyStart()
		{
			var run = Assert.Single(Script("<script>var id=\"abc\";</script>"));
			Assert.Equal("var id=\"abc\";", run.ScriptContext);
			Assert.DoesNotContain("…", run.ScriptContext);
		}

		[Fact]
		public void EventHandlerLiteral_AlsoCarriesContext()
		{
			// An on* handler's value is JS too: its literal gets a window of the handler source.
			var run = Assert.Single(Script("<button onclick=\"showMsg('hallo welt')\">x</button>"));
			Assert.Equal("hallo welt", run.RawText);
			Assert.Equal("showMsg('hallo welt')", run.ScriptContext);
		}

		[Fact]
		public void NonScriptRuns_HaveNullContext()
		{
			var runs = DomTraverser.Traverse(Doc("<p>Hallo Welt</p>"), skipAttributeNames: null, checkJavaScript: true).ToList();
			Assert.All(runs.Where(r => r.Source != RunSource.Script), r => Assert.Null(r.ScriptContext));
		}
	}
}
