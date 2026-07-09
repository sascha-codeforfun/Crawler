namespace Crawler
{
	using System.IO;
	using System.Reflection;

	/// <summary>
	/// Distribution-integrity self-heal for the third-party license notices.
	///
	/// The license texts under <c>licenses/</c> are embedded into the assembly at
	/// build time (see the &lt;EmbeddedResource&gt; entries in the .csproj). On
	/// startup this writes any that are absent next to the executable, so a build
	/// or copy step that drops the folder cannot ship the app without its notices.
	///
	/// Only missing files are written: once a file is present on disk it is treated
	/// as authoritative and left untouched (the operator may have a newer notices
	/// set; we never overwrite it). Never throws — a read-only install location
	/// degrades to a logged warning rather than failing the run.
	///
	/// AppContext.BaseDirectory is the bundle directory under a single-file publish;
	/// Assembly.Location is empty there, so it must not be used for the target path.
	/// </summary>
	internal static class LicenseAssets
	{
		private const string LicensesFolder = "licenses";

		// Matched against embedded resource names by trailing filename, so the lookup
		// stays independent of the root namespace and MSBuild's path-to-dot mangling
		// (e.g. "licenses/MPL-1.1.txt" embeds as "Crawler.licenses.MPL-1.1.txt").
		private static readonly string[] FileNames =
		{
			"THIRD-PARTY-NOTICES.txt",
			"MIT.txt",
			"MPL-1.1.txt",
			"WeCantSpell.Hunspell.txt",
		};

		/// <summary>
		/// Restore any license notice missing from the <c>licenses/</c> folder beside
		/// the executable from its embedded copy. Safe to call once at startup after
		/// the logger is initialized; never throws.
		/// </summary>
		public static void EmitIfMissing()
		{
			try
			{
				var assembly = Assembly.GetExecutingAssembly();
				var resourceNames = assembly.GetManifestResourceNames();
				var targetDir = Path.Combine(AppContext.BaseDirectory, LicensesFolder);

				foreach (var fileName in FileNames)
				{
					var targetPath = Path.Combine(targetDir, fileName);
					if (File.Exists(targetPath))
					{
						continue; // present and authoritative — leave it.
					}

					var resourceName = Array.Find(
						resourceNames,
						n => n.EndsWith("." + fileName, StringComparison.Ordinal));

					if (resourceName is null)
					{
						Logger.LogWarning(
							$"License notice '{fileName}' is missing on disk and no embedded " +
							"copy was found to restore it; the distribution may be incomplete.");
						continue;
					}

					using var stream = assembly.GetManifestResourceStream(resourceName);
					if (stream is null)
					{
						Logger.LogWarning(
							$"Embedded license resource '{resourceName}' could not be opened.");
						continue;
					}

					Directory.CreateDirectory(targetDir);
					using (var file = File.Create(targetPath))
					{
						stream.CopyTo(file);
					}

					Logger.LogWarning(
						$"License notice '{LicensesFolder}/{fileName}' was missing and has been " +
						"restored from the embedded copy; check the packaging step that drops it.");
				}
			}
			catch (Exception ex)
			{
				// A notices-folder problem (e.g. a read-only install directory) must never
				// stop the run — the app is usable without the folder being writable.
				Logger.LogWarning(
					$"Could not verify or restore the license notices: " +
					$"{ex.GetType().Name}: {ex.Message}");
			}
		}
	}
}
