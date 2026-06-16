using System.Text;
using System.Text.RegularExpressions;

namespace Crawler
{
	// ── Base64AssetExtractor ──────────────────────────────────────────────────
	//
	// Scans downloaded asset files (JS, CSS, and any other extensions configured
	// via Base64AssetFileExtensions) for embedded Base64 data URIs, decodes them,
	// and saves the decoded files to a base64assets/ folder alongside the download
	// directory.
	//
	// Output: 19-base64assets.log — one row per extracted asset, @@@ delimited:
	//   SourceFileUrl@@@SavedFile@@@MediaType@@@EncodedBytes@@@DecodedBytes
	//
	// Design notes:
	// - Runs outside the crawl pipeline — safe to re-run against existing downloads
	// - Files are overwritten on re-run (same source, same counter); never deleted
	// - SourceFileUrl is resolved via the crawler index; falls back to filename
	// - Files are named {sourcefilename}__{counter}.{ext} for traceability
	// ─────────────────────────────────────────────────────────────────────────

	public static class Base64AssetExtractor
	{
		private const string Header =
			"SourceFileUrl@@@SavedFile@@@MediaType@@@EncodedBytes@@@DecodedBytes";

		// [KEEP] Reads files as Latin-1 — lossless byte-to-char mapping for binary
		// content. Data URIs are ASCII-safe so Latin-1 correctly locates all markers.
		private static readonly Encoding Latin1 = Encoding.Latin1;

		// Minimum encoded length to bother extracting — avoids tiny inline icons
		// and noise matches. 64 chars ≈ 48 decoded bytes.
		private const int MinEncodedLength = 64;

		// ── Media type → extension map ────────────────────────────────────────

		private static readonly Dictionary<string, string> MediaTypeToExt =
			new(StringComparer.OrdinalIgnoreCase)
		{
			{ "image/png",                ".png"  },
			{ "image/jpeg",               ".jpg"  },
			{ "image/jpg",                ".jpg"  },
			{ "image/gif",                ".gif"  },
			{ "image/svg+xml",            ".svg"  },
			{ "image/webp",               ".webp" },
			{ "image/avif",               ".avif" },
			{ "image/x-icon",             ".ico"  },
			{ "font/woff",                ".woff" },
			{ "font/woff2",               ".woff2"},
			{ "font/ttf",                 ".ttf"  },
			{ "font/otf",                 ".otf"  },
			{ "application/font-woff",    ".woff" },
			{ "application/octet-stream", ".bin"  },
			{ "application/pdf",          ".pdf"  },
			{ "audio/mpeg",               ".mp3"  },
			{ "audio/ogg",                ".ogg"  },
			{ "video/mp4",                ".mp4"  },
			{ "video/webm",               ".webm" },
			{ "text/plain",               ".txt"  },
			{ "text/css",                 ".css"  },
			{ "text/html",                ".html" },
			{ "text/javascript",          ".js"   },
			{ "application/javascript",   ".js"   },
			{ "application/json",         ".json" },
			{ "application/wasm",         ".wasm" },
		};

		// ── Data URI regex ────────────────────────────────────────────────────

		// Matches: data:[mediatype][;charset=...][;base64],<data>
		// [KEEP] The data group is linear-time / catastrophic-backtracking-safe — but
		// NOT because it is possessive (it is a plain greedy '+'). It is safe because:
		//   - it is a SINGLE character class with one quantifier (no nesting), and
		//   - it is followed by '=*' over a DISJOINT set ('=' is not in the class), so
		//     the boundary is unambiguous and no give-back occurs, and
		//   - the data group is the LAST token in the pattern, so a greedy match never
		//     needs to backtrack to let a later token succeed.
		// The data group stops at the first non-Base64 character (quote, space, etc.).
		//
		// WARNING — do not append a token after the data group, and do not let the class
		// overlap '='. Either change breaks the disjoint/terminal invariant above and
		// turns the greedy '+' into a catastrophic backtrack ON REAL DATA: these data
		// URIs routinely run to multiple MB (8 MB+ of base64 inside 12 MB JS files), so a
		// single trailing token could blow up an 8 MB give-back. If a trailing token is
		// ever genuinely required, make the data group possessive ('++') / atomic in the
		// SAME edit.

#pragma warning disable SYSLIB1045 // Not in a hot path — compiled once at startup
		private static readonly Regex DataUriRegex = new(
			@"data:(?<mt>[a-zA-Z0-9!#$&\-^_]+/[a-zA-Z0-9!#$&\-^_.+]+)?(?:;charset=[^;,""'\s]+)?(?:;(?<b64>base64))?,(?<data>[A-Za-z0-9+/\r\n]+=*)",
			RegexOptions.Compiled);
#pragma warning restore SYSLIB1045

		// ── Public entry point ────────────────────────────────────────────────

		public static List<IssueTracking.IssueRecord> Extract(
			string downloadDirectory,
			string base64AssetsDirectory,
			string logPath,
			IReadOnlyList<string> fileExtensions,
			int largeAssetThresholdBytes = 102_400) // 100KB
		{
			if (!Directory.Exists(downloadDirectory))
			{
				Logger.LogInfo("Base64AssetExtractor: download directory not found, skipping.");
				return [];
			}

			if (fileExtensions.Count == 0)
			{
				Logger.LogInfo("Base64AssetExtractor: no extensions configured, skipping.");
				return [];
			}

			Directory.CreateDirectory(base64AssetsDirectory);
			var strippedDirectory = Path.Combine(base64AssetsDirectory, "sourcesstripped");
			Directory.CreateDirectory(strippedDirectory);

			var extSet = new HashSet<string>(
				fileExtensions.Select(e => e.StartsWith('.') ? e : '.' + e),
				StringComparer.OrdinalIgnoreCase);

			var files = Directory.GetFiles(downloadDirectory, "*.*", SearchOption.TopDirectoryOnly)
				.Where(f => extSet.Contains(Path.GetExtension(f)))
				.OrderBy(f => f)
				.ToList();

			if (files.Count == 0)
			{
				Logger.LogInfo($"Base64AssetExtractor: no matching files in download directory.");
				WriteLog(logPath, []);
				return [];
			}

			Logger.LogInfo($"Base64AssetExtractor: scanning {files.Count} file(s) for embedded Base64 assets.");

			var rows = new List<string>();
			var issues = new List<IssueTracking.IssueRecord>();

			foreach (var file in files)
			{
				var filename = Path.GetFileName(file);
				var sourceUrl = CrawlIndex.LookUpUrlForFile(filename);
				if (string.IsNullOrEmpty(sourceUrl) || sourceUrl == "error")
				{
					sourceUrl = filename;
				}

				ProcessFile(file, filename, sourceUrl, base64AssetsDirectory,
					strippedDirectory, rows, issues, largeAssetThresholdBytes);
			}

			WriteLog(logPath, rows);
			Logger.LogInfo($"Base64AssetExtractor: {rows.Count} asset(s) extracted, "
				+ $"{issues.Count} above {largeAssetThresholdBytes / 1024}KB threshold."
				+ $" See {Path.GetFileName(logPath)}.");
			return issues;
		}

		// ── File processor ────────────────────────────────────────────────────

		private static void ProcessFile(
			string filePath,
			string filename,
			string sourceUrl,
			string outputDir,
			string strippedDir,
			List<string> rows,
			List<IssueTracking.IssueRecord> issues,
			int thresholdBytes)
		{
			string content;
			try
			{
				content = File.ReadAllText(filePath, Latin1);
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Base64AssetExtractor: could not read {filename}: {ex.Message}");
				return;
			}

			var matches = DataUriRegex.Matches(content);
			if (matches.Count == 0)
			{
				return;
			}

			var safeBase = MakeSafeFileName(Path.GetFileNameWithoutExtension(filename));
			int counter = 0;
			var stripped = new System.Text.StringBuilder(content.Length);
			int lastIndex = 0;

			foreach (Match m in matches)
			{
				var isBase64 = m.Groups["b64"].Success;
				if (!isBase64)
				{
					continue;               // skip non-base64 data URIs
				}

				var rawData = m.Groups["data"].Value;
				if (rawData.Length < MinEncodedLength)
				{
					continue;
				}

				// Strip whitespace (base64 may be line-wrapped)
				var normalized = rawData.Replace("\r", "").Replace("\n", "");
				// Pad if needed
				var rem = normalized.Length % 4;
				if (rem == 2)
				{
					normalized += "==";
				}
				else if (rem == 3)
				{
					normalized += "=";
				}

				byte[] decoded;
				try { decoded = Convert.FromBase64String(normalized); }
				catch { continue; }

				var mediaType = m.Groups["mt"].Success ? m.Groups["mt"].Value : null;
				var ext = ResolveExtension(mediaType);

				counter++;
				// Detect WebAssembly by magic bytes (\0asm) regardless of declared
				// media type — bundlers often inline WASM as application/octet-stream.
				if (decoded.Length >= 4 &&
					decoded[0] == 0x00 && decoded[1] == 0x61 &&
					decoded[2] == 0x73 && decoded[3] == 0x6D)
				{
					ext = ".wasm";
				}

				var savedName = $"{safeBase}__{counter}{ext}";
				var savedPath = Path.Combine(outputDir, savedName);

				try
				{
					File.WriteAllBytes(savedPath, decoded);
				}
				catch (Exception ex)
				{
					Logger.LogWarning($"Base64AssetExtractor: could not write {savedName}: {ex.Message}");
					continue;
				}

				rows.Add(
					$"{sourceUrl}@@@{savedName}@@@{mediaType ?? "unknown"}@@@{rawData.Length}@@@{decoded.Length}");

				// Promote large assets to IssueTracking — anything above the threshold
				// is unambiguously too large to inline and belongs as a standalone file.
				if (decoded.Length >= thresholdBytes)
				{
					issues.Add(new IssueTracking.IssueRecord
					{
						Type = "QUALITY",
						Url = sourceUrl,
						Word = "BASE64_LARGE_ASSET",
						SourceLabel = mediaType ?? "unknown",
						Excerpt = $"{savedName} ({decoded.Length / 1024}KB)",
					});
				}

				// Append text before this match, then a readable placeholder.
				stripped.Append(content, lastIndex, m.Index - lastIndex);
				stripped.Append($"/* BASE64_STRIPPED:{savedName} */");
				lastIndex = m.Index + m.Length;
			}

			// Append tail after last match and write stripped file.
			stripped.Append(content, lastIndex, content.Length - lastIndex);
			var strippedPath = Path.Combine(strippedDir, filename);
			try
			{
				File.WriteAllText(strippedPath, stripped.ToString(), Latin1);
			}
			catch (Exception ex)
			{
				Logger.LogWarning(
					$"Base64AssetExtractor: could not write stripped {filename}: {ex.Message}");
			}
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		private static string ResolveExtension(string? mediaType)
		{
			if (string.IsNullOrEmpty(mediaType))
			{
				return ".bin";
			}

			if (MediaTypeToExt.TryGetValue(mediaType, out var ext))
			{
				return ext;
			}

			if (mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
			{
				return ".xml";
			}

			if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
			{
				return ".txt";
			}

			if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
			{
				return ".bin";
			}

			if (mediaType.StartsWith("font/", StringComparison.OrdinalIgnoreCase))
			{
				return ".bin";
			}

			return ".bin";
		}

		private static string MakeSafeFileName(string name)
		{
			foreach (var c in Path.GetInvalidFileNameChars())
			{
				name = name.Replace(c, '_');
			}

			return name;
		}

		private static void WriteLog(string logPath, List<string> rows)
		{
			var lines = new List<string> { Header };
			lines.AddRange(rows);
			FileIo.WriteAllLinesWithRetry(logPath, lines, Path.GetFileName(logPath));
		}
	}
}
