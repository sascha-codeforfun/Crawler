using Crawler.Quality;
using Crawler.Urls;
using Xunit;

namespace Crawler.Tests.Quality
{
	public class UnwantedPatternsTests
	{
		private static ContentQualityConfig DefaultConfig() => new()
		{
			ContentQualityExcerptRadius    = 120,
			ContentQualityQuoteFullSentence = false,  // keep tests deterministic
			ContentQualityMaxExcerpt  = 400,
		};

		// ── Check ─────────────────────────────────────────────

		[Fact]
		public void Check_NoMatch_ReturnsEmpty()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Template", Name = "Unfilled", Patterns = ["%("], CaseSensitive = true }
			};
			var issues = UnwantedPatterns.Check("f.html", "<p>Clean content</p>", patterns, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_Match_ReturnsIssue()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Template", Name = "Unfilled", Patterns = ["%("], CaseSensitive = true }
			};
			var issues = UnwantedPatterns.Check("f.html", "<p>%(unfilled_var)</p>", patterns, DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("UNWANTED_PATTERN", issues[0].IssueType);
		}

		[Fact]
		public void Check_CaseInsensitive_MatchesRegardlessOfCase()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Test", Name = "Keyword", Patterns = ["todo"], CaseSensitive = false }
			};
			var issues = UnwantedPatterns.Check("f.html", "<p>TODO: fix this</p>", patterns, DefaultConfig()).ToList();
			Assert.Single(issues);
		}

		[Fact]
		public void Check_CaseSensitive_NoMatchOnWrongCase()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Test", Name = "Keyword", Patterns = ["todo"], CaseSensitive = true }
			};
			var issues = UnwantedPatterns.Check("f.html", "<p>TODO: fix this</p>", patterns, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_MultipleOccurrences_ReportsAll()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Template", Name = "Unfilled", Patterns = ["%("], CaseSensitive = true }
			};
			var issues = UnwantedPatterns.Check("f.html", "%(var1) and %(var2)", patterns, DefaultConfig()).ToList();
			Assert.Equal(2, issues.Count);
		}

		[Fact]
		public void Check_UnconfiguredGroup_Skipped()
		{
			// IsConfigured returns false when Patterns is empty
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Test", Name = "Empty", Patterns = [], CaseSensitive = true }
			};
			var issues = UnwantedPatterns.Check("f.html", "anything", patterns, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_OpenEnvelopeWithReferenceHits_CoalescesToOneIssue()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"], Reference = "CMS-Editor-Error" },
				new() { Category = "Security", Name = "CMS-Editor-Error", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["produkt.", "p_name"] }
			};
			// Opener %( present, closer )% absent (the broken case); produkt. and p_name sit
			// in the unbroken run after the opener → all three collapse into one finding.
			var issues = UnwantedPatterns.Check(
				"f.html", "<x>%(produkt.278.p_name)</x>", patterns, DefaultConfig()).ToList();

			var issue = Assert.Single(issues);
			Assert.Equal("UNWANTED_PATTERN", issue.IssueType);
			Assert.Contains("CMS-Parameter-Leak", issue.Detail);
			Assert.Contains("missing closing ')%'", issue.Detail);
			Assert.Contains("— patterns: %(, produkt., p_name", issue.Detail);
		}

		[Fact]
		public void Check_OpenEnvelopeNoReferenceHitsInRange_EachFiresAlone()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"], Reference = "CMS-Editor-Error" },
				new() { Category = "Security", Name = "CMS-Editor-Error", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["produkt.", "p_name"] }
			};
			// produkt. is past the whitespace bounding the open envelope's region → not folded.
			var issues = UnwantedPatterns.Check(
				"f.html", "%(foo.bar) produkt.", patterns, DefaultConfig()).ToList();

			Assert.Equal(2, issues.Count);
			Assert.Contains(issues, x => x.Detail.Contains("— pattern: %("));
			Assert.Contains(issues, x => x.Detail.Contains("— pattern: produkt."));
			Assert.DoesNotContain(issues, x => x.Detail.Contains("open placeholder"));
		}

		[Fact]
		public void Check_EnvelopeWithoutReference_DoesNotCoalesce()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"] },   // no Reference → no coalescing
				new() { Category = "Security", Name = "CMS-Editor-Error", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["produkt.", "p_name"] }
			};
			var issues = UnwantedPatterns.Check(
				"f.html", "<x>%(produkt.278.p_name)</x>", patterns, DefaultConfig()).ToList();

			// Envelope fires (pattern: %() and both editor hits fire separately — three cards.
			Assert.Equal(3, issues.Count);
			Assert.DoesNotContain(issues, x => x.Detail.Contains("open placeholder"));
		}

		[Fact]
		public void Check_BalancedEnvelope_NotCoalesced()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"], Reference = "CMS-Editor-Error" },
				new() { Category = "Security", Name = "CMS-Editor-Error", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["produkt.", "p_name"] }
			};
			// Both delimiters present → balanced, not the broken case → no coalescing.
			var issues = UnwantedPatterns.Check(
				"f.html", "<x>%(produkt.278.p_name)%</x>", patterns, DefaultConfig()).ToList();

			Assert.DoesNotContain(issues, x => x.Detail.Contains("open placeholder"));
			Assert.Contains(issues, x => x.Detail.Contains("— patterns: %(, )%"));
			Assert.Equal(3, issues.Count);
		}

		[Fact]
		public void Check_RegionStopsAtMarkupBoundary()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"], Reference = "Inner" },
				new() { Category = "Security", Name = "Inner", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["institut.", "name"] }
			};
			// The 'name' inside the placeholder folds; the 'name' after </p> is past the '<'
			// boundary and must NOT be folded — it surfaces on its own.
			var issues = UnwantedPatterns.Check(
				"f.html", "%(institut.name)</p>name", patterns, DefaultConfig()).ToList();

			Assert.Contains(issues, x => x.Detail.Contains("open placeholder")
				&& x.Detail.Contains("institut."));
			Assert.Contains(issues, x => x.Detail == "Security: Inner — pattern: name");
		}

		// ── ExtractHighlightPatterns: UNWANTED_PATTERN merged-Detail round-trip ──

		[Fact]
		public void ExtractHighlightPatterns_MergedEnvelopeDetail_ReturnsAllPatterns()
		{
			// The merged Detail must round-trip through the highlighter so every folded
			// pattern is marked on the card and in the ticket.
			var word = "UNWANTED_PATTERN:Security: CMS-Parameter-Leak — open placeholder, " +
				"missing closing ')%' — patterns: %(, produkt., p_name";
			var result = ContentQualityTriage.ExtractHighlightPatterns(word);
			Assert.Equal(new[] { "%(", "produkt.", "p_name" }, result);
		}

		// ── OnlyFlagUnbalanced (envelope-only): well-formed binding silent, malformed fires ──

		private static List<ContentUnwantedPattern> MustacheBinding() => new()
		{
			new()
			{
				Category = "Security", Name = "Mustache", GroupPatterns = true,
				CaseSensitive = true, OnlyFlagUnbalanced = true, Patterns = ["{{", "}}"]
			}
		};

		[Theory]
		[InlineData("in unserer {{link1}} und dem {{link2}}.")] // both well-formed — the real-data fix
		[InlineData("text {{foo}} text")]                       // well-formed
		[InlineData("path {{object.name}} here")]               // well-formed dotted
		[InlineData("Ihre Suche ergab {n} Treffer.")]          // single-brace placeholder — not a doubled fence
		[InlineData("a {foo broke")]                            // lone single opener brace
		[InlineData("broke foo} here")]                         // lone single closer brace
		[InlineData("<p>no braces here at all</p>")]            // no binding
		public void OnlyFlagUnbalanced_WellFormedOrNoFullFence_Silent(string html)
		{
			var issues = UnwantedPatterns.Check("f.html", html, MustacheBinding(), DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Theory]
		[InlineData("text {foo}} text")]                        // single opener brace
		[InlineData("balance account_balance}} broke")]         // opener missing
		[InlineData("text {{foo} text")]                        // single closer brace
		[InlineData("text {{foo broke")]                        // closer missing
		public void OnlyFlagUnbalanced_Malformed_Fires(string html)
		{
			var issues = UnwantedPatterns.Check("f.html", html, MustacheBinding(), DefaultConfig()).ToList();
			var issue = Assert.Single(issues);
			Assert.Equal("UNWANTED_PATTERN", issue.IssueType);
		}

		[Theory]
		[InlineData("@media (max-width:680px){.nav a.txt{display:none}}")] // CSS — ':' flank
		[InlineData("...\\\"section\\\": 2}},{\\\"id\\\": \\\"f1\\\"...")]  // JSON — ',' after }}
		[InlineData("...&quot;s&quot;:null}}'>")]                          // JSON — ':' / '\'' flanks
		[InlineData("...&#34;buttonlabelnext&#34;:&#34;Weiter&#34;}}\">")] // encoded — '\"' flank
		public void OnlyFlagUnbalanced_StructuralBraces_Ignored(string html)
		{
			var issues = UnwantedPatterns.Check("f.html", html, MustacheBinding(), DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void OnlyFlagUnbalanced_DefaultFalse_AnyOccurrenceStillFires()
		{
			// %( )% must surface even when perfectly paired — the delimiter must never reach
			// the user. OnlyFlagUnbalanced defaults false, so the binding-shape gate is bypassed.
			var patterns = new List<ContentUnwantedPattern>
			{
				new()
				{
					Category = "Security", Name = "Percent", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"]
				}
			};
			var issues = UnwantedPatterns.Check("f.html", "<p>%(institut.name)%</p>", patterns, DefaultConfig()).ToList();
			Assert.Single(issues);
		}

		// ── CheckStyle / CheckScript: per-pattern region opt-out ──

		private static List<ContentUnwantedPattern> MustacheStyleOff() => new()
		{
			new()
			{
				Category = "Security", Name = "Mustache", GroupPatterns = true, CaseSensitive = true,
				OnlyFlagUnbalanced = true, CheckStyle = false, Patterns = ["{{", "}}"]
			}
		};

		// A CSS block close like "30px 1fr}}" is a space-flanked dangling-close shape — it fires
		// by default, which is the real-corpus false positive CheckStyle:false removes.
		private const string StyledCssClose =
			"<style>@media (max-width:560px){li.row{grid-template-columns:30px 1fr}} </style><p>clean body</p>";

		[Fact]
		public void CheckStyle_DefaultTrue_FiresInsideStyle()
		{
			var issues = UnwantedPatterns.Check("f.html", StyledCssClose, MustacheBinding(), DefaultConfig()).ToList();
			Assert.Single(issues);
		}

		[Fact]
		public void CheckStyle_False_SilentInsideStyle()
		{
			var issues = UnwantedPatterns.Check("f.html", StyledCssClose, MustacheStyleOff(), DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void CheckStyle_False_StillFiresInBody()
		{
			// Masking <style> is surgical: a real malformed binding in the body still fires.
			const string html = "<style>li{grid:30px 1fr}}</style><p>balance account_balance}} broke</p>";
			var issues = UnwantedPatterns.Check("f.html", html, MustacheStyleOff(), DefaultConfig()).ToList();
			var issue = Assert.Single(issues);
			Assert.Equal("UNWANTED_PATTERN", issue.IssueType);
		}

		[Fact]
		public void CheckScript_False_SilentInsideScript_DefaultFires()
		{
			const string html = "<script>foo bar}} baz</script><p>clean</p>";
			var on = UnwantedPatterns.Check("f.html", html, MustacheBinding(), DefaultConfig()).ToList();
			Assert.Single(on); // default CheckScript:true sees the space-flanked }} in script

			var off = new List<ContentUnwantedPattern>
			{
				new()
				{
					Category = "Security", Name = "Mustache", GroupPatterns = true, CaseSensitive = true,
					OnlyFlagUnbalanced = true, CheckScript = false, Patterns = ["{{", "}}"]
				}
			};
			var offIssues = UnwantedPatterns.Check("f.html", html, off, DefaultConfig()).ToList();
			Assert.Empty(offIssues);
		}
	}

	// All ExcludeHref tests live here. With ExcludeHref enabled the scan resolves the
	// page url via the crawl index (Pass 4, culprit check), and a lookup miss legitimately
	// logs — so these tests need the Logger/Cache setup other index-touching tests use.
	// Under normal operation a crawled+indexed file always resolves; a miss only happens if
	// the crawl index (log 3) or the downloads folder was tampered with, which is why the
	// miss path logs rather than staying silent. Fixtures are synthetic (invented pattern
	// "oldword", example.com urls).
	[Collection("Logger")]
	public class UnwantedPatternsExcludeHrefTests : System.IDisposable
	{
		private readonly string _tempDir;

		public UnwantedPatternsExcludeHrefTests()
		{
			_tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"));
			System.IO.Directory.CreateDirectory(_tempDir);
			Logger.Initialize(System.IO.Path.Combine(_tempDir, "log.txt"), silent: true);
		}

		public void Dispose()
		{
			try { System.IO.Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		private static ContentQualityConfig DefaultConfig() => new()
		{
			ContentQualityExcerptRadius = 120,
			ContentQualityQuoteFullSentence = false,
			ContentQualityMaxExcerpt = 400,
		};

		private void MapFileToUrl(string filename, string url)
		{
			var lookup = System.IO.Path.Combine(_tempDir, $"lookup_{System.Guid.NewGuid():N}.log");
			System.IO.File.WriteAllLines(lookup, new[] { $"{filename}|{url}|discovery" }, System.Text.Encoding.UTF8);
			Cache.Load(lookup);
		}

		private static List<ContentUnwantedPattern> HrefSet(bool excludeHref) =>
		[
			new()
			{
				Category = "Legacy", Name = "Old", GroupPatterns = false,
				CaseSensitive = false, ExcludeHref = excludeHref, Patterns = ["oldword"]
			}
		];

		private static List<ContentUnwantedPattern> HrefSet() => HrefSet(excludeHref: true);

		// ── mask href VALUES before scanning ─────────────────────────────────
		// A pattern surviving only in a link slug (removed from content, still in the
		// URL) repeats on every page carrying that link and buries the operator.
		// ExcludeHref blanks href values so the pattern does not fire there, while the
		// anchor's own text and all other content still scan on raw input.

		[Fact]
		public void ExcludeHref_False_PatternInHref_Fires()
		{
			var html = "<a href=\"/path/oldword-page.html\">Link</a>";
			var issues = UnwantedPatterns.Check("f.html", html, HrefSet(excludeHref: false), DefaultConfig()).ToList();
			Assert.Single(issues);
		}

		[Fact]
		public void ExcludeHref_True_PatternInHref_Suppressed()
		{
			var html = "<a href=\"/path/oldword-page.html\">Link</a>";
			var issues = UnwantedPatterns.Check("f.html", html, HrefSet(excludeHref: true), DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void ExcludeHref_True_PatternInAnchorText_StillFires()
		{
			// The term in the link's visible text is a genuine leftover in content and
			// must still surface even when href values are excluded.
			var html = "<a href=\"/path/clean-page.html\">See oldword here</a>";
			var issues = UnwantedPatterns.Check("f.html", html, HrefSet(excludeHref: true), DefaultConfig()).ToList();
			Assert.Single(issues);
		}

		[Fact]
		public void ExcludeHref_True_PatternInBodyText_StillFires()
		{
			var html = "<p>oldword in prose</p><a href=\"/path/oldword-x.html\">L</a>";
			var issues = UnwantedPatterns.Check("f.html", html, HrefSet(excludeHref: true), DefaultConfig()).ToList();
			// Only the body-text occurrence survives; the href one is masked.
			Assert.Single(issues);
		}

		[Fact]
		public void ExcludeHref_True_DataHref_NotMasked_StillFires()
		{
			// Word-boundary safety: data-href / *-href are not real href attributes and
			// are not masked.
			var html = "<a data-href=\"/path/oldword-x.html\">L</a>";
			var issues = UnwantedPatterns.Check("f.html", html, HrefSet(excludeHref: true), DefaultConfig()).ToList();
			Assert.Single(issues);
		}

		[Fact]
		public void ExcludeHref_True_SingleQuotedHref_Suppressed()
		{
			var html = "<a href='/path/oldword-page.html'>L</a>";
			var issues = UnwantedPatterns.Check("f.html", html, HrefSet(excludeHref: true), DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void ExcludeHref_True_ExcerptStillShowsOriginalText()
		{
			// Masking affects matching only; the excerpt is taken from the original html,
			// so a surviving finding's context still shows the real surrounding markup.
			var html = "<p>oldword</p><a href=\"/path/oldword-x.html\">L</a>";
			var issue = UnwantedPatterns.Check("f.html", html, HrefSet(excludeHref: true), DefaultConfig()).Single();
			Assert.Contains("oldword", issue.Context);
		}

		// ── culprit url — page whose OWN url carries the pattern stays visible ──

		[Fact]
		public void ExcludeHref_PatternInPageOwnUrl_StillFlagged_AsCulprit()
		{
			// The page whose OWN url carries the pattern is the source of the slug the
			// masked links point at; it must stay visible even though the repeated links
			// are silenced. Body has no occurrence — only the url does.
			var filename = $"culprit_{System.Guid.NewGuid():N}.html";
			MapFileToUrl(filename, "https://example.com/products/oldword-page.html");

			var html = "<a href=\"/products/oldword-page.html\">Home</a><p>clean body</p>";
			var issues = UnwantedPatterns.Check(filename, html, HrefSet(), DefaultConfig()).ToList();

			var culprit = Assert.Single(issues);
			Assert.Contains("in this page's url", culprit.Detail);
			Assert.Contains("oldword", culprit.Detail);
		}

		[Fact]
		public void ExcludeHref_PatternNotInOwnUrl_NoCulpritFinding()
		{
			// A page merely carrying the nav link (its own url is clean) yields nothing:
			// the href is masked and the url does not match.
			var filename = $"carrier_{System.Guid.NewGuid():N}.html";
			MapFileToUrl(filename, "https://example.com/products/clean-page.html");

			var html = "<a href=\"/products/oldword-page.html\">Home</a><p>clean body</p>";
			var issues = UnwantedPatterns.Check(filename, html, HrefSet(), DefaultConfig()).ToList();

			Assert.Empty(issues);
		}

		[Fact]
		public void ExcludeHref_UrlUnresolvable_NoCulpritFinding_FailSafe()
		{
			// Unmapped filename → LookUpUrlForFile returns "error" → no culprit finding is
			// invented from an unresolvable url.
			var filename = $"unmapped_{System.Guid.NewGuid():N}.html";
			var html = "<a href=\"/products/oldword-page.html\">Home</a><p>clean body</p>";
			var issues = UnwantedPatterns.Check(filename, html, HrefSet(), DefaultConfig()).ToList();

			Assert.Empty(issues);
		}

		// ── ExcludeUrl — tag url-type occurrences for per-page grouping ────────
		// The detector tags an occurrence whose token is slash-bearing with the
		// UrlMarker (locator + url), so the triage layer can collapse a page's url
		// occurrences into one summary. Non-url occurrences (bare slug, comment,
		// whitespace-broken value) are left untagged and stay one finding each.
		// Synthetic fixtures only.

		private static List<ContentUnwantedPattern> UrlSet(bool excludeUrl) =>
		[
			new()
			{
				Category = "Legacy", Name = "Old", GroupPatterns = false,
				CaseSensitive = false, ExcludeUrl = excludeUrl, Patterns = ["oldword"]
			}
		];

		private const string UrlMark = Crawler.Quality.UnwantedPatterns.UrlMarker;

		[Fact]
		public void ExcludeUrl_False_NoTagging()
		{
			var html = "<img src=\"/assets/oldword.jpg\">";
			var issue = UnwantedPatterns.Check("f.html", html, UrlSet(excludeUrl: false), DefaultConfig()).Single();
			Assert.DoesNotContain(UrlMark, issue.Detail);
		}

		[Fact]
		public void ExcludeUrl_SlashBearingSrc_TaggedAsUrl()
		{
			var html = "<img src=\"/assets/oldword.jpg\">";
			var issue = UnwantedPatterns.Check("f.html", html, UrlSet(excludeUrl: true), DefaultConfig()).Single();
			Assert.Contains(UrlMark, issue.Detail);
			Assert.Contains("[src]", issue.Detail);
			Assert.Contains("/assets/oldword.jpg", issue.Detail);
		}

		[Fact]
		public void ExcludeUrl_SlashBearingHrefText_LinkLocator()
		{
			var html = "<a href=\"/x/oldword.html\">t</a>";
			var issue = UnwantedPatterns.Check("f.html", html, UrlSet(excludeUrl: true), DefaultConfig()).Single();
			Assert.Contains($"{UrlMark}[link] /x/oldword.html", issue.Detail);
		}

		[Fact]
		public void ExcludeUrl_SlashlessRelativeRef_NotTagged()
		{
			// A bare filename with no path separator is not classified as url — it
			// surfaces as its own untagged finding (acceptable, low-volume flat sites).
			var html = "<img src=\"oldword.jpg\">";
			var issue = UnwantedPatterns.Check("f.html", html, UrlSet(excludeUrl: true), DefaultConfig()).Single();
			Assert.DoesNotContain(UrlMark, issue.Detail);
		}

		[Fact]
		public void ExcludeUrl_WhitespaceInsideValue_BreaksToken_NotTagged()
		{
			// A raw space inside the value (a url missing its percent-encoding) breaks
			// the token; the fragment holding the match is slash-less → not tagged →
			// surfaces separately, which also surfaces the malformed-url defect.
			var html = "<img src=\"/assets oldword.jpg\">";
			var issue = UnwantedPatterns.Check("f.html", html, UrlSet(excludeUrl: true), DefaultConfig()).Single();
			Assert.DoesNotContain(UrlMark, issue.Detail);
		}

		[Fact]
		public void ExcludeUrl_CommentSlug_NotTagged()
		{
			// A slug in an html comment has no slash → not url-type → untagged.
			var html = "<!--oldword-marker--><p>clean</p>";
			var issue = UnwantedPatterns.Check("f.html", html, UrlSet(excludeUrl: true), DefaultConfig()).Single();
			Assert.DoesNotContain(UrlMark, issue.Detail);
		}

		[Fact]
		public void ExcludeUrl_TrailingMarkupWhitespace_NotPartOfToken()
		{
			// The " />" after the closing quote is outside the value; the token is the
			// clean slash-bearing quoted value → tagged, url ends at the quote.
			var html = "<img src=\"/assets/oldword.jpg\" />";
			var issue = UnwantedPatterns.Check("f.html", html, UrlSet(excludeUrl: true), DefaultConfig()).Single();
			Assert.Contains($"{UrlMark}[src] /assets/oldword.jpg", issue.Detail);
		}
	}
}
