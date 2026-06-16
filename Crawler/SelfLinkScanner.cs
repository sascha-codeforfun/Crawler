namespace Crawler
{
	using HtmlAgilityPack;
	using System.Collections.Concurrent;
	using System.Text;
	using System.Web;

	public static class SelfLinkScanner
	{
		public static void FindSelfLinks(
			string directoryToScanPath,
			string scanningResultsPath,
			List<string> queryStringsToIgnoreForSelfLinkDetermination,
			string filePattern,
			int maxDegreeOfParallelism = 0,
			int contextRadiusChars = 120,
			int contextSnippetLength = 240)
		{
			var htmlFiles = Directory.GetFiles(directoryToScanPath, filePattern, SearchOption.AllDirectories);
			var results = new ConcurrentQueue<string>();

			var ignoredQueryKeys = queryStringsToIgnoreForSelfLinkDetermination is { Count: > 0 }
				? new HashSet<string>(queryStringsToIgnoreForSelfLinkDetermination, StringComparer.OrdinalIgnoreCase)
				: null;

			int resolvedDop = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
			var maxDop = Math.Max(1, resolvedDop - 1);
			var po = new ParallelOptions { MaxDegreeOfParallelism = maxDop };

			Parallel.ForEach(htmlFiles, po, diskFile =>
			{
				try
				{
					var fileName = Path.GetFileName(diskFile);
					var fileUrl = CrawlIndex.LookUpUrlForFile(fileName);

					if (string.IsNullOrWhiteSpace(fileUrl))
					{
						return;
					}

					Uri baseUri;
					try
					{
						baseUri = new Uri(fileUrl);
					}
					catch
					{
						return;
					}

					string fileContent;
					try
					{
						fileContent = File.ReadAllText(diskFile, Encoding.UTF8);
					}
					catch
					{
						return;
					}

					var doc = new HtmlDocument();
					doc.LoadHtml(fileContent);

					var anchorNodes = doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>();

					var baseOrigin = baseUri.GetLeftPart(UriPartial.Authority);

					foreach (var a in anchorNodes)
					{
						var hrefValue = a.GetAttributeValue("href", string.Empty);
						if (string.IsNullOrWhiteSpace(hrefValue))
						{
							continue;
						}

						var hrefNormalized = hrefValue.Trim();

						bool startsWithSlash = hrefNormalized.StartsWith('/');
						bool startsWithDomain = false;

						if (!startsWithSlash)
						{
							if (Uri.TryCreate(hrefNormalized, UriKind.Absolute, out var hrefUri))
							{
								var hrefOrigin = hrefUri.GetLeftPart(UriPartial.Authority);
								startsWithDomain = string.Equals(baseOrigin, hrefOrigin, StringComparison.OrdinalIgnoreCase);
							}
						}

						if (!(startsWithSlash || startsWithDomain))
						{
							continue;
						}

						Uri resolvedHrefUri;
						try
						{
							if (Uri.TryCreate(hrefNormalized, UriKind.Absolute, out var abs))
							{
								resolvedHrefUri = abs;
							}
							else
							{
								resolvedHrefUri = new Uri(baseUri, hrefNormalized);
							}
						}
						catch
						{
							continue;
						}

						if (ignoredQueryKeys is not null && !string.IsNullOrEmpty(resolvedHrefUri.Query))
						{
							var q = HttpUtility.ParseQueryString(resolvedHrefUri.Query);
							var keys = q.AllKeys;
							if (keys is not null)
							{
								bool hasIgnored = false;
								foreach (var k in keys)
								{
									if (k is null)
									{
										continue;
									}

									if (ignoredQueryKeys.Contains(k))
									{
										hasIgnored = true;
										break;
									}
								}
								if (hasIgnored)
								{
									continue;
								}
							}
						}

						var hrefPath = resolvedHrefUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
						string filePath;
						try
						{
							filePath = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
						}
						catch
						{
							filePath = fileUrl.TrimEnd('/');
						}

						if (string.Equals(hrefPath, filePath, StringComparison.OrdinalIgnoreCase))
						{
							if (hrefNormalized.Contains('#'))
							{
								continue;
							}

							var snippet = ExtractHtmlContextSnippet(fileContent, a, contextRadiusChars, contextSnippetLength);

							// [KEEP] Composed via IssueLogWriter — replaces the
							// previous local EscapeForCsvPipe helper. Each field
							// is sanitized for CR / LF / control chars / delimiter.
							var line = IssueLogWriter.ComposeLine(IssueLogWriter.PipeDelimiter,
								fileName, fileUrl, hrefValue, snippet ?? string.Empty);

							results.Enqueue(line);
						}
					}
				}
				catch
				{
					// swallow per-file exceptions
				}
			});

			// Use IssueLogWriter.Utf8WithBom (BOM-emitting) for consistency
			// with the rest of the log corpus. Earlier the encoding was
			// `new UTF8Encoding(false)` (no BOM), producing inconsistent output
			// vs. the BOM-prefixed logs written via IssueLogWriter's non-streaming
			// helpers (10, 16, 17, etc.).
			using var writer = new StreamWriter(scanningResultsPath, false, IssueLogWriter.Utf8WithBom);
			// Header — composed through IssueLogWriter for consistency. Field names
			// contain no delimiter / control chars so this passes through unchanged.
			writer.WriteLine(IssueLogWriter.ComposeLine(IssueLogWriter.PipeDelimiter,
				"File", "FileUrl", "LinkFound", "ContextSnippet"));

			// Drain queue and sort deterministically before write. The
			// Parallel.ForEach above enqueues in completion order, which is
			// CPU-scheduling-dependent — same input could produce different
			// byte-level output across runs. Sort by the composed line directly:
			// the line starts with the file hash + filename, which gives a
			// stable, alphabetic ordering that's also useful to operators reading
			// the log (related files cluster).
			var sortedResults = new List<string>(results.Count);
			while (results.TryDequeue(out var csvLine))
			{
				sortedResults.Add(csvLine);
			}

			sortedResults.Sort(StringComparer.Ordinal);
			foreach (var csvLine in sortedResults)
			{
				writer.WriteLine(csvLine);
			}
		}

		public static string ExtractHtmlContextSnippet(string fileContent, HtmlNode aNode, int contextRadiusChars, int maxSnippetLength)
		{
			if (string.IsNullOrEmpty(fileContent) || aNode == null)
			{
				return string.Empty;
			}

			string needle = aNode.OuterHtml ?? aNode.InnerHtml ?? aNode.InnerText ?? string.Empty;
			if (string.IsNullOrEmpty(needle))
			{
				return string.Empty;
			}

			int idx = fileContent.IndexOf(needle, StringComparison.Ordinal);
			if (idx < 0)
			{
				idx = fileContent.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
			}

			if (idx < 0)
			{
				string textOnly = aNode.InnerText ?? string.Empty;
				if (!string.IsNullOrEmpty(textOnly))
				{
					idx = fileContent.IndexOf(textOnly, StringComparison.Ordinal);
					if (idx < 0)
					{
						idx = fileContent.IndexOf(textOnly, StringComparison.OrdinalIgnoreCase);
					}
				}
			}

			if (idx < 0)
			{
				return fileContent.Length <= maxSnippetLength ? fileContent : fileContent.Substring(0, maxSnippetLength);
			}

			int start = Math.Max(0, idx - contextRadiusChars);
			int end = Math.Min(fileContent.Length, idx + needle.Length + contextRadiusChars);
			int length = end - start;

			if (length > maxSnippetLength)
			{
				int center = idx + needle.Length / 2;
				start = Math.Max(0, center - maxSnippetLength / 2);
				if (start + maxSnippetLength > fileContent.Length)
				{
					start = Math.Max(0, fileContent.Length - maxSnippetLength);
				}

				length = Math.Min(maxSnippetLength, fileContent.Length - start);
			}
			else if (length < maxSnippetLength)
			{
				int need = maxSnippetLength - length;
				end = Math.Min(fileContent.Length, end + need);
				length = end - start;
				if (length < maxSnippetLength)
				{
					int more = Math.Min(start, maxSnippetLength - length);
					start -= more;
				}
			}

			return fileContent.Substring(start, Math.Min(maxSnippetLength, end - start));
		}
	}
}
