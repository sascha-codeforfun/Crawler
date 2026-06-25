namespace Crawler
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using Crawler.SpellCheck;

	/// <summary>
	/// Delivery 634 — the CONFIGURATION panel: a one-glance, display-only summary of behaviour-changing
	/// settings, rendered at startup right under the CRAWLER block (before download/run). It does NOT
	/// mirror every config key — the config file is the source of truth; this surfaces the settings that
	/// silently change a run, so a misconfiguration (e.g. bulk scan on with no dictionaries, or a stale
	/// CMS list) is visible at a glance instead of only discoverable by opening a log.
	///
	/// Colour follows the established Proxy pattern, three states: DIM = default / off / empty;
	/// NORMAL = active / configured; RED (alert) = a problem worth stopping for. Rows read by deviation —
	/// the eye catches what is active or wrong, not the defaults.
	///
	/// Human-readable labels and values throughout (never raw config-property names).
	/// </summary>
	internal static class ConfigSummary
	{
		internal static void WriteConfigurationBlock(Config config)
		{
			var engine = config.SpellCheckEngine;
			var js = engine.SpellCheckJavaScript;

			ConsoleUi.WriteHeader("CONFIGURATION");

			// ── Language ──
			int overrides = engine.PageLanguageOverrides.Count;
			int chromeDefects = engine.KnownChromeLanguageDefects.Count;
			ConsoleUi.WriteStepRow(
				"Language",
				$"default {engine.DefaultLanguage} · {overrides} page override(s)");
			ConsoleUi.WriteStepRow(
				"Chrome defects",
				$"{chromeDefects} rule(s)",
				dimmed: chromeDefects == 0);

			// 655 — the dictionary catalog moved to its own LANGUAGES section (WriteLanguagesBlock,
			// called right after this block). The named list outgrew a one-line config row.

			// ── Ignore rules ──
			ConsoleUi.WriteStepRow(
				"Ignore rules",
				$"{engine.GlobalHtmlTagsToIgnore.Count} tag(s) · {engine.GlobalXpathToIgnore.Count} xpath · {engine.GlobalAttributesToIgnore.Count} attribute(s)",
				dimmed: engine.GlobalHtmlTagsToIgnore.Count == 0 && engine.GlobalXpathToIgnore.Count == 0 && engine.GlobalAttributesToIgnore.Count == 0);

			// ── Boilerplate ──
			int groups = engine.BoilerplateGroups.Count;
			int selectors = engine.BoilerplateGroups.Sum(g => g.BoilerplateSelectors.Count);
			ConsoleUi.WriteStepRow(
				"Boilerplate",
				$"{groups} group(s) · {selectors} selector(s)",
				dimmed: groups == 0);

			// ── Unwanted patterns (total + per-category) ──
			ConsoleUi.WriteStepRow(
				"Unwanted patterns",
				DescribeUnwantedPatterns(config.ContentUnwantedPatterns),
				dimmed: config.ContentUnwantedPatterns.Count == 0);

			// ── Content quality (by exception) ──
			ConsoleUi.WriteStepRow("Content quality", DescribeContentQualityChecks(config.ContentQuality));

			// ── CQ suppressions ──
			int suppressions = config.ContentQuality.ContentQualityIssueSuppressions.Count;
			ConsoleUi.WriteStepRow("CQ suppressions", suppressions > 0 ? $"{suppressions} rule(s)" : "none", dimmed: suppressions == 0);

			// ── Asset quality (compact — the result file alone never shows what was set) ──
			ConsoleUi.WriteStepRow("Asset quality", DescribeAssetQuality(config.AssetQuality), dimmed: !config.AssetQuality.IsEnabled);

			// ── SEO flags ──
			var seo = config.Seo;
			ConsoleUi.WriteStepRow(
				"SEO flags",
				$"meta-keywords {ErrOk(seo.MetaKeywordsFlagAsError)} · missing-H1 {ErrOk(seo.MissingH1FlagAsError)} · multiple-H1 {ErrOk(seo.MultipleH1FlagAsError)}");

			// ── Spell-check JS ──
			ConsoleUi.WriteStepRow("Spell-check JS", DescribeSpellCheckJs(js, config.SpellCheckWordPrefixesToStrip.Count), dimmed: !js.Enabled);

			// ── Bulk JS scan (the layer-8 catcher) ──
			var (bulkText, bulkAlert, bulkDim) = DescribeBulkScan(js);
			ConsoleUi.WriteStepRow("Bulk JS scan", bulkText, dimmed: bulkDim, accent: bulkAlert ? ConsoleColor.Red : (ConsoleColor?)null);

			// ── CMS content list (freshness of the PRIMARY site's resolved CSV) ──
			var (cmsText, cmsAccent, cmsDim) = DescribeCms(config, DateTime.Now);
			ConsoleUi.WriteStepRow("CMS content list", cmsText, dimmed: cmsDim, accent: cmsAccent);

			// ── Triage (content-quality · spell · dictionary) — traffic-light per lever ──
			var triageSegments = new List<(string Text, ConsoleColor Color)>
			{
				($"content {(config.EnableContentQualityTriage ? "on" : "off")}",
					config.EnableContentQualityTriage ? ConsoleColor.Green : ConsoleColor.Red),
				($"spell {(config.InteractiveSpellCheckTriage ? "interactive" : "off")}",
					config.InteractiveSpellCheckTriage ? ConsoleColor.Green : ConsoleColor.Red),
				DescribeDictionaryTriageSegment(config.DictionaryMaintenance),
			};
			ConsoleUi.WriteStepRowSegments("Triage", triageSegments);
		}

		/// <summary>
		/// 655 — the dictionary catalog as its own section. The named list ("DisplayName (code)",
		/// sorted by code) outgrew the CONFIGURATION glance-table — one row was wrapping to five lines
		/// and swamping its one-line neighbours — so it lives here with room to lay out.
		/// </summary>
		internal static void WriteLanguagesBlock(Config config)
		{
			ConsoleUi.WriteHeader("LANGUAGES");

			var dictNames = FormatDictionaryLabels(config.DictionaryBundles);
			if (dictNames.Count > 0)
			{
				ConsoleUi.WriteStepRowWithList("Dictionaries", $"{dictNames.Count} configured", string.Join(" · ", dictNames));
			}
			else
			{
				ConsoleUi.WriteStepRow("Dictionaries", "(none)", dimmed: true);
			}
		}

		private static string ErrOk(bool flagAsError) => flagAsError ? "ERROR" : "ok";

		// ── Pure, testable summarizers ──

		// "58 total (filetypes 12 · tracking 9 · legal 6)" — total patterns, then per-category counts.
		internal static string DescribeUnwantedPatterns(IReadOnlyList<ContentUnwantedPattern> patterns)
		{
			if (patterns == null || patterns.Count == 0)
			{
				return "none";
			}

			int total = patterns.Sum(p => p.Patterns.Count);

			var byCategory = patterns
				.GroupBy(p => string.IsNullOrWhiteSpace(p.Category) ? "(uncategorised)" : p.Category)
				.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
				.Select(g => $"{g.Key} {g.Sum(p => p.Patterns.Count)}");

			return $"{total} total ({string.Join(" · ", byCategory)})";
		}

		// "all checks on" or "on · off: ligatures, quote-mixing" — a disabled check is the signal, plus a
		// tail for the structural malformed-HTML / adjacent-anchor sub-flags.
		internal static string DescribeContentQualityChecks(ContentQualityConfig cq)
		{
			var checks = new (bool On, string Name)[]
			{
				(cq.CheckLigatures, "ligatures"),
				(cq.CheckLanguageMismatch, "language-mismatch"),
				(cq.CheckControlCharsInContent, "control-chars"),
				(cq.CheckQuoteSystemMixing, "quote-mixing"),
				(cq.CheckQuotePairing, "quote-pairing"),
				(cq.CheckPotentialTranslation, "translation"),
				(cq.CheckSplitWordAnchors, "split-word-anchors"),
				(cq.CheckMisplacedAnchors, "misplaced-anchors"),
				(cq.CheckBareTextInContainers, "bare-text"),
				(cq.CheckWordCollisions, "word-collisions"),
				(cq.CheckCmsTemplateAuthoringDefects, "cms-template-defects"),
			};

			var off = checks.Where(c => !c.On).Select(c => c.Name).ToList();
			string head = off.Count == 0 ? "all checks on" : $"on · off: {string.Join(", ", off)}";

			string malformed = DescribeMalformed(cq.MalformedHtml);
			string tail = malformed.Length > 0 ? $" · malformed: {malformed}" : " · malformed: off";

			string anchors = cq.AnchorDetection.DetectAdjacent ? " · anchors: adjacent" : string.Empty;

			return head + tail + anchors;
		}

		// "doctype + parse (1 code suppressed)" — the active malformed-HTML detectors, with suppressed count.
		internal static string DescribeMalformed(MalformedHtmlConfig m)
		{
			var parts = new List<string>();
			if (m.DetectContentBeforeDoctype)
			{
				parts.Add("doctype");
			}

			if (m.DetectHtmlParseErrors)
			{
				parts.Add("parse");
			}

			if (parts.Count == 0)
			{
				return string.Empty;
			}

			string s = string.Join(" + ", parts);
			if (m.SuppressParseErrorCodes.Count > 0)
			{
				s += $" ({m.SuppressParseErrorCodes.Count} code(s) suppressed)";
			}

			return s;
		}

		// "metadata · dimensions · size · ≤500 KB · ≤5000 px" — what's checked and the limits.
		internal static string DescribeAssetQuality(AssetQualityConfig a)
		{
			if (!a.IsEnabled)
			{
				return "off";
			}

			var parts = new List<string>();
			if (a.CheckMetadataLeakage)
			{
				parts.Add("metadata");
			}

			if (a.CheckDimensions)
			{
				parts.Add("dimensions");
			}

			if (a.CheckSize)
			{
				parts.Add("size");
			}

			parts.Add($"≤{FormatBytes(a.MaxImageBytes)}");
			parts.Add($"≤{a.MaxImageDimensionPixels} px");
			return string.Join(" · ", parts);
		}

		internal static string DescribeSpellCheckJs(JavaScriptSpellCheckOptions js, int prefixCount)
		{
			if (!js.Enabled)
			{
				return "off";
			}

			var parts = new List<string> { "on" };
			if (!string.IsNullOrWhiteSpace(js.ScriptFallbackDictionary))
			{
				parts.Add($"fallback {js.ScriptFallbackDictionary}");
			}

			parts.Add($"prefixes {prefixCount}");
			return string.Join(" · ", parts);
		}

		// off → dim; on + dictionaries → normal; on + NONE → red (the slip that wrote an empty log 29).
		internal static (string Text, bool Alert, bool Dim) DescribeBulkScan(JavaScriptSpellCheckOptions js)
		{
			if (!js.BulkScanPageScript)
			{
				return ("off", false, true);
			}

			if (js.ScriptBulkScanDictionaries.Count == 0)
			{
				return ("on — NO DICTIONARIES", true, false);
			}

			return ($"on ({string.Join(", ", js.ScriptBulkScanDictionaries)})", false, false);
		}

		// Resolves the PRIMARY site's tenant into the CMS list path and reports that file's freshness.
		// The CMS list is per-site (path carries a {tenant} token resolved per site at run time), but
		// exactly one site is primary, so the primary's resolved CSV is the meaningful one to show at the
		// global, pre-site-selection stage. Read-only: never calls Config.ResolveForSite (which mutates),
		// it substitutes {tenant} itself so the per-site run resolution downstream is untouched.
		private static (string Text, ConsoleColor? Accent, bool Dim) DescribeCms(Config config, DateTime now)
		{
			var cms = config.CmsContentList;
			if (cms == null || string.IsNullOrWhiteSpace(cms.Path))
			{
				return ("off", null, true);
			}

			var primary = config.Sites.FirstOrDefault(s => s.IsPrimary);
			if (primary == null)
			{
				return ("configured · no primary site", null, true);
			}

			string tenant = string.IsNullOrWhiteSpace(primary.Tenant) ? Config.DefaultTenant : primary.Tenant;
			string resolvedPath = cms.Path.Replace("{tenant}", tenant, StringComparison.Ordinal);

			var freshness = CmsContentListFreshnessCheck.Evaluate(
				new CmsContentListConfig { Path = resolvedPath, MaxAgeDays = cms.MaxAgeDays }, now);

			return DescribeCmsFreshness(freshness, resolvedPath);
		}

		// Pure mapping from a freshness verdict to row text + colour. current=green, older=amber,
		// missing=red (with the resolved path so a real miss is diagnosable), present-but-no-limit=green.
		internal static (string Text, ConsoleColor? Accent, bool Dim) DescribeCmsFreshness(CmsContentListFreshness f, string resolvedPath)
		{
			if (!f.IsConfigured)
			{
				return ("off", null, true);
			}

			if (!f.FileExists)
			{
				return ($"configured · missing → {resolvedPath}", ConsoleColor.Red, false);
			}

			if (f.CheckDisabled)
			{
				return ($"configured · present ({f.AgeDays} d, no age limit)", ConsoleColor.Green, false);
			}

			if (f.IsStale)
			{
				return ($"configured · older ({f.AgeDays} d, limit {f.MaxAgeDays} d)", ConsoleColor.DarkYellow, false);
			}

			return ($"configured · current ({f.AgeDays} d)", ConsoleColor.Green, false);
		}

		// Dictionary-maintenance posture: off (also empty/unrecognised), report (analyse, no mutation),
		// or interactive with its update targets. Mirrors Step_MaintainDictionaries' mode parsing.
		internal static string DescribeDictionaryMaintenance(DictionaryMaintenanceConfig m)
		{
			string mode = (m.Mode ?? "Off").Trim();

			if (string.Equals(mode, "Interactive", StringComparison.OrdinalIgnoreCase))
			{
				var targets = new List<string>();
				if (m.UpdateUserDictionary)
				{
					targets.Add("user");
				}

				if (m.UpdateSiteSpecificDictionary)
				{
					targets.Add("site");
				}

				return targets.Count > 0 ? $"interactive ({string.Join("+", targets)})" : "interactive";
			}

			if (string.Equals(mode, "Report", StringComparison.OrdinalIgnoreCase))
			{
				return "report";
			}

			return "off";
		}

		internal static bool IsDictionaryMaintenanceActive(DictionaryMaintenanceConfig m)
		{
			string mode = (m.Mode ?? "Off").Trim();
			return string.Equals(mode, "Interactive", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(mode, "Report", StringComparison.OrdinalIgnoreCase);
		}

		// Dictionary-triage segment for the traffic-light row: off → red, report → amber, interactive →
		// green. Text reuses DescribeDictionaryMaintenance so the wording stays in one place.
		internal static (string Text, ConsoleColor Color) DescribeDictionaryTriageSegment(DictionaryMaintenanceConfig m)
		{
			string mode = (m.Mode ?? "Off").Trim();
			ConsoleColor color =
				string.Equals(mode, "Interactive", StringComparison.OrdinalIgnoreCase) ? ConsoleColor.Green
				: string.Equals(mode, "Report", StringComparison.OrdinalIgnoreCase) ? ConsoleColor.DarkYellow
				: ConsoleColor.Red;

			return ($"dictionary {DescribeDictionaryMaintenance(m)}", color);
		}

		private static string FormatBytes(long bytes)
		{
			if (bytes >= 1_048_576 && bytes % 1_048_576 == 0)
			{
				return $"{bytes / 1_048_576} MB";
			}

			if (bytes >= 1024)
			{
				return $"{bytes / 1024} KB";
			}

			return $"{bytes} B";
		}

		/// <summary>
		/// 654 — formats the configured dictionary bundles for the summary row as "DisplayName (code)"
		/// (e.g. "German (de)"), sorted by language code so the order is stable and matches the rest of
		/// the app. A bundle with no DisplayName falls back to the bare code — defensive only, since
		/// DisplayName is required and the integrity gate halts a nameless bundle, but the summary row
		/// may render before that gate runs. Bundles with an empty LanguageCode are dropped, as before.
		/// </summary>
		internal static List<string> FormatDictionaryLabels(IReadOnlyList<DictionaryBundleConfig> bundles)
			=> bundles
				.Where(b => !string.IsNullOrWhiteSpace(b.LanguageCode))
				.OrderBy(b => b.LanguageCode, StringComparer.OrdinalIgnoreCase)
				.Select(b => string.IsNullOrWhiteSpace(b.DisplayName)
					? b.LanguageCode
					: $"{b.DisplayName} ({b.LanguageCode})")
				.ToList();
	}
}
