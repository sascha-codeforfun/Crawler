namespace Crawler.Html
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics.CodeAnalysis;
	using System.IO;
	using System.Text;
	using HtmlAgilityPack;

	/// <summary>
	/// File-level operations on HTML markup on disk: XPath-based element removal
	/// (read → parse → prune → write utf8-no-bom) and head-only byte reading.
	/// Extracted from Tools. <see cref="RemoveByXPath"/> stays [ExcludeFromCodeCoverage]
	/// — exercised end-to-end by the boilerplate equivalence tests; <see cref="ReadHeadBytes"/>
	/// has direct characterization tests for its cap / no-</head> branches.
	/// </summary>
	public static class MarkupFile
	{
		/// <summary>Number of leading bytes read from a saved file for HTML sniffing.</summary>
		public const int SniffByteCount = 1024;

		// Use SelectNodes + loop so ALL matching nodes are removed, not just the first.
		[ExcludeFromCodeCoverage(Justification =
			"Filesystem read + HtmlAgilityPack parse + filesystem write. " +
			"XPath removal correctness is exercised end-to-end by the " +
			"normalization pipeline; unit-testing this method would require " +
			"temp file fixtures with little added confidence over what " +
			"HtmlAgilityPack already guarantees.")]
		public static void RemoveByXPath(string sourceFilePath, List<string> elementsToRemove, string destinationDirectory)
		{
			if (string.IsNullOrEmpty(sourceFilePath) || elementsToRemove == null)
			{
				return;
			}

			byte[] bytes = File.ReadAllBytes(sourceFilePath);

			// Detect encoding from BOM or meta charset (fallback to Windows-1252)
			Encoding detected = DetectEncoding.FromBytes(bytes) ?? Encoding.GetEncoding(1252);
			string html = detected.GetString(bytes);

			var doc = new HtmlDocument();
			doc.LoadHtml(html);

			foreach (var xpath in elementsToRemove)
			{
				var nodes = doc.DocumentNode.SelectNodes(xpath);
				if (nodes != null)
				{
					// Snapshot the list before iterating — the collection changes as nodes are removed.
					foreach (var node in nodes.ToList())
					{
						node.Remove();
					}
				}
			}

			string destFilePath = Path.Combine(destinationDirectory, Path.GetFileName(sourceFilePath));

			Directory.CreateDirectory(destinationDirectory);

			// Save as UTF-8 without BOM
			var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
			using var fs = File.Create(destFilePath);
			using var sw = new StreamWriter(fs, utf8NoBom);
			doc.Save(sw);
		}

		// ── Head-only HTML reading ───────────────────────────────────────────
		//
		// For analyses that only need <head> content (e.g. checking <meta name="robots">
		// for sitemap inclusion), reading the entire file is wasteful — typical HTML
		// pages are 100KB-1MB but the head is under 4KB. This helper streams just
		// enough bytes to find the closing </head> tag, capped at maxBytes for safety.
		//
		// The cap (default 16KB) is ~8× a typical CMS head and handles outliers with
		// rich JSON-LD, social meta tags, or instrumentation scripts. If the cap is
		// reached without finding </head>, the returned bytes still cover the typical
		// position of robots/canonical/title metas — the caller decides whether to
		// log or treat as truncated.

		/// <summary>
		/// Reads the bytes from the start of an HTML file up to (and including) the
		/// closing &lt;/head&gt; tag, or up to <paramref name="maxBytes"/>, whichever
		/// comes first. Match for &lt;/head&gt; is case-insensitive on the ASCII bytes.
		/// </summary>
		/// <param name="path">Path to the HTML file.</param>
		/// <param name="maxBytes">Hard cap on bytes read (default 16384).</param>
		/// <param name="reachedCap">
		/// True when <paramref name="maxBytes"/> was reached before &lt;/head&gt; was
		/// seen — the caller may want to warn since the head is unusually large or
		/// malformed.
		/// </param>
		/// <returns>Bytes from offset 0 to either &lt;/head&gt; end or the cap.</returns>
		public static byte[] ReadHeadBytes(string path, int maxBytes, out bool reachedCap)
		{
			reachedCap = false;
			if (maxBytes <= 0)
			{
				return [];
			}

			using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
				bufferSize: 4096, useAsync: false);

			// Read up to maxBytes; allocate the buffer at the size we actually need.
			var fileLen = fs.Length;
			int toRead = (int)Math.Min(fileLen, maxBytes);
			var buf = new byte[toRead];
			int read = 0;
			while (read < toRead)
			{
				int n = fs.Read(buf, read, toRead - read);
				if (n <= 0)
				{
					break;
				}

				read += n;
			}

			// Search for "</head" (case-insensitive, ASCII). The closing '>' may have
			// whitespace before it; we accept any byte that follows "</head" and look
			// for the '>' within a small lookahead. In practice </head> appears
			// directly without whitespace, but tolerating it costs nothing.
			//
			// Pattern bytes (lowercase) for case-insensitive match:
			ReadOnlySpan<byte> pat = [(byte)'<', (byte)'/', (byte)'h', (byte)'e', (byte)'a', (byte)'d'];

			int endIdx = -1;
			for (int i = 0; i + pat.Length <= read; i++)
			{
				bool ok = true;
				for (int k = 0; k < pat.Length; k++)
				{
					byte b = buf[i + k];
					// Lowercase ASCII letters: A..Z (0x41..0x5A) ORed with 0x20.
					byte lower = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b | 0x20) : b;
					if (lower != pat[k]) { ok = false; break; }
				}
				if (!ok)
				{
					continue;
				}

				// Found "</head"; advance to the '>' (allow up to 16 trailing chars
				// of whitespace/attributes, though </head> takes none in practice).
				int j = i + pat.Length;
				int limit = Math.Min(read, j + 16);
				while (j < limit && buf[j] != (byte)'>')
				{
					j++;
				}

				if (j < limit && buf[j] == (byte)'>')
				{
					endIdx = j + 1;
					break;
				}
			}

			if (endIdx > 0 && endIdx <= read)
			{
				var result = new byte[endIdx];
				Buffer.BlockCopy(buf, 0, result, 0, endIdx);
				return result;
			}

			// </head> not found within the buffered window. If the file is larger
			// than maxBytes we hit the cap; otherwise the file just has no </head>
			// (truncated HTML, fragment, etc.) and we return what we have.
			reachedCap = fileLen > maxBytes;
			if (read == buf.Length)
			{
				return buf;
			}

			var trimmed = new byte[read];
			Buffer.BlockCopy(buf, 0, trimmed, 0, read);
			return trimmed;
		}
	}
}
