namespace Crawler.Urls
{
	using System;
	using System.Security.Cryptography;
	using System.Text;

	/// <summary>
	/// Derives the on-disk filename for a downloaded asset from its URL.
	/// The result is a load-bearing contract: it determines body/header sidecar
	/// pairing and cross-archive correlation, so the mapping is deliberately
	/// deterministic. <see cref="Hash"/> is the SHA-256 helper backing the
	/// generated name and is internal — exercised directly by the tests.
	/// </summary>
	public static class Naming
	{
		public static string GenerateFileName(string url)
		{
			var uri = new Uri(url);

			// [KEEP] Security boundary — a download name must be a SINGLE path
			// segment that cannot escape the capture root. Hash the full
			// AbsoluteUri (query included, so distinct query variants stay distinct
			// and never collide onto one file) and append the path-derived
			// extension. A query-bearing URL previously fell through to
			// uri.AbsolutePath — a rooted, separator-bearing path that Path.Combine
			// resolved OUTSIDE the download dir, which PathContainmentCheck then had
			// to block, silently dropping legitimate query-bearing assets
			// (cache-busted fonts, counter beacons). Hashing unconditionally removes
			// the escape at the source; containment remains the backstop.
			var extension = ".htm";

			foreach (var item in uri.Segments)
			{
				if (item.Contains('.'))
				{
					extension = item;
				}
			}

			return Hash(uri.AbsoluteUri) + extension;
		}

		internal static string Hash(string filename)
		{
			if (string.IsNullOrEmpty(filename))
			{
				throw new ArgumentException("Filename cannot be null or empty.", nameof(filename));
			}

			byte[] bytes = Encoding.UTF8.GetBytes(filename);
			byte[] hashBytes = SHA256.HashData(bytes);
			StringBuilder hashString = new();
			foreach (byte b in hashBytes)
			{
				hashString.Append(b.ToString("x2"));
			}
			return hashString.ToString();
		}
	}
}
