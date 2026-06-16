using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using Crawler.Boilerplate;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery: the boilerplate prune — typed-selector matcher + pruning traversal (§4). A
	/// BLACKLIST: default checks everything; declared selectors are suppressed on non-entry pages;
	/// undeclared content is always checked (never silently dropped). All fixtures synthetic.
	/// </summary>
	public class SpellCheckBoilerplateTests
	{
		private static HtmlDocument Doc(string html)
		{
			var d = new HtmlDocument();
			d.LoadHtml(html);
			return d;
		}

		private static BoilerplateMatcher Class(params string[] values) =>
			new(values.Select(v => new BoilerplateSelector("class", v)));

		// ---- class subset-token matching ----

		[Fact]
		public void Class_MatchesWholeToken_NotSubstring()
		{
			var m = Class("foot");
			// "foot" must NOT match an element whose class token is "footer".
			var node = HtmlNode.CreateNode("<div class=\"footer\">x</div>");
			Assert.False(m.IsBoilerplate(node));
		}

		[Fact]
		public void Class_MatchesMultiClassElement_Correctly()
		{
			var m = Class("foo_footer");
			// exact-string match would fail here; subset/token match must succeed.
			var node = HtmlNode.CreateNode("<div class=\"foo_outer foo_footer\">x</div>");
			Assert.True(m.IsBoilerplate(node));
		}

		[Fact]
		public void Class_MultiToken_RequiresAllPresent()
		{
			var m = Class("foo_outer foo_footer");
			Assert.True(m.IsBoilerplate(HtmlNode.CreateNode("<div class=\"foo_outer foo_footer x\">y</div>")));
			Assert.False(m.IsBoilerplate(HtmlNode.CreateNode("<div class=\"foo_outer\">y</div>"))); // missing foo_footer
		}

		[Fact]
		public void Class_IsCaseSensitive()
		{
			var m = Class("Footer");
			Assert.False(m.IsBoilerplate(HtmlNode.CreateNode("<div class=\"footer\">x</div>")));
		}

		// ---- ancestor pruning ----

		[Fact]
		public void Node_InsideBoilerplateAncestor_IsBoilerplate()
		{
			var doc = Doc("<div class=\"nav_block\"><ul><li><a>Link</a></li></ul></div>");
			var anchorText = doc.DocumentNode.SelectSingleNode("//a").FirstChild;
			var m = Class("nav_block");
			Assert.True(m.IsBoilerplate(anchorText));
		}

		// ---- xpath selector ----

		[Fact]
		public void Xpath_MatchesByRoleAttribute()
		{
			var doc = Doc("<div role=\"navigation\"><p>Menu</p></div><p>Content</p>");
			var m = new BoilerplateMatcher(new[] { new BoilerplateSelector("xpath", "//div[@role='navigation']") });

			var menuText = doc.DocumentNode.SelectSingleNode("//div[@role='navigation']/p").FirstChild;
			var contentText = doc.DocumentNode.SelectNodes("//p").Last().FirstChild;

			Assert.True(m.IsBoilerplate(menuText));
			Assert.False(m.IsBoilerplate(contentText));
		}

		[Fact]
		public void Xpath_MatchesByDataAttribute()
		{
			var doc = Doc("<div data-region=\"footer\"><span>foot</span></div>");
			var m = new BoilerplateMatcher(new[] { new BoilerplateSelector("xpath", "//div[@data-region='footer']") });
			var span = doc.DocumentNode.SelectSingleNode("//span");
			Assert.True(m.IsBoilerplate(span));
		}

		// ---- empty / fail-loud default ----

		[Fact]
		public void NoSelectors_IsEmpty_NothingBoilerplate()
		{
			var m = new BoilerplateMatcher(null);
			Assert.True(m.IsEmpty);
			Assert.False(m.IsBoilerplate(HtmlNode.CreateNode("<div class=\"footer\">x</div>")));
		}

		// ---- pruning traversal ----

		[Fact]
		public void Traverse_NonEntryPage_PrunesBoilerplate_KeepsContent()
		{
			var doc = Doc("<body><div class=\"nav_block\"><a>NavWord</a></div><p>ContentWord</p></body>");
			var m = Class("nav_block");

			var runs = DomTraverser.Traverse(doc, m, isEntryPage: false).ToList();

			Assert.Contains(runs, r => r.RawText.Contains("ContentWord"));
			Assert.DoesNotContain(runs, r => r.RawText.Contains("NavWord"));
		}

		[Fact]
		public void Traverse_EntryPage_KeepsBoilerplate()
		{
			var doc = Doc("<body><div class=\"nav_block\"><a>NavWord</a></div><p>ContentWord</p></body>");
			var m = Class("nav_block");

			var runs = DomTraverser.Traverse(doc, m, isEntryPage: true).ToList();

			Assert.Contains(runs, r => r.RawText.Contains("ContentWord"));
			Assert.Contains(runs, r => r.RawText.Contains("NavWord")); // boilerplate checked on entry page
		}

		[Fact]
		public void Traverse_NoSelectors_ChecksEverything_FailLoudDefault()
		{
			var doc = Doc("<body><div class=\"nav_block\"><a>NavWord</a></div><p>ContentWord</p></body>");
			var m = new BoilerplateMatcher(null);

			var runs = DomTraverser.Traverse(doc, m, isEntryPage: false).ToList();

			// Nothing declared → everything checked, including the nav.
			Assert.Contains(runs, r => r.RawText.Contains("NavWord"));
			Assert.Contains(runs, r => r.RawText.Contains("ContentWord"));
		}

		[Fact]
		public void Traverse_PrunesBoilerplateAttributes_Too()
		{
			var doc = Doc("<body><div class=\"nav_block\"><img alt=\"NavAlt\"></div><img alt=\"ContentAlt\"></body>");
			var m = Class("nav_block");

			var runs = DomTraverser.Traverse(doc, m, isEntryPage: false).ToList();

			Assert.Contains(runs, r => r.RawText.Contains("ContentAlt"));
			Assert.DoesNotContain(runs, r => r.RawText.Contains("NavAlt")); // attr inside boilerplate pruned
		}
	}
}
