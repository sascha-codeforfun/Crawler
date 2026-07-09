namespace Crawler
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>
	/// Pure decision logic for choosing which configured site a run will process.
	///
	/// A single run processes ONE site (multi-site sweep is a later stage). The
	/// selection model:
	///   * Silent mode       → the one <see cref="SiteConfig.IsPrimary"/> site, no
	///                          prompt. Silent runs must never block on input.
	///   * Interactive mode  → the operator is shown a numbered menu of all sites
	///                          and picks one; pressing Enter (empty input) selects
	///                          the primary, so the default matches silent mode.
	///
	/// The "exactly one primary" invariant is enforced at validation time
	/// (Config.ValidateResolvedSiteSelection), not here — by the time this runs the
	/// config is already known to hold exactly one primary, so <see cref="Primary"/>
	/// is total. This type is the TESTABLE half: it maps a mode + an optional
	/// already-parsed operator choice to a selected site, with no console I/O.
	///
	/// The I/O half (rendering the numbered menu, reading the keypress/line, the
	/// re-prompt-on-invalid loop, the explicit [Q] cancel) lives in Program, composed
	/// from ConsoleUi primitives and operator-eyeball-verified — following the same
	/// split as <see cref="ProxyCredentialResolution"/>. The loop warns and re-prompts
	/// on an invalid choice and cancels only on an explicit cancel key
	/// — it never silently falls through.
	/// </summary>
	internal static class SiteSelection
	{
		/// <summary>
		/// The primary (default) site: the single entry with
		/// <see cref="SiteConfig.IsPrimary"/> = true. Used directly in silent mode and
		/// as the Enter-default in interactive mode. Assumes the exactly-one-primary
		/// invariant has been validated; throws if it has not (defensive — a caller
		/// reaching here with zero primaries is a validation-ordering bug).
		/// </summary>
		internal static SiteConfig Primary(IReadOnlyList<SiteConfig> sites)
		{
			ArgumentNullException.ThrowIfNull(sites);
			var primaries = sites.Where(s => s.IsPrimary).ToList();
			if (primaries.Count != 1)
			{
				throw new InvalidOperationException(
					$"SiteSelection.Primary requires exactly one IsPrimary site; found {primaries.Count}. "
					+ "This should have been caught by config validation before selection.");
			}

			return primaries[0];
		}

		/// <summary>
		/// The silent-mode choice: always the primary, no interaction.
		/// </summary>
		internal static SiteConfig SelectSilent(IReadOnlyList<SiteConfig> sites)
			=> Primary(sites);

		/// <summary>
		/// The interactive choice, given the operator's already-parsed selection.
		/// <paramref name="oneBasedChoice"/> is the number the operator typed
		/// (1-based, matching the displayed menu), or null when they pressed Enter
		/// for the default. A value out of range returns null so the caller's I/O
		/// loop re-prompts (it must NOT be silently coerced to a default — that would
		/// violate the keypress-prompt contract). Enter (null) selects the primary.
		/// </summary>
		/// <returns>The selected site, or null if the choice was out of range.</returns>
		internal static SiteConfig? SelectInteractive(
			IReadOnlyList<SiteConfig> sites, int? oneBasedChoice)
		{
			ArgumentNullException.ThrowIfNull(sites);

			if (oneBasedChoice is null)
			{
				return Primary(sites);                  // Enter → default → primary
			}

			int idx = oneBasedChoice.Value - 1;
			if (idx < 0 || idx >= sites.Count)
			{
				return null;                            // out of range → caller re-prompts
			}

			return sites[idx];
		}
	}
}
