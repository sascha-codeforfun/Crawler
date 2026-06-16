namespace Crawler
{
	/// <summary>
	/// Result of a freshness check against a CmsContentListConfig.
	/// Stateless snapshot — recomputed at each call site rather than carried
	/// in ctx. If the operator updates the CSV mid-run, later checks see the
	/// new state. (Edge case explicitly accepted.)
	/// </summary>
	internal sealed record CmsContentListFreshness(
		bool IsConfigured,
		bool FileExists,
		bool CheckDisabled,
		bool IsStale,
		int AgeDays,
		int MaxAgeDays,
		string Path,
		System.DateTime? FileDate,
		string Comment)
	{
		/// <summary>
		/// Convenience for the post-crawl gate: "is this CSV usable for the pass?"
		/// True when configured, file exists, and either fresh or check disabled.
		/// </summary>
		internal bool IsUsableForPostCrawlPass =>
			IsConfigured && FileExists && (CheckDisabled || !IsStale);
	}

	/// <summary>
	/// Pure helpers for CmsContentList freshness inspection. Separated from the
	/// rest of Config so callers can unit-test staleness logic without touching
	/// JSON or HTTP layers. The class itself is stateless — every method takes
	/// inputs explicitly.
	///
	/// Decision rules:
	///   * cfg null or Path empty → not configured (downstream features skip).
	///   * Path set but file missing → handled by the existing
	///     "string.IsNullOrEmpty || !File.Exists" guards at read sites; the
	///     freshness check reports FileExists=false but does not throw.
	///   * MaxAgeDays &lt;= 0 → check disabled; IsStale is always false.
	///   * Otherwise IsStale = (age in days) &gt; MaxAgeDays.
	/// </summary>
	internal static class CmsContentListFreshnessCheck
	{
		/// <summary>
		/// Evaluates the freshness of the configured CSV. Uses
		/// <see cref="System.IO.File.GetLastWriteTime"/> as the modification
		/// time source. Pure with respect to its inputs given a stable
		/// filesystem and clock.
		/// </summary>
		internal static CmsContentListFreshness Evaluate(CmsContentListConfig? cfg)
			=> Evaluate(cfg, System.DateTime.Now);

		/// <summary>
		/// Test seam — accepts an explicit "now" for deterministic age
		/// computation. Production callers use the no-now overload above.
		/// </summary>
		internal static CmsContentListFreshness Evaluate(
			CmsContentListConfig? cfg, System.DateTime now)
		{
			if (cfg == null || string.IsNullOrWhiteSpace(cfg.Path))
			{
				return new CmsContentListFreshness(
					IsConfigured: false, FileExists: false, CheckDisabled: true,
					IsStale: false, AgeDays: 0, MaxAgeDays: 0,
					Path: string.Empty, FileDate: null, Comment: string.Empty);
			}

			var path = cfg.Path;
			var max = cfg.MaxAgeDays;
			var comment = cfg.Comment ?? string.Empty;
			var disabled = max <= 0;

			if (!System.IO.File.Exists(path))
			{
				return new CmsContentListFreshness(
					IsConfigured: true, FileExists: false, CheckDisabled: disabled,
					IsStale: false, AgeDays: 0, MaxAgeDays: max,
					Path: path, FileDate: null, Comment: comment);
			}

			var fileDate = System.IO.File.GetLastWriteTime(path);
			var ageDays = (int)System.Math.Floor((now - fileDate).TotalDays);
			var stale = !disabled && ageDays > max;

			return new CmsContentListFreshness(
				IsConfigured: true, FileExists: true, CheckDisabled: disabled,
				IsStale: stale, AgeDays: ageDays, MaxAgeDays: max,
				Path: path, FileDate: fileDate, Comment: comment);
		}
	}
}
