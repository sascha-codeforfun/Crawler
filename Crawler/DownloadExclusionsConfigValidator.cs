using System.Text;

namespace Crawler
{
	/// <summary>
	/// Startup validator for <see cref="Config.DownloadExclusions"/>. Catches
	/// one specific class of operator config error: an entry with
	/// <c>Enabled = true</c> and an empty or whitespace-only <c>Value</c>.
	///
	/// Why halt: <c>Tools.IsValidLink</c> applies each enabled exclusion via
	/// <c>link.Contains(entry.Value, ...)</c>. <c>Contains("")</c> returns
	/// <c>true</c> for every non-null string. An enabled empty-Value entry
	/// would therefore reject every link the crawler considers, producing a
	/// silent crawl-killer: the operator sees zero downloads and no obvious
	/// reason why. Halting at startup with a pointed message saves them that
	/// debugging.
	///
	/// Disabled entries with empty Value are explicitly allowed — they're a
	/// normal audit-trail state ("I disabled this entry and cleared its value
	/// while I think about whether to keep it"). The validator only halts on
	/// the active dangerous combination.
	///
	/// Mirrors <see cref="CrawlHistoryDiagnosticConfigValidator.CheckOrHalt"/>
	/// in shape and convention: collect all offending entries, emit a framed
	/// operator-facing message naming each by index, return false on halt
	/// (caller is expected to PressEnterToExit and return).
	/// </summary>
	public static class DownloadExclusionsConfigValidator
	{
		/// <summary>
		/// Returns true if config is valid (or there's nothing to validate),
		/// false if any enabled entry has an empty or whitespace Value. On
		/// failure, emits a framed message and returns false; the caller must
		/// halt.
		/// </summary>
		public static bool CheckOrHalt(Config config)
		{
			var entries = config?.DownloadExclusions;
			if (entries == null || entries.Count == 0)
			{
				return true;
			}

			var failures = new List<string>();

			for (var i = 0; i < entries.Count; i++)
			{
				var entry = entries[i];

				// Disabled entries are an explicit audit-trail state — operator
				// keeping the Comment around while the entry is dormant. Empty
				// Value on a disabled entry is fine. Only enabled entries with
				// empty Value are the dangerous combination.
				if (entry == null || !entry.Enabled)
				{
					continue;
				}

				if (string.IsNullOrWhiteSpace(entry.Value))
				{
					var commentPart = string.IsNullOrEmpty(entry.Comment)
						? ""
						: $" (Comment: \"{entry.Comment}\")";
					failures.Add($"DownloadExclusions[{i}]: Enabled = true but Value is empty.{commentPart}");
				}
			}

			if (failures.Count == 0)
			{
				return true;
			}

			var sb = new StringBuilder();
			sb.AppendLine("One or more DownloadExclusions entries are invalid:");
			foreach (var f in failures)
			{
				sb.AppendLine($"  - {f}");
			}
			sb.AppendLine();
			sb.AppendLine("An enabled entry with empty Value would match every link via Contains(\"\") and");
			sb.AppendLine("reject the entire crawl. Either populate the Value, set Enabled = false to keep");
			sb.AppendLine("the entry as audit trail without effect, or remove the entry from config.");

			ConsoleUi.WriteHeader("DownloadExclusions config FAILED");
			Logger.LogError(sb.ToString());
			ConsoleUi.WriteFooter();

			return false;
		}
	}
}
