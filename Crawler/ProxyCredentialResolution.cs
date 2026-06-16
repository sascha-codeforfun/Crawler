namespace Crawler
{
	/// <summary>
	/// Pure decision logic for resolving the proxy credentials a run will use.
	///
	/// Credentials are resolved ONCE, at startup (Program.RunAsync), into
	/// CrawlerRunContext — which is the single source of truth thereafter. The
	/// connectivity preflight, the main crawl, and the post-crawl pass all read
	/// the resolved values from ctx, so every HTTP path authenticates the same
	/// way. (Earlier the prompt happened mid-crawl, AFTER the preflight, so
	/// an operator relying on typed credentials had the preflight authenticate
	/// with a different identity than the crawl. Resolving up-front closes that.)
	///
	/// Resolution model:
	///   * Silent mode            → config wins, no prompt. Whatever config holds
	///                              (populated or blank) is used as-is; blank then
	///                              flows to ProxyConfig.Build's UseDefaultCredentials
	///                              branch (the process's OS identity — the service
	///                              account on a scheduled run).
	///   * No proxy / proxy off   → nothing to resolve; config values pass through
	///                              (blank, normally) and never trigger a prompt.
	///   * Interactive + proxy on → the operator may always supply credentials by
	///                              hand. Config is a DEFAULT, not a lock:
	///                                - config blank      → prompt for new credentials.
	///                                - config populated  → offer use-or-override
	///                                                      (Program presents [U]/[O];
	///                                                      this helper only reports
	///                                                      that an override is offered).
	///
	/// Config-held credentials are bridging technology: a future revision may move
	/// them out of config for security reasons, at which point the interactive
	/// prompt is the only supply path. That is WHY manual input must always be
	/// reachable interactively even when config is populated — do not re-gate the
	/// prompt on config being blank.
	///
	/// This type is the testable half. The I/O half (the actual [U]/[O] keypress,
	/// the username read, the masked-password read) lives in Program composed from
	/// ConsoleUi primitives, and is operator-eyeball-verified, not unit-tested.
	/// </summary>
	internal static class ProxyCredentialResolution
	{
		/// <summary>
		/// How the run should obtain proxy credentials, decided from mode and config
		/// alone (before any operator interaction).
		/// </summary>
		internal enum Outcome
		{
			/// <summary>Use the credentials carried on the decision as-is; do not prompt.</summary>
			UseAsConfigured,

			/// <summary>Interactive: no configured credentials — prompt for fresh ones.</summary>
			PromptFresh,

			/// <summary>
			/// Interactive: configured credentials exist — offer the operator the
			/// choice to use them or override with hand-typed ones.
			/// </summary>
			OfferUseOrOverride,
		}

		/// <summary>
		/// The result of <see cref="Decide"/>: the action to take, plus the
		/// credentials to use when no prompt is needed (or to offer as the default
		/// when an override is presented).
		/// </summary>
		internal readonly record struct Decision(Outcome Outcome, string User, string Password);

		/// <summary>
		/// Decides how to resolve proxy credentials for the run. Pure — no I/O, no
		/// console, no static state. Program calls this once, acts on the Outcome,
		/// and writes the resolved credentials onto CrawlerRunContext.
		/// </summary>
		/// <param name="silent">CrawlerContext.Silent — unattended run.</param>
		/// <param name="useProxy">config.UseProxy.</param>
		/// <param name="proxyUrl">config.ProxyUrl.</param>
		/// <param name="configUser">config.ProxyUser.</param>
		/// <param name="configPassword">config.ProxyPassword.</param>
		internal static Decision Decide(
			bool silent, bool useProxy, string? proxyUrl,
			string? configUser, string? configPassword)
		{
			string user = configUser ?? string.Empty;
			string password = configPassword ?? string.Empty;

			// No proxy in play, or unattended: config is authoritative, no prompt.
			// (Silent must never block on input; no-proxy has nothing to resolve.)
			if (silent || !useProxy || string.IsNullOrWhiteSpace(proxyUrl))
			{
				return new Decision(Outcome.UseAsConfigured, user, password);
			}

			// Interactive with a proxy: the operator can always supply credentials.
			// Config is a default to accept or override, never a lock.
			bool hasConfigured =
				!string.IsNullOrWhiteSpace(configUser)
				|| !string.IsNullOrWhiteSpace(configPassword);

			return hasConfigured
				? new Decision(Outcome.OfferUseOrOverride, user, password)
				: new Decision(Outcome.PromptFresh, user, password);
		}
	}
}
