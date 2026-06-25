namespace Crawler.Urls
{
	using System;
	using System.Web;

	/// <summary>
	/// URL query-string manipulation helpers extracted from Tools.
	/// Pure functions over URL strings: stripping the query entirely, removing
	/// selected query keys, and unwrapping a modal-carrier URL's encoded target.
	/// </summary>
	public static class Query
	{
		// Guard against relative URLs which cause UriFormatException.
		public static string RemoveQueryStringElements(string url, string keys)
		{
			if (string.IsNullOrEmpty(url))
			{
				throw new ArgumentException("URL cannot be null or empty.", nameof(url));
			}

			if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			{
				// Relative URL — no query string manipulation possible, return as-is.
				Logger.LogError($"RemoveQueryStringElements: '{url}' is not an absolute URL; returning unchanged.");
				return url;
			}

			UriBuilder uriBuilder = new(uri);
			var query = HttpUtility.ParseQueryString(uriBuilder.Query);
			var keysToRemove = keys.Split('|');
			foreach (var key in keysToRemove)
			{
				query.Remove(key);
			}
			uriBuilder.Query = query.ToString();
			return uriBuilder.ToString();
		}

		public static string RemoveQueryString(string url)
		{
			Uri uri = new(url);
			return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}{uri.AbsolutePath}";
		}

		/// <summary>
		/// Extracts a URL-encoded target URL from a modal/overlay query parameter.
		/// Some sites load modal content by encoding the target URL as a query parameter
		/// on a carrier page (e.g. page.html?lightbox=%2Fmodal%2Fbox1.htm).
		/// Returns the decoded target URL when the parameter is present, or the carrier
		/// URL with the query string stripped when it is not.
		/// </summary>
		public static string ExtractModalUrl(string carrierUrl, string baseUrl, string parameterName)
		{
			var uriBuilder = new UriBuilder(new Uri(carrierUrl));
			var query = HttpUtility.ParseQueryString(uriBuilder.Query);
			var encoded = query.Get(parameterName);

			if (!string.IsNullOrEmpty(encoded))
			{
				var decoded = HttpUtility.UrlDecode(encoded);
				if (!decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase))
				{
					decoded = baseUrl.TrimEnd('/') + "/" + decoded.TrimStart('/');
				}

				return RemoveQueryString(decoded);
			}

			// Parameter not found — return the carrier URL without its query string.
			return carrierUrl.Split('?')[0];
		}
	}
}
