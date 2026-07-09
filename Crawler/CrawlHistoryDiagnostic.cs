using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Crawler
{
	/// <summary>
	/// Forensic auditing tool for crawl history. Walks every timestamp folder
	/// under each configured site's resolved BaseDirectory, computes per-crawl
	/// size and file-count statistics, scans HTML bodies for operator-curated
	/// substring markers, joins marker-positive findings with their header
	/// sidecars via configured regex extractors, and writes one per-site log
	/// to that site's working folder. Useful for verifying that downloaded
	/// content matches expectations across runs and for handing forensic
	/// records to operators of the systems being crawled.
	///
	/// Lifecycle:
	///   Runtime-gated by <c>Config.CrawlHistoryDiagnostic.Enabled</c>. When
	///   disabled (the default), no prompt fires and no diagnostic work runs.
	///   When enabled and the operator is in interactive mode, a Y/N prompt
	///   appears at startup before site selection so the operator can peek
	///   any site's report before choosing which site to crawl this run.
	///   Silent mode always skips the prompt regardless of Enabled.
	///
	///   All curation lives in config: <c>HtmlMarkers</c> defines what body
	///   substrings to flag; <c>HeaderExtractors</c> defines what regex fields
	///   to pull from header sidecars for forensic correlation. The class
	///   itself ships no investigation-specific knowledge.
	///
	/// Entry point: <see cref="PromptAndRunAsync(Config)"/> — called from
	/// Program.cs before site selection.
	/// </summary>
	//
	// [KEEP] STANDALONE BY DESIGN — DO NOT INTEGRATE.
	// This diagnostic is intentionally self-contained and writes directly to
	// System.Console. Do NOT route its prompt/progress/error output through
	// ConsoleUi or Logger, and do NOT weave it into the analysis pipeline,
	// the run log (application.log), the styling grammar, or any future
	// translation/resource layer. It stands alone on purpose: a throwaway
	// forensic probe that must run and report without touching — or being
	// touched by — the rest of the app's plumbing. The raw Console calls
	// below are deliberate, not an oversight; leave them as-is.
	internal static class CrawlHistoryDiagnostic
	{
		/// <summary>Matches the crawler's timestamp-folder naming: YYYY-MM-DD-HH-MM-SS.</summary>
		private static readonly Regex TimestampFolderRegex =
			new(@"^\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}$", RegexOptions.Compiled);

		/// <summary>
		/// Extracts the Date header value (RFC 7231 IMF-fixdate form). Built-in
		/// rather than operator-curated because every header has one and the
		/// timestamp is the canonical correlation field — a diagnostic without
		/// it loses most of its forensic value.
		/// </summary>
		private static readonly Regex DateHeaderRegex =
			new(@"^Date:\s*(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);

		/// <summary>
		/// Walks every configured site's crawl history and writes one diagnostic
		/// log per site to that site's working folder. Gated by two conditions:
		/// <c>Config.CrawlHistoryDiagnostic.Enabled</c> must be true (the master
		/// runtime switch), AND the caller must be in interactive mode (silent
		/// crawls skip the diagnostic to avoid hanging on an absent operator).
		/// When both are satisfied, prompts Y/N (default N); the work runs only
		/// on Y.
		///
		/// Failure mode: per-site errors (missing BaseDirectory on disk, unreadable
		/// file, corrupted header) are absorbed into the log file itself rather
		/// than thrown. The diagnostic should never block crawl startup; if it
		/// cannot do its job cleanly, it writes what it can and returns.
		/// </summary>
		internal static async Task PromptAndRunAsync(Config config)
		{
			ArgumentNullException.ThrowIfNull(config);

			// Master switch gate: when the diagnostic config exists but has
			// Enabled = false (the shipped default), do nothing — no prompt,
			// no walk, no log output. Operator flips Enabled to true in
			// config.private.json when they want to investigate; flips back
			// to false when done. The audit trail of HtmlMarkers and
			// HeaderExtractors stays preserved across the on/off cycles.
			if (config.CrawlHistoryDiagnostic is null || !config.CrawlHistoryDiagnostic.Enabled)
			{
				return;
			}

			Console.WriteLine();
			Console.Write("Run crawl-history diagnostic across all sites? (y/N): ");
			var answer = Console.ReadLine()?.Trim() ?? string.Empty;
			if (!answer.Equals("y", StringComparison.OrdinalIgnoreCase)
				&& !answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}

			Console.WriteLine("Running crawl-history diagnostic...");

			// Snapshot the enabled markers once at startup so all sites use the same
			// set. Empty config (or all-disabled) → the diagnostic still runs and
			// reports the size/file-count table; marker hits will be zero throughout.
			// This matches the design decision to keep the size table useful even
			// without operator-curated markers in place.
			var markers = (config.CrawlHistoryDiagnostic.HtmlMarkers ?? [])
				.Where(m => m.Enabled && !string.IsNullOrEmpty(m.Value))
				.ToList();

			// Compile enabled header-field extractors once at startup. The regex
			// patterns have already been validated by
			// CrawlHistoryDiagnosticConfigValidator.CheckOrHalt before we got here,
			// so construction can't throw — but we still wrap defensively in case
			// the validator and the runtime construction ever drift apart.
			var extractors = new List<CompiledExtractor>();
			foreach (var e in config.CrawlHistoryDiagnostic.HeaderExtractors ?? [])
			{
				if (!e.Enabled || string.IsNullOrWhiteSpace(e.Pattern) || string.IsNullOrWhiteSpace(e.Label))
				{
					continue;
				}
				try
				{
					extractors.Add(new CompiledExtractor
					{
						Label = e.Label,
						Comment = e.Comment,
						Regex = new Regex(e.Pattern, RegexOptions.Compiled),
					});
				}
				catch (ArgumentException ex)
				{
					Console.WriteLine($"  Skipping extractor '{e.Label}': {ex.Message}");
				}
			}

			foreach (var site in config.Sites)
			{
				try
				{
					await RunForSiteAsync(config, site, markers, extractors);
				}
				catch (Exception ex)
				{
					// Defensive: a per-site failure should not abort the whole diagnostic.
					// Surface the failure to the operator (so they know one site's log was
					// skipped) but continue with the next site.
					Console.WriteLine($"  [{site.Name}] diagnostic failed: {ex.Message}");
				}
			}

			Console.WriteLine("Crawl-history diagnostic complete.");
			Console.WriteLine();
		}

		/// <summary>
		/// Runs the diagnostic for one site: resolves the site's BaseDirectory by
		/// substituting {tenant} and {productiongroup} tokens (mirroring Config's logic),
		/// computes the urlDirectory the crawler uses, enumerates timestamp folders, and
		/// writes the per-site diagnostic log.
		///
		/// If the resolved per-site directory doesn't exist on disk (site configured but
		/// never crawled, or path mismatch from a moved/renamed working folder), the site
		/// is skipped silently — no log file, no error. Matches the operator's "skip
		/// silently if no data" decision.
		/// </summary>
		private static async Task RunForSiteAsync(Config config, SiteConfig site, IReadOnlyList<CrawlHistoryDiagnosticMarker> markers, IReadOnlyList<CompiledExtractor> extractors)
		{
			// Mirror Config.SubstituteTokens minus the private visibility. Same rules:
			// empty/whitespace Tenant resolves to "default"; same for ProductionGroup.
			var tenant = string.IsNullOrWhiteSpace(site.Tenant) ? Config.DefaultTenant : site.Tenant;
			var productionGroup = string.IsNullOrWhiteSpace(site.ProductionGroup) ? Config.DefaultTenant : site.ProductionGroup;

			var baseDir = config.BaseDirectory
				.Replace("{tenant}", tenant, StringComparison.Ordinal)
				.Replace("{productiongroup}", productionGroup, StringComparison.Ordinal);

			if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir))
			{
				// Site configured but BaseDirectory absent — skip silently.
				return;
			}

			// Mirror Program.cs's urlDirectory derivation: site.Url with ":" "/" "." → "_".
			var urlDirectory = site.Url.Replace(":", "_").Replace("/", "_").Replace(".", "_");
			var siteRoot = Path.Combine(baseDir, urlDirectory);

			if (!Directory.Exists(siteRoot))
			{
				// Site configured but never crawled (no per-site root on disk) — skip.
				return;
			}

			// Enumerate timestamp folders. The crawler stamps folders as YYYY-MM-DD-HH-MM-SS;
			// anything else under siteRoot (loose files, ad-hoc folders) is ignored to keep
			// the report focused on actual crawl outputs.
			var timestampFolders = Directory.EnumerateDirectories(siteRoot)
				.Select(d => new { Path = d, Name = Path.GetFileName(d) })
				.Where(d => TimestampFolderRegex.IsMatch(d.Name))
				.OrderBy(d => d.Name, StringComparer.Ordinal)
				.ToList();

			if (timestampFolders.Count == 0)
			{
				// No timestamped crawl directories — nothing to diagnose. Skip silently.
				return;
			}

			// Analyse each timestamp folder. Per-folder we collect:
			//   - HTML file count and total bytes
			//   - count of files containing any marker substring (if markers empty: 0)
			//   - per-marker-positive file: the full URL (best-effort from header), the
			//     response Date, configured extractor values, and the file size
			var perFolder = new List<FolderReport>();
			foreach (var tf in timestampFolders)
			{
				var report = await AnalyseTimestampFolderAsync(tf.Path, tf.Name, markers, extractors);
				perFolder.Add(report);
			}

			// Write the per-site diagnostic log. Filename includes the run-time stamp so
			// successive diagnostic runs build a history instead of overwriting.
			var logFilename = $"crawl-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.log";
			var logPath = Path.Combine(siteRoot, logFilename);
			await WriteDiagnosticLogAsync(logPath, site, siteRoot, perFolder, markers, extractors);
		}

		/// <summary>
		/// One pass over a timestamp folder's <c>download/</c> subdirectory. Computes
		/// aggregate stats and per-marker-positive-file detail. Per-file read errors are
		/// captured into <see cref="FolderReport.ReadWarnings"/> so the operator sees them
		/// in the log without the diagnostic crashing.
		/// </summary>
		private static async Task<FolderReport> AnalyseTimestampFolderAsync(string timestampFolderPath, string timestampName, IReadOnlyList<CrawlHistoryDiagnosticMarker> markers, IReadOnlyList<CompiledExtractor> extractors)
		{
			var report = new FolderReport
			{
				TimestampName = timestampName,
			};

			var downloadDir = Path.Combine(timestampFolderPath, "download");
			if (!Directory.Exists(downloadDir))
			{
				report.Notes.Add($"No download/ subdirectory under {timestampFolderPath}.");
				return report;
			}
			report.DownloadDir = downloadDir;

			// HTML files only — header sidecars and other artifacts excluded. The crawler
			// writes downloaded bodies with .html extension regardless of original content
			// type for browser-shape pages (text/html responses), so .html is the right
			// filter for "what the operator's content checks see."
			IEnumerable<string> htmlFiles;
			try
			{
				htmlFiles = Directory.EnumerateFiles(downloadDir, "*.html", SearchOption.TopDirectoryOnly);
			}
			catch (Exception ex)
			{
				report.Notes.Add($"Could not enumerate {downloadDir}: {ex.Message}");
				return report;
			}

			// Fast-path: with no markers configured, skip body reading entirely. The size
			// and file-count table is still useful (operators can spot crawl-size anomalies
			// without any markers in place), and skipping the per-file read makes the
			// no-markers pass essentially free.
			var hasMarkers = markers.Count > 0;

			// Parallelise the per-file scan. The work is I/O-bound (one read per file,
			// ~1800 files/folder) plus an Ordinal substring scan; running it serially
			// overlaps no disk I/O and uses one core. Bounded DOP = ProcessorCount.
			// Accumulation is made thread-safe: counters via Interlocked into locals
			// (FolderReport's are properties, which can't be Interlocked-ref'd, so we
			// fold back after the loop), collections under a single lock (the locked
			// paths — marker hits and read warnings — are the rare cases, not the
			// per-file hot path, so contention is negligible).
			// Stays fully self-contained: no ConsoleUi/Logger/pipeline involvement
			// (see the [KEEP] STANDALONE contract on the class).
			var htmlFileCount = 0;
			long totalHtmlBytes = 0;
			var markerPositiveCount = 0;
			var gate = new object();

			await Parallel.ForEachAsync(
				htmlFiles,
				new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
				async (htmlPath, ct) =>
				{
					Interlocked.Increment(ref htmlFileCount);

					FileInfo fi;
					try
					{
						fi = new FileInfo(htmlPath);
						Interlocked.Add(ref totalHtmlBytes, fi.Length);
					}
					catch (Exception ex)
					{
						lock (gate) { report.ReadWarnings.Add($"FileInfo failed for {Path.GetFileName(htmlPath)}: {ex.Message}"); }
						return;
					}

					if (!hasMarkers)
					{
						return;
					}

					// Read the body for marker scanning. Full-read (not streaming) kept
					// for simplicity — see the original note: ~100KB median, debug tool.
					string body;
					try
					{
						body = await File.ReadAllTextAsync(htmlPath, Encoding.UTF8, ct);
					}
					catch (Exception ex)
					{
						lock (gate) { report.ReadWarnings.Add($"Read failed for {Path.GetFileName(htmlPath)}: {ex.Message}"); }
						return;
					}

					var matchedMarker = markers.FirstOrDefault(m =>
						body.Contains(m.Value, StringComparison.Ordinal));
					if (matchedMarker is null)
					{
						return;
					}

					Interlocked.Increment(ref markerPositiveCount);

					// Marker-positive file: pull the .header sidecar for forensic detail.
					// Missing/corrupt sidecar: warn-and-skip per the design decision; the
					// finding still counts toward MarkerPositiveCount because the body match
					// is the truth — header is supplementary forensic info.
					//
					// Sidecar filename construction: the crawler uses Path.ChangeExtension
					// (see Crawler.WriteHeaderSidecar) which REPLACES the body's extension
					// rather than appending. So foo.html → foo.header, NOT foo.html.header.
					// We mirror that exactly here.
					var headerPath = Path.ChangeExtension(htmlPath,
						HeaderSidecar.HeaderSidecarExtension.TrimStart('.'));
					var hit = new MarkerHit
					{
						FilenameOnDisk = Path.GetFileName(htmlPath),
						Marker = matchedMarker.Value,
						MarkerName = matchedMarker.Name,
						SizeBytes = fi.Length,
					};

					if (File.Exists(headerPath))
					{
						try
						{
							var headerText = await File.ReadAllTextAsync(headerPath, Encoding.UTF8, ct);
							hit.RequestUrl = ExtractRequestUrl(headerText);
							hit.ResponseDate = DateHeaderRegex.Match(headerText) is { Success: true } md ? md.Groups[1].Value.Trim() : "";
							// Apply each configured extractor. Failures (no match, fewer than
							// two groups) become empty values rather than warnings — the
							// extractors are operator-curated, and a field-absent-from-this-
							// response is a normal forensic outcome, not a diagnostic error.
							foreach (var ex in extractors)
							{
								var match = ex.Regex.Match(headerText);
								hit.Extracted[ex.Label] = (match.Success && match.Groups.Count > 1)
									? match.Groups[1].Value.Trim()
									: "";
							}

							// Accumulate the first enabled extractor's value into the
							// folder's distinct-set for the HEADLINE TokenCount column.
							// Operator orders config so the meaningful clustering field
							// comes first (a value that recurs across related responses
							// rather than one unique per response). Empty values are
							// skipped — they represent "extractor didn't match this
							// response," not a distinct token worth counting.
							if (extractors.Count > 0)
							{
								var firstLabel = extractors[0].Label;
								if (hit.Extracted.TryGetValue(firstLabel, out var firstVal)
									&& !string.IsNullOrEmpty(firstVal))
								{
									lock (gate) { report.TokenSet.Add(firstVal); }
								}
							}
						}
						catch (Exception ex)
						{
							lock (gate) { report.ReadWarnings.Add($"Header read failed for {Path.GetFileName(headerPath)}: {ex.Message}"); }
						}
					}
					else
					{
						lock (gate) { report.ReadWarnings.Add($"Missing header sidecar {Path.GetFileName(headerPath)} (for body {Path.GetFileName(htmlPath)})."); }
					}

					lock (gate) { report.Hits.Add(hit); }
				});

			report.HtmlFileCount = htmlFileCount;
			report.TotalHtmlBytes = totalHtmlBytes;
			report.MarkerPositiveCount = markerPositiveCount;

			// Parallel collection makes Hits arrival order non-deterministic. Sort by
			// on-disk filename so the "FIRST TOKEN FOUND" row (Hits[0]) — and the
			// per-crawl detail order — are now STABLE across re-runs, an improvement
			// over the previous filesystem-enumeration order the comments flagged as
			// unstable.
			report.Hits.Sort((a, b) => string.CompareOrdinal(a.FilenameOnDisk, b.FilenameOnDisk));

			return report;
		}

		/// <summary>
		/// Extracts the GET URL from a header sidecar's REQUEST block. The crawler writes
		/// the request line as "GET {url}" (see HeaderSidecar.FormatHeaderSidecar). Falls back to
		/// empty string if the line is missing/malformed; consumers should treat empty as
		/// "URL not recovered from sidecar."
		///
		/// BOM tolerance: header sidecars are UTF-8-with-BOM (Encoding.UTF8 writes one).
		/// .NET's StreamReader (used inside File.ReadAllText*) strips the BOM by default,
		/// but the StringReader path used here does NOT — so the first line may carry
		/// the BOM. We trim it from the marker comparison defensively.
		/// </summary>
		internal static string ExtractRequestUrl(string headerText)
		{
			// Format: "=== REQUEST ===\nGET <url>\n=== RESPONSE ===\n..."
			using var reader = new StringReader(headerText);
			string? line;
			var inRequest = false;
			while ((line = reader.ReadLine()) is not null)
			{
				// Strip leading BOM if present on the first line.
				if (line.Length > 0 && line[0] == '\uFEFF')
				{
					line = line[1..];
				}
				if (line.StartsWith("=== REQUEST", StringComparison.Ordinal))
				{
					inRequest = true;
					continue;
				}
				if (line.StartsWith("=== RESPONSE", StringComparison.Ordinal))
				{
					return string.Empty;
				}
				if (inRequest && line.StartsWith("GET ", StringComparison.Ordinal))
				{
					return line["GET ".Length..].Trim();
				}
			}
			return string.Empty;
		}

		/// <summary>
		/// Writes the per-site diagnostic log: top headline table (one row per timestamp
		/// folder), then per-folder detail blocks with the marker-positive file rows. The
		/// log is the only output of this diagnostic — no console summary by design, per
		/// the operator's "low-level diag, just do stuff to find root cause" framing.
		/// </summary>
		private static async Task WriteDiagnosticLogAsync(string logPath, SiteConfig site, string siteRoot, List<FolderReport> perFolder, IReadOnlyList<CrawlHistoryDiagnosticMarker> markers, IReadOnlyList<CompiledExtractor> extractors)
		{
			var sb = new StringBuilder();

			sb.AppendLine($"=== CRAWL DIAGNOSTIC for {site.Name} ({site.Url}) ===");
			sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			sb.AppendLine($"Site root: {siteRoot}");
			if (markers.Count == 0)
			{
				sb.AppendLine("Marker set: (none configured — size/file-count table only)");
			}
			else
			{
				sb.AppendLine("Marker set:");
				foreach (var m in markers)
				{
					var labelPart = string.IsNullOrEmpty(m.Name) ? "" : $" [{m.Name}]";
					var commentPart = string.IsNullOrEmpty(m.Comment) ? "" : $" — {m.Comment}";
					sb.AppendLine($"  - {m.Value}{labelPart}{commentPart}");
				}
			}
			if (extractors.Count == 0)
			{
				sb.AppendLine("Header extractors: (none configured — URL/Date/Size only)");
			}
			else
			{
				sb.AppendLine("Header extractors:");
				foreach (var ex in extractors)
				{
					var commentPart = string.IsNullOrEmpty(ex.Comment) ? "" : $" — {ex.Comment}";
					sb.AppendLine($"  - {ex.Label}{commentPart}");
				}
			}
			sb.AppendLine();

			// Headline table — alignment is space-padded for terminal scanning. Numbers
			// formatted with thousands separators (invariant culture for stable parsing if
			// the operator ever pipes this into a tool). The TokenCount column appears
			// only when at least one extractor is configured: distinct non-empty values
			// of the first enabled extractor seen among this crawl's marker-positive
			// responses. A small count (e.g., 1) signals all bad responses cluster on
			// the same value; a count matching MarkerHits signals spread across many.
			// Operator orders config so the meaningful clustering field comes first.
			var showTokenCount = extractors.Count > 0;
			sb.AppendLine("== HEADLINE TABLE ==");
			if (showTokenCount)
			{
				sb.AppendLine($"{"Timestamp",-22} {"HTML bytes",16} {"Files",8} {"Marker hits",12} {"Bad %",8} {"TokenCount",12}");
				sb.AppendLine(new string('-', 22 + 1 + 16 + 1 + 8 + 1 + 12 + 1 + 8 + 1 + 12));
			}
			else
			{
				sb.AppendLine($"{"Timestamp",-22} {"HTML bytes",16} {"Files",8} {"Marker hits",12} {"Bad %",8}");
				sb.AppendLine(new string('-', 22 + 1 + 16 + 1 + 8 + 1 + 12 + 1 + 8));
			}
			foreach (var r in perFolder)
			{
				var badPct = r.HtmlFileCount > 0
					? (100.0 * r.MarkerPositiveCount / r.HtmlFileCount).ToString("0.00", CultureInfo.InvariantCulture) + "%"
					: "n/a";
				if (showTokenCount)
				{
					sb.AppendLine($"{r.TimestampName,-22} {r.TotalHtmlBytes.ToString("N0", CultureInfo.InvariantCulture),16} {r.HtmlFileCount,8} {r.MarkerPositiveCount,12} {badPct,8} {r.TokenSet.Count,12}");
				}
				else
				{
					sb.AppendLine($"{r.TimestampName,-22} {r.TotalHtmlBytes.ToString("N0", CultureInfo.InvariantCulture),16} {r.HtmlFileCount,8} {r.MarkerPositiveCount,12} {badPct,8}");
				}
			}
			sb.AppendLine();

			// First-token-found table — one row per crawl that has at least one
			// marker-positive response. "First" is Hits[0], which is now the
			// alphabetically-first on-disk filename (Hits is sorted by filename after
			// the parallel scan), so this row is STABLE across re-runs — sufficient for
			// the operator's "is this always the same file / section / variant?"
			// cross-crawl eyeball. Crawls with zero hits are omitted (no first failure
			// to point at). Disk paths are absolute so they are clickable in IDEs and
			// modern terminals.
			var anyHits = perFolder.Any(r => r.Hits.Count > 0);
			if (anyHits)
			{
				sb.AppendLine("== FIRST TOKEN FOUND TABLE ==");
				sb.AppendLine($"{"Timestamp",-22} {"URL",-90} {"Diskname",-110} {"HTML bytes",12}");
				sb.AppendLine(new string('-', 22 + 1 + 90 + 1 + 110 + 1 + 12));
				foreach (var r in perFolder)
				{
					if (r.Hits.Count == 0)
					{
						continue;
					}

					var first = r.Hits[0];
					var url = string.IsNullOrEmpty(first.RequestUrl) ? "(url not recovered)" : first.RequestUrl;
					var diskPath = Path.Combine(r.DownloadDir, first.FilenameOnDisk);
					sb.AppendLine($"{r.TimestampName,-22} {url,-90} {diskPath,-110} {first.SizeBytes.ToString("N0", CultureInfo.InvariantCulture),12}");
				}
				sb.AppendLine();
			}

			// Per-crawl detail blocks. Each block lists the marker-positive files for that
			// crawl sorted by on-disk filename (stable across re-runs, set once after the
			// parallel scan). Column set widens dynamically based on configured extractors
			// — one column per extractor Label inserted between Response Date and Size.
			sb.AppendLine("== PER-CRAWL DETAIL ==");

			// Per-extractor column width: the longer of (Label, 12). 12 is the typical
			// width for short token values; longer labels get more room. Keeps the table
			// readable when label strings vary in length.
			var extractorWidths = extractors.Select(ex => Math.Max(ex.Label.Length, 12)).ToList();

			foreach (var r in perFolder)
			{
				sb.AppendLine();
				sb.AppendLine($"[{r.TimestampName}]");

				if (r.Notes.Count > 0)
				{
					foreach (var n in r.Notes)
					{
						sb.AppendLine($"  Note: {n}");
					}
				}

				if (r.Hits.Count == 0)
				{
					sb.AppendLine("  No marker-positive files.");
				}
				else
				{
					sb.AppendLine($"  Marker-positive files ({r.Hits.Count}):");

					// Header row: URL | Response Date | <extractor labels...> | Size
					var headerLine = new StringBuilder();
					headerLine.Append($"  {"URL",-90} {"Response Date",-32}");
					for (var i = 0; i < extractors.Count; i++)
					{
						headerLine.Append(' ');
						headerLine.Append(extractors[i].Label.PadRight(extractorWidths[i]));
					}
					headerLine.Append($" {"Size",10}");
					sb.AppendLine(headerLine.ToString());

					foreach (var h in r.Hits)
					{
						var url = string.IsNullOrEmpty(h.RequestUrl) ? "(url not recovered)" : h.RequestUrl;
						var row = new StringBuilder();
						row.Append($"  {url,-90} {h.ResponseDate,-32}");
						for (var i = 0; i < extractors.Count; i++)
						{
							row.Append(' ');
							var v = h.Extracted.TryGetValue(extractors[i].Label, out var ev) ? ev : "";
							row.Append(v.PadRight(extractorWidths[i]));
						}
						row.Append($" {h.SizeBytes,10}");
						sb.AppendLine(row.ToString());
					}
				}

				if (r.ReadWarnings.Count > 0)
				{
					sb.AppendLine($"  Warnings ({r.ReadWarnings.Count}):");
					foreach (var w in r.ReadWarnings)
					{
						sb.AppendLine($"    - {w}");
					}
				}
			}

			await File.WriteAllTextAsync(logPath, sb.ToString(), Encoding.UTF8);
		}

		/// <summary>Per-timestamp-folder analysis result. Populated by AnalyseTimestampFolderAsync.</summary>
		private sealed class FolderReport
		{
			public string TimestampName { get; set; } = string.Empty;
			public long TotalHtmlBytes { get; set; }
			public int HtmlFileCount { get; set; }
			public int MarkerPositiveCount { get; set; }
			public List<MarkerHit> Hits { get; } = [];
			public List<string> Notes { get; } = [];
			public List<string> ReadWarnings { get; } = [];

			/// <summary>
			/// Absolute path of this crawl's download subdirectory (the folder
			/// containing the body HTML files and their header sidecars). Stored
			/// here so the FIRST TOKEN FOUND section can emit absolute paths for
			/// each crawl's first marker-positive file without re-resolving paths
			/// at report-write time. Empty when the download directory was
			/// missing (in which case there will also be no Hits).
			/// </summary>
			public string DownloadDir { get; set; } = string.Empty;

			/// <summary>
			/// Distinct non-empty values of the first enabled header extractor,
			/// accumulated across marker-positive responses in this crawl folder.
			/// Surfaces in the HEADLINE TABLE as the TokenCount column — a quick
			/// scope indicator: 1 means all marker-positive responses in this
			/// crawl share a single token, higher counts indicate the failure
			/// is spread across multiple distinct values. Empty when no
			/// extractors are configured; populated regardless of extractor
			/// label, with the operator ordering config so the meaningful
			/// clustering field comes first.
			/// </summary>
			public HashSet<string> TokenSet { get; } = [];
		}

		/// <summary>Per-file forensic record for a marker-positive HTML response.</summary>
		private sealed class MarkerHit
		{
			public string FilenameOnDisk { get; set; } = string.Empty;
			public string Marker { get; set; } = string.Empty;
			public string MarkerName { get; set; } = string.Empty;
			public long SizeBytes { get; set; }
			public string RequestUrl { get; set; } = string.Empty;
			public string ResponseDate { get; set; } = string.Empty;

			/// <summary>
			/// Values extracted from the header sidecar by configured header
			/// extractors, keyed by extractor Label. Missing keys (or empty
			/// values) indicate the extractor's pattern did not match this
			/// particular response — a normal forensic outcome, not an error.
			/// </summary>
			public Dictionary<string, string> Extracted { get; } = [];
		}

		/// <summary>
		/// Compiled form of a <see cref="CrawlHistoryDiagnosticHeaderExtractor"/>:
		/// Label and Comment from config plus the compiled Regex. Built once at
		/// PromptAndRunAsync startup (after config validation), reused across
		/// every header sidecar in every crawl folder.
		/// </summary>
		private sealed class CompiledExtractor
		{
			public string Label { get; set; } = string.Empty;
			public string Comment { get; set; } = string.Empty;
			public Regex Regex { get; set; } = new(string.Empty);
		}
	}
}
