using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for DetectEncoding.FromBytes — byte-level encoding detection.
	/// Order: UTF-8 BOM → UTF-16 BE BOM → UTF-16 LE BOM → meta-charset regex
	/// in first 4096 bytes → Windows-1252 fallback.
	/// </summary>
	public class DetectEncodingTests
	{
		// On .NET Core / .NET 5+ the default encoding set excludes legacy
		// code pages like Windows-1252. The production app must register the
		// code-pages provider at startup for DetectEncoding.FromBytes's fallback
		// (Encoding.GetEncoding(1252)) to work; if it does not, that fallback
		// path will throw NotSupportedException — a latent runtime issue worth
		// addressing separately. For the tests below we register here so the
		// fallback behaviour can be asserted in isolation.
		static DetectEncodingTests()
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		}

		[Fact]
		public void DetectEncoding_Empty_ReturnsUtf8()
		{
			Assert.Equal(Encoding.UTF8.CodePage,
				DetectEncoding.FromBytes([]).CodePage);
		}

		[Fact]
		public void DetectEncoding_Utf8Bom_ReturnsUtf8()
		{
			byte[] bom = [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i'];
			Assert.Equal(Encoding.UTF8.CodePage,
				DetectEncoding.FromBytes(bom).CodePage);
		}

		[Fact]
		public void DetectEncoding_Utf16BeBom_ReturnsBigEndianUnicode()
		{
			byte[] bom = [0xFE, 0xFF, 0x00, 0x68];
			Assert.Equal(Encoding.BigEndianUnicode.CodePage,
				DetectEncoding.FromBytes(bom).CodePage);
		}

		[Fact]
		public void DetectEncoding_Utf16LeBom_ReturnsLittleEndianUnicode()
		{
			byte[] bom = [0xFF, 0xFE, 0x68, 0x00];
			Assert.Equal(Encoding.Unicode.CodePage,
				DetectEncoding.FromBytes(bom).CodePage);
		}

		[Fact]
		public void DetectEncoding_MetaCharset_Iso88591_Recognized()
		{
			var html = "<html><head><meta charset=\"iso-8859-1\"></head></html>";
			var bytes = Encoding.ASCII.GetBytes(html);
			var enc = DetectEncoding.FromBytes(bytes);
			Assert.Equal(28591, enc.CodePage); // ISO-8859-1
		}

		[Fact]
		public void DetectEncoding_MetaCharset_SingleQuoted_AlsoRecognized()
		{
			var html = "<html><head><meta charset='utf-8'></head></html>";
			var bytes = Encoding.ASCII.GetBytes(html);
			Assert.Equal(Encoding.UTF8.CodePage,
				DetectEncoding.FromBytes(bytes).CodePage);
		}

		[Fact]
		public void DetectEncoding_MetaCharset_Unquoted_AlsoRecognized()
		{
			var html = "<html><head><meta charset=utf-8 /></head></html>";
			var bytes = Encoding.ASCII.GetBytes(html);
			Assert.Equal(Encoding.UTF8.CodePage,
				DetectEncoding.FromBytes(bytes).CodePage);
		}

		// The http-equiv form is the meta-charset declaration seen most often in the
		// wild: <meta http-equiv="content-type" content="text/html; charset=..."/>.
		// The pattern matches charset= wherever it sits inside the meta tag, including
		// inside the content attribute value. Locks this real-world form.
		[Fact]
		public void DetectEncoding_MetaHttpEquiv_Utf8_Recognized()
		{
			var html = "<html><head><meta http-equiv=\"content-type\" content=\"text/html; charset=UTF-8\"/></head></html>";
			var bytes = Encoding.ASCII.GetBytes(html);
			Assert.Equal(Encoding.UTF8.CodePage,
				DetectEncoding.FromBytes(bytes).CodePage);
		}

		[Fact]
		public void DetectEncoding_MetaHttpEquiv_Iso88591_Recognized()
		{
			var html = "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=iso-8859-1\"></head></html>";
			var bytes = Encoding.ASCII.GetBytes(html);
			Assert.Equal(28591, DetectEncoding.FromBytes(bytes).CodePage); // ISO-8859-1
		}

		[Fact]
		public void DetectEncoding_MetaCharset_Invalid_FallsBackTo1252()
		{
			// "nosuchcoding" causes GetEncoding to throw, which is swallowed.
			var html = "<html><head><meta charset=\"nosuchcoding\"></head></html>";
			var bytes = Encoding.ASCII.GetBytes(html);
			Assert.Equal(1252, DetectEncoding.FromBytes(bytes).CodePage);
		}

		[Fact]
		public void DetectEncoding_NoBomNoMeta_FallsBackTo1252()
		{
			var bytes = Encoding.ASCII.GetBytes("<html><body>hello</body></html>");
			Assert.Equal(1252, DetectEncoding.FromBytes(bytes).CodePage);
		}
	}
}
