namespace Crawler
{
	// ── CrawlerContext ────────────────────────────────────────────────────────
	//
	// Global runtime state set once at startup and read throughout the process.
	// These are process-level flags, not configuration — they are derived from
	// command-line arguments rather than config files, and never change after
	// initialisation.
	//
	// Kept separate from Config to avoid coupling runtime behaviour (e.g. silent
	// mode) with the configuration model, and to avoid threading silent through
	// every method signature.
	// ─────────────────────────────────────────────────────────────────────────

	internal static class CrawlerContext
	{
		/// <summary>
		/// True when running in silent mode (--silent / -s argument).
		/// Suppresses all console output and interactive prompts.
		/// Set once at startup in Program.Main before any pipeline runs.
		/// </summary>
		internal static bool Silent { get; set; }
	}
}
