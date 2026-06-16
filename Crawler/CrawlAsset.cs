using System.Collections.Concurrent;

namespace Crawler
{
	public class CrawlAsset
	{
		private static HttpClientHandler? handler;
		private static HttpClient? client;
		private static readonly ConcurrentDictionary<string, byte> visited = new();

		public static void Initialize(string? proxyUrl, string? proxyUser, string? proxyPassword)
		{
			handler = new HttpClientHandler { AllowAutoRedirect = false };

			var proxy = ProxyConfig.Build(proxyUrl, proxyUser, proxyPassword, "CrawlAsset");
			if (proxy != null)
			{
				handler.Proxy = proxy;
				handler.UseProxy = true;
			}
			else
			{
				handler.UseProxy = false;
			}

			client = new HttpClient(handler);
		}

		// The "download" subfolder is appended here, so the
		// caller must pass the session root (saveDirectory), not the download subfolder.
		public static async Task DownloadAssetAsync(string assetUrl, string saveDirectory, string logFilePath, string source = "discovery")
		{
			if (client is null)
			{
				throw new InvalidOperationException("CrawlAsset.Initialize() must be called before DownloadAssetAsync.");
			}

			if (!visited.TryAdd(assetUrl, 0))
			{
				return;
			}

			Tools.Log(assetUrl, "crawled", "info", logFilePath);

			try
			{
				var response = await client.GetAsync(assetUrl).ConfigureAwait(false);
				response.EnsureSuccessStatusCode();

				var assetContent = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
				var assetFileName = Tools.GenerateFileName(assetUrl);
				var downloadDir = Path.Combine(saveDirectory, "download");
				Directory.CreateDirectory(downloadDir);
				var filePath = Path.Combine(downloadDir, assetFileName);

				await File.WriteAllBytesAsync(filePath, assetContent).ConfigureAwait(false);

				// Capture full request+response headers next to the asset body.
				Crawl.WriteHeaderSidecar(response, assetUrl, downloadDir, assetFileName);

				Tools.Log(assetUrl, "savedAsset", assetFileName, logFilePath, source);

				// [KEEP] CSS files are scanned one level deep for url() references.
				// This catches fonts, background images, and PDFs referenced in stylesheets.
				// JS files are NOT scanned — minified JS yields no useful URL signal.
				// One level only: CSS referenced from CSS is not followed to prevent
				// circular dependency chains and runaway crawl depth.
				if (assetUrl.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
				{
					var cssText = System.Text.Encoding.UTF8.GetString(assetContent);
					var cssUrls = UrlExtractor.ExtractFromCss(cssText, assetUrl);
					var cssTasks = cssUrls
						.Select(u => DownloadAssetAsync(u.Url, saveDirectory, logFilePath, source))
						.ToList();
					await Task.WhenAll(cssTasks).ConfigureAwait(false);
				}
			}
			catch (HttpRequestException httpEx)
			{
				var status = httpEx.StatusCode?.ToString() ?? "N/A";
				Tools.Log(assetUrl, status, httpEx.Message, logFilePath);
			}
			catch (Exception ex)
			{
				Tools.Log(assetUrl, "N/A", ex.Message, logFilePath);
			}
		}
	}
}
