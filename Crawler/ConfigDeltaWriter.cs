namespace Crawler
{
	using System.Text;
	using System.Text.Encodings.Web;
	using System.Text.Json;
	using System.Text.Json.Nodes;

	// ── ConfigDeltaWriter ─────────────────────────────────────────────────────
	//
	// Produces config.private.delta — a slim JSON file showing only the fields
	// where config.private.json differs from config.json. Run on every startup
	// AFTER all halt-able config validity checks pass (ValidateConfig and
	// Integrity.CheckOrHalt) so the operator never gets a delta for
	// a config that didn't run.
	//
	// Purpose (fileset #317):
	//   * Operator audit — see at a glance what's genuinely customized vs what
	//     redundantly redeclares baseline.
	//   * Public-repo prep — identify private-only settings that may belong in
	//     baseline (e.g. a generally-useful ContentQualityIssueSuppressions rule
	//     that's currently hidden in private).
	//   * Migration path — operator can review the delta, accept it as the new
	//     private config by renaming over the original.
	//
	// Conventions:
	//   * Overwritten on every run. No state preserved between runs.
	//   * No-op when config.private.json doesn't exist (no private = no diff).
	//   * Best-effort: any error writes a warning to application.log and skips
	//     the file; never aborts the run. The delta is diagnostic, not load-bearing.
	//   * No header comments inside the delta — the file should read as a
	//     drop-in replacement candidate. README.TXT documents the mechanism.
	//
	// Mechanism:
	//   Pass 1 — Structural diff over parsed Config instances. Reflection walk
	//            comparing baseline.X to private.X for every property. Builds a
	//            JsonNode tree containing only differing fields. List equality
	//            is order-sensitive at parsed-value level — the operator's
	//            chosen order IS the override even if semantically equivalent.
	//   Pass 2 — Serialize the JsonNode tree to indented JSON text.
	//   Pass 3 — Re-inject operator-authored adjacent comments from
	//            config.private.json. For each "propName": line in the emitted
	//            output, locate the matching declaration in the original text
	//            (by name + indentation depth), walk upward collecting
	//            consecutive // comment lines (stopping at blank or non-//),
	//            insert above the property in the output.
	//
	// Comment-walking rule: strict adjacency. Blank line or non-// line stops
	// the walk. Mirrors the operator's authoring layout — no duplication, no
	// synthesis. If the operator wrote sloppy adjacency, the delta is sloppy
	// in the same way. If tidy, the delta is tidy.
	// ─────────────────────────────────────────────────────────────────────────

	internal static class ConfigDeltaWriter
	{
		/// <summary>
		/// Writes config.private.delta to the directory containing the source
		/// files. No-op when private file doesn't exist. Best-effort — any
		/// exception is logged and swallowed; never propagates.
		/// </summary>
		/// <param name="baseConfigPath">Path to config.json (baseline).</param>
		/// <param name="privateConfigPath">Path to config.private.json. May not exist.</param>
		internal static void Write(string baseConfigPath, string privateConfigPath)
		{
			try
			{
				// No-op when no private config exists. Per the convention: no
				// private = no overrides = nothing to diff.
				if (!System.IO.File.Exists(privateConfigPath))
				{
					return;
				}

				// Read both files. Baseline must exist; private existed at the
				// File.Exists check above. Filter // comments the same way
				// Config.LoadFromJson does so deserialization sees clean JSON.
				if (!System.IO.File.Exists(baseConfigPath))
				{
					Logger.LogWarning(
						$"ConfigDeltaWriter: baseline config not found at '{baseConfigPath}' — skipping delta.");
					return;
				}

				var baseRaw = System.IO.File.ReadAllText(baseConfigPath, Encoding.UTF8);
				var privateRaw = System.IO.File.ReadAllText(privateConfigPath, Encoding.UTF8);

				var baseJson = FilterComments(baseRaw);
				var privateJson = FilterComments(privateRaw);

				// Pass 1: parse both, build a JsonNode tree containing only
				// fields where private differs from baseline.
				var baseNode = JsonNode.Parse(baseJson);
				var privateNode = JsonNode.Parse(privateJson);
				if (baseNode is not JsonObject baseObj || privateNode is not JsonObject privateObj)
				{
					Logger.LogWarning("ConfigDeltaWriter: root of one or both configs is not an object — skipping delta.");
					return;
				}

				var diff = ComputeObjectDiff(baseObj, privateObj);

				// Pass 2: serialize diff to indented JSON. The encoder choice
				// matters here (#317b): System.Text.Json's default encoder
				// escapes ", &, <, >, German umlauts, em dashes, and many
				// other characters as \uXXXX. That's a defense-in-depth choice
				// for JSON destined for HTML/JS embedding — but for a config
				// file the operator will read, rename, and overwrite their
				// source with, it's noise. A literal " written as \u0022 looks
				// alarming in a diff against a source file that uses \" — but
				// it's the SAME character. UnsafeRelaxedJsonEscaping uses
				// standard JSON escapes only (\", \\, \n, \r, \t, etc.) and
				// emits non-ASCII characters as their literal Unicode bytes
				// (which is why config.private.delta carries a UTF-8 BOM —
				// the file genuinely needs the encoding declaration). The
				// "Unsafe" in the name refers to HTML/JS embedding contexts,
				// NOT to JSON validity. Both encoders produce valid JSON; the
				// loader (System.Text.Json.JsonSerializer.Deserialize) accepts
				// either form identically.
				var emittedJson = diff.ToJsonString(new JsonSerializerOptions
				{
					WriteIndented = true,
					Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
				});

				// Pass 3: re-inject adjacent comments from private source.
				var privateLines = privateRaw.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
				var withComments = InjectAdjacentComments(emittedJson, privateLines);

				// Output path: alongside config.private.json.
				var dir = System.IO.Path.GetDirectoryName(privateConfigPath) ?? string.Empty;
				var deltaPath = System.IO.Path.Combine(dir, "config.private.delta");
				System.IO.File.WriteAllText(deltaPath, withComments, Encoding.UTF8);
				Logger.LogInfo($"Wrote config diff to {deltaPath}");
			}
			catch (System.Exception ex)
			{
				// Best-effort: log and swallow. A delta-writer failure must
				// never block the run.
				Logger.LogWarning($"ConfigDeltaWriter: failed to write delta — {ex.Message}");
			}
		}

		// ── Pass 1: structural diff ─────────────────────────────────────────

		/// <summary>
		/// Recursively compares two JsonObjects and returns a new JsonObject
		/// containing only the keys where private differs from baseline.
		/// For nested objects, recurses (emits only differing sub-fields).
		/// For lists/arrays and scalars, emits private's value if it differs
		/// from baseline's at parsed-value level.
		/// Keys present in private but absent in baseline are always emitted
		/// (they're overrides for fields baseline doesn't declare).
		/// </summary>
		internal static JsonObject ComputeObjectDiff(JsonObject baseObj, JsonObject privateObj)
		{
			var result = new JsonObject();

			foreach (var (key, privateValue) in privateObj)
			{
				if (!baseObj.TryGetPropertyValue(key, out var baseValue))
				{
					// Key is private-only — emit as-is.
					result[key] = privateValue?.DeepClone();
					continue;
				}

				if (privateValue is JsonObject privateChildObj && baseValue is JsonObject baseChildObj)
				{
					// Both are objects — recurse.
					var subDiff = ComputeObjectDiff(baseChildObj, privateChildObj);
					if (subDiff.Count > 0)
					{
						result[key] = subDiff;
					}

					continue;
				}

				// Scalar, array, or type-mismatch — compare via canonical JSON
				// representation. JsonNode doesn't expose deep-equals directly,
				// but serializing both sides with the same options produces
				// stable comparable strings.
				if (!JsonNodesEqual(baseValue, privateValue))
				{
					result[key] = privateValue?.DeepClone();
				}
			}

			return result;
		}

		/// <summary>
		/// Equality for JsonNodes via canonical serialization. Two nodes are
		/// equal iff their serialized representations match character-for-
		/// character under the same options. This is parsed-value equality:
		/// whitespace inside the source JSON is gone by serialization time,
		/// so [ "a", "b" ] and ["a","b"] compare equal. Order is significant
		/// for arrays — same elements at same positions.
		/// </summary>
		internal static bool JsonNodesEqual(JsonNode? a, JsonNode? b)
		{
			if (a is null && b is null)
			{
				return true;
			}

			if (a is null || b is null)
			{
				return false;
			}

			var opts = new JsonSerializerOptions { WriteIndented = false };
			return a.ToJsonString(opts) == b.ToJsonString(opts);
		}

		// ── Pass 3: comment injection ───────────────────────────────────────

		/// <summary>
		/// Walks the emitted JSON line-by-line. For each line declaring a
		/// JSON property ("propName":), looks up the matching declaration in
		/// the original private source by property name + indentation level,
		/// collects adjacent // comment lines above it (strict adjacency:
		/// stop at first blank or non-// line), inserts those comments above
		/// the property in the output.
		///
		/// Indentation-level matching disambiguates property names that
		/// appear at multiple depths (e.g. nested "Name" fields). The
		/// pathological case where two properties at the same depth in
		/// different parent objects share a name takes the first match —
		/// acceptable on first cut; rare in practice.
		/// </summary>
		internal static string InjectAdjacentComments(string emittedJson, List<string> sourceLines)
		{
			var emittedLines = emittedJson.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
			var output = new StringBuilder();

			// Track which source-line indices have already been used as comment-
			// anchor points. Without this, repeated patterns (homogeneous list
			// objects sharing a "Name" field at the same depth, or repeated
			// scalar values across different lists) would all anchor to the
			// FIRST source occurrence. The consumed-set forces lookups to
			// advance through the source as the emitted output progresses.
			var consumedSourceLines = new HashSet<int>();

			foreach (var line in emittedLines)
			{
				// Classify the emitted line and find its source counterpart
				// (if any). #317a extends classification to scalar list entries
				// and object openers — previously only property declarations
				// triggered comment lookup, which dropped comments above bare
				// array entries (e.g. an inline comment sitting directly above a
				// bare string element inside a config array).
				var sourceLineIndex = FindMatchingSourceLine(line, sourceLines, consumedSourceLines);
				if (sourceLineIndex >= 0)
				{
					consumedSourceLines.Add(sourceLineIndex);
					var comments = WalkUpAdjacentComments(sourceLines, sourceLineIndex);
					var emittedIndent = CountLeadingSpaces(line);
					foreach (var c in comments)
					{
						// Re-indent comments to the emitted indent level so
						// they sit cleanly above the emitted line.
						var trimmedComment = c.TrimStart();
						output.Append(new string(' ', emittedIndent));
						output.Append(trimmedComment);
						output.Append('\n');
					}
				}

				output.Append(line);
				output.Append('\n');
			}

			return output.ToString().TrimEnd('\n');
		}

		/// <summary>
		/// Classifies the emitted line and dispatches to the appropriate
		/// source-matcher. Returns the matching source line index, or -1 if
		/// the line isn't a comment-eligible target (e.g. closing bracket,
		/// opening bracket of root, blank line) or no match found.
		///
		/// Three classifications:
		///   * Property declaration ("name": value or "name": {) — by name + depth.
		///   * Object opener inside array ({) — by next unconsumed { at right depth.
		///   * Scalar list entry (bare value, optionally trailing comma) — by
		///     trimmed-content + depth.
		/// </summary>
		internal static int FindMatchingSourceLine(
			string emittedLine,
			List<string> sourceLines,
			HashSet<int> consumedSourceLines)
		{
			var trimmed = emittedLine.TrimStart();
			var emittedIndent = CountLeadingSpaces(emittedLine);

			// Classification 1: property declaration.
			var propName = ExtractPropertyName(emittedLine);
			if (propName != null)
			{
				return FindSourceLine(sourceLines, propName, emittedIndent, consumedSourceLines);
			}

			// Skip lines that don't carry meaning for comment anchoring:
			// closing brackets, blank lines, root opener.
			if (trimmed.Length == 0)
			{
				return -1;
			}

			if (trimmed == "{")
			{
				// Classification 2: object opener inside an array.
				return FindSourceObjectOpener(sourceLines, emittedIndent, consumedSourceLines);
			}
			if (trimmed.StartsWith("}"))
			{
				return -1;
			}

			if (trimmed.StartsWith("]"))
			{
				return -1;
			}

			if (trimmed.StartsWith("["))
			{
				return -1;
			}

			// Classification 3: bare scalar list entry. Strip a trailing
			// comma so "input"," and "input" both compare the same way.
			var canonical = trimmed.TrimEnd(',').TrimEnd();
			return FindSourceScalarListEntry(sourceLines, canonical, emittedIndent, consumedSourceLines);
		}

		/// <summary>
		/// Locates a scalar list entry in the source by matching trimmed
		/// content (with trailing comma stripped) at the right depth. Skips
		/// already-consumed indices. Returns the zero-based line index or -1.
		/// Same depth-reconciliation rules as FindSourceLine — emitted uses
		/// 2-space indent per level, source uses tabs (one per level) with
		/// space-indent fallback.
		/// </summary>
		internal static int FindSourceScalarListEntry(
			List<string> sourceLines,
			string canonical,
			int emittedIndent,
			HashSet<int> consumedSourceLines)
		{
			var emittedDepth = emittedIndent / 2;

			for (int i = 0; i < sourceLines.Count; i++)
			{
				if (consumedSourceLines.Contains(i))
				{
					continue;
				}

				var line = sourceLines[i];
				var trimmed = line.TrimStart();
				if (trimmed.Length == 0)
				{
					continue;
				}

				if (trimmed.StartsWith("//"))
				{
					continue;
				}

				var sourceCanonical = trimmed.TrimEnd(',').TrimEnd();
				if (sourceCanonical != canonical)
				{
					continue;
				}

				if (IsAtEmittedDepth(line, emittedDepth))
				{
					return i;
				}
			}
			return -1;
		}

		/// <summary>
		/// Locates the next unconsumed object-opener line ("{" possibly with
		/// trailing whitespace) at the right depth in the source. Returns
		/// the zero-based line index or -1. Used to anchor comments above
		/// object entries inside arrays — comments authored before a "{" line
		/// in source travel with the object entry.
		/// </summary>
		internal static int FindSourceObjectOpener(
			List<string> sourceLines,
			int emittedIndent,
			HashSet<int> consumedSourceLines)
		{
			var emittedDepth = emittedIndent / 2;

			for (int i = 0; i < sourceLines.Count; i++)
			{
				if (consumedSourceLines.Contains(i))
				{
					continue;
				}

				var line = sourceLines[i];
				var trimmed = line.TrimEnd().TrimStart();
				if (trimmed != "{")
				{
					continue;
				}

				if (IsAtEmittedDepth(line, emittedDepth))
				{
					return i;
				}
			}
			return -1;
		}

		/// <summary>
		/// Helper: does the source line's leading whitespace correspond to
		/// the given emitted-depth level? Source files use tabs (one per
		/// level) by convention; some operators may use spaces (two per
		/// level, matching System.Text.Json default). Both are accepted.
		/// </summary>
		internal static bool IsAtEmittedDepth(string sourceLine, int emittedDepth)
		{
			int sourceWhitespace = 0;
			foreach (var ch in sourceLine)
			{
				if (ch == '\t' || ch == ' ')
				{
					sourceWhitespace++;
				}
				else
				{
					break;
				}
			}
			if (sourceLine.Length > 0 && sourceLine[0] == '\t')
			{
				return sourceWhitespace == emittedDepth;
			}

			return sourceWhitespace / 2 == emittedDepth;
		}

		/// <summary>
		/// Returns the property name from a JSON property declaration line
		/// like '"PropertyName": value,' — or null if the line is not a
		/// property declaration (e.g. array entry, closing bracket).
		/// </summary>
		internal static string? ExtractPropertyName(string line)
		{
			var trimmed = line.TrimStart();
			if (!trimmed.StartsWith("\""))
			{
				return null;
			}

			var endQuote = trimmed.IndexOf('"', 1);
			if (endQuote < 0)
			{
				return null;
			}

			// Must be followed by colon (possibly with whitespace).
			var afterQuote = trimmed[(endQuote + 1)..].TrimStart();
			if (!afterQuote.StartsWith(":"))
			{
				return null;
			}

			return trimmed[1..endQuote];
		}

		/// <summary>
		/// Locates a property declaration in the source by name and indentation
		/// depth, skipping any line indices in <paramref name="consumedSourceLines"/>.
		/// Depth here is "leading whitespace columns" — converted from either
		/// spaces or tabs in the source. The emitted output uses spaces
		/// (System.Text.Json default), so the source's tab indents are
		/// expanded to equivalent space counts for comparison. Returns the
		/// zero-based line index, or -1 if not found.
		/// </summary>
		internal static int FindSourceLine(
			List<string> sourceLines,
			string propName,
			int emittedIndent,
			HashSet<int> consumedSourceLines)
		{
			// Match on property name + relative depth. Depth reconciliation
			// (tabs in source vs 2-space-per-level in emitted) is delegated
			// to the shared IsAtEmittedDepth helper.
			var emittedDepth = emittedIndent / 2;

			for (int i = 0; i < sourceLines.Count; i++)
			{
				if (consumedSourceLines.Contains(i))
				{
					continue;
				}

				var line = sourceLines[i];
				var trimmed = line.TrimStart();
				if (!trimmed.StartsWith("\"" + propName + "\""))
				{
					continue;
				}

				// Confirm it's a property declaration (followed by :).
				var afterName = trimmed[(propName.Length + 2)..].TrimStart();
				if (!afterName.StartsWith(":"))
				{
					continue;
				}

				if (IsAtEmittedDepth(line, emittedDepth))
				{
					return i;
				}
			}
			return -1;
		}

		/// <summary>
		/// Walks upward from <paramref name="lineIndex"/> in the source,
		/// collecting consecutive // comment lines. Stops at the first line
		/// that is blank OR does not start (after trim) with //. Returns
		/// the captured comments in source order (top-to-bottom).
		/// </summary>
		internal static List<string> WalkUpAdjacentComments(List<string> sourceLines, int lineIndex)
		{
			var result = new List<string>();
			for (int i = lineIndex - 1; i >= 0; i--)
			{
				var line = sourceLines[i];
				var trimmed = line.TrimStart();
				if (trimmed.Length == 0)
				{
					break;      // blank → stop
				}

				if (!trimmed.StartsWith("//"))
				{
					break; // non-comment → stop
				}

				result.Add(line);
			}
			result.Reverse();
			return result;
		}

		/// <summary>Counts leading space characters in a line.</summary>
		internal static int CountLeadingSpaces(string line)
		{
			int count = 0;
			foreach (var ch in line)
			{
				if (ch == ' ')
				{
					count++;
				}
				else
				{
					break;
				}
			}
			return count;
		}

		/// <summary>
		/// Mirror of Config.FilterComments — strips whole-line // comments
		/// before JSON parsing. Kept private here to avoid coupling to
		/// Config.cs's private helper.
		/// </summary>
		private static string FilterComments(string json)
		{
			return string.Join("\n",
				json.Split('\n').Where(line => !line.TrimStart().StartsWith("//")));
		}
	}
}
