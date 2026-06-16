using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using Crawler.Boilerplate;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery: the 7a config model + page-to-group resolver. Tests the resolution rules
	/// (longest PathPrefix governs; check-page membership → prune nothing; no match → check
	/// everything). All fixtures synthetic.
	/// </summary>
	public class SpellCheckEngineConfigTests
	{
		private static BoilerplateGroupConfig Group(string prefix, string[] checkPages, params string[] classTokens)
		{
			return new BoilerplateGroupConfig
			{
				PathPrefix = prefix,
				PagesToCheckBoiler = new List<string>(checkPages),
				BoilerplateSelectors = classTokens
					.Select(t => new BoilerplateSelectorConfig { Type = "class", Value = t })
					.ToList(),
			};
		}

		// A node carrying a class, for probing whether a resolved matcher prunes it.
		private static HtmlNode NodeWithClass(string cls) => HtmlNode.CreateNode($"<div class=\"{cls}\">x</div>");

		[Fact]
		public void NoGroups_ReturnsNullMatcher_CheckEverything()
		{
			var r = new BoilerplateResolver(null);
			var (matcher, isCheck) = r.Resolve("/any/page.html");
			Assert.Null(matcher);
			Assert.False(isCheck);
		}

		[Fact]
		public void LongestPrefix_Governs()
		{
			var r = new BoilerplateResolver(new[]
			{
				Group("/de/", new[] { "/de/home.html" }, "general_nav"),
				Group("/de/section-a/", new[] { "/de/section-a/entry.html" }, "section_nav"),
			});

			// A page under the more specific prefix uses the section group's selectors.
			var (matcher, isCheck) = r.Resolve("/de/section-a/page1.html");
			Assert.False(isCheck);
			Assert.NotNull(matcher);
			Assert.True(matcher!.IsBoilerplate(NodeWithClass("section_nav")));
			Assert.False(matcher.IsBoilerplate(NodeWithClass("general_nav"))); // not the general group's

			// A page under only the general prefix uses the general group's selectors.
			var (m2, _) = r.Resolve("/de/other/page2.html");
			Assert.True(m2!.IsBoilerplate(NodeWithClass("general_nav")));
			Assert.False(m2.IsBoilerplate(NodeWithClass("section_nav")));
		}

		[Fact]
		public void CheckPage_FlaggedAsEntry_UsesOwnGroupsSelectors()
		{
			var r = new BoilerplateResolver(new[]
			{
				Group("/de/", new[] { "/de/home.html" }, "nav_block"),
			});

			var (matcher, isCheck) = r.Resolve("/de/home.html");
			Assert.True(isCheck);                 // the check page → prune nothing (caller passes entry=true)
			Assert.NotNull(matcher);              // its own group's matcher available
			Assert.True(matcher!.IsBoilerplate(NodeWithClass("nav_block")));
		}

		[Fact]
		public void CheckPage_LocationIndependentOfPrefix()
		{
			// Check page sits OUTSIDE its group's PathPrefix (root file governing /de/ descendants).
			var r = new BoilerplateResolver(new[]
			{
				Group("/de/", new[] { "/de-home.html" }, "nav_block"),
			});

			var (_, isCheckRoot) = r.Resolve("/de-home.html");
			Assert.True(isCheckRoot);  // recognized as a check page despite not being under /de/

			var (matcher, isCheckDesc) = r.Resolve("/de/page1.html");
			Assert.False(isCheckDesc); // a governed descendant prunes
			Assert.True(matcher!.IsBoilerplate(NodeWithClass("nav_block")));
		}

		[Fact]
		public void NoMatchingPrefix_AndNotCheckPage_ChecksEverything()
		{
			var r = new BoilerplateResolver(new[]
			{
				Group("/de/", new[] { "/de/home.html" }, "nav_block"),
			});

			var (matcher, isCheck) = r.Resolve("/en/page.html"); // no /en/ group
			Assert.Null(matcher);
			Assert.False(isCheck);
		}

		[Fact]
		public void Resolve_AcceptsFullUrl_NotJustPath()
		{
			var r = new BoilerplateResolver(new[]
			{
				Group("/de/", new[] { "/de/home.html" }, "nav_block"),
			});

			var (matcher, _) = r.Resolve("https://example.com/de/deep/page.html");
			Assert.NotNull(matcher);
			Assert.True(matcher!.IsBoilerplate(NodeWithClass("nav_block")));
		}

		[Fact]
		public void FourBranches_SharedSelectors_EachGoverned()
		{
			// Mirrors the real shape: four prefixes, same selector set, one home check-page each.
			var shared = new[] { "skip_a", "head_b", "foot_c" };
			var r = new BoilerplateResolver(new[]
			{
				Group("/de/", new[] { "/de/home.html" }, shared),
				Group("/en/", new[] { "/en/home.html" }, shared),
				Group("/fi/", new[] { "/fi/home.html" }, shared),
				Group("/sv/", new[] { "/sv/home.html" }, shared),
			});

			foreach (var branch in new[] { "de", "en", "fi", "sv" })
			{
				var (matcher, isCheck) = r.Resolve($"/{branch}/some/page.html");
				Assert.False(isCheck);
				Assert.True(matcher!.IsBoilerplate(NodeWithClass("head_b")));

				var (_, homeIsCheck) = r.Resolve($"/{branch}/home.html");
				Assert.True(homeIsCheck);
			}
		}
	}
}
