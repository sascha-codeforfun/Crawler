using System.Text.RegularExpressions;
using Crawler.Quality;

namespace Crawler
{
	/// <summary>
	/// Operator-configured suppression of content-quality findings. Pure
	/// filtering layer between detectors and log emission — detectors do not
	/// know about suppression. The layer dampens template-emitted
	/// noise (e.g. CMS heading-as-div patterns producing thousands of
	/// BARE_TEXT_IN_CONTAINER findings on a single site) without disabling
	/// the underlying check, which would mask similar findings on other
	/// container classes.
	///
	/// Rule shape (see <see cref="IssueSuppressionRule"/>):
	///   Type     — required, exact match against QualityIssue.IssueType.
	///   Value    — optional case-sensitive substring; matched against
	///              "{Detail} {Context}". Empty/missing matches any value
	///              within the Type.
	///   Pages    — optional list of *-only globs matched against
	///              QualityIssue.Filename. Empty/missing = global.
	///   Enabled  — optional, default true. False = rule ignored entirely.
	///   Comment  — optional free text. Not consumed by the matcher;
	///              surfaced in the per-rule summary for audit trail.
	///
	/// A finding is suppressed if ANY enabled rule matches it. For hit-counting
	/// the first matching rule is credited (deterministic by rule order).
	///
	/// Cross-run state caveat: suppression filters findings on the *current
	/// run only*. Findings that were previously promoted into IssueTracking.log
	/// (via the triage flow) are unaffected by adding a suppression rule —
	/// the tracking record's status stays as-is. Suppression hides; it does
	/// not retroactively un-promote.
	/// </summary>
	internal static class IssueSuppressions
	{
		/// <summary>
		/// Result of applying suppression rules to a list of findings.
		/// </summary>
		/// <param name="Emitted">Findings that survived suppression — pass to the log writer.</param>
		/// <param name="RuleHits">For each rule index (0-based, matching the input rules list),
		/// the count of findings suppressed by that rule via first-match attribution.
		/// Rules with no hits appear with count 0.</param>
		internal record FilterResult(
			List<QualityIssue> Emitted,
			IReadOnlyDictionary<int, int> RuleHits);

		/// <summary>
		/// Filters a sequence of findings against a list of suppression rules.
		/// Preserves input order in <see cref="FilterResult.Emitted"/>.
		/// Disabled rules and rules with missing/empty Type are skipped.
		/// </summary>
		internal static FilterResult Apply(
			IEnumerable<QualityIssue> findings,
			IReadOnlyList<IssueSuppressionRule>? rules)
		{
			var emitted = new List<QualityIssue>();
			var hits = new Dictionary<int, int>();

			// Pre-build per-rule active flags + Pages-glob regexes so we don't
			// recompile on every finding. Rules that fail validation
			// (Enabled=false, missing Type) are flagged inactive and never
			// participate in matching.
			if (rules is null || rules.Count == 0)
			{
				foreach (var issue in findings)
				{
					emitted.Add(issue);
				}

				return new FilterResult(emitted, hits);
			}

			var active = new bool[rules.Count];
			var pageRegexes = new Regex?[rules.Count][];

			for (int i = 0; i < rules.Count; i++)
			{
				hits[i] = 0;
				var r = rules[i];
				if (!r.Enabled)
				{
					continue;
				}

				if (string.IsNullOrEmpty(r.Type))
				{
					continue;
				}

				active[i] = true;
				if (r.Pages is { Count: > 0 })
				{
					pageRegexes[i] = new Regex[r.Pages.Count];
					for (int p = 0; p < r.Pages.Count; p++)
					{
						pageRegexes[i]![p] = GlobToRegex(r.Pages[p]);
					}
				}
			}

			foreach (var issue in findings)
			{
				int matchedRule = -1;
				for (int i = 0; i < rules.Count; i++)
				{
					if (!active[i])
					{
						continue;
					}

					if (Matches(rules[i], pageRegexes[i], issue))
					{
						matchedRule = i;
						break;
					}
				}

				if (matchedRule >= 0)
				{
					hits[matchedRule] = hits[matchedRule] + 1;
				}
				else
				{
					emitted.Add(issue);
				}
			}

			return new FilterResult(emitted, hits);
		}

		/// <summary>
		/// True if the rule matches the given finding. Pre-computed page
		/// regexes are passed in to avoid recompiling per call.
		/// </summary>
		internal static bool Matches(
			IssueSuppressionRule rule,
			Regex?[]? pageRegexes,
			QualityIssue issue)
		{
			// Type — required exact match. Case-sensitive (IssueType constants are uppercase).
			if (!string.Equals(rule.Type, issue.IssueType, StringComparison.Ordinal))
			{
				return false;
			}

			// Value — optional substring against Detail + " " + Context.
			// Case-sensitive: the operator copy-pastes from the log, and
			// findings carry HTML markup where case is significant
			// (e.g. class="h2" vs class="H2" are different things).
			if (!string.IsNullOrEmpty(rule.Value))
			{
				var haystack = $"{issue.Detail} {issue.Context}";
				if (!haystack.Contains(rule.Value, StringComparison.Ordinal))
				{
					return false;
				}
			}

			// Pages — optional glob list against Filename. Any match = match.
			if (pageRegexes is { Length: > 0 })
			{
				bool anyMatch = false;
				for (int p = 0; p < pageRegexes.Length; p++)
				{
					if (pageRegexes[p]!.IsMatch(issue.Filename))
					{
						anyMatch = true;
						break;
					}
				}
				if (!anyMatch)
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>
		/// Converts a simple `*`-wildcard glob to a regex. `*` matches any
		/// sequence of characters (including empty); all other regex
		/// metacharacters are escaped literally. Anchored on both ends so
		/// the entire filename must match. The empty glob matches only the
		/// empty string (so callers should not pass empty entries — empty
		/// Pages list is the way to express "global").
		/// </summary>
		internal static Regex GlobToRegex(string glob)
		{
			var sb = new System.Text.StringBuilder("^");
			for (int i = 0; i < glob.Length; i++)
			{
				char ch = glob[i];
				if (ch == '*')
				{
					sb.Append(".*");
				}
				else
				{
					sb.Append(Regex.Escape(ch.ToString()));
				}
			}
			sb.Append('$');
			return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
		}
	}

	/// <summary>
	/// One operator-configured suppression rule. See <see cref="IssueSuppressions"/>
	/// for full semantics. Configured under <c>ContentQualityIssueSuppressions</c>
	/// in config.json / config.private.json.
	/// </summary>
	public record IssueSuppressionRule
	{
		/// <summary>Required. Exact IssueType to suppress (e.g. "BARE_TEXT_IN_CONTAINER").</summary>
		public string Type { get; init; } = "";

		/// <summary>
		/// Optional case-sensitive substring matched against
		/// "{Detail} {Context}". Empty/missing matches any value within the Type.
		/// </summary>
		public string Value { get; init; } = "";

		/// <summary>
		/// Optional list of *-only globs matched against Filename.
		/// Empty/missing = global (every page).
		/// </summary>
		public List<string> Pages { get; init; } = [];

		/// <summary>
		/// Optional. When false, the rule is ignored entirely — convenient
		/// for temporarily un-suppressing a category to audit what it was
		/// hiding without deleting the rule. Default true.
		/// </summary>
		public bool Enabled { get; init; } = true;

		/// <summary>
		/// Optional free text. Not consumed by the matcher. Surfaced in
		/// the per-rule console summary so the operator's reasoning is
		/// visible at audit time.
		/// </summary>
		public string Comment { get; init; } = "";
	}
}
