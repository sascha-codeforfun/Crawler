namespace Crawler.Security
{
	using System;
	using System.IO;

	/// <summary>
	/// Pure path-containment guard for the write surface. Given a capture root
	/// and a candidate file name, resolves the final absolute path and reports
	/// whether it stays under the root. No I/O, no logging, no prompting, no
	/// throwing — the caller owns reporting and the abort/continue policy. Keeping
	/// the primitive deterministic and side-effect-free is what lets it be
	/// exhaustively tested against hostile inputs.
	/// </summary>
	public static class PathContainmentCheck
	{
		// [KEEP] Security boundary — this is the last line of defence when the
		// crawled origin itself is hostile. Under the project threat model an
		// in-scope host may be compromised and can choose every URL and every
		// response, so a download's file name is attacker-influenced (query-bearing
		// assets in particular take a URL-path-derived naming branch). The host
		// allowlist cannot help once the host is owned; only pinning every write
		// under the capture root can. Treat the candidate name as adversarial and
		// never trust it to be benign. Fail-closed: any resolution error, or any
		// doubt, resolves to Escaped (refuse the write) — never to Contained.

		/// <summary>
		/// Outcome of a containment check. <see cref="Safe"/> is true only when the
		/// resolved path is strictly under the capture root; <see cref="FullPath"/>
		/// then carries the concrete absolute path that is safe to write. On an
		/// Escaped verdict <see cref="FullPath"/> is empty and <see cref="Reason"/>
		/// carries a short machine-readable cause.
		/// </summary>
		public readonly record struct Verdict(bool Safe, string FullPath, string Reason)
		{
			internal static Verdict Contained(string fullPath) => new(true, fullPath, "contained");

			internal static Verdict Escaped(string reason) => new(false, string.Empty, reason);
		}

		/// <summary>
		/// Resolves <paramref name="candidateName"/> under <paramref name="captureRoot"/>
		/// and verifies the result stays within the root. Returns a Contained verdict
		/// (with the safe absolute path) only when containment holds; otherwise an
		/// Escaped verdict. Never throws.
		/// </summary>
		public static Verdict Resolve(string captureRoot, string candidateName)
		{
			if (string.IsNullOrEmpty(captureRoot))
			{
				return Verdict.Escaped("empty-root");
			}

			if (string.IsNullOrEmpty(candidateName))
			{
				return Verdict.Escaped("empty-name");
			}

			string rootFull;
			string combinedFull;
			try
			{
				// Normalise the root to an absolute form ending in a single directory
				// separator so the prefix test below cannot be fooled by a sibling
				// whose name merely shares the root's prefix (…/download vs
				// …/download-evil).
				rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(captureRoot))
					+ Path.DirectorySeparatorChar;

				// Path.Combine returns the candidate verbatim when it is rooted
				// (e.g. "/etc/passwd" or "C:\\x"); GetFullPath then resolves it — along
				// with any ".." / dot-segments — to a concrete absolute path. Both the
				// rooted escape and the climb-out escape are caught by the test below.
				combinedFull = Path.GetFullPath(Path.Combine(captureRoot, candidateName));
			}
			catch (Exception ex) when (ex is ArgumentException
				or PathTooLongException
				or NotSupportedException
				or System.Security.SecurityException)
			{
				return Verdict.Escaped($"unresolvable:{ex.GetType().Name}");
			}

			// Ordinal compare is correct here: the contained case shares the exact
			// root string (both derive from captureRoot), so casing always matches;
			// the escape case mismatches and is rejected. Erring toward mismatch is
			// the fail-closed direction.
			if (!combinedFull.StartsWith(rootFull, StringComparison.Ordinal))
			{
				return Verdict.Escaped("outside-root");
			}

			return Verdict.Contained(combinedFull);
		}
	}
}
