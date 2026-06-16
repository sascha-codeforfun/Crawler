using System.Globalization;
using System.Text;
using Crawler.AssetMetadata;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for the native Crawler.AssetMetadata readers — the EXIF / IPTC / XMP
	/// parsers and the PNG / WebP / JPEG container dispatch. These parse raw bytes,
	/// so fixtures are built in-code from the specs (TIFF/IFD, Photoshop 8BIM/IIM,
	/// PNG chunks, RIFF chunks, XMP packets) rather than loaded from image files —
	/// keeping the suite self-contained and every fixture self-documenting.
	///
	/// The reader classes are internal; these tests reach them through the public
	/// <see cref="AssetMetadataReader"/> / <see cref="AssetMetadataReportRenderer"/>
	/// surface and, for branch-level cases, the internal parser entry points
	/// (visible via the project's existing InternalsVisibleTo to Crawler.Tests).
	/// </summary>
	public class AssetMetadataReaderTests
	{
		// Host-style per-value sanitiser (strips delimiters / control chars).
		private static string San(string s) =>
			new string((s ?? "").Where(c => c != '|' && c != '\r' && c != '\n' && !char.IsControl(c)).ToArray()).Trim();

		// ════════════════════════ EXIF / TIFF ════════════════════════

		[Fact]
		public void Exif_LittleEndian_ReadsAsciiTags()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "Canon"), AssetMetadataFixtures.Ascii(0x0110, "EOS 5D") });
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.True(r.HasExif);
			Assert.Equal("Canon", r.Make);
			Assert.Equal("EOS 5D", r.Model);
		}

		[Fact]
		public void Exif_BigEndian_ReadsAsciiTags()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: false, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "NIKON") });
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Equal("NIKON", r.Make);
		}

		[Fact]
		public void Exif_DualNullCopyright_SplitWithSlash()
		{
			// EXIF Copyright (0x8298) holds photographer\0editor\0 — surfaced as "A / B".
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.AsciiRaw(0x8298, "Photographer\0Editor\0") });
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Equal("Photographer / Editor", r.Copyright);
		}

		[Fact]
		public void Exif_ShortArray_JoinedDisplay()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Short(0x0102, 8, 8, 8) }); // BitsPerSample
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Equal("8, 8, 8", r.Find(ExifIfd.Primary, 0x0102)!.Display);
		}

		[Fact]
		public void Exif_Rational_DisplayedAsFraction()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Rational(0x011A, (72u, 1u)) }); // XResolution
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Equal("72/1", r.Find(ExifIfd.Primary, 0x011A)!.Display);
		}

		[Fact]
		public void Exif_SRational_Negative()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.SRational(0x9204, (-3, 1)) }); // ExposureBias
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Equal("-3/1", r.Find(ExifIfd.Primary, 0x9204)!.Display);
		}

		[Fact]
		public void Exif_Float_And_Double()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Float(0x9000, 1.5f), AssetMetadataFixtures.Double(0x9001, 2.25) });
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Equal("1.5", r.Find(ExifIfd.Primary, 0x9000)!.Display);
			Assert.Equal("2.25", r.Find(ExifIfd.Primary, 0x9001)!.Display);
		}

		[Fact]
		public void Exif_Undefined_PrintableShownAsAscii()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Undef(0x9000, Encoding.ASCII.GetBytes("0231")) });
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Equal("0231", r.Find(ExifIfd.Primary, 0x9000)!.Display);
		}

		[Fact]
		public void Exif_Undefined_BinaryShownAsByteCount()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Undef(0x927C, new byte[] { 0x00, 0x01, 0xFF, 0x80, 0x00 }) });
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Equal("<5 bytes>", r.Find(ExifIfd.Primary, 0x927C)!.Display);
		}

		[Fact]
		public void Exif_ExifSubIfd_DateTimeOriginalResolved()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true,
				ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "Sony") },
				exif: new[] { AssetMetadataFixtures.Ascii(0x9003, "2019:09:08 17:37:44"), AssetMetadataFixtures.Ascii(0xA431, "SN-12345") });
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Equal("2019:09:08 17:37:44", r.DateTimeOriginal);
			Assert.Equal("SN-12345", r.BodySerialNumber);
		}

		[Fact]
		public void Exif_Gps_NorthEast_PositiveDegrees()
		{
			var gps = new[]
			{
				AssetMetadataFixtures.Ascii(0x0001, "N"), AssetMetadataFixtures.Rational(0x0002, (37u,1u),(46u,1u),(2988u,100u)),
				AssetMetadataFixtures.Ascii(0x0003, "W"), AssetMetadataFixtures.Rational(0x0004, (122u,1u),(25u,1u),(168u,100u)),
			};
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "X") }, gps: gps);
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			var loc = r.Location!;
			Assert.NotNull(loc);
			Assert.True(loc.Latitude > 37.7 && loc.Latitude < 37.8);     // N => positive
			Assert.True(loc.Longitude < -122.4 && loc.Longitude > -122.5); // W => negative
		}

		[Fact]
		public void Exif_Gps_SouthGivesNegativeLatitude()
		{
			var gps = new[]
			{
				AssetMetadataFixtures.Ascii(0x0001, "S"), AssetMetadataFixtures.Rational(0x0002, (33u,1u),(0u,1u),(0u,1u)),
				AssetMetadataFixtures.Ascii(0x0003, "E"), AssetMetadataFixtures.Rational(0x0004, (151u,1u),(0u,1u),(0u,1u)),
			};
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "X") }, gps: gps);
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Equal(-33.0, r.Location!.Latitude, 3);
			Assert.Equal(151.0, r.Location!.Longitude, 3);
		}

		[Fact]
		public void Exif_Gps_AltitudeBelowSeaLevel_Negative()
		{
			var gps = new[]
			{
				AssetMetadataFixtures.Ascii(0x0001, "N"), AssetMetadataFixtures.Rational(0x0002, (1u,1u),(0u,1u),(0u,1u)),
				AssetMetadataFixtures.Ascii(0x0003, "E"), AssetMetadataFixtures.Rational(0x0004, (1u,1u),(0u,1u),(0u,1u)),
				AssetMetadataFixtures.Byte(0x0005, 1),  AssetMetadataFixtures.Rational(0x0006, (100u,1u)), // ref 1 = below sea level
			};
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "X") }, gps: gps);
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Equal(-100.0, r.Location!.AltitudeMeters!.Value, 3);
		}

		[Fact]
		public void Exif_Gps_MissingRef_NoLocation()
		{
			// Latitude present but no ref / no longitude => cannot resolve.
			var gps = new[] { AssetMetadataFixtures.Rational(0x0002, (10u,1u),(0u,1u),(0u,1u)) };
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "X") }, gps: gps);
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Null(r.Location);
		}

		[Fact]
		public void Exif_ThumbnailIfd_TagsTaggedThumbnail()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true,
				ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "X") },
				ifd1: new[] { AssetMetadataFixtures.Short(0x0103, 6) }); // Compression in thumbnail IFD
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.NotNull(r.Find(ExifIfd.Thumbnail, 0x0103));
		}

		[Fact]
		public void Exif_IfdCycle_DoesNotHang()
		{
			// nextIFD points back to IFD0; the cycle guard must stop and warn.
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "X") }, cycleIfd1: true);
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Equal("X", r.Make); // parsed once, no infinite loop
		}

		[Fact]
		public void Exif_BadMagic_NoTagsButNoThrow()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "X") });
			tiff[2] = 0xFF; tiff[3] = 0xFF; // corrupt the "42" magic
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Empty(r.Tags);
		}

		[Fact]
		public void Exif_UnknownByteOrder_Warns()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "X") });
			tiff[0] = (byte)'Z'; tiff[1] = (byte)'Z';
			var r = new ExifResult();
			ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
			Assert.Empty(r.Tags);
			Assert.NotEmpty(r.Warnings);
		}

		[Fact]
		public void Exif_TooSmallBlob_Warns()
		{
			var r = new ExifResult();
			ExifReader.ParseExifBlob(new byte[] { 0x49, 0x49 }, 0, 2, r);
			Assert.NotEmpty(r.Warnings);
		}

		[Fact]
		public void Exif_BlobWithExifHeaderPrefix_Stripped()
		{
			// ParseExifBlob tolerates a leading "Exif\0\0" (JPEG-style) on a raw blob.
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "X") });
			var withHeader = AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("Exif\0\0"), tiff);
			var r = new ExifResult();
			ExifReader.ParseExifBlob(withHeader, 0, withHeader.Length, r);
			Assert.Equal("X", r.Make);
		}

		// ════════════════════════ IPTC / IIM ════════════════════════

		[Fact]
		public void Iptc_NoIrb_Warns()
		{
			var res = IptcReader.ParseIrb(Encoding.ASCII.GetBytes("not a resource block"));
			Assert.False(res.Present);
			Assert.NotEmpty(res.Warnings);
		}

		[Fact]
		public void Iptc_NamedDatasets_Parsed()
		{
			var irb = AssetMetadataFixtures.Iptc8bim(
				(2, 116, Encoding.UTF8.GetBytes("CARSTEN HEIDMANN")),
				(2, 80, Encoding.UTF8.GetBytes("Carsten Heidmann")),
				(2, 110, Encoding.UTF8.GetBytes("Agency")),
				(2, 115, Encoding.UTF8.GetBytes("Wire")));
			var res = IptcReader.ParseIrb(irb);
			Assert.True(res.Present);
			Assert.Equal("CARSTEN HEIDMANN", res.CopyrightNotice);
			Assert.Equal("Carsten Heidmann", res.Byline);
			Assert.Equal("Agency", res.Credit);
			Assert.Equal("Wire", res.Source);
		}

		[Fact]
		public void Iptc_Utf8CharsetEscape_DecodesUtf8()
		{
			var irb = AssetMetadataFixtures.Iptc8bim(
				(1, 90, new byte[] { 0x1B, 0x25, 0x47 }),               // ESC % G => UTF-8
				(2, 80, Encoding.UTF8.GetBytes("José Photographer")));
			var res = IptcReader.ParseIrb(irb);
			Assert.Equal("José Photographer", res.Byline);
		}

		[Fact]
		public void Iptc_NoCharset_DecodesLatin1()
		{
			// 0xE9 = 'é' in Latin-1; without the UTF-8 marker we must read Latin-1.
			var irb = AssetMetadataFixtures.Iptc8bim((2, 80, new byte[] { (byte)'J', (byte)'o', (byte)'s', 0xE9 }));
			var res = IptcReader.ParseIrb(irb);
			Assert.Equal("José", res.Byline);
		}

		[Fact]
		public void Iptc_ExtendedLength_ReadsLongValue()
		{
			// A value longer than 0x7FFF would need extended length; use the extended
			// form explicitly with a moderate payload to exercise the branch.
			var big = new string('A', 200);
			var irb = AssetMetadataFixtures.Iptc8bimExtended(2, 120, Encoding.ASCII.GetBytes(big));
			var res = IptcReader.ParseIrb(irb);
			Assert.Equal(big, res.Caption);
		}

		[Fact]
		public void Iptc_UnknownDataset_NumericName()
		{
			var irb = AssetMetadataFixtures.Iptc8bim((2, 199, Encoding.ASCII.GetBytes("x")));
			var res = IptcReader.ParseIrb(irb);
			Assert.Contains(res.Fields, f => f.Name == "2:199" && f.Value == "x");
		}

		[Fact]
		public void Iptc_NewLabels_TimeCreatedAndTransmissionRef()
		{
			var irb = AssetMetadataFixtures.Iptc8bim(
				(2, 60, Encoding.ASCII.GetBytes("173744+0000")),
				(2, 103, Encoding.ASCII.GetBytes("pwme2e")));
			var res = IptcReader.ParseIrb(irb);
			Assert.Contains(res.Fields, f => f.Name == "TimeCreated");
			Assert.Contains(res.Fields, f => f.Name == "OriginalTransmissionReference");
		}

		[Fact]
		public void Iptc_NonIptcResourceId_Ignored()
		{
			// A resource block that is not 0x0404 must be skipped without yielding fields.
			var irb = AssetMetadataFixtures.Bim(0x0405, new byte[] { 1, 2, 3, 4 });
			var res = IptcReader.ParseIrb(irb);
			Assert.False(res.Present);
		}

		// ════════════════════════ XMP ════════════════════════

		[Fact]
		public void Xmp_DcRightsAltLi_Collected()
		{
			var xml = AssetMetadataFixtures.XmpPacket(
				"<dc:rights><rdf:Alt><rdf:li xml:lang=\"x-default\">© 2024 Holder</rdf:li></rdf:Alt></dc:rights>");
			var res = XmpReader.ParseXmlPacket(Encoding.UTF8.GetBytes(xml));
			Assert.Contains("© 2024 Holder", res.Rights);
			Assert.True(res.Present);
		}

		[Fact]
		public void Xmp_DcCreatorSeq_And_SubjectBag()
		{
			var xml = AssetMetadataFixtures.XmpPacket(
				"<dc:creator><rdf:Seq><rdf:li>Alice</rdf:li></rdf:Seq></dc:creator>" +
				"<dc:subject><rdf:Bag><rdf:li>travel</rdf:li><rdf:li>city</rdf:li></rdf:Bag></dc:subject>");
			var res = XmpReader.ParseXmlPacket(Encoding.UTF8.GetBytes(xml));
			Assert.Contains("Alice", res.Creators);
			Assert.Equal(new[] { "travel", "city" }, res.Keywords);
		}

		[Fact]
		public void Xmp_RightsAttributeForm_Marked()
		{
			// xmpRights:Marked carried as an attribute on rdf:Description.
			var xml = AssetMetadataFixtures.XmpPacketAttr("xmlns:xmpRights=\"http://ns.adobe.com/xap/1.0/rights/\" xmpRights:Marked=\"True\"");
			var res = XmpReader.ParseXmlPacket(Encoding.UTF8.GetBytes(xml));
			Assert.Equal("True", res.Marked);
		}

		[Fact]
		public void Xmp_PhotoshopAndWebStatement()
		{
			var xml = AssetMetadataFixtures.XmpPacket(
				"<photoshop:Credit>Stock Co</photoshop:Credit>" +
				"<xmpRights:WebStatement>https://example.com/lic</xmpRights:WebStatement>");
			var res = XmpReader.ParseXmlPacket(Encoding.UTF8.GetBytes(xml));
			Assert.Equal("Stock Co", res.Credit);
			Assert.Equal("https://example.com/lic", res.WebStatement);
		}

		[Fact]
		public void Xmp_DuplicateValues_Deduped()
		{
			var xml = AssetMetadataFixtures.XmpPacket(
				"<dc:subject><rdf:Bag><rdf:li>dup</rdf:li><rdf:li>dup</rdf:li></rdf:Bag></dc:subject>");
			var res = XmpReader.ParseXmlPacket(Encoding.UTF8.GetBytes(xml));
			Assert.Single(res.Keywords);
		}

		[Fact]
		public void Xmp_NoXml_Warns()
		{
			var res = XmpReader.ParseXmlPacket(Encoding.UTF8.GetBytes("no angle brackets here"));
			Assert.False(res.Present);
			Assert.NotEmpty(res.Warnings);
		}

		[Fact]
		public void Xmp_DtdBomb_Rejected()
		{
			// Entity-expansion bomb: DtdProcessing=Prohibit must refuse it (no expansion).
			var bomb =
				"<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"AAAAAAAAAA\">" +
				"<!ENTITY b \"&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;\">]>" +
				"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
				"<rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:rights>&c;</dc:rights>" +
				"</rdf:Description></rdf:RDF></x:xmpmeta>";
			var res = XmpReader.ParseXmlPacket(Encoding.UTF8.GetBytes(bomb));
			Assert.False(res.Present);
			Assert.NotEmpty(res.Warnings);
		}

		[Fact]
		public void Xmp_Oversize_Skipped()
		{
			var big = "<dc:rights>" + new string('x', 1_100_000) + "</dc:rights>";
			var res = XmpReader.ParseXmlPacket(Encoding.UTF8.GetBytes(AssetMetadataFixtures.XmpPacket(big)));
			Assert.False(res.Present);
			Assert.NotEmpty(res.Warnings);
		}

		[Fact]
		public void Xmp_LeadingXpacketJunk_Trimmed()
		{
			var xml = "junk before <?xpacket begin=\"\"?>" +
				AssetMetadataFixtures.XmpPacket("<dc:title><rdf:Alt><rdf:li>T</rdf:li></rdf:Alt></dc:title>");
			var res = XmpReader.ParseXmlPacket(Encoding.UTF8.GetBytes(xml));
			Assert.Contains("T", res.Titles);
		}

		[Fact]
		public void Xmp_JpegSegment_StripsHeaderSignature()
		{
			var packet = AssetMetadataFixtures.XmpPacket("<dc:title><rdf:Alt><rdf:li>JT</rdf:li></rdf:Alt></dc:title>");
			var payload = AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0"), Encoding.UTF8.GetBytes(packet));
			var res = XmpReader.ParseJpegSegment(payload, 0, payload.Length);
			Assert.Contains("JT", res.Titles);
		}

		[Fact]
		public void Xmp_JpegSegment_TooShort_Warns()
		{
			var res = XmpReader.ParseJpegSegment(Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0"), 0, 5);
			Assert.False(res.Present);
		}

		// ════════════════════════ PNG ════════════════════════

		[Fact]
		public void Png_IsPng_DetectsSignature()
		{
			Assert.True(PngReader.IsPng(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
			Assert.False(PngReader.IsPng(new byte[] { 0xFF, 0xD8, 0xFF }));
		}

		[Fact]
		public void Png_EXifChunk_ParsesExif()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "PngCam") });
			var png = AssetMetadataFixtures.Png(("eXIf", tiff));
			var exif = new ExifResult();
			PngReader.Read(png, exif, out _, new List<string>());
			Assert.Equal("PngCam", exif.Make);
		}

		[Fact]
		public void Png_ITxtXmp_ParsesXmp()
		{
			var packet = AssetMetadataFixtures.XmpPacket("<dc:rights><rdf:Alt><rdf:li>PNG ©</rdf:li></rdf:Alt></dc:rights>");
			var png = AssetMetadataFixtures.Png(("iTXt", AssetMetadataFixtures.ITxt("XML:com.adobe.xmp", packet, compressed: false)));
			PngReader.Read(png, new ExifResult(), out var xmp, new List<string>());
			Assert.Contains("PNG ©", xmp.Rights);
		}

		[Fact]
		public void Png_ITxtCompressed_Skipped()
		{
			var png = AssetMetadataFixtures.Png(("iTXt", AssetMetadataFixtures.ITxt("XML:com.adobe.xmp", "<x/>", compressed: true)));
			var warns = new List<string>();
			PngReader.Read(png, new ExifResult(), out var xmp, warns);
			Assert.False(xmp.Present);
			Assert.Contains(warns, w => w.Contains("compressed"));
		}

		[Fact]
		public void Png_ITxtOtherKeyword_Ignored()
		{
			var png = AssetMetadataFixtures.Png(("iTXt", AssetMetadataFixtures.ITxt("Comment", "hello", compressed: false)));
			PngReader.Read(png, new ExifResult(), out var xmp, new List<string>());
			Assert.False(xmp.Present);
		}

		[Fact]
		public void Png_ChunkLengthOutOfBounds_Warns()
		{
			var png = AssetMetadataFixtures.Png(("eXIf", new byte[] { 1, 2 }));
			// Corrupt the eXIf length field (first chunk, right after the 8-byte sig) to overflow.
			png[8] = 0x7F; png[9] = 0xFF; png[10] = 0xFF; png[11] = 0xFF;
			var warns = new List<string>();
			PngReader.Read(png, new ExifResult(), out _, warns);
			Assert.NotEmpty(warns);
		}

		// ════════════════════════ WebP ════════════════════════

		[Fact]
		public void Webp_IsWebp_DetectsRiff()
		{
			Assert.True(WebpReader.IsWebp(AssetMetadataFixtures.Webp(("VP8 ", new byte[] { 0 }))));
			Assert.False(WebpReader.IsWebp(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }));
		}

		[Fact]
		public void Webp_ExifChunk_NoPrefix_Parsed()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "WebpCam") });
			var webp = AssetMetadataFixtures.Webp(("EXIF", tiff));
			var exif = new ExifResult();
			WebpReader.Read(webp, exif, out _, new List<string>());
			Assert.Equal("WebpCam", exif.Make);
		}

		[Fact]
		public void Webp_ExifChunk_WithExifPrefix_Parsed()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "WebpCam2") });
			var webp = AssetMetadataFixtures.Webp(("EXIF", AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("Exif\0\0"), tiff)));
			var exif = new ExifResult();
			WebpReader.Read(webp, exif, out _, new List<string>());
			Assert.Equal("WebpCam2", exif.Make);
		}

		[Fact]
		public void Webp_XmpChunk_Parsed()
		{
			var packet = AssetMetadataFixtures.XmpPacket("<dc:creator><rdf:Seq><rdf:li>Bob</rdf:li></rdf:Seq></dc:creator>");
			var webp = AssetMetadataFixtures.Webp(("XMP ", Encoding.UTF8.GetBytes(packet)));
			WebpReader.Read(webp, new ExifResult(), out var xmp, new List<string>());
			Assert.Contains("Bob", xmp.Creators);
		}

		[Fact]
		public void Webp_OddSizedChunk_PaddingHandled()
		{
			// An odd-length leading chunk must be padded to even so the next chunk aligns.
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "Aligned") });
			var webp = AssetMetadataFixtures.Webp(("VP8 ", new byte[] { 0x01 }), ("EXIF", tiff)); // 1-byte chunk => padded
			var exif = new ExifResult();
			WebpReader.Read(webp, exif, out _, new List<string>());
			Assert.Equal("Aligned", exif.Make);
		}

		// ════════════════════ Format dispatch (public Read) ════════════════════

		[Fact]
		public void Read_DetectsJpeg_AndExtractsAllThreeBlocks()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: false,
				ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "NIKON"), AssetMetadataFixtures.Ascii(0x8298, "© Owner\0") },
				exif: new[] { AssetMetadataFixtures.Ascii(0x9003, "2020:01:01 00:00:00") });
			var iptc = AssetMetadataFixtures.Iptc8bim(
				(1, 90, new byte[] { 0x1B, 0x25, 0x47 }),               // declare UTF-8
				(2, 116, Encoding.UTF8.GetBytes("© IPTC Owner")));
			var xmp = AssetMetadataFixtures.XmpPacket("<dc:rights><rdf:Alt><rdf:li>© XMP</rdf:li></rdf:Alt></dc:rights>");
			var jpeg = AssetMetadataFixtures.Jpeg(
				(0xE1, AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("Exif\0\0"), tiff)),
				(0xE1, AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0"), Encoding.UTF8.GetBytes(xmp))),
				(0xED, AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("Photoshop 3.0\0"), iptc)));

			var meta = AssetMetadataReader.Read(jpeg);
			Assert.Equal(ImageFormat.Jpeg, meta.Format);
			Assert.Equal("NIKON", meta.Exif.Make);
			Assert.Equal("© IPTC Owner", meta.Iptc.CopyrightNotice);
			Assert.Contains("© XMP", meta.Xmp.Rights);
		}

		[Fact]
		public void Read_MultipleApp13_ReassemblesSplitIrb()
		{
			// One IRB split byte-wise across two APP13 segments must reassemble.
			var iptc = AssetMetadataFixtures.Iptc8bim(
				(2, 116, Encoding.ASCII.GetBytes("Split Owner")),
				(2, 80, Encoding.ASCII.GetBytes("Split Byline")));
			var full = AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("Photoshop 3.0\0"), iptc);
			int cut = full.Length / 2;
			var part1 = full[..cut];
			var part2 = AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("Photoshop 3.0\0"), full[cut..]); // sig repeats per segment
			var jpeg = AssetMetadataFixtures.Jpeg((0xED, part1), (0xED, part2));

			var meta = AssetMetadataReader.Read(jpeg);
			Assert.Equal("Split Owner", meta.Iptc.CopyrightNotice);
			Assert.Equal("Split Byline", meta.Iptc.Byline);
		}

		[Fact]
		public void Read_ExifItems_SynthesisesGpsLocationFirst()
		{
			var gps = new[]
			{
				AssetMetadataFixtures.Ascii(0x0001, "N"), AssetMetadataFixtures.Rational(0x0002, (10u,1u),(0u,1u),(0u,1u)),
				AssetMetadataFixtures.Ascii(0x0003, "E"), AssetMetadataFixtures.Rational(0x0004, (20u,1u),(0u,1u),(0u,1u)),
			};
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "X") }, gps: gps);
			var jpeg = AssetMetadataFixtures.Jpeg((0xE1, AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("Exif\0\0"), tiff)));
			var meta = AssetMetadataReader.Read(jpeg);
			Assert.Equal("GPSLocation", meta.ExifItems[0].Name);
		}

		[Fact]
		public void Read_DetectsPng()
		{
			var png = AssetMetadataFixtures.Png(("eXIf", AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "P") })));
			Assert.Equal(ImageFormat.Png, AssetMetadataReader.Read(png).Format);
		}

		[Fact]
		public void Read_DetectsWebp()
		{
			var webp = AssetMetadataFixtures.Webp(("VP8 ", new byte[] { 0 }));
			Assert.Equal(ImageFormat.Webp, AssetMetadataReader.Read(webp).Format);
		}

		[Fact]
		public void Read_DetectsGif_NoExtraction()
		{
			var gif = Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[20]).ToArray();
			var meta = AssetMetadataReader.Read(gif);
			Assert.Equal(ImageFormat.Gif, meta.Format);
			Assert.True(meta.IsImage);
		}

		[Fact]
		public void Read_UnknownFormat_NotImage()
		{
			var meta = AssetMetadataReader.Read(Encoding.ASCII.GetBytes("%PDF-1.7 stuff"));
			Assert.Equal(ImageFormat.Unknown, meta.Format);
			Assert.False(meta.IsImage);
		}

		[Fact]
		public void Read_NonexistentFile_WarnsNoThrow()
		{
			var meta = AssetMetadataReader.Read(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.jpg"));
			Assert.False(meta.IsImage);
			Assert.NotEmpty(meta.Warnings);
		}

		[Fact]
		public void Read_TruncatedJpeg_NoThrow()
		{
			var meta = AssetMetadataReader.Read(new byte[] { 0xFF, 0xD8, 0xFF });
			Assert.Equal(ImageFormat.Jpeg, meta.Format); // detected, nothing extractable
		}

		// ════════════════════ Renderer (caps / empties / tokens) ════════════════════

		[Fact]
		public void Render_NoCuratedToken_NoFinding()
		{
			// Only a benign tag (Orientation) present => no curated token => no finding.
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Short(0x0112, 1) });
			var meta = AssetMetadataReader.Read(AssetMetadataFixtures.Jpeg((0xE1, AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("Exif\0\0"), tiff))));
			var f = AssetMetadataReportRenderer.Build(meta, San);
			Assert.False(f.HasFinding);
		}

		[Fact]
		public void Render_EmptyAfterSanitize_FieldDropped()
		{
			// IPTC record-version markers (binary) sanitise to empty and must be dropped,
			// while a real copyright field survives.
			var iptc = AssetMetadataFixtures.Iptc8bim(
				(2, 0, new byte[] { 0x00, 0x04 }),                       // ApplicationRecordVersion (binary)
				(2, 116, Encoding.ASCII.GetBytes("Real Owner")));
			var jpeg = AssetMetadataFixtures.Jpeg((0xED, AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("Photoshop 3.0\0"), iptc)));
			var f = AssetMetadataReportRenderer.Build(AssetMetadataReader.Read(jpeg), San);
			Assert.DoesNotContain("2:0=", f.Iptc);
			Assert.Contains("CopyrightNotice=Real Owner", f.Iptc);
		}

		[Fact]
		public void Render_ValueOverCap_Truncated()
		{
			var huge = new string('Z', 600);
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.AsciiRaw(0x8298, huge + "\0") });
			var f = AssetMetadataReportRenderer.Build(
				AssetMetadataReader.Read(AssetMetadataFixtures.Jpeg((0xE1, AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("Exif\0\0"), tiff)))), San);
			Assert.True(f.Exif.Length < 300);          // value capped well under 600
			Assert.Contains("…", f.Exif);
		}

		[Fact]
		public void Render_OverHundredFields_ShowsMoreMarker()
		{
			// >100 visible EXIF tags in one column => "(+N more)".
			var entries = Enumerable.Range(0, 130)
				.Select(i => AssetMetadataFixtures.Ascii((ushort)(0x4000 + i), "v" + i)).ToArray();
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: entries);
			// add one curated token so a finding is produced
			var tiff2 = AssetMetadataFixtures.Tiff(little: true,
				ifd0: entries.Append(AssetMetadataFixtures.Ascii(0x010F, "Cam")).ToArray());
			var f = AssetMetadataReportRenderer.Build(
				AssetMetadataReader.Read(AssetMetadataFixtures.Jpeg((0xE1, AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("Exif\0\0"), tiff2)))), San);
			Assert.Contains("more)", f.Exif);
		}

		[Fact]
		public void Render_ColumnOverHardCap_Truncated()
		{
			// ~25 tags of ~190 chars each exceeds the 4096-char column ceiling.
			var entries = Enumerable.Range(0, 25)
				.Select(i => AssetMetadataFixtures.Ascii((ushort)(0x4000 + i), new string((char)('a' + i % 26), 190))).ToArray();
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: entries.Append(AssetMetadataFixtures.Ascii(0x010F, "Cam")).ToArray());
			var f = AssetMetadataReportRenderer.Build(
				AssetMetadataReader.Read(AssetMetadataFixtures.Jpeg((0xE1, AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("Exif\0\0"), tiff)))), San);
			Assert.True(f.Exif.Length <= 4096);
			Assert.EndsWith("…", f.Exif);
		}

		[Fact]
		public void Render_ContextReportsVisibleFlags()
		{
			var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "Cam"), AssetMetadataFixtures.Ascii(0x8298, "© c\0") });
			var f = AssetMetadataReportRenderer.Build(
				AssetMetadataReader.Read(AssetMetadataFixtures.Jpeg((0xE1, AssetMetadataFixtures.Concat(Encoding.ASCII.GetBytes("Exif\0\0"), tiff)))), San);
			Assert.True(f.HasFinding);
			Assert.Contains("camera", f.Context);
			Assert.Contains("copyright", f.Context);
		}

		// ════════════════════ Culture invariance ════════════════════

		[Fact]
		public void Formatting_UsesInvariantDecimals_UnderCommaLocale()
		{
			// A locale whose decimal separator is ',' (e.g. de-DE) must NOT leak into
			// the numeric Display values — a comma decimal inside the comma-joined GPS
			// "lat, long" string would be unparseable.
			var original = CultureInfo.CurrentCulture;
			try
			{
				CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

				var tiff = AssetMetadataFixtures.Tiff(little: true, ifd0: new[]
				{
					AssetMetadataFixtures.Float(0x9000, 1.5f),
					AssetMetadataFixtures.Double(0x9001, 2.25),
					AssetMetadataFixtures.Rational(0x011A, (72u, 1u)),
				});
				var r = new ExifResult();
				ExifReader.ParseExifBlob(tiff, 0, tiff.Length, r);
				Assert.Equal("1.5", r.Find(ExifIfd.Primary, 0x9000)!.Display);
				Assert.Equal("2.25", r.Find(ExifIfd.Primary, 0x9001)!.Display);
				Assert.Equal("72/1", r.Find(ExifIfd.Primary, 0x011A)!.Display);

				var gps = new[]
				{
					AssetMetadataFixtures.Ascii(0x0001, "N"), AssetMetadataFixtures.Rational(0x0002, (48u,1u),(51u,1u),(2960u,100u)),
					AssetMetadataFixtures.Ascii(0x0003, "E"), AssetMetadataFixtures.Rational(0x0004, (2u,1u),(17u,1u),(4020u,100u)),
				};
				var tiff2 = AssetMetadataFixtures.Tiff(little: true, ifd0: new[] { AssetMetadataFixtures.Ascii(0x010F, "X") }, gps: gps);
				var r2 = new ExifResult();
				ExifReader.ParseExifBlob(tiff2, 0, tiff2.Length, r2);
				Assert.Equal("48.858222, 2.2945", r2.Location!.ToString());
			}
			finally
			{
				CultureInfo.CurrentCulture = original;
			}
		}
	}
}
