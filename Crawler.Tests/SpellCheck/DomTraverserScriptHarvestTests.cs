using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the script-harvest path in <see cref="DomTraverser"/>: when script checking is enabled,
	/// an inline &lt;script&gt; body is lexed into string literals and each becomes its own
	/// <see cref="RunSource.Script"/> run — decoded, bound to the &lt;script&gt; element, and located
	/// by line:column within the body. Gating (which literals are actually checked) is RunChecker's
	/// job, so harvest emits ALL literals here.
	///
	/// Fixtures are invented, neutral JavaScript.
	/// </summary>
	public class DomTraverserScriptHarvestTests
	{
		private static HtmlDocument Doc(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		[Fact]
		public void ScriptCheckingOff_EmitsNoScriptRun()
		{
			// Default (checkJavaScript = false): the script subtree is pruned, nothing harvested.
			var runs = DomTraverser.Traverse(Doc("<html><body><script>var x = 'hallo welt';</script></body></html>")).ToList();
			Assert.DoesNotContain(runs, r => r.Source == RunSource.Script);
			Assert.DoesNotContain(runs, r => r.RawText.Contains("hallo"));
		}

		[Fact]
		public void ScriptCheckingOn_EmitsScriptRun_DecodedTextAndLineCol()
		{
			var runs = DomTraverser.Traverse(
				Doc("<html><body><script>var x = 'hallo welt';</script></body></html>"),
				checkJavaScript: true).ToList();

			var run = runs.Single(r => r.Source == RunSource.Script);
			Assert.Equal("hallo welt", run.RawText);
			Assert.Equal("script[L1:9]", run.SourcePath);
			Assert.Equal("script", run.Node.Name);
		}

		[Fact]
		public void ScriptRun_BoundToScriptElement_NotBlockAncestor()
		{
			// Even inside a <div>, the run binds to the <script> element (so location is computable
			// and the excerpt never falls back to the div's text).
			var runs = DomTraverser.Traverse(
				Doc("<div><script>fn('hallo welt');</script></div>"),
				checkJavaScript: true).ToList();

			var run = runs.Single(r => r.Source == RunSource.Script);
			Assert.Equal("script", run.Node.Name);
		}

		[Fact]
		public void Script_ObjectKeysDropped_ValuesKept()
		{
			var runs = DomTraverser.Traverse(
				Doc("<script>var o = {'key': 'wert gut'};</script>"),
				checkJavaScript: true).ToList();

			var scriptRuns = runs.Where(r => r.Source == RunSource.Script).ToList();
			Assert.Single(scriptRuns);
			Assert.Equal("wert gut", scriptRuns[0].RawText);
			Assert.Equal("script[L1:17]", scriptRuns[0].SourcePath);
			Assert.DoesNotContain(scriptRuns, r => r.RawText == "key");
		}

		[Fact]
		public void Script_MultipleLiterals_LineColPerLiteral()
		{
			var runs = DomTraverser.Traverse(
				Doc("<script>a('eins');\nb('zwei');</script>"),
				checkJavaScript: true).ToList();

			var scriptRuns = runs.Where(r => r.Source == RunSource.Script).ToList();
			Assert.Equal(2, scriptRuns.Count);
			Assert.Equal("eins", scriptRuns[0].RawText);
			Assert.Equal("script[L1:3]", scriptRuns[0].SourcePath);
			Assert.Equal("zwei", scriptRuns[1].RawText);
			Assert.Equal("script[L2:3]", scriptRuns[1].SourcePath);
		}

		[Fact]
		public void Script_EmitsAllLiterals_GatingIsNotDoneHere()
		{
			// A machine-slug literal is still emitted by harvest; RunChecker is what drops it later.
			var runs = DomTraverser.Traverse(
				Doc("<script>fn('fooBar');</script>"),
				checkJavaScript: true).ToList();

			Assert.Contains(runs, r => r.Source == RunSource.Script && r.RawText == "fooBar");
		}
	}
}
