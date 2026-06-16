using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for Config.ResolveForSite — projecting one selected site onto the config
	/// and resolving the {tenant} token across the allow-listed path/filter/URL fields.
	///
	/// Contract pinned here:
	///   * PROJECTION: Url, UrlSubdomainsAllowed (wholesale copy — no aliasing/leak),
	///     and CmsContentList.PostCrawlPass are stamped from the selected site; the
	///     selected site is recorded on ResolvedSite.
	///   * TOKEN RESOLUTION: {tenant} → the site's Tenant in every allow-listed field,
	///     including list elements (ExtendedCrawlJsonPathPrefixes, RowsToExclude).
	///   * DEFAULT TENANT: an empty/whitespace Tenant resolves {tenant} to "default"
	///     (well-formed in every field shape), NOT to an empty string.
	///   * SURVIVES-HALT: a token that never resolves (a field outside the allow-list,
	///     or a typo'd token) is a halt; a token that resolves to "default" is fine.
	///   * NON-ALLOW-LISTED fields (e.g. Comment, parsing mechanics) are never touched.
	/// </summary>
	public class SiteResolutionTests
	{
		private static Config BaseConfig() => new()
		{
			Sites = [],
			CmsContentList = new CmsContentListConfig
			{
				Path = "D:\\Crawler\\{tenant}_content.csv",
				RowFilter = "/content/site/{tenant}/work",
				RowNegativeFilter = "Deactivate",
				RowsToExclude = ["/home/a", "/content/{tenant}/skip"],
				Comment = "Operator note — excluded from substitution, never walked.",
				ColumnDelimiter = ";",   // parsing mechanic — never substituted
			},
			ExtendedCrawlJsonPathPrefixes = ["/content/dam/{tenant}", "/static/nochange"],
			CustomDictionaryFile = "user_{tenant}_dictionary.dic",
			TicketGeneration = new TicketGenerationConfig
			{
				// Deeplink fields exercise BOTH tokens in one field per the deeplink
				// routing model: {tenant} = the tenant subtree, {productiongroup} = the
				// CMS routing group (e.g. pg01/pg02).
				CmsEditorBaseUrl = "https://cms.example.com/{productiongroup}/{tenant}/admin",
				CmsEditorBaseUrlSuffix = ".html",
			},
		};

		private static SiteConfig Site(string tenant, bool postCrawlPass = false, string productionGroup = "pg01") => new()
		{
			Name = "TestSite",
			Tenant = tenant,
			ProductionGroup = productionGroup,
			Url = "https://www.test.example.com",
			UrlSubdomainsAllowed = ["https://assets.test.example.com"],
			IsPrimary = true,
			PostCrawlPass = postCrawlPass,
		};

		// ── Projection ──────────────────────────────────────────────────────────

		[Fact]
		public void Projects_Url_Subdomains_PostCrawlPass_AndRecordsResolvedSite()
		{
			var cfg = BaseConfig();
			var site = Site("abc123", postCrawlPass: true);

			cfg.ResolveForSite(site);

			Assert.Equal("https://www.test.example.com", cfg.Url);
			Assert.Equal(["https://assets.test.example.com"], cfg.UrlSubdomainsAllowed);
			Assert.True(cfg.CmsContentList!.PostCrawlPass);
			Assert.Same(site, cfg.ResolvedSite);
		}

		[Fact]
		public void Subdomains_AreCopiedNotAliased_NoLeakAcrossResolve()
		{
			// Security boundary: the projected list must be a copy, so mutating the
			// site's list afterwards (or re-resolving another site) cannot bleed.
			var cfg = BaseConfig();
			var site = Site("abc123");
			cfg.ResolveForSite(site);

			site.UrlSubdomainsAllowed.Add("https://sneaky.example.com");

			Assert.DoesNotContain("https://sneaky.example.com", cfg.UrlSubdomainsAllowed);
			Assert.Single(cfg.UrlSubdomainsAllowed);
		}

		// ── Empty/whitespace subdomain entries sanitised at resolution (#445) ─────
		//
		// Security boundary: an empty string in UrlSubdomainsAllowed neutralises the
		// entire scope check downstream (StartsWith("") is always true), so the
		// crawler would follow any URL. ResolveForSite must strip empty/whitespace
		// entries so a sloppy config ("UrlSubdomainsAllowed": [ "" ]) cannot silently
		// disable containment. (IsValidLink also guards this — defence in depth.)

		[Fact]
		public void Resolve_StripsEmptyStringSubdomainEntry()
		{
			var cfg = BaseConfig();
			var site = Site("abc123");
			site.UrlSubdomainsAllowed = [""];

			cfg.ResolveForSite(site);

			Assert.Empty(cfg.UrlSubdomainsAllowed);
		}

		[Fact]
		public void Resolve_StripsWhitespaceSubdomainEntry()
		{
			var cfg = BaseConfig();
			var site = Site("abc123");
			site.UrlSubdomainsAllowed = ["   "];

			cfg.ResolveForSite(site);

			Assert.Empty(cfg.UrlSubdomainsAllowed);
		}

		[Fact]
		public void Resolve_KeepsValidEntries_DropsEmptyOnes()
		{
			var cfg = BaseConfig();
			var site = Site("abc123");
			site.UrlSubdomainsAllowed = ["", "https://assets.test.example.com", "   "];

			cfg.ResolveForSite(site);

			Assert.Equal(["https://assets.test.example.com"], cfg.UrlSubdomainsAllowed);
		}

		// ── Tenant substitution: real tenant ─────────────────────────────────────

		[Fact]
		public void RealTenant_SubstitutedInScalarAndListFields()
		{
			var cfg = BaseConfig();
			cfg.ResolveForSite(Site("abc123"));

			Assert.Equal("D:\\Crawler\\abc123_content.csv", cfg.CmsContentList!.Path);
			Assert.Equal("/content/site/abc123/work", cfg.CmsContentList.RowFilter);
			Assert.Equal("/content/abc123/skip", cfg.CmsContentList.RowsToExclude[1]);
			Assert.Equal("/content/dam/abc123", cfg.ExtendedCrawlJsonPathPrefixes[0]);
			Assert.Equal("user_abc123_dictionary.dic", cfg.CustomDictionaryFile);
			// Deeplink: both tokens resolved (default site builder uses ProductionGroup="pg01").
			Assert.Equal("https://cms.example.com/pg01/abc123/admin", cfg.TicketGeneration.CmsEditorBaseUrl);
		}

		// ── {productiongroup} substitution ──────────────────────────────────────

		[Fact]
		public void ProductionGroup_SubstitutedAlongsideTenant()
		{
			// Both tokens substitute independently in the same field; the order does not matter.
			var cfg = BaseConfig();
			cfg.ResolveForSite(Site("abc123", productionGroup: "pg02"));
			Assert.Equal("https://cms.example.com/pg02/abc123/admin", cfg.TicketGeneration.CmsEditorBaseUrl);
		}

		[Theory]
		[InlineData("")]
		[InlineData("   ")]
		[InlineData(null)]
		public void EmptyOrWhitespaceProductionGroup_ResolvesToDefault(string? productionGroup)
		{
			// Symmetric with empty Tenant — empty ProductionGroup → "default". A default-
			// group deeplink simply does not match any CMS route (harmless miss).
			var cfg = BaseConfig();
			cfg.ResolveForSite(Site("abc123", productionGroup: productionGroup!));
			Assert.Equal("https://cms.example.com/default/abc123/admin", cfg.TicketGeneration.CmsEditorBaseUrl);
			Assert.DoesNotContain("{productiongroup}", cfg.TicketGeneration.CmsEditorBaseUrl);
		}

		[Fact]
		public void BothTokensEmpty_BothResolveToDefault()
		{
			var cfg = BaseConfig();
			cfg.ResolveForSite(Site("", productionGroup: ""));
			Assert.Equal("https://cms.example.com/default/default/admin", cfg.TicketGeneration.CmsEditorBaseUrl);
			Assert.Equal("D:\\Crawler\\default_content.csv", cfg.CmsContentList!.Path);
		}

		[Fact]
		public void NonAllowListedFields_AreNeverSubstituted()
		{
			var cfg = BaseConfig();
			cfg.ResolveForSite(Site("abc123"));

			// Comment is excluded from the allow-list — it is operator prose, not a
			// path, and is never walked. (It carries no token here; the point is the
			// resolver does not touch it.)
			Assert.Equal("Operator note — excluded from substitution, never walked.", cfg.CmsContentList!.Comment);
			// Parsing mechanic untouched.
			Assert.Equal(";", cfg.CmsContentList.ColumnDelimiter);
			// A list element with no token is unchanged.
			Assert.Equal("/static/nochange", cfg.ExtendedCrawlJsonPathPrefixes[1]);
			Assert.Equal(".html", cfg.TicketGeneration.CmsEditorBaseUrlSuffix);
		}

		// ── Default tenant (empty/whitespace) ─────────────────────────────────────

		[Theory]
		[InlineData("")]
		[InlineData("   ")]
		[InlineData(null)]
		public void EmptyOrWhitespaceTenant_ResolvesToDefault_NotEmptyString(string? tenant)
		{
			var cfg = BaseConfig();
			cfg.ResolveForSite(Site(tenant!));

			Assert.Equal("D:\\Crawler\\default_content.csv", cfg.CmsContentList!.Path);
			Assert.Equal("/content/dam/default", cfg.ExtendedCrawlJsonPathPrefixes[0]);
			Assert.Equal("/content/site/default/work", cfg.CmsContentList.RowFilter);
			// No empty segments, no surviving token, no halt.
			Assert.DoesNotContain("{tenant}", cfg.CmsContentList.Path);
		}

		// ── Survives-halt ─────────────────────────────────────────────────────────

		[Fact]
		public void SelfReferentialTenant_LeavesSurvivingToken_Halts()
		{
			// The survives-halt is scoped to allow-listed fields, which are exactly the
			// fields just substituted — so after a normal resolve none can still hold a
			// token. The one way a token survives is a pathological per-site value that
			// itself contains the token: substituting {tenant} -> "x{tenant}y" re-introduces
			// it. Same applies to {productiongroup}. This pins the halt fires by name.
			var cfg = BaseConfig();
			var ex = Assert.Throws<InvalidOperationException>(
				() => cfg.ResolveForSite(Site("pre{tenant}post")));
			Assert.Contains("Unresolved token", ex.Message);
		}

		[Fact]
		public void SelfReferentialProductionGroup_LeavesSurvivingToken_Halts()
		{
			// Same pathological case for {productiongroup}.
			var cfg = BaseConfig();
			var ex = Assert.Throws<InvalidOperationException>(
				() => cfg.ResolveForSite(Site("abc123", productionGroup: "pre{productiongroup}post")));
			Assert.Contains("Unresolved token", ex.Message);
		}

		[Fact]
		public void NormalResolve_NeverHalts_NoSurvivingToken()
		{
			// Positive: after a normal resolve, no allow-listed field retains either
			// token, so the survives-halt does not fire.
			var cfg = BaseConfig();
			cfg.ResolveForSite(Site("abc123"));   // must not throw

			Assert.DoesNotContain("{tenant}", cfg.CmsContentList!.Path);
			Assert.DoesNotContain("{tenant}", cfg.CmsContentList.RowFilter);
			Assert.DoesNotContain("{tenant}", cfg.ExtendedCrawlJsonPathPrefixes[0]);
			Assert.DoesNotContain("{tenant}", cfg.CustomDictionaryFile);
			Assert.DoesNotContain("{tenant}", cfg.TicketGeneration.CmsEditorBaseUrl);
			Assert.DoesNotContain("{productiongroup}", cfg.TicketGeneration.CmsEditorBaseUrl);
		}

		// ── Dev-debt net: the templatable-field roster ────────────────────────────

		[Fact]
		public void EveryTemplatableField_ResolvesTheToken()
		{
			// THIS IS THE ROSTER. It lists every field registered in
			// Config.TenantSubstitutableFields, each seeded with {tenant}, and asserts
			// each one resolved. When a dev adds a new templatable field, they extend
			// the allow-list AND add a line here. If they forget the allow-list, the
			// new assertion fails and points straight at the unregistered field.
			// (Forgetting to register is dev debt, caught at test time — not an
			// operator-facing runtime concern.)
			var cfg = BaseConfig();   // every registered field carries {tenant}
			cfg.ResolveForSite(Site("abc123"));

			// CmsContentList path/filter family
			Assert.Equal("D:\\Crawler\\abc123_content.csv", cfg.CmsContentList!.Path);
			Assert.Equal("/content/site/abc123/work", cfg.CmsContentList.RowFilter);
			Assert.Equal("/content/abc123/skip", cfg.CmsContentList.RowsToExclude[1]);
			// RowNegativeFilter carries no token in the fixture but is registered;
			// seed-and-check it explicitly so the roster covers it too.
			var cfg2 = BaseConfig();
			cfg2.CmsContentList!.RowNegativeFilter = "/neg/{tenant}";
			cfg2.ResolveForSite(Site("abc123"));
			Assert.Equal("/neg/abc123", cfg2.CmsContentList.RowNegativeFilter);

			// Top-level / cross-object fields
			Assert.Equal("/content/dam/abc123", cfg.ExtendedCrawlJsonPathPrefixes[0]);
			Assert.Equal("user_abc123_dictionary.dic", cfg.CustomDictionaryFile);
			// Deeplink fields carry BOTH tokens — both resolved (Site() defaults
			// ProductionGroup to "pg01"; CmsEditorBaseUrl in fixture has both tokens).
			Assert.Equal("https://cms.example.com/pg01/abc123/admin", cfg.TicketGeneration.CmsEditorBaseUrl);
			// CmsEditorBaseUrlSuffix is registered; seed-and-check it with BOTH tokens
			// to lock that it accepts either or both.
			var cfg3 = BaseConfig();
			cfg3.TicketGeneration.CmsEditorBaseUrlSuffix = "_{tenant}_{productiongroup}.html";
			cfg3.ResolveForSite(Site("abc123", productionGroup: "pg02"));
			Assert.Equal("_abc123_pg02.html", cfg3.TicketGeneration.CmsEditorBaseUrlSuffix);
		}
	}
}
