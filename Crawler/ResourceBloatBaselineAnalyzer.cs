using System.Text;
using System.Text.RegularExpressions;

namespace Crawler
{
	// ── ResourceBloatBaselineAnalyzer ────────────────────────────────────────
	//
	// Reads log 20 (20-resource-bloat.log) and produces log 21
	// (21-resource-bloat-above-baseline.log) — an executive summary showing:
	//
	//   Row 1: BASELINE — the JS/CSS payload every page pays, with file list
	//   Rows 2+: Pages that load MORE than baseline, sorted by delta descending
	//
	// Baseline detection: files present on >= 90% of pages (auto-detected).
	// OVERSIZED flags dropped — meaningless when baseline already qualifies.
	// Only BASE64_LARGE, INLINED_CSS, JSON_BLOBS retained as meaningful signals.
	//
	// Output columns:
	//   PageUrl (or "BASELINE") @@@
	//   DeltaJSCSSFileCount @@@
	//   DeltaJSCSSTotalBytes @@@
	//   DeltaBase64Count @@@
	//   DeltaBase64TotalBytes @@@
	//   Base64LargeAssets @@@
	//   DeltaInlinedCSSBytes @@@
	//   JSONBlobCount @@@
	//   JSONBlobTotalBytes @@@
	//   Issues
	//
	// BASELINE row uses absolute values (not deltas) and lists baseline files.
	// ─────────────────────────────────────────────────────────────────────────

	public static class ResourceBloatBaselineAnalyzer
	{
		private const string Header =
			"PageUrl@@@DeltaJSCSSFileCount@@@DeltaJSCSSTotalBytes@@@" +
			"DeltaJSBytes@@@DeltaCSSBytes@@@" +
			"DeltaBase64Count@@@DeltaBase64TotalBytes@@@Base64LargeAssets@@@" +
			"DeltaInlinedCSSBytes@@@JSONBlobCount@@@JSONBlobTotalBytes@@@Issues";

		// Pages that load a file on >= this fraction are considered baseline.
		private const double BaselineThreshold = 0.90;

		// ── Public entry point ────────────────────────────────────────────────

		public static void Analyse(
			string downloadDirectory,
			string resourceBloatLogPath,
			string logPath,
			string siteUrl,
			string configuredPageExt,
			int base64ThresholdBytes = 102_400,
			int jsThresholdBytes = 512_000,
			int cssThresholdBytes = 512_000,
			int aboveBaselineThresholdBytes = 3_072_000)
		{
			if (!File.Exists(resourceBloatLogPath))
			{
				Logger.LogInfo("ResourceBloatBaselineAnalyzer: log 20 not found, skipping.");
				ConsoleUi.WriteStepRow("Resource bloat (baseline)", "skipped", dimmed: true);
				return;
			}

			// ── Step 1: Load log 20 ───────────────────────────────────────────
			var rows = LoadLog20(resourceBloatLogPath);
			if (rows.Count == 0)
			{
				Logger.LogInfo("ResourceBloatBaselineAnalyzer: no rows in log 20, skipping.");
				ConsoleUi.WriteStepRow("Resource bloat (baseline)", "skipped", dimmed: true);
				return;
			}

			Logger.LogInfo($"ResourceBloatBaselineAnalyzer: analysing {rows.Count} pages from log 20.");

			// ── Step 2: Detect baseline files ─────────────────────────────────
			// Scan HTML files to count how many pages reference each JS/CSS URL.
			var urlFrequency = BuildUrlFrequency(downloadDirectory, siteUrl, rows.Count, configuredPageExt);
			var baselineUrls = urlFrequency
				.Where(kv => kv.Value >= BaselineThreshold)
				.Select(kv => kv.Key)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			// ── Step 3: Calculate baseline metrics ────────────────────────────
			// Use the minimum JSCSSTotalBytes row as the baseline total.
			var baselineTotal = rows.Min(r => r.JsCssTotal);
			var baselineCount = rows.Where(r => r.JsCssTotal == baselineTotal)
									   .Select(r => r.JsCssFileCount).Min();
			var baselineJsBytes = rows.Where(r => r.JsCssTotal == baselineTotal)
									   .Select(r => r.JsBytes).Min();
			var baselineCssBytes = rows.Where(r => r.JsCssTotal == baselineTotal)
									   .Select(r => r.CssBytes).Min();
			var baselineB64Count = rows.Where(r => r.JsCssTotal == baselineTotal)
									   .Select(r => r.Base64Count).Min();
			var baselineB64Bytes = rows.Where(r => r.JsCssTotal == baselineTotal)
									   .Select(r => r.Base64TotalBytes).Min();
			var baselineInlined = rows.Where(r => r.JsCssTotal == baselineTotal)
									   .Select(r => r.InlinedCssBytes).Min();

			// Format baseline file list — short names, pipe-separated.
			var baselineFiles = baselineUrls
				.Select(u => ShortenUrl(u))
				.OrderBy(n => n)
				.ToList();
			var baselineFilesCol = baselineFiles.Count > 0
				? string.Join("|", baselineFiles)
				: "unknown";

			// ── Step 4: Build output rows ─────────────────────────────────────
			var outputRows = new List<string>();

			// BASELINE row — absolute values, file list in Base64LargeAssets column.
			outputRows.Add(
				$"BASELINE@@@{baselineCount}@@@{baselineTotal}@@@" +
				$"{baselineJsBytes}@@@{baselineCssBytes}@@@" +
				$"{baselineB64Count}@@@{baselineB64Bytes}@@@{baselineFilesCol}@@@" +
				$"{baselineInlined}@@@0@@@0@@@SITEWIDE");

			// Data rows — pages above baseline where at least one threshold exceeded.
			var aboveBaseline = rows
				.Where(r => r.JsCssTotal > baselineTotal)
				.Where(r => (r.JsCssTotal - baselineTotal) >= aboveBaselineThresholdBytes ||
							r.Issues.Contains("BASE64_LARGE") ||
							r.Issues.Contains("INLINED_CSS") ||
							r.Issues.Contains("JSON_BLOBS"))
				.Where(r =>
					(r.JsBytes - baselineJsBytes) >= jsThresholdBytes ||
					(r.CssBytes - baselineCssBytes) >= cssThresholdBytes ||
					r.Issues.Contains("BASE64_LARGE") ||
					r.Issues.Contains("INLINED_CSS") ||
					r.Issues.Contains("JSON_BLOBS"))
				.OrderByDescending(r => r.JsCssTotal - baselineTotal)
				.ToList();

			foreach (var row in aboveBaseline)
			{
				var delta = row.JsCssTotal - baselineTotal;
				var deltaCount = row.JsCssFileCount - baselineCount;
				var deltaJs = row.JsBytes - baselineJsBytes;
				var deltaCss = row.CssBytes - baselineCssBytes;
				var deltaB64Count = row.Base64Count - baselineB64Count;
				var deltaB64Bytes = row.Base64TotalBytes - baselineB64Bytes;
				var deltaInlined = row.InlinedCssBytes - baselineInlined;

				// Strip OVERSIZED flags — not meaningful above baseline.
				// Add EXTRA_JS / EXTRA_CSS when page loads more than baseline.
				var issues = row.Issues
					.Split('|')
					.Where(i => i != "OVERSIZED_JS" && i != "OVERSIZED_CSS"
							&& i != "ABOVE_BASELINE")
					.ToList();
				if (deltaJs >= jsThresholdBytes)
				{
					issues.Add("EXTRA_JS");
				}

				if (deltaCss >= cssThresholdBytes)
				{
					issues.Add("EXTRA_CSS");
				}

				var issueCol = issues.Count > 0
					? string.Join("|", issues.OrderBy(i => i))
					: "ABOVE_BASELINE";

				outputRows.Add(
					$"{row.PageUrl}@@@{deltaCount}@@@{delta}@@@" +
					$"{deltaJs}@@@{deltaCss}@@@" +
					$"{deltaB64Count}@@@{deltaB64Bytes}@@@{row.Base64LargeAssets}@@@" +
					$"{deltaInlined}@@@{row.JsonBlobCount}@@@{row.JsonBlobTotalBytes}@@@" +
					$"{issueCol}");
			}

			// ── Step 5: Write log 21 ──────────────────────────────────────────
			var lines = new List<string> { Header };
			lines.AddRange(outputRows);
			FileIo.WriteAllLinesWithRetry(logPath, lines, Path.GetFileName(logPath));

			Logger.LogInfo(
				$"ResourceBloatBaselineAnalyzer: baseline={baselineTotal / 1024 / 1024:F1}MB, " +
				$"{aboveBaseline.Count} pages above baseline. See {Path.GetFileName(logPath)}.");
			ConsoleUi.WriteStepRow("Resource bloat (baseline)", $"{aboveBaseline.Count} over baseline");
		}

		// ── Build URL frequency map ───────────────────────────────────────────

		private static Dictionary<string, double> BuildUrlFrequency(
			string downloadDirectory,
			string siteUrl,
			int totalPages,
			string configuredPageExt)
		{
			if (totalPages == 0)
			{
				return [];
			}

			var root = Encoding.Latin1;
			var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

#pragma warning disable SYSLIB1045
			var scriptPat = new Regex(
				@"<script[^>]+\bsrc=[""']([^""']+\.js[^""']*)[""']",
				RegexOptions.IgnoreCase);
			var linkPat = new Regex(
				@"<link[^>]+\bhref=[""']([^""']+\.css[^""']*)[""']",
				RegexOptions.IgnoreCase);
#pragma warning restore SYSLIB1045

			var htmlFiles = Directory.GetFiles(downloadDirectory, "*.*",
				SearchOption.TopDirectoryOnly)
				.Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
						 || (!string.IsNullOrEmpty(configuredPageExt)
							 && f.EndsWith(configuredPageExt, StringComparison.OrdinalIgnoreCase)));

			foreach (var file in htmlFiles)
			{
				string html;
				try { html = File.ReadAllText(file, root); }
				catch { continue; }

				var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (Match m in scriptPat.Matches(html))
				{
					seen.Add(ResolveRef(m.Groups[1].Value, siteUrl));
				}

				foreach (Match m in linkPat.Matches(html))
				{
					seen.Add(ResolveRef(m.Groups[1].Value, siteUrl));
				}

				foreach (var url in seen)
				{
					var key = NormaliseUrl(url);
					counts[key] = counts.GetValueOrDefault(key, 0) + 1;
				}
			}

			return counts.ToDictionary(
				kv => kv.Key,
				kv => (double)kv.Value / totalPages,
				StringComparer.OrdinalIgnoreCase);
		}

		// ── Load log 20 ───────────────────────────────────────────────────────

		private static List<Log20Row> LoadLog20(string path)
		{
			var rows = new List<Log20Row>();
			foreach (var line in File.ReadLines(path, Encoding.UTF8).Skip(1))
			{
				var p = line.Split("@@@");
				if (p.Length < 12)
				{
					continue;
				}

				try
				{
					rows.Add(new Log20Row
					{
						PageUrl = p[0].Trim(),
						JsCssFileCount = int.Parse(p[1].Trim()),
						JsCssTotal = long.Parse(p[2].Trim()),
						JsBytes = long.Parse(p[3].Trim()),
						CssBytes = long.Parse(p[4].Trim()),
						Base64Count = int.Parse(p[5].Trim()),
						Base64TotalBytes = long.Parse(p[6].Trim()),
						Base64LargeAssets = p[7].Trim(),
						InlinedCssBytes = long.Parse(p[8].Trim()),
						JsonBlobCount = int.Parse(p[9].Trim()),
						JsonBlobTotalBytes = long.Parse(p[10].Trim()),
						Issues = p[11].Trim(),
					});
				}
				catch { }
			}
			return rows;
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		private static string ResolveRef(string href, string siteUrl)
		{
			if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
				href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				return href;
			}

			var root = siteUrl.TrimEnd('/');
			return href.StartsWith('/') ? root + href : root + '/' + href;
		}

		private static string NormaliseUrl(string url)
		{
			var q = url.IndexOf('?');
			if (q >= 0)
			{
				url = url[..q];
			}

			var h = url.IndexOf('#');
			if (h >= 0)
			{
				url = url[..h];
			}

			return url.TrimEnd('/');
		}

		// Shorten a full clientlib URL to just the meaningful filename part.
		// e.g. https://www.example.com/etc/
		//        stylesheet.min.aabbccddeehash.css
		//		→ stylesheet.min.css
		internal static string ShortenUrl(string url)
		{
			var seg = url.Split('/').Last();
			// Strip content hash: name.{32hex}.ext → name.ext
			var m = Regex.Match(seg,
				@"^(.+?)\.[0-9a-f]{32}(\.[a-z]+)$",
				RegexOptions.IgnoreCase);
			return m.Success ? m.Groups[1].Value + m.Groups[2].Value : seg;
		}

		// ── Data structure ────────────────────────────────────────────────────

		private record Log20Row
		{
			public required string PageUrl { get; init; }
			public required int JsCssFileCount { get; init; }
			public required long JsCssTotal { get; init; }
			public required long JsBytes { get; init; }
			public required long CssBytes { get; init; }
			public required int Base64Count { get; init; }
			public required long Base64TotalBytes { get; init; }
			public required string Base64LargeAssets { get; init; }
			public required long InlinedCssBytes { get; init; }
			public required int JsonBlobCount { get; init; }
			public required long JsonBlobTotalBytes { get; init; }
			public required string Issues { get; init; }
		}
	}
}
