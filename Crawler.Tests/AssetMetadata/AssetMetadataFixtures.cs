using System.Buffers.Binary;
using System.Text;
using Crawler.AssetMetadata;

namespace Crawler.Tests
{
	/// <summary>
	/// Byte-fixture builders for AssetMetadata tests — small, spec-accurate
	/// constructors for TIFF/EXIF blocks, Photoshop 8BIM/IIM resource blocks,
	/// PNG chunk streams, WebP RIFF chunk streams, and XMP packets. Keeping the
	/// fixtures in code (rather than binary image files) makes each test's input
	/// explicit and the suite dependency-free.
	/// </summary>
	internal static class AssetMetadataFixtures
	{
		// ── EXIF entry descriptors ─────────────────────────────────────────
		internal sealed record Ent(ushort Tag, ExifType Type, object Val);

		internal static Ent Ascii(ushort t, string s) => new(t, ExifType.Ascii, s + "\0");
		internal static Ent AsciiRaw(ushort t, string s) => new(t, ExifType.Ascii, s); // caller supplies NULs
		internal static Ent Byte(ushort t, params int[] v) => new(t, ExifType.Byte, v.Select(x => (long)x).ToArray());
		internal static Ent Short(ushort t, params int[] v) => new(t, ExifType.Short, v.Select(x => (long)x).ToArray());
		internal static Ent Long(ushort t, params long[] v) => new(t, ExifType.Long, v);
		internal static Ent Rational(ushort t, params (uint n, uint d)[] v) => new(t, ExifType.Rational, v);
		internal static Ent SRational(ushort t, params (int n, int d)[] v) => new(t, ExifType.SRational, v);
		internal static Ent Float(ushort t, params float[] v) => new(t, ExifType.Float, v);
		internal static Ent Double(ushort t, params double[] v) => new(t, ExifType.Double, v);
		internal static Ent Undef(ushort t, byte[] v) => new(t, ExifType.Undefined, v);

		private static int Count(Ent e) => e.Val switch
		{
			string s => Encoding.ASCII.GetByteCount(s),
			long[] a => a.Length,
			(uint, uint)[] a => a.Length,
			(int, int)[] a => a.Length,
			float[] a => a.Length,
			double[] a => a.Length,
			byte[] a => a.Length,
			_ => 0,
		};

		private static byte[] Payload(Ent e, bool little)
		{
			byte[] U16(int v) { var b = new byte[2]; if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); else BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); return b; }
			byte[] U32(long v) { var b = new byte[4]; if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v); else BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); return b; }
			byte[] I32(int v) { var b = new byte[4]; if (little) BinaryPrimitives.WriteInt32LittleEndian(b, v); else BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }

			var ms = new List<byte>();
			switch (e.Type)
			{
				case ExifType.Ascii: ms.AddRange(Encoding.ASCII.GetBytes((string)e.Val)); break;
				case ExifType.Byte: foreach (var x in (long[])e.Val) ms.Add((byte)x); break;
				case ExifType.Short: foreach (var x in (long[])e.Val) ms.AddRange(U16((int)x)); break;
				case ExifType.Long: foreach (var x in (long[])e.Val) ms.AddRange(U32(x)); break;
				case ExifType.Rational: foreach (var (n, d) in ((uint, uint)[])e.Val) { ms.AddRange(U32(n)); ms.AddRange(U32(d)); } break;
				case ExifType.SRational: foreach (var (n, d) in ((int, int)[])e.Val) { ms.AddRange(I32(n)); ms.AddRange(I32(d)); } break;
				case ExifType.Float: foreach (var x in (float[])e.Val) { var b = BitConverter.GetBytes(x); if (BitConverter.IsLittleEndian != little) Array.Reverse(b); ms.AddRange(b); } break;
				case ExifType.Double: foreach (var x in (double[])e.Val) { var b = BitConverter.GetBytes(x); if (BitConverter.IsLittleEndian != little) Array.Reverse(b); ms.AddRange(b); } break;
				case ExifType.Undefined: ms.AddRange((byte[])e.Val); break;
			}
			return ms.ToArray();
		}

		/// <summary>
		/// Build a TIFF/EXIF block: header + IFD0 (+ optional Exif and GPS sub-IFDs
		/// reached via injected pointer tags 0x8769 / 0x8825) + optional IFD1
		/// (thumbnail) reached via IFD0's next-IFD link. Overflow values (&gt;4 bytes)
		/// go to a shared heap after all IFDs, offsets being TIFF-relative.
		/// </summary>
		internal static byte[] Tiff(bool little, Ent[] ifd0, Ent[]? exif = null, Ent[]? gps = null,
			Ent[]? ifd1 = null, bool cycleIfd1 = false)
		{
			int n0 = ifd0.Length + (exif != null ? 1 : 0) + (gps != null ? 1 : 0);
			int IfdSize(int n) => 2 + 12 * n + 4;
			int ifd0Size = IfdSize(n0);
			int exifSize = exif != null ? IfdSize(exif.Length) : 0;
			int gpsSize = gps != null ? IfdSize(gps.Length) : 0;
			int ifd1Size = ifd1 != null ? IfdSize(ifd1.Length) : 0;

			int ifd0Off = 8;
			int exifOff = ifd0Off + ifd0Size;
			int gpsOff = exifOff + exifSize;
			int ifd1Off = gpsOff + gpsSize;
			int heapBase = ifd1Off + ifd1Size;

			var heap = new List<byte>();
			byte[] U16(int v) { var b = new byte[2]; if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); else BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); return b; }
			byte[] U32(long v) { var b = new byte[4]; if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v); else BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); return b; }

			byte[] Entry(Ent e)
			{
				var b = new List<byte>();
				b.AddRange(U16(e.Tag));
				b.AddRange(U16((int)e.Type));
				b.AddRange(U32(Count(e)));
				var payload = Payload(e, little);
				if (payload.Length <= 4)
				{
					var field = new byte[4];
					Array.Copy(payload, field, payload.Length); // left-justified, zero-padded
					b.AddRange(field);
				}
				else
				{
					int off = heapBase + heap.Count;
					heap.AddRange(payload);
					if ((payload.Length & 1) != 0) heap.Add(0); // keep heap word-aligned
					b.AddRange(U32(off));
				}
				return b.ToArray();
			}

			byte[] Ifd(IEnumerable<Ent> entries, int nextOff)
			{
				var list = entries.ToList();
				var b = new List<byte>();
				b.AddRange(U16(list.Count));
				foreach (var e in list) b.AddRange(Entry(e));
				b.AddRange(U32(nextOff));
				return b.ToArray();
			}

			// Inject sub-IFD pointer tags into IFD0 (type Long, inline offset value).
			var ifd0Entries = new List<Ent>(ifd0);
			if (exif != null) ifd0Entries.Add(Long(0x8769, exifOff));
			if (gps != null) ifd0Entries.Add(Long(0x8825, gpsOff));

			int ifd0Next = ifd1 != null ? ifd1Off : (cycleIfd1 ? ifd0Off : 0);

			var outp = new List<byte>();
			outp.AddRange(little ? new byte[] { 0x49, 0x49 } : new byte[] { 0x4D, 0x4D }); // II / MM
			outp.AddRange(U16(42));
			outp.AddRange(U32(ifd0Off));
			outp.AddRange(Ifd(ifd0Entries, ifd0Next));
			if (exif != null) outp.AddRange(Ifd(exif, 0));
			if (gps != null) outp.AddRange(Ifd(gps, 0));
			if (ifd1 != null) outp.AddRange(Ifd(ifd1, 0));
			outp.AddRange(heap);
			return outp.ToArray();
		}

		// ── JPEG container ─────────────────────────────────────────────────
		internal static byte[] Jpeg(params (byte marker, byte[] payload)[] apps)
		{
			var b = new List<byte> { 0xFF, 0xD8 }; // SOI
			foreach (var (marker, payload) in apps)
			{
				int segLen = payload.Length + 2;
				b.Add(0xFF); b.Add(marker);
				b.Add((byte)(segLen >> 8)); b.Add((byte)segLen); // big-endian length
				b.AddRange(payload);
			}
			b.Add(0xFF); b.Add(0xD9); // EOI
			return b.ToArray();
		}

		// ── Photoshop 8BIM / IPTC-IIM ──────────────────────────────────────
		internal static byte[] Bim(int resourceId, byte[] data)
		{
			var b = new List<byte>();
			b.AddRange(Encoding.ASCII.GetBytes("8BIM"));
			b.Add((byte)(resourceId >> 8)); b.Add((byte)resourceId);
			b.Add(0); b.Add(0); // empty Pascal name, padded to even
			b.Add((byte)(data.Length >> 24)); b.Add((byte)(data.Length >> 16));
			b.Add((byte)(data.Length >> 8)); b.Add((byte)data.Length);
			b.AddRange(data);
			if ((data.Length & 1) != 0) b.Add(0);
			return b.ToArray();
		}

		private static byte[] Dataset(int rec, int ds, byte[] data)
		{
			var b = new List<byte> { 0x1C, (byte)rec, (byte)ds };
			b.Add((byte)(data.Length >> 8)); b.Add((byte)data.Length); // standard 2-byte length
			b.AddRange(data);
			return b.ToArray();
		}

		internal static byte[] Iptc8bim(params (int rec, int ds, byte[] data)[] datasets)
		{
			var iim = new List<byte>();
			foreach (var (rec, ds, data) in datasets) iim.AddRange(Dataset(rec, ds, data));
			return Bim(0x0404, iim.ToArray());
		}

		/// <summary>IIM dataset using the extended-length form (high bit set in the length field).</summary>
		internal static byte[] Iptc8bimExtended(int rec, int ds, byte[] data)
		{
			var b = new List<byte> { 0x1C, (byte)rec, (byte)ds };
			b.Add(0x80 | 0x00); b.Add(0x02);                 // extended marker: length field is 2 bytes
			b.Add((byte)(data.Length >> 8)); b.Add((byte)data.Length);
			b.AddRange(data);
			return Bim(0x0404, b.ToArray());
		}

		// ── PNG ────────────────────────────────────────────────────────────
		private static readonly byte[] PngSig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

		internal static byte[] Png(params (string type, byte[] data)[] chunks)
		{
			var b = new List<byte>(PngSig);
			void Chunk(string type, byte[] data)
			{
				int len = data.Length;
				b.Add((byte)(len >> 24)); b.Add((byte)(len >> 16)); b.Add((byte)(len >> 8)); b.Add((byte)len);
				var typeBytes = Encoding.ASCII.GetBytes(type);
				int start = b.Count;
				b.AddRange(typeBytes); b.AddRange(data);
				uint crc = Crc32(b, start, typeBytes.Length + data.Length);
				b.Add((byte)(crc >> 24)); b.Add((byte)(crc >> 16)); b.Add((byte)(crc >> 8)); b.Add((byte)crc);
			}
			foreach (var (type, data) in chunks) Chunk(type, data);
			Chunk("IEND", Array.Empty<byte>());
			return b.ToArray();
		}

		/// <summary>PNG iTXt chunk body: keyword \0 compFlag compMethod lang \0 transKeyword \0 text.</summary>
		internal static byte[] ITxt(string keyword, string text, bool compressed)
		{
			var b = new List<byte>();
			b.AddRange(Encoding.ASCII.GetBytes(keyword)); b.Add(0);
			b.Add((byte)(compressed ? 1 : 0)); b.Add(0); // compression flag + method
			b.Add(0);                                     // empty language tag
			b.Add(0);                                     // empty translated keyword
			b.AddRange(Encoding.UTF8.GetBytes(text));
			return b.ToArray();
		}

		// ── WebP (RIFF) ────────────────────────────────────────────────────
		internal static byte[] Webp(params (string fourcc, byte[] data)[] chunks)
		{
			var body = new List<byte>(Encoding.ASCII.GetBytes("WEBP"));
			foreach (var (fourcc, data) in chunks)
			{
				body.AddRange(Encoding.ASCII.GetBytes(fourcc));
				int len = data.Length;
				body.Add((byte)len); body.Add((byte)(len >> 8)); body.Add((byte)(len >> 16)); body.Add((byte)(len >> 24)); // LE
				body.AddRange(data);
				if ((len & 1) != 0) body.Add(0); // pad to even
			}
			var b = new List<byte>(Encoding.ASCII.GetBytes("RIFF"));
			int riffLen = body.Count;
			b.Add((byte)riffLen); b.Add((byte)(riffLen >> 8)); b.Add((byte)(riffLen >> 16)); b.Add((byte)(riffLen >> 24));
			b.AddRange(body);
			return b.ToArray();
		}

		// ── XMP packets ────────────────────────────────────────────────────
		internal static string XmpPacket(string innerProperties) =>
			"<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
			"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
			"<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
			"<rdf:Description rdf:about=\"\"" +
			" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"" +
			" xmlns:xmpRights=\"http://ns.adobe.com/xap/1.0/rights/\"" +
			" xmlns:photoshop=\"http://ns.adobe.com/photoshop/1.0/\">" +
			innerProperties +
			"</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

		internal static string XmpPacketAttr(string descriptionAttrs) =>
			"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
			"<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
			"<rdf:Description rdf:about=\"\" " + descriptionAttrs + "/>" +
			"</rdf:RDF></x:xmpmeta>";

		// ── misc ───────────────────────────────────────────────────────────
		internal static byte[] Concat(params byte[][] parts)
		{
			var outp = new List<byte>();
			foreach (var p in parts) outp.AddRange(p);
			return outp.ToArray();
		}

		// CRC-32 (IEEE) over a slice of a list — PNG chunk CRC.
		private static readonly uint[] CrcTable = BuildCrcTable();
		private static uint[] BuildCrcTable()
		{
			var t = new uint[256];
			for (uint n = 0; n < 256; n++)
			{
				uint c = n;
				for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
				t[n] = c;
			}
			return t;
		}
		private static uint Crc32(List<byte> data, int start, int len)
		{
			uint c = 0xFFFFFFFF;
			for (int i = start; i < start + len; i++) c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
			return c ^ 0xFFFFFFFF;
		}
	}
}
