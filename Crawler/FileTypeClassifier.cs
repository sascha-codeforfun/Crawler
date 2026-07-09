namespace Crawler
{
	using System.Text;

	/// <summary>
	/// Decides whether a downloaded file is the type it claims to be, by cross-checking
	/// three identity signals: the URL/file extension, the server's Content-Type header,
	/// and a byte-level sniff of the leading bytes. Covers HTML, PDF, and raster images.
	/// The Classify* methods are pure: they take the three pre-computed signals plus the
	/// relevant Unverified*Policy and return a verdict struct (TreatAs.. / IsMismatch /
	/// Reason); callers do the sniffing and pass the results in. Extracted from Tools.
	/// </summary>
	public static class FileTypeClassifier
	{
		// NOTE (provisional home): UnverifiedExtension is a download/settle lifecycle
		// constant parked here — FileTypeClassifier resolves it — until the root-file
		// cleanup gives the download/settle constants a permanent home. Revisit then.
		/// <summary>The provisional extension a download is saved under before settle.</summary>
		public const string UnverifiedExtension = ".unverified";

		/// <summary>
		/// True when an HTTP response's media type indicates HTML content that
		/// should be parsed for link extraction. Anchor hrefs frequently point
		/// at PDFs, archives, images, and other non-HTML assets — those are
		/// saved for later pipeline stages but contribute no anchor links.
		/// Previously every download was decoded and parsed regardless of type,
		/// producing spurious encoding errors in 01-crawler.log on PDFs and
		/// wasted CPU on every non-HTML download.
		///
		/// Accepts text/html and application/xhtml+xml (HTML5+XML sites).
		/// Null/empty media type is treated as non-HTML — when the server
		/// hasn't declared a type, don't assume the bytes are parseable.
		/// </summary>
		public static bool IsHtmlContentType(string? mediaType)
		{
			if (string.IsNullOrEmpty(mediaType))
			{
				return false;
			}

			return mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
				|| mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// True when the media type declares a PDF (application/pdf). Counterpart to
		/// <see cref="IsHtmlContentType"/>. Null/empty is treated as not-PDF — an
		/// undeclared type is not assumed to be a PDF.
		/// </summary>
		public static bool IsPdfContentType(string? mediaType)
		{
			if (string.IsNullOrEmpty(mediaType))
			{
				return false;
			}

			return mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
		}

		// ── Content-Type / page-identification helpers ────────────────
		//
		// The settle phase (Step_SettleExtensions) names a downloaded page file by
		// what it actually is, not by the requested URL's extension. Three signals
		// feed the decision: the requested URL extension, the response Content-Type
		// header (persisted in 00-crawler.log), and a byte sniff of the saved file's
		// head. UnverifiedHtmlPolicy governs what happens when they disagree.
		// See scoping-contenttype-page-identification.md for the full design.

		/// <summary>
		/// Heuristic byte sniff: does <paramref name="head"/> (the first ~1 KB of a
		/// saved file) look like HTML? Scans the decoded head for a strong HTML opener
		/// (<c>&lt;!doctype html</c>, <c>&lt;html</c>, <c>&lt;head</c>, <c>&lt;body</c>)
		/// appearing ANYWHERE in the window — not only at the very start — because real
		/// pages may emit leading content before the doctype (e.g. a server-side error
		/// banner prepended above an otherwise complete document). This is the same idea
		/// as a browser's MIME sniff. It is a heuristic, not proof: plain text that
		/// happens to contain literal "&lt;html" would read as HTML — such ambiguous
		/// cases are surfaced in log #23. Binary content (images, PDFs) effectively
		/// never contains these ASCII tag strings in its first 1 KB.
		/// </summary>
		public static bool LooksLikeHtml(byte[] head)
		{
			if (head is null || head.Length == 0)
			{
				return false;
			}

			// Decode defensively as UTF-8 (HTML markers are ASCII, so encoding nuance
			// elsewhere in the window does not matter for detection).
			var text = Encoding.UTF8.GetString(head);

			// Scan the whole sniffed window for a strong HTML opener. "Contains" rather
			// than "StartsWith" so leading content (BOM, whitespace, comments, or an
			// error/notice block prepended before the doctype) does not hide a real
			// document. Bare <meta> is intentionally excluded from the anywhere-match —
			// it is too weak a signal on its own.
			return text.Contains("<!doctype html", StringComparison.OrdinalIgnoreCase)
				|| text.Contains("<html", StringComparison.OrdinalIgnoreCase)
				|| text.Contains("<head", StringComparison.OrdinalIgnoreCase)
				|| text.Contains("<body", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// The outcome of classifying one downloaded file during settle.
		/// <see cref="TreatAsHtml"/> drives the final extension (HTML → the configured
		/// page extension; otherwise the file keeps <see cref="UnverifiedExtension"/>).
		/// <see cref="IsMismatch"/> is true when the three signals disagreed and the
		/// case warrants a log #23 row.
		/// </summary>
		public readonly record struct PageClassification(bool TreatAsHtml, bool IsMismatch, string Reason);

		/// <summary>
		/// Heuristic byte sniff: does <paramref name="head"/> (the first bytes of a
		/// saved file) look like a PDF? A PDF file begins with the magic "%PDF-".
		/// Unlike the HTML sniff this is a strict leading-magic check, not a
		/// scan-anywhere, because the PDF format mandates the marker at offset 0
		/// (a leading BOM or whitespace before "%PDF-" is itself malformed). A short
		/// leading-byte tolerance is allowed only for an optional UTF-8 BOM, which
		/// some misbehaving servers prepend.
		/// </summary>
		public static bool LooksLikePdf(byte[] head)
		{
			if (head is null || head.Length < 5)
			{
				return false;
			}

			int i = 0;
			// Tolerate a single leading UTF-8 BOM (EF BB BF) — malformed for a PDF,
			// but seen in practice; the magic still follows.
			if (head.Length >= 8 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
			{
				i = 3;
			}

			// "%PDF-" = 0x25 0x50 0x44 0x46 0x2D
			return i + 5 <= head.Length
				&& head[i] == 0x25  // %
				&& head[i + 1] == 0x50  // P
				&& head[i + 2] == 0x44  // D
				&& head[i + 3] == 0x46  // F
				&& head[i + 4] == 0x2D; // -
		}

		/// <summary>
		/// The outcome of classifying one downloaded file during settle as a PDF.
		/// PDF counterpart to <see cref="PageClassification"/>. <see cref="TreatAsPdf"/>
		/// drives whether the file is handed to the PDF pipeline; <see cref="IsMismatch"/>
		/// is true when the three signals disagreed (a finding-worthy case, in EITHER
		/// direction: declared-PDF-but-not, or is-PDF-but-undeclared).
		/// </summary>
		public readonly record struct PdfClassification(bool TreatAsPdf, bool IsMismatch, string Reason);

		// ── Image identification helpers ──────────────────────────────
		//
		// Mirror the PDF helpers above. AssetQuality classifies a downloaded asset
		// as an image (and which image-format checks apply) from three signals:
		// the requested URL extension, the Content-Type header (from the sidecar /
		// 00-crawler.log), and a leading-magic byte sniff. UnverifiedImagePolicy
		// governs disagreement, exactly as UnverifiedPdfPolicy does for PDFs.

		/// <summary>
		/// Does the media type name a raster image format AssetQuality inspects
		/// (JPEG, PNG, GIF, WebP)? Null/empty is treated as not-image. SVG is
		/// intentionally excluded — it is XML, not a raster asset, and carries no
		/// EXIF / pixel-dimension header of the kind these checks target.
		/// </summary>
		public static bool IsImageContentType(string? mediaType)
		{
			if (string.IsNullOrEmpty(mediaType))
			{
				return false;
			}

			var m = mediaType.Trim();
			return m.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
				|| m.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)
				|| m.Equals("image/png", StringComparison.OrdinalIgnoreCase)
				|| m.Equals("image/gif", StringComparison.OrdinalIgnoreCase)
				|| m.Equals("image/webp", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Does the requested URL extension imply one of the raster image formats
		/// AssetQuality inspects?
		/// </summary>
		public static bool IsImageExtension(string? path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return false;
			}

			var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
			return ext is "jpg" or "jpeg" or "png" or "gif" or "webp";
		}

		/// <summary>
		/// Do the leading bytes look like one of the raster image formats we
		/// inspect? Strict leading-magic check per format:
		///   JPEG  FF D8 FF
		///   PNG   89 50 4E 47 0D 0A 1A 0A
		///   GIF   "GIF87a" / "GIF89a"
		///   WebP  "RIFF" .... "WEBP"
		/// </summary>
		public static bool LooksLikeImage(byte[] head)
		{
			if (head is null || head.Length < 4)
			{
				return false;
			}

			// JPEG: FF D8 FF
			if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
			{
				return true;
			}

			// PNG: 89 50 4E 47 0D 0A 1A 0A
			if (head.Length >= 8
				&& head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
				&& head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
			{
				return true;
			}

			// GIF: "GIF87a" or "GIF89a"
			if (head.Length >= 6
				&& head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F'
				&& head[3] == (byte)'8' && (head[4] == (byte)'7' || head[4] == (byte)'9')
				&& head[5] == (byte)'a')
			{
				return true;
			}

			// WebP: "RIFF" at 0, "WEBP" at 8
			if (head.Length >= 12
				&& head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
				&& head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P')
			{
				return true;
			}

			return false;
		}

		/// <summary>
		/// Returns the canonical file extension (no dot) for the image format the
		/// leading bytes sniff as — "jpg", "png", "gif", "webp" — or null if the
		/// bytes are not a recognised image. Used by the re-settle gate to give a
		/// promoted .unverified image its correct extension.
		/// </summary>
		public static string? SniffedImageExtension(byte[] head)
		{
			if (head is null)
			{
				return null;
			}

			if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
			{
				return "jpg";
			}

			if (head.Length >= 8
				&& head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
				&& head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
			{
				return "png";
			}

			if (head.Length >= 6
				&& head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F'
				&& head[3] == (byte)'8' && (head[4] == (byte)'7' || head[4] == (byte)'9')
				&& head[5] == (byte)'a')
			{
				return "gif";
			}

			if (head.Length >= 12
				&& head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
				&& head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P')
			{
				return "webp";
			}

			return null;
		}

		/// <summary>
		/// The outcome of classifying a downloaded asset as an image. Image
		/// counterpart to <see cref="PdfClassification"/>. <see cref="TreatAsImage"/>
		/// drives whether AssetQuality runs image-format checks on the file;
		/// <see cref="IsMismatch"/> is true when the three signals disagreed (a
		/// finding-worthy case in EITHER direction).
		/// </summary>
		public readonly record struct ImageClassification(bool TreatAsImage, bool IsMismatch, string Reason);

		/// <summary>
		/// Pure image classification from three signals, mirroring
		/// <see cref="ClassifyPdf"/>. Agreement → no mismatch, no policy needed.
		/// Disagreement → policy decides AND it is a mismatch (finding-worthy),
		/// whichever way it resolves. No I/O — caller supplies the sniff result.
		/// </summary>
		public static ImageClassification ClassifyImage(
			UnverifiedImagePolicy policy,
			bool requestedExtIsImage,
			bool headerIsImage,
			bool sniffIsImage)
		{
			bool allImage = requestedExtIsImage && headerIsImage && sniffIsImage;
			bool noneImage = !requestedExtIsImage && !headerIsImage && !sniffIsImage;
			if (allImage)
			{
				return new ImageClassification(true, false, "all signals agree: image");
			}

			if (noneImage)
			{
				return new ImageClassification(false, false, "all signals agree: not image");
			}

			var reason = $"signals disagree (ext={requestedExtIsImage}, header={headerIsImage}, sniff={sniffIsImage}); policy={policy}";
			bool treatAsImage = policy switch
			{
				UnverifiedImagePolicy.TrustByteSniff => sniffIsImage,
				UnverifiedImagePolicy.TrustContentType => headerIsImage,
				UnverifiedImagePolicy.Quarantine => false,
				UnverifiedImagePolicy.AnalyseBlindly => true,
				_ => sniffIsImage // defensive: default
			};
			return new ImageClassification(treatAsImage, true, reason);
		}

		/// <summary>
		/// Pure PDF classification from three signals, mirroring <see cref="ClassifyPage"/>.
		/// Agreement (all three say PDF, or all three say not-PDF) → no mismatch, no
		/// policy needed. Disagreement → policy decides AND it is always a mismatch
		/// (finding-worthy), regardless of which way the policy resolves. Symmetric:
		/// a body that claims PDF but is not, and a body that is a PDF but was not
		/// declared, are both mismatches.
		/// </summary>
		public static PdfClassification ClassifyPdf(
			UnverifiedPdfPolicy policy,
			bool requestedExtIsPdf,
			bool headerIsPdf,
			bool sniffIsPdf)
		{
			bool allPdf = requestedExtIsPdf && headerIsPdf && sniffIsPdf;
			bool nonePdf = !requestedExtIsPdf && !headerIsPdf && !sniffIsPdf;
			if (allPdf)
			{
				return new PdfClassification(true, false, "all signals agree: PDF");
			}

			if (nonePdf)
			{
				return new PdfClassification(false, false, "all signals agree: not PDF");
			}

			var reason = $"signals disagree (ext={requestedExtIsPdf}, header={headerIsPdf}, sniff={sniffIsPdf}); policy={policy}";
			bool treatAsPdf = policy switch
			{
				UnverifiedPdfPolicy.TrustByteSniff => sniffIsPdf,
				UnverifiedPdfPolicy.TrustContentType => headerIsPdf,
				UnverifiedPdfPolicy.Quarantine => false,
				UnverifiedPdfPolicy.AnalyseBlindly => true,
				_ => sniffIsPdf // defensive: default
			};
			return new PdfClassification(treatAsPdf, true, reason);
		}

		/// <summary>
		/// Pure classification: given the three signals and the policy, decide whether
		/// a file is treated as HTML, whether the signals disagreed (→ #23), and a
		/// short human-readable reason for the #23 row. No I/O — the caller supplies
		/// the sniff result so this stays unit-testable.
		/// </summary>
		/// <param name="requestedExtIsHtml">Did the requested URL's extension imply HTML
		/// (.html/.htm/.htmlx, or the provisional .unverified for extensionless URLs)?</param>
		/// <param name="headerIsHtml">Did the Content-Type header say HTML?</param>
		/// <param name="sniffIsHtml">Did the byte sniff say HTML?</param>
		public static PageClassification ClassifyPage(
			UnverifiedHtmlPolicy policy,
			bool requestedExtIsHtml,
			bool headerIsHtml,
			bool sniffIsHtml)
		{
			// Agreement: all three say HTML, or all three say not-HTML → no mismatch,
			// no policy needed.
			bool allHtml = requestedExtIsHtml && headerIsHtml && sniffIsHtml;
			bool noneHtml = !requestedExtIsHtml && !headerIsHtml && !sniffIsHtml;
			if (allHtml)
			{
				return new PageClassification(true, false, "all signals agree: HTML");
			}

			if (noneHtml)
			{
				return new PageClassification(false, false, "all signals agree: not HTML");
			}

			// Disagreement → policy decides. Always a mismatch (→ #23).
			var reason = $"signals disagree (ext={requestedExtIsHtml}, header={headerIsHtml}, sniff={sniffIsHtml}); policy={policy}";
			bool treatAsHtml = policy switch
			{
				UnverifiedHtmlPolicy.TrustByteSniff => sniffIsHtml,
				UnverifiedHtmlPolicy.TrustContentType => headerIsHtml,
				UnverifiedHtmlPolicy.Quarantine => false,
				UnverifiedHtmlPolicy.AnalyseBlindly => true,
				_ => sniffIsHtml // defensive: treat as default
			};
			return new PageClassification(treatAsHtml, true, reason);
		}
	}
}
