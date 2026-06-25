using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// D048 — WORD_COLLISION de-mask pilot. A page with N collisions collapses to
	/// one tracked record (Key = QUALITY|url|WORD_COLLISION) carrying only a
	/// representative excerpt. These tests pin the two new joins that restore the
	/// full group from this run's BuildGroups accumulation:
	///   * WordCollisionContextsByKey — extracts every occurrence, keyed by the
	///     promoted IssueRecord.Key, in document order.
	///   * the ticket — a ticketed collision row expands to one "Context:" line per
	///     occurrence (one bullet for the page+type, not N bullets), with the
	///     pre-D048 single-excerpt behaviour preserved when no map is supplied.
	/// The interactive review walk reads the keyboard and isn't unit-tested; it
	/// renders from the same map, so the extraction test covers its data path.
	/// </summary>
	public class ContentQualityDemaskTests
	{
		// ── WordCollisionContextsByKey (shared review + ticket source) ────────

		private static ContentQualityTriage.TriageGroup CollisionGroup(
			string url, params string[] contexts)
			=> new(
				DisplayType: "WORD_COLLISION",
				Url: url,
				Word: "WORD_COLLISION",
				Comment: "",
				Excerpt: contexts.Length > 0 ? contexts[0] : "",
				IsTranslation: false,
				DisplayLines: contexts.Select(c => $"HTML    : {c}").ToList());

		[Fact]
		public void ContextsByKey_CarriesEveryOccurrence_InOrder_KeyedByRecordKey()
		{
			var url = "https://www.example.com/de/dispositionskredit.html";
			var g = CollisionGroup(url,
				"<span class=\"h2\">1. Ihr Einkommen angeben</span>Machen Sie Angaben.",
				"<span class=\"h2\">2. Kreditrahmen wählen</span>Wählen Sie die Höhe.",
				"<span class=\"h2\">3. Direkt profitieren</span>Nach Genehmigung nutzbar.");

			var map = ContentQualityTriage.WordCollisionContextsByKey(new[] { g });

			var key = $"QUALITY|{url}|WORD_COLLISION";
			Assert.True(map.ContainsKey(key));
			Assert.Equal(
				new[]
				{
					"<span class=\"h2\">1. Ihr Einkommen angeben</span>Machen Sie Angaben.",
					"<span class=\"h2\">2. Kreditrahmen wählen</span>Wählen Sie die Höhe.",
					"<span class=\"h2\">3. Direkt profitieren</span>Nach Genehmigung nutzbar.",
				},
				map[key]);
		}

		[Fact]
		public void ContextsByKey_IgnoresNonCollisionGroups()
		{
			var ligature = new ContentQualityTriage.TriageGroup(
				DisplayType: "LIGATURE",
				Url: "https://x/p",
				Word: "LIGATURE",
				Comment: "",
				Excerpt: "office",
				IsTranslation: false,
				DisplayLines: new List<string> { "Ligature : office" });

			var map = ContentQualityTriage.WordCollisionContextsByKey(new[] { ligature });

			Assert.Empty(map);
		}

		// ── Ticket expansion (one bullet, N Context lines) ───────────────────

		private static TicketGenerationConfig QualityTicketConfig()
			=> new()
			{
				TicketShellTemplate = "{Url}",
				TicketIssueTypes = [new TicketIssueTypeEntry { Type = "QUALITY", Label = "QUALITY" }],
				TicketSectionIntros = [new TicketSectionIntro { Type = "QUALITY", Text = "Quality issues:" }],
				TicketHeadlineTemplate = "{PathIndicator}",
				TicketPrefix = "WEB",
			};

		private static IssueTracking.IssueRecord PendingCollision(string url)
			=> new()
			{
				Type = "QUALITY",
				Url = url,
				Status = "pending",
				Word = "WORD_COLLISION",
				SourceLabel = "WORD_COLLISION",
				Excerpt = "<span class=\"h2\">1. step</span>Alpha",
				DateFound = System.DateTime.UtcNow.Date.ToString("yyyy-MM-dd"),
				Language = "de",
			};

		[Fact]
		public void Ticket_WordCollision_WithMap_EmitsOneContextLinePerOccurrence()
		{
			var url = "https://www.example.com/de/dispositionskredit.html";
			var tmp = System.IO.Path.Combine(
				System.IO.Path.GetTempPath(), $"d048-{System.Guid.NewGuid():N}.log");
			try
			{
				var map = new Dictionary<string, List<string>>
				{
					[$"QUALITY|{url}|WORD_COLLISION"] = new()
					{
						"<span class=\"h2\">1. step</span>Alpha",
						"<span class=\"h2\">2. step</span>Beta",
						"<span class=\"h2\">3. step</span>Gamma",
					},
				};

				TicketRenderer.WriteTicketText(
					tmp,
					new List<IssueTracking.IssueRecord>(),
					QualityTicketConfig(),
					null,
					_ => new SpellMetadataLookup.TicketMetadata("", "", "", ""),
					null,
					null,
					new List<IssueTracking.IssueRecord> { PendingCollision(url) },
					map);

				var content = System.IO.File.ReadAllText(tmp);

				// All three occurrences named — not just the first stored excerpt.
				Assert.Contains("Alpha", content);
				Assert.Contains("Beta", content);
				Assert.Contains("Gamma", content);
				// One Context line per occurrence …
				Assert.Equal(3, CountOccurrences(content, "Context:"));
				// … under a single bullet for the page+type (collapse preserved).
				Assert.Equal(1, CountOccurrences(content, "* WORD_COLLISION"));
			}
			finally
			{
				if (System.IO.File.Exists(tmp)) { System.IO.File.Delete(tmp); }
			}
		}

		[Fact]
		public void Ticket_WordCollision_NoMap_FallsBackToSingleExcerpt()
		{
			var url = "https://www.example.com/de/dispositionskredit.html";
			var tmp = System.IO.Path.Combine(
				System.IO.Path.GetTempPath(), $"d048-{System.Guid.NewGuid():N}.log");
			try
			{
				// No map supplied → pre-D048 behaviour: the single stored excerpt only.
				TicketRenderer.WriteTicketText(
					tmp,
					new List<IssueTracking.IssueRecord>(),
					QualityTicketConfig(),
					null,
					_ => new SpellMetadataLookup.TicketMetadata("", "", "", ""),
					null,
					null,
					new List<IssueTracking.IssueRecord> { PendingCollision(url) });

				var content = System.IO.File.ReadAllText(tmp);

				Assert.Contains("Alpha", content);
				Assert.DoesNotContain("Beta", content);
				Assert.Equal(1, CountOccurrences(content, "Context:"));
			}
			finally
			{
				if (System.IO.File.Exists(tmp)) { System.IO.File.Delete(tmp); }
			}
		}

		private static int CountOccurrences(string haystack, string needle)
		{
			int count = 0, i = 0;
			while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0)
			{
				count++;
				i += needle.Length;
			}
			return count;
		}
	}
}
