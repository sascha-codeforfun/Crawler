namespace Crawler
{
	/// <summary>
	/// Foundational startup check for <see cref="Config.BaseDirectory"/>: it must sit
	/// on a drive or root that exists on this machine. Every crawl session folder is
	/// created beneath BaseDirectory, so the app can create a missing folder, but it
	/// cannot invent a missing drive. An absolute BaseDirectory whose root is absent
	/// (e.g. "X:\..." with no X: drive) would otherwise crash later at
	/// <c>Directory.CreateDirectory</c> with an unhandled <c>DirectoryNotFoundException</c>.
	///
	/// This is an ENVIRONMENT check (machine-dependent) and is deliberately separate
	/// from <c>Config.ValidateConfig</c>, which is machine-independent structure
	/// validation: a structurally-valid config can still name a drive this machine
	/// lacks. Keeping it out of LoadFromJson also keeps the config round-trip drift
	/// guard machine-independent.
	///
	/// Mirrors <see cref="DictionaryIntegrity.CheckOrHalt"/> in shape and convention:
	/// run at startup (after config load, before any directory creation or I/O); on
	/// failure write the detail to application.log and a calm CONFIG CHECK screen to
	/// the console, then return false (caller is expected to PressEnterToExit and return).
	/// </summary>
	public static class BaseDirectoryCheck
	{
		/// <summary>
		/// Returns true if BaseDirectory's root is reachable (caller may proceed).
		/// Returns false after issuing a halt screen (caller must abort).
		/// </summary>
		public static bool CheckOrHalt(Config config)
		{
			var baseDirectory = config?.BaseDirectory;
			if (IsRootReachable(baseDirectory))
			{
				return true;
			}

			// Exhaustive plain detail to the log (also the only record in a silent run);
			// the console gets the calm CONFIG CHECK screen instead of a wall of red.
			Logger.LogDetailToFile(
				$"BaseDirectory '{baseDirectory}' is on a drive or root that does not exist on this "
				+ "machine. Every crawl session folder is created under BaseDirectory; the app cannot "
				+ "create folders on a drive that is not present, so it cannot proceed. Set BaseDirectory "
				+ "to a path on an existing drive.");
			ConsoleUi.WriteConfigCheck("Working folder", BuildHaltBlocks(baseDirectory));

			return false;
		}

		// ── Internals (visible to tests) ──────────────────────────────────

		/// <summary>
		/// Pure reachability predicate. A null/empty BaseDirectory is treated as
		/// reachable here (emptiness is <c>Config.ValidateConfig</c>'s concern, not
		/// this check's). A relative path roots under the program directory, which
		/// always exists, so it is reachable. A rooted/absolute path is reachable iff
		/// its root (drive or UNC share) exists on this machine. A missing folder on an
		/// existing root is reachable — it is auto-created downstream; only a missing
		/// drive/root is not.
		/// </summary>
		internal static bool IsRootReachable(string? baseDirectory)
		{
			if (string.IsNullOrWhiteSpace(baseDirectory)
				|| !System.IO.Path.IsPathRooted(baseDirectory))
			{
				return true;
			}

			var root = System.IO.Path.GetPathRoot(baseDirectory);
			return string.IsNullOrEmpty(root) || System.IO.Directory.Exists(root);
		}

		/// <summary>
		/// Builds the calm CONFIG CHECK screen: Problem / Why / Fix, mirroring the
		/// dictionary-integrity halt. Prose lines word-wrap to the value column.
		/// </summary>
		private static IReadOnlyList<ConsoleUi.CheckBlock> BuildHaltBlocks(string? baseDirectory)
		{
			static ConsoleUi.CheckLine P(string t) => new(ConsoleUi.CheckTone.Prose, t);

			return new List<ConsoleUi.CheckBlock>
			{
				new ConsoleUi.CheckBlock("Problem", new[]
				{
					P($"BaseDirectory '{baseDirectory}' can't be reached: its drive or root "
						+ "doesn't exist on this machine."),
				}),
				new ConsoleUi.CheckBlock("Why", new[]
				{
					P("Every crawl session folder is created under BaseDirectory. The app can "
						+ "create a missing folder, but it cannot invent a missing drive — so it "
						+ "can't proceed."),
				}),
				new ConsoleUi.CheckBlock("Fix", new[]
				{
					P("Set BaseDirectory in your config to a path on an existing drive: a folder "
						+ "under the program directory, or a valid absolute path."),
				}),
			};
		}
	}
}
