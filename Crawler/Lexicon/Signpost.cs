namespace Crawler.Lexicon
{
	using System.IO;

	/// <summary>
	/// Fresh-rig signpost for spell-check dictionaries.
	///
	/// Dictionaries (.dic/.aff) are operator-supplied and never shipped, so a new
	/// deployment has nothing to spell-check against and no obvious place to start.
	/// <see cref="Integrity"/> owns the configured-but-broken cases
	/// (missing files, the checksum bootstrap, mismatches); it deliberately passes
	/// when no bundles are configured at all. This fills exactly that gap: when
	/// DictionaryBundles is empty, it ensures a labelled <c>dictionaries\</c> folder
	/// exists with a readme telling the operator where to put files, how to wire
	/// them up in config.private.json, and where to find dictionaries — then lets
	/// the run continue (spell-check simply finds nothing until the operator acts).
	///
	/// Never overwrites an existing readme (the operator may have edited it) and
	/// never throws — a read-only install degrades to a logged warning. Call after
	/// the logger is initialised and config is loaded; AppContext.BaseDirectory is
	/// the bundle directory under a single-file publish (Assembly.Location is empty
	/// there, so it must not be used).
	/// </summary>
	internal static class Signpost
	{
		private const string DictionariesFolder = "dictionaries";
		private const string ReadmeFileName = "readme.txt";

		/// <summary>
		/// When no dictionary bundles are configured, ensure the dictionaries folder
		/// and its readme exist and hint the operator at them, then return. When one
		/// or more bundles are configured, do nothing (Integrity owns it).
		/// </summary>
		public static void EmitIfUnconfigured(Config config)
		{
			try
			{
				// Operator has configured bundles -> Integrity owns it; stay silent.
				if (config?.DictionaryBundles is { Count: > 0 })
				{
					return;
				}

				var folder = Path.Combine(AppContext.BaseDirectory, DictionariesFolder);
				var readmePath = Path.Combine(folder, ReadmeFileName);

				Directory.CreateDirectory(folder);    // no-op if it already exists
				if (!File.Exists(readmePath))          // never overwrite an operator's edits
				{
					File.WriteAllText(readmePath, ReadmeText);
				}

				Logger.LogWarning(
					$"No spell-check dictionaries are configured. See " +
					$"'{DictionariesFolder}\\{ReadmeFileName}' for how to add them — " +
					"spell-check will find nothing until you do.");
			}
			catch (Exception ex)
			{
				// A folder problem (e.g. a read-only install directory) must never stop
				// the run; the app is fully usable without the signpost being writable.
				Logger.LogWarning(
					$"Could not create the dictionaries signpost: " +
					$"{ex.GetType().Name}: {ex.Message}");
			}
		}

		// Emitted verbatim to dictionaries\readme.txt. Paths use double backslashes so
		// the JSON block pastes straight into config.private.json without re-escaping.
		private const string ReadmeText =
"""
DICTIONARIES
============
Spell-check needs Hunspell dictionaries. None ship with this app — you
supply your own and wire them up in config.

0. USE YOUR OWN CONFIG, NOT THE TEMPLATE
   Copy  config.json  ->  config.private.json  and make all your edits there.
   The app prefers config.private.json automatically when it exists.

   config.json is the shipped reference template — don't edit it directly:
     - the app may overwrite it when you update to a new version, and
     - it isn't private to you.
   And if you keep the app under version control, editing config.json means
   your changes clash with the template every time you pull an update —
   keeping your settings in config.private.json avoids that entirely.

1. DROP YOUR DICTIONARY FILES HERE
   Each language is a pair in this folder:  <name>.dic  and  <name>.aff
   Find them, for example, at:
     https://github.com/LibreOffice/dictionaries

2. WIRE THEM UP IN config.private.json
   Add one entry per language to the "DictionaryBundles" array — note the
   comma between entries, and that Windows paths use double backslashes:

   "DictionaryBundles": [
     {
       "DisplayName":  "English",
       "Comment":      "en_US — primary audience",
       "LanguageCode": "en",
       "DicFile":      "dictionaries\\en_US.dic",
       "AffFile":      "dictionaries\\en_US.aff",
       "DicChecksum":  "",
       "AffChecksum":  ""
     },
     {
       "DisplayName":  "German",
       "Comment":      "de_DE not de_AT — audience is Germany",
       "LanguageCode": "de",
       "DicFile":      "dictionaries\\de_DE.dic",
       "AffFile":      "dictionaries\\de_DE.aff",
       "DicChecksum":  "",
       "AffChecksum":  ""
     }
   ]

   LanguageCode is the bare ISO 639-1 code matching the page's html lang
   ("en", "de") — even though the files are named en_US / de_DE.

3. FIRST RUN PINS THE FILES
   Leave DicChecksum / AffChecksum empty the first time. On startup the app
   computes each file's SHA-256, writes the values to application.log, and
   halts asking you to paste them into config. Paste them, run again, done.
   (This catches any later silent change to a dictionary.)

----------------------------------------------------------------------
Redistribution note: using dictionaries is your choice. If you ever
re-distribute this app WITH dictionaries included, each dictionary carries
its own licence, separate from this app's — check them first.
""";
	}
}
