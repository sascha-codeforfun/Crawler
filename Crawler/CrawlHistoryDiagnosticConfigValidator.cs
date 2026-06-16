using System.Text;
using System.Text.RegularExpressions;

namespace Crawler
{
	/// <summary>
	/// Startup validator for crawl-history diagnostic config entries. Currently
	/// validates the regex patterns in
	/// <see cref="CrawlHistoryDiagnosticConfig.HeaderExtractors"/>; any malformed
	/// pattern halts the app with a pointed message naming the offending entry.
	///
	/// Mirrors <c>DictionaryIntegrity.CheckOrHalt</c> in shape and rationale:
	/// validate early, halt with one combined message that names every
	/// individual failure across the list (so the operator can fix all entries
	/// in one editing pass rather than fix-one, run, fix-next).
	///
	/// Validation runs regardless of whether
	/// <c>Config.CrawlHistoryDiagnostic.Enabled</c> is true or false. A broken
	/// regex in a currently-disabled diagnostic config should still halt at
	/// startup so the operator finds out at edit time, not when they later
	/// flip Enabled to true and discover the breakage mid-investigation.
	/// </summary>
	public static class CrawlHistoryDiagnosticConfigValidator
	{
		/// <summary>
		/// Returns true if config is valid (or there's nothing to validate),
		/// false if any extractor entry has a malformed regex or missing label.
		/// On failure, emits a framed operator-facing message naming each
		/// offending entry, then returns false (caller must abort —
		/// PressEnterToExit and return from RunAsync).
		/// </summary>
		public static bool CheckOrHalt(Config config)
		{
			var extractors = config?.CrawlHistoryDiagnostic?.HeaderExtractors;
			if (extractors == null || extractors.Count == 0)
			{
				return true;
			}

			var failures = new List<string>();

			for (var i = 0; i < extractors.Count; i++)
			{
				var entry = extractors[i];

				// Disabled entries are skipped entirely — they're allowed to carry
				// broken patterns without halting because the operator may have
				// disabled the entry precisely BECAUSE the pattern broke and they
				// want to preserve the audit trail until they can fix it. This
				// matches the spirit of the Enabled toggle: dormant but not lost.
				if (!entry.Enabled)
				{
					continue;
				}

				if (string.IsNullOrWhiteSpace(entry.Label))
				{
					failures.Add($"CrawlHistoryDiagnostic.HeaderExtractors[{i}]: Label is required (empty).");
				}

				if (string.IsNullOrWhiteSpace(entry.Pattern))
				{
					failures.Add($"CrawlHistoryDiagnostic.HeaderExtractors[{i}] ({entry.Label}): Pattern is required (empty).");
					continue;
				}

				try
				{
					// Construct the regex and inspect its capture-group count. We
					// require exactly one capture group; the diagnostic uses group 1
					// as the extracted value. Zero groups means there's nothing to
					// extract; multiple groups is permitted (group 1 wins silently)
					// but warned about would clutter — we accept multi-group without
					// objection, which matches the documented "capture group 1 is
					// the extracted value" contract.
					var rx = new Regex(entry.Pattern, RegexOptions.Compiled);
					if (rx.GetGroupNumbers().Length < 2)
					{
						// GetGroupNumbers includes the implicit "group 0" (whole match),
						// so a regex with no capture groups returns length 1. We need
						// at least one capture group, so length must be >= 2.
						failures.Add($"CrawlHistoryDiagnostic.HeaderExtractors[{i}] ({entry.Label}): Pattern must contain at least one capture group. Pattern: {entry.Pattern}");
					}
				}
				catch (System.ArgumentException ex)
				{
					failures.Add($"CrawlHistoryDiagnostic.HeaderExtractors[{i}] ({entry.Label}): invalid regex — {ex.Message}");
				}
			}

			if (failures.Count == 0)
			{
				return true;
			}

			var sb = new StringBuilder();
			sb.AppendLine("One or more CrawlHistoryDiagnostic.HeaderExtractors entries are invalid:");
			foreach (var f in failures)
			{
				sb.AppendLine($"  - {f}");
			}
			sb.AppendLine("Fix the entries in config (or config.private.json) and restart.");

			ConsoleUi.WriteHeader("Crawl-history diagnostic config FAILED");
			Logger.LogError(sb.ToString());
			ConsoleUi.WriteFooter();

			return false;
		}
	}
}
