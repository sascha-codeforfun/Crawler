using System.Text;
using Crawler.AssetMetadata;

namespace Crawler
{
	// ── AssetQuality ─────────────────────────────────────────────────────────
	//
	// Asset-level quality analyzer for downloaded raster images
	// (JPEG / PNG / GIF / WebP). Three checks, all config-gated:
	//
	//   ASSET_METADATA   — embedded image metadata (EXIF / IPTC / XMP), reported
	//                      as present facts (camera make/model, author/artist,
	//                      copyright, software, timestamps, GPS) so the operator
	//                      sees what is there. Read by the native, clean-room
	//                      Crawler.AssetMetadata component (spec-derived; no third-
	//                      party library) — replacing the previous MetadataExtractor
	//                      path. Output is DESCRIPTIVE, never a verdict: it states
	//                      what is present and its value; whether that matters is a
	//                      human decision downstream.
	//   ASSET_DIMENSIONS — degenerate (0/1-px) or implausibly large pixel
	//                      dimensions. Read from image header bytes, no decode.
	//   ASSET_SIZE       — declared Content-Length (header sidecar) vs actual
	//                      bytes-on-disk mismatch (truncated/corrupt), and
	//                      implausibly large byte size.
	//
	// Findings are returned as IssueTracking.IssueRecord (Type = "ASSET",
	// Word = "<CHECK>:<detail>") for the end-of-run merge — same direct-record
	// pattern as PdfQualityAnalyzer. Extracted metadata values are UNTRUSTED web
	// content and are sanitised before they reach any field; per-value and per-
	// column volume caps in the reader bound the human-facing detail log against
	// poisoning by extreme/malformed input.
	//
	// Image identity: an asset reached as ".unverified" is classified via
	// FileTypeClassifier.ClassifyImage under the configured UnverifiedImagePolicy (the
	// analysis-time half; settle-time renaming on a fresh crawl is wired
	// separately). Files that do not settle as images are skipped.
	// ─────────────────────────────────────────────────────────────────────────

	public static class AssetQuality
	{
		private static readonly string?[] HeaderFields =
			["Filename", "Url", "IssueType", "Detail", "Context", "Exif", "Iptc", "Xmp"];

		// Leading bytes read once per file: enough for the magic sniff AND the
		// fixed-position dimension headers (PNG IHDR ends at byte 24, GIF at 10).
		// JPEG/WebP dimensions need streaming and are handled separately.
		private const int HeadByteCount = 24;

		// ── Public entry point ────────────────────────────────────────────────

		/// <summary>
		/// Scans the download directory for raster images and returns one
		/// IssueRecord per (asset, finding). Writes a dual-locale CSV pair via
		/// <see cref="IssueLogWriter.WriteCsvPair"/> to <paramref name="csvBasePath"/>
		/// (".._semicolon.csv" / ".._comma.csv"; same row shape as log 10). Pure of
		/// pipeline state; callable in isolation for testing.
		/// </summary>
		public static List<IssueTracking.IssueRecord> Analyse(
			string downloadDirectory,
			string csvBasePath,
			AssetQualityConfig config)
		{
			var records = new List<IssueTracking.IssueRecord>();
			var logRows = new List<string?[]> { HeaderFields };

			if (!config.IsEnabled)
			{
				Logger.LogInfo("AssetQuality: all checks disabled — skipping.");
				return records;
			}
			if (!System.IO.Directory.Exists(downloadDirectory))
			{
				Logger.LogInfo("AssetQuality: download directory not found, skipping.");
				return records;
			}

			var policy = config.ResolvedUnverifiedImagePolicy;

			// Enumerate every file, excluding the header sidecars (blanket-glob
			// rule, gotchas) — analyzers that target specific extensions ignore
			// them, but we glob "*.*" so we must exclude explicitly.
			var files = System.IO.Directory
				.GetFiles(downloadDirectory, "*.*", SearchOption.TopDirectoryOnly)
				.Where(f => !f.EndsWith(HeaderSidecar.HeaderSidecarExtension, StringComparison.OrdinalIgnoreCase))
				.OrderBy(f => f, StringComparer.Ordinal)   // deterministic output
				.ToList();

			var perFileLog = new List<string?[]>[files.Count];
			var perFileRecords = new List<IssueTracking.IssueRecord>[files.Count];

			// Per-file analysis is independent and I/O-bound (header read, optional
			// metadata parse), so fan out across cores. Results are slotted by the
			// file's index in the already-ordered list and flattened in order, so the
			// emitted log and returned records stay byte-for-byte deterministic
			// regardless of thread scheduling.
			Parallel.For(0, files.Count,
				new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
				i =>
				{
					var localLog = new List<string?[]>();
					var localRecords = new List<IssueTracking.IssueRecord>();
					perFileLog[i] = localLog;
					perFileRecords[i] = localRecords;

					var file = files[i];
					var filename = Path.GetFileName(file);

					byte[] head;
					long actualBytes;
					try
					{
						var info = new FileInfo(file);
						actualBytes = info.Length;
						head = ReadHead(file, HeadByteCount);
					}
					catch (Exception ex)
					{
						Logger.LogWarning($"AssetQuality: could not read {filename}: {ex.Message}");
						return;
					}

					// Header sidecar read once and reused for both the content-type
					// identity check and the Content-Length size check.
					var sidecar = ReadSidecarResponseHeaders(file);
					var declaredLength = ParseContentLength(GetSidecarHeader(sidecar, "Content-Length"));

					// ── Image identity (settle-by-reading) ───────────────────────
					var requestedExtIsImage = FileTypeClassifier.IsImageExtension(filename);
					var headerIsImage = FileTypeClassifier.IsImageContentType(
						NormaliseContentType(GetSidecarHeader(sidecar, "Content-Type")));
					var sniffIsImage = FileTypeClassifier.LooksLikeImage(head);

					var classification = FileTypeClassifier.ClassifyImage(
						policy, requestedExtIsImage, headerIsImage, sniffIsImage);

					if (!classification.TreatAsImage)
					{
						return;
					}

					var url = CrawlIndex.LookUpUrlForFile(filename);
					var urlForExclusion = string.IsNullOrEmpty(url) || url == "error" ? filename : url;
					var excluded = config.SizeAndDimensionExclusions
						.Any(p => !string.IsNullOrEmpty(p)
							&& urlForExclusion.Contains(p, StringComparison.OrdinalIgnoreCase));

					var findings = new List<(string Word, string Detail, string Context, string Exif, string Iptc, string Xmp)>();

					// ── ASSET_SIZE ───────────────────────────────────────────────
					if (config.CheckSize && !excluded)
					{
						findings.AddRange(CheckSize(file, actualBytes, config, declaredLength));
					}

					// ── ASSET_DIMENSIONS ─────────────────────────────────────────
					if (config.CheckDimensions && !excluded)
					{
						findings.AddRange(CheckDimensions(file, head, config));
					}

					// ── ASSET_METADATA (always — even on excluded press images) ──
					if (config.CheckMetadataLeakage)
					{
						findings.AddRange(CheckMetadata(file));
					}

					var recordUrl = string.IsNullOrEmpty(url) || url == "error" ? filename : url;

					// Column 2 of the asset log is the live URL. The empty branch is an
					// invariant violation, not a runtime case: every on-disk asset was
					// downloaded and indexed together, and the crawl's integrity check
					// halts upstream if disk and index ever disagree. So url is always
					// resolvable here; string.Empty is emitted only to keep the column
					// well-formed in the structurally-impossible miss — deliberately no
					// recovery logic and no filename fallback, so the guarantee stays
					// visible rather than being papered over.
					var urlForLog = string.IsNullOrEmpty(url) || url == "error" ? string.Empty : url;
					var crawlSource = CrawlIndex.LookUpSourceForFile(filename);
					foreach (var (word, detail, context, exif, iptc, xmp) in findings)
					{
						localLog.Add([filename, urlForLog, $"ASSET_{word}", detail, context, exif, iptc, xmp]);
						localRecords.Add(new IssueTracking.IssueRecord
						{
							Type = "ASSET",
							Url = recordUrl,
							Word = $"ASSET_{word}",
							SourceLabel = detail,
							Excerpt = context,
							CrawlSource = crawlSource,
						});
					}
				});

			foreach (var slot in perFileLog)
			{
				logRows.AddRange(slot);
			}
			foreach (var slot in perFileRecords)
			{
				records.AddRange(slot);
			}

			IssueLogWriter.WriteCsvPair(csvBasePath, logRows);
			Logger.LogInfo($"AssetQuality: {records.Count} finding(s) across {files.Count} file(s). " +
				$"See {Path.GetFileName(csvBasePath)}{IssueLogWriter.CsvSemicolonSuffix} / " +
				$"{Path.GetFileName(csvBasePath)}{IssueLogWriter.CsvCommaSuffix}.");
			return records;
		}

		// ── ASSET_SIZE ──────────────────────────────────────────────────────

		internal static IEnumerable<(string Word, string Detail, string Context, string Exif, string Iptc, string Xmp)> CheckSize(
			string file, long actualBytes, AssetQualityConfig config, long? declaredContentLength = null)
		{
			// (a) declared Content-Length vs actual — truncated / corrupt download.
			// Use the caller's pre-read value when supplied to avoid re-reading the
			// header sidecar; fall back to reading it for standalone callers.
			var declared = declaredContentLength ?? ReadSidecarContentLength(file);
			if (declared is long d && d != actualBytes)
			{
				yield return ("SIZE",
					"LENGTH_MISMATCH",
					$"declared Content-Length {d} != actual {actualBytes} bytes " +
					"(truncated or corrupt download)",
					string.Empty, string.Empty, string.Empty);
			}

			// (b) implausibly large.
			if (actualBytes > config.MaxImageBytes)
			{
				yield return ("SIZE",
					"OVERSIZE",
					$"{actualBytes} bytes exceeds threshold {config.MaxImageBytes}",
					string.Empty, string.Empty, string.Empty);
			}
		}

		// ── ASSET_DIMENSIONS ────────────────────────────────────────────────

		internal static IEnumerable<(string Word, string Detail, string Context, string Exif, string Iptc, string Xmp)> CheckDimensions(
			string file, byte[] head, AssetQualityConfig config)
		{
			var dims = ReadImageDimensions(file, head);
			if (dims is not (int w, int h))
			{
				yield break; // unreadable / unsupported — no finding
			}

			if (w <= 1 || h <= 1)
			{
				yield return ("DIMENSIONS",
					"DEGENERATE",
					$"{w}x{h} px (tracking-pixel or degenerate image)",
					string.Empty, string.Empty, string.Empty);
			}
			else if (w > config.MaxImageDimensionPixels || h > config.MaxImageDimensionPixels)
			{
				yield return ("DIMENSIONS",
					"OVERSIZE",
					$"{w}x{h} px exceeds {config.MaxImageDimensionPixels} px on a side",
					string.Empty, string.Empty, string.Empty);
			}
		}

		// ── ASSET_METADATA ──────────────────────────────────────────────────

		internal static IEnumerable<(string Word, string Detail, string Context, string Exif, string Iptc, string Xmp)> CheckMetadata(string file)
		{
			// Native, clean-room reader (Crawler.AssetMetadata): parses EXIF / IPTC /
			// XMP across JPEG, PNG and WebP straight from the file bytes. It never
			// throws — unreadable or unsupported input yields an empty result — so no
			// try/catch is needed around the read; other checks have already run.
			var meta = AssetMetadataReader.Read(file);

			// Render to the log columns. Extracted values are UNTRUSTED web content,
			// so each is passed through the host sanitiser; the renderer also applies
			// per-value and per-column volume caps against poisoning. A finding is
			// produced only when at least one curated category is present; the three
			// block columns then carry the full (capped) inventory for the operator.
			var finding = AssetMetadataReportRenderer.Build(
				meta, s => IssueLogWriter.SanitizeField(s).Cleaned);

			if (!finding.HasFinding)
			{
				yield break;
			}

			yield return ("METADATA",
				finding.Detail, finding.Context,
				finding.Exif, finding.Iptc, finding.Xmp);
		}

		// ── Image dimension reading (header bytes, no decode) ───────────────

		/// <summary>
		/// Reads pixel dimensions from image header bytes for PNG / GIF / JPEG /
		/// WebP. Returns null when the format is unsupported or the header is
		/// malformed/short. PNG and GIF are decided from the supplied head buffer;
		/// JPEG requires scanning marker segments, so the file is streamed.
		/// </summary>
		internal static (int Width, int Height)? ReadImageDimensions(string file, byte[] head)
		{
			// PNG / GIF are decided purely from the header buffer.
			var fromHead = ReadDimensionsFromHead(head);
			if (fromHead is not null)
			{
				return fromHead;
			}

			// JPEG: scan marker segments for an SOF (height then width, big-endian).
			if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
			{
				return ReadJpegDimensions(file);
			}

			// WebP variants and anything else: not parsed here (null → no dimension
			// finding). Size and metadata checks still apply. A later fileset can
			// add WebP dimension parsing.
			return null;
		}

		/// <summary>
		/// Pure dimension read from a header buffer for the formats whose size
		/// lives in the fixed-position header (PNG, GIF). Returns null for JPEG /
		/// WebP / unsupported (those need streaming or aren't handled). Unit-testable
		/// without file I/O.
		/// </summary>
		internal static (int Width, int Height)? ReadDimensionsFromHead(byte[] head)
		{
			if (head is null)
			{
				return null;
			}

			// PNG: IHDR width at 16..19, height at 20..23, big-endian.
			if (head.Length >= 24
				&& head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
			{
				int w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
				int h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
				return (w, h);
			}

			// GIF: logical-screen width at 6..7, height at 8..9, little-endian.
			if (head.Length >= 10
				&& head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F')
			{
				int w = head[6] | (head[7] << 8);
				int h = head[8] | (head[9] << 8);
				return (w, h);
			}

			return null;
		}

		private static (int Width, int Height)? ReadJpegDimensions(string file)
		{
			try
			{
				using var fs = File.OpenRead(file);
				// Skip the SOI (FF D8).
				if (fs.ReadByte() != 0xFF || fs.ReadByte() != 0xD8)
				{
					return null;
				}

				while (true)
				{
					int marker = fs.ReadByte();
					if (marker < 0)
					{
						return null;
					}
					if (marker != 0xFF)
					{
						continue;        // resync to a marker byte
					}
					int code = fs.ReadByte();
					while (code == 0xFF)
					{
						code = fs.ReadByte(); // skip fill bytes
					}
					if (code < 0)
					{
						return null;
					}

					// Standalone markers (no length): RSTn, SOI, EOI, TEM.
					if (code == 0xD8 || code == 0xD9 || code == 0x01 ||
						(code >= 0xD0 && code <= 0xD7))
					{
						continue;
					}

					int len = (fs.ReadByte() << 8) | fs.ReadByte();
					if (len < 2)
					{
						return null;
					}

					// SOF markers carrying dimensions: C0-CF except C4(DHT),
					// C8(JPG), CC(DAC).
					bool isSof = code >= 0xC0 && code <= 0xCF
						&& code != 0xC4 && code != 0xC8 && code != 0xCC;
					if (isSof)
					{
						fs.ReadByte();                    // precision
						int h = (fs.ReadByte() << 8) | fs.ReadByte();
						int w = (fs.ReadByte() << 8) | fs.ReadByte();
						return (w, h);
					}

					// Not an SOF — skip this segment's payload.
					fs.Seek(len - 2, SeekOrigin.Current);
				}
			}
			catch { return null; }
		}

		// ── Sidecar reading ─────────────────────────────────────────────────

		/// <summary>Reads the Content-Length declared in the file's header
		/// sidecar, or null if absent/unparseable (e.g. chunked responses).</summary>
		internal static long? ReadSidecarContentLength(string file)
			=> ParseContentLength(GetSidecarHeader(ReadSidecarResponseHeaders(file), "Content-Length"));

		/// <summary>Reads the Content-Type declared in the file's header sidecar
		/// (media type only, parameters stripped), or null if absent.</summary>
		internal static string? ReadSidecarContentType(string file)
			=> NormaliseContentType(GetSidecarHeader(ReadSidecarResponseHeaders(file), "Content-Type"));

		private static long? ParseContentLength(string? raw)
			=> long.TryParse(raw?.Trim(), out var n) ? n : null;

		private static string? NormaliseContentType(string? raw)
		{
			if (string.IsNullOrEmpty(raw))
			{
				return null;
			}
			var semi = raw.IndexOf(';');
			return (semi >= 0 ? raw[..semi] : raw).Trim();
		}

		private static string? GetSidecarHeader(Dictionary<string, string>? headers, string name)
			=> headers is not null && headers.TryGetValue(name, out var v) ? v : null;

		// Reads all response-side headers from the "<stem>.header" sidecar in a
		// single pass. The sidecar format (HeaderSidecar.FormatHeaderSidecar) is
		// "Name: Value" lines under a "=== RESPONSE ===" marker; first value wins
		// (matching the prior single-header reader). Returns null when the sidecar
		// is absent or unreadable.
		private static Dictionary<string, string>? ReadSidecarResponseHeaders(string file)
		{
			var sidecar = Path.ChangeExtension(file, HeaderSidecar.HeaderSidecarExtension.TrimStart('.'));
			if (!File.Exists(sidecar))
			{
				return null;
			}

			try
			{
				var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				bool inResponse = false;
				foreach (var line in File.ReadLines(sidecar, Encoding.UTF8))
				{
					if (line.StartsWith("=== RESPONSE ===", StringComparison.Ordinal))
					{
						inResponse = true;
						continue;
					}
					if (!inResponse)
					{
						continue;
					}

					var colon = line.IndexOf(':');
					if (colon <= 0)
					{
						continue;
					}
					var key = line[..colon].Trim();
					if (!headers.ContainsKey(key))
					{
						headers[key] = line[(colon + 1)..];
					}
				}
				return headers;
			}
			catch { return null; }
		}

		// ── Helpers ─────────────────────────────────────────────────────────

		private static byte[] ReadHead(string file, int count)
		{
			using var fs = File.OpenRead(file);
			var buf = new byte[count];
			int read = fs.Read(buf, 0, count);
			if (read == count)
			{
				return buf;
			}
			var trimmed = new byte[read];
			Array.Copy(buf, trimmed, read);
			return trimmed;
		}
	}
}
