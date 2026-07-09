using System.Security.Cryptography;
using System.Text;

namespace Crawler.Lexicon
{
	/// <summary>
	/// Dictionary integrity verification.
	///
	/// On every startup, before any pipeline runs, the app verifies that every
	/// configured Bundle's .dic and .aff files match the SHA-256
	/// checksums recorded in config. Three failure modes:
	///
	///   1. File missing on disk — halt with file-not-found message.
	///   2. Checksum empty in config — bootstrap halt. The app computes the
	///      file's actual checksum, writes a paste-ready snippet to
	///      application.log, and halts. The operator pastes the checksum into
	///      config and re-runs.
	///   3. Checksum present but doesn't match the file — mismatch halt. The
	///      app reports expected vs actual and asks the operator to investigate
	///      (legitimate update vs tampering).
	///
	/// Why this exists: the app loads .aff and .dic files directly into the
	/// Hunspell spell-checker. A tampered or untested dictionary file could
	/// produce incorrect findings or behave unexpectedly. Checksums in config
	/// lock the dictionary files to a known state.
	///
	/// Why bundle-inline (not a separate DictionaryChecksums section): locality
	/// of reference. An operator looking at a bundle sees its checksums right
	/// there. Renames stay linked.
	///
	/// Why SHA-256, no algorithm prefix: simple, modern, fast, in BCL.
	/// 64 hex chars unambiguously identifies the algorithm. If we ever need
	/// to switch, that's a config schema change.
	///
	/// CustomDictionaryFile is NOT checked here — it's intentionally mutable
	/// (operator triage adds words). Filesystem ACLs protect that file
	/// instead. Documented in README.
	/// </summary>
	public static class Integrity
	{
		// ── Per-field check result ────────────────────────────────────────

		public enum FieldStatus
		{
			Pass,
			MissingFile,      // path didn't resolve to an existing file
			MissingChecksum,  // file exists, checksum empty in config — bootstrap
			Mismatch          // file exists, checksum present but doesn't match
		}

		public sealed record FieldResult(
			string LanguageCode,
			string FieldName,       // "DicFile" or "AffFile"
			string FilePath,
			string ChecksumFieldName,  // "DicChecksum" or "AffChecksum"
			FieldStatus Status,
			string ExpectedChecksum, // from config (may be empty)
			string ActualChecksum);  // computed (empty if MissingFile)

		// ── Public entry point ────────────────────────────────────────────

		/// <summary>
		/// Verifies all configured DictionaryBundles. Returns true if all pass
		/// (caller may proceed). Returns false after issuing a halt message
		/// (caller must abort — PressEnterToExit and return from RunAsync).
		///
		/// On halt, writes a framed operator-facing message to console
		/// (via ConsoleUi) AND to application.log via Logger.LogError.
		/// </summary>
		public static bool CheckOrHalt(Config config)
		{
			if (config?.DictionaryBundles == null || config.DictionaryBundles.Count == 0)
			{
				// No bundles configured = nothing to verify. Spell-check
				// won't work either, but that's an orthogonal config issue
				// surfaced by Config validation, not by this check.
				return true;
			}

			var results = VerifyAll(config.DictionaryBundles);
			var failures = results.Where(r => r.Status != FieldStatus.Pass).ToList();

			// 652 — DisplayName is required: a bundle with no name can't be labelled, so a nameless
			// bundle halts alongside file/checksum failures, on the same unified screen.
			var nameless = config.DictionaryBundles
				.Where(b => string.IsNullOrWhiteSpace(b.DisplayName))
				.ToList();

			if (failures.Count == 0 && nameless.Count == 0)
			{
				return true;
			}

			// One screen describing every issue across all bundles, so the operator sees the complete
			// picture in one halt rather than fixing one, re-running, and hitting the next.
			var message = BuildHaltMessage(failures);
			if (nameless.Count > 0)
			{
				message += Environment.NewLine + Environment.NewLine + "MISSING DISPLAYNAME" + Environment.NewLine
					+ string.Join(Environment.NewLine, nameless.Select(b => $"  Bundle \"{b.LanguageCode}\" has no DisplayName."));
			}

			// 651 — the exhaustive plain detail goes to the log (also the only record in a silent run);
			// the console gets a calm CONFIG CHECK screen instead of a wall of red. 652 renders it by
			// bundle (stable config order), each bundle showing its whole situation in one place.
			Logger.LogDetailToFile(message);
			ConsoleUi.WriteConfigCheck("Dictionary integrity", BuildHaltBlocks(config.DictionaryBundles, results));

			return false;
		}

		// ── Internals (visible to tests) ──────────────────────────────────

		/// <summary>
		/// Verifies every DicFile and AffFile across all bundles. Returns one
		/// FieldResult per file (two per bundle). Order: each bundle's
		/// DicFile then AffFile, bundles in config order.
		/// </summary>
		internal static List<FieldResult> VerifyAll(List<DictionaryBundleConfig> bundles)
		{
			List<FieldResult> results = [];
			foreach (var bundle in bundles)
			{
				results.Add(VerifyField(
					bundle.LanguageCode, "DicFile", bundle.DicFile,
					"DicChecksum", bundle.DicChecksum));
				results.Add(VerifyField(
					bundle.LanguageCode, "AffFile", bundle.AffFile,
					"AffChecksum", bundle.AffChecksum));
			}
			return results;
		}

		/// <summary>
		/// Verifies a single file/checksum pair.
		/// </summary>
		internal static FieldResult VerifyField(
			string languageCode,
			string fieldName,
			string filePath,
			string checksumFieldName,
			string expectedChecksum)
		{
			// Empty path is a config-level error (validated elsewhere) — treat
			// as MissingFile here to surface it consistently.
			if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
			{
				return new FieldResult(
					languageCode, fieldName, filePath, checksumFieldName,
					FieldStatus.MissingFile,
					ExpectedChecksum: expectedChecksum ?? string.Empty,
					ActualChecksum: string.Empty);
			}

			var actual = ComputeSha256(filePath);

			if (string.IsNullOrEmpty(expectedChecksum))
			{
				return new FieldResult(
					languageCode, fieldName, filePath, checksumFieldName,
					FieldStatus.MissingChecksum,
					ExpectedChecksum: string.Empty,
					ActualChecksum: actual);
			}

			// Case-insensitive compare on the hex string — operators who paste
			// from `sha256sum` (lowercase) or `CertUtil` (uppercase by default
			// on some Windows versions) should both work without surprise.
			if (!string.Equals(expectedChecksum.Trim(), actual,
				StringComparison.OrdinalIgnoreCase))
			{
				return new FieldResult(
					languageCode, fieldName, filePath, checksumFieldName,
					FieldStatus.Mismatch,
					ExpectedChecksum: expectedChecksum,
					ActualChecksum: actual);
			}

			return new FieldResult(
				languageCode, fieldName, filePath, checksumFieldName,
				FieldStatus.Pass,
				ExpectedChecksum: expectedChecksum,
				ActualChecksum: actual);
		}

		/// <summary>
		/// Computes the SHA-256 of a file's content, returned as a lowercase
		/// 64-character hex string with no algorithm prefix.
		/// </summary>
		internal static string ComputeSha256(string filePath)
		{
			using var stream = File.OpenRead(filePath);
			var hash = SHA256.HashData(stream);
			return Convert.ToHexString(hash).ToLowerInvariant();
		}

		// ── Halt message builder ──────────────────────────────────────────

		/// <summary>
		/// Builds the operator-facing halt message describing all failures.
		/// Segments the message by failure category for clarity.
		/// </summary>
		internal static string BuildHaltMessage(List<FieldResult> failures)
		{
			var sb = new StringBuilder();

			var missingFiles = failures.Where(f => f.Status == FieldStatus.MissingFile).ToList();
			var missingChecksums = failures.Where(f => f.Status == FieldStatus.MissingChecksum).ToList();
			var mismatches = failures.Where(f => f.Status == FieldStatus.Mismatch).ToList();

			sb.AppendLine();

			if (missingFiles.Count > 0)
			{
				sb.AppendLine("MISSING DICTIONARY FILES");
				sb.AppendLine();
				sb.AppendLine("The following file paths in DictionaryBundles do not point to");
				sb.AppendLine("existing files. Check for typos in DicFile/AffFile, or that the");
				sb.AppendLine("dictionary files are present in the configured paths.");
				sb.AppendLine();
				foreach (var r in missingFiles)
				{
					sb.AppendLine($"  Bundle \"{r.LanguageCode}\" — {r.FieldName}:");
					sb.AppendLine($"    Configured path: {(string.IsNullOrEmpty(r.FilePath) ? "(empty)" : r.FilePath)}");
				}
				sb.AppendLine();
			}

			if (missingChecksums.Count > 0)
			{
				sb.AppendLine("CHECKSUMS REQUIRED (FIRST-RUN BOOTSTRAP)");
				sb.AppendLine();
				sb.AppendLine("The following DictionaryBundles entries are missing checksums.");
				sb.AppendLine("Copy the *Checksum lines below into the matching bundle entries");
				sb.AppendLine("in your config, OR remove the bundle if you don't want this");
				sb.AppendLine("language.");
				sb.AppendLine();

				// Group by LanguageCode so each bundle's two checksums appear
				// together — easier to paste.
				foreach (var grp in missingChecksums.GroupBy(r => r.LanguageCode))
				{
					sb.AppendLine($"  Bundle \"{grp.Key}\":");
					foreach (var r in grp)
					{
						sb.AppendLine($"    \"{r.ChecksumFieldName}\": \"{r.ActualChecksum}\"");
					}
				}
				sb.AppendLine();
			}

			if (mismatches.Count > 0)
			{
				sb.AppendLine("CHECKSUM MISMATCH");
				sb.AppendLine();
				sb.AppendLine("The following dictionary files do not match their configured");
				sb.AppendLine("checksums. If you updated a file deliberately (e.g. LibreOffice");
				sb.AppendLine("upstream release), verify the new content is what you want and");
				sb.AppendLine("update the corresponding *Checksum in your config to the actual");
				sb.AppendLine("value shown below.");
				sb.AppendLine();
				sb.AppendLine("If you did NOT update these files deliberately, INVESTIGATE");
				sb.AppendLine("before proceeding. A mismatched checksum may indicate tampering.");
				sb.AppendLine();
				foreach (var r in mismatches)
				{
					sb.AppendLine($"  Bundle \"{r.LanguageCode}\" — {r.FieldName}: {r.FilePath}");
					sb.AppendLine($"    Expected (from config): {r.ExpectedChecksum}");
					sb.AppendLine($"    Actual (current file):  {r.ActualChecksum}");
					sb.AppendLine($"    Paste-ready replacement: \"{r.ChecksumFieldName}\": \"{r.ActualChecksum}\"");
				}
				sb.AppendLine();
			}

			sb.AppendLine("WHY THIS HALT EXISTS");
			sb.AppendLine();
			sb.AppendLine("The app loads .aff and .dic files directly into the spell-checker.");
			sb.AppendLine("A tampered or untested dictionary file could produce incorrect");
			sb.AppendLine("findings or behave unexpectedly. Checksums in your config lock the");
			sb.AppendLine("dictionary files to a known state — the app refuses to run if a");
			sb.AppendLine("file changes unexpectedly. For maximum protection in production,");
			sb.AppendLine("set filesystem permissions so the user account running the app");
			sb.AppendLine("cannot modify config or the dictionary files (see README).");

			return sb.ToString();
		}

		/// <summary>
		/// 651 — the calm CONFIG CHECK shape of the same halt: Problem / Why / Fix in the app's
		/// label·detail vocabulary, then per-bundle data the operator reads or pastes. Mirrors
		/// BuildHaltMessage's content (which stays the exhaustive log form) but structured for the
		/// console renderer; colour is applied by the renderer from each line's tone — prose default,
		/// data dim, and only the genuine attention tokens amber. No full-block red.
		/// </summary>
		/// <summary>
		/// 652 — the calm CONFIG CHECK shape, rendered BY BUNDLE in stable config order so findings do
		/// not wander between fix-and-reload passes. For each bundle that has any issue (no DisplayName,
		/// a missing file, a wrong or unset checksum) it shows the bundle's whole situation in one place:
		/// file → DicChecksum → AffChecksum. Colour carries severity — missing file / wrong checksum are
		/// errors (red); a missing name or an unset checksum is setup-incomplete (amber); the on-disk
		/// value is neutral (dim), presented but not emphasised. `bundles` gives the name and the order;
		/// `results` is the full VerifyAll output (Dic then Aff per bundle, in bundle order).
		/// </summary>
		internal static List<ConsoleUi.CheckBlock> BuildHaltBlocks(
			List<DictionaryBundleConfig> bundles, List<FieldResult> results)
		{
			static ConsoleUi.CheckLine P(string t) => new(ConsoleUi.CheckTone.Prose, t);
			static ConsoleUi.CheckLine A(string t) => new(ConsoleUi.CheckTone.Accent, t);

			int filesMissing = results.Count(r => r.Status == FieldStatus.MissingFile);
			int mismatched = results.Count(r => r.Status == FieldStatus.Mismatch);
			int checksumsUnset = results.Count(r => r.Status == FieldStatus.MissingChecksum);
			int namesMissing = bundles.Count(b => string.IsNullOrWhiteSpace(b.DisplayName));

			var blocks = new List<ConsoleUi.CheckBlock>();

			// Problem — aggregate counts, most severe first.
			var problem = new List<ConsoleUi.CheckLine>();
			if (filesMissing > 0)
			{
				problem.Add(P($"{filesMissing} dictionary file(s) not found at the configured path."));
			}
			if (mismatched > 0)
			{
				problem.Add(P($"{mismatched} dictionary file(s) no longer match the checksum in your config."));
			}
			if (checksumsUnset > 0)
			{
				problem.Add(P($"{checksumsUnset} checksum(s) not set yet."));
			}
			if (namesMissing > 0)
			{
				problem.Add(P($"{namesMissing} bundle(s) have no DisplayName."));
			}
			blocks.Add(new ConsoleUi.CheckBlock("Problem", problem));

			// Why — calm rationale.
			blocks.Add(new ConsoleUi.CheckBlock("Why", new[]
			{
				P("The app loads the .aff and .dic files straight into the spell-checker, and shows each "
					+ "dictionary by name. The checksums in your config lock those files to a state you have "
					+ "already trusted, so the app can tell when one changes — an upstream release, or "
					+ "tampering. It stops here so you decide which."),
			}));

			// Fix — concrete, per category present. The one amber line is the genuine attention case.
			var fix = new List<ConsoleUi.CheckLine>();
			if (filesMissing > 0)
			{
				fix.Add(P("Not found: check DicFile/AffFile for a typo, or place the dictionary file at the configured path."));
			}
			if (mismatched > 0)
			{
				fix.Add(P("Changed on purpose (e.g. an upstream release): confirm the new content, then copy the "
					+ "file-on-disk value into the matching *Checksum in your config."));
				fix.Add(A("Did NOT change them: investigate before proceeding — a checksum should not move on its own."));
			}
			if (checksumsUnset > 0)
			{
				fix.Add(P("Not set yet (first run): copy the file-on-disk value into the matching *Checksum in your config."));
			}
			if (namesMissing > 0)
			{
				fix.Add(P("No name: add a DisplayName to each bundle below, e.g. \"DisplayName\": \"Greek\"."));
			}
			blocks.Add(new ConsoleUi.CheckBlock("Fix", fix));

			// Per-bundle findings — stable config order; within a bundle: name → DicFile → AffFile.
			// VerifyAll emits Dic then Aff per bundle, so results[2i]/[2i+1] pair with bundles[i].
			for (int i = 0; i < bundles.Count; i++)
			{
				var b = bundles[i];
				FieldResult? dic = (2 * i) < results.Count ? results[2 * i] : null;
				FieldResult? aff = (2 * i + 1) < results.Count ? results[2 * i + 1] : null;

				bool nameMissing = string.IsNullOrWhiteSpace(b.DisplayName);
				bool dicBad = dic != null && dic.Status != FieldStatus.Pass;
				bool affBad = aff != null && aff.Status != FieldStatus.Pass;
				if (!nameMissing && !dicBad && !affBad)
				{
					continue; // bundle is clean — nothing to show, nothing to wander.
				}

				string header = nameMissing ? $"({b.LanguageCode})" : $"{b.DisplayName} ({b.LanguageCode})";
				var lines = new List<ConsoleUi.CheckLine>();

				if (nameMissing)
				{
					lines.Add(new ConsoleUi.CheckLine(ConsoleUi.CheckTone.Accent, "no DisplayName — add one", "name"));
				}
				if (dicBad)
				{
					AppendFileFinding(lines, dic!);
				}
				if (affBad)
				{
					AppendFileFinding(lines, aff!);
				}

				blocks.Add(new ConsoleUi.CheckBlock(header, lines, HeadingOnOwnLine: true));
			}

			return blocks;
		}

		// One file's finding lines: a missing file is an error (red) with its path; a checksum issue
		// shows the path (neutral), then `configured` (amber if unset, red if wrong) and the neutral
		// `file on disk` value. Values are emitted whole so they stay copy-paste-able.
		private static void AppendFileFinding(List<ConsoleUi.CheckLine> lines, FieldResult r)
		{
			if (r.Status == FieldStatus.MissingFile)
			{
				lines.Add(new ConsoleUi.CheckLine(ConsoleUi.CheckTone.Error, "file not found", r.FieldName));
				lines.Add(new ConsoleUi.CheckLine(ConsoleUi.CheckTone.Data,
					string.IsNullOrEmpty(r.FilePath) ? "(empty)" : r.FilePath, "path"));
				return;
			}

			lines.Add(new ConsoleUi.CheckLine(ConsoleUi.CheckTone.Data, r.FilePath, r.FieldName));
			if (r.Status == FieldStatus.MissingChecksum)
			{
				lines.Add(new ConsoleUi.CheckLine(ConsoleUi.CheckTone.Accent, "MISSING", "configured"));
			}
			else // Mismatch — present but wrong.
			{
				lines.Add(new ConsoleUi.CheckLine(ConsoleUi.CheckTone.Error, r.ExpectedChecksum, "configured"));
			}
			lines.Add(new ConsoleUi.CheckLine(ConsoleUi.CheckTone.Data, r.ActualChecksum, "file on disk"));
		}
	}
}
