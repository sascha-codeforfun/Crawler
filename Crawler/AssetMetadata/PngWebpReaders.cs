// SPDX-License-Identifier: MIT
//
// Clean-room implementation — written solely from public specifications, with
// no third-party source consulted. Derived from:
//   - PNG (W3C / ISO 15948): 8-byte signature; chunks of 4-byte big-endian
//     length, 4-byte type, data, 4-byte CRC. The "eXIf" chunk carries an EXIF
//     (TIFF) blob; XMP travels in an "iTXt" chunk with keyword
//     "XML:com.adobe.xmp" (keyword, compression flag/method, language tag and
//     translated keyword, then the text).
//   - WebP (Google WebP Container Specification): RIFF "RIFF"<size>"WEBP",
//     then FourCC chunks of 4-byte little-endian size with even padding; the
//     "EXIF" chunk carries EXIF, the "XMP " chunk carries the XMP packet.
//
// These locate the blobs only; parsing is delegated to the spec-built EXIF and
// XMP readers. Bounds-checked and non-throwing.

using System.Text;

namespace Crawler.AssetMetadata
{
    internal static class PngReader
    {
        private static readonly byte[] Sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static bool IsPng(ReadOnlySpan<byte> d) => d.Length >= 8 && d.Slice(0, 8).SequenceEqual(Sig);

        public static void Read(ReadOnlySpan<byte> d, ExifResult exif, out XmpResult xmp, List<string> warnings)
        {
            xmp = new XmpResult();
            int pos = 8;
            while (pos + 8 <= d.Length)
            {
                long length = (uint)((d[pos] << 24) | (d[pos + 1] << 16) | (d[pos + 2] << 8) | d[pos + 3]);
                int typeAt = pos + 4;
                int dataAt = pos + 8;
                if (length < 0 || dataAt + length > d.Length) { warnings.Add("PNG chunk length out of bounds."); break; }

                ReadOnlySpan<byte> type = d.Slice(typeAt, 4);
                if (type.SequenceEqual("IEND"u8)) break;

                if (type.SequenceEqual("eXIf"u8))
                {
                    ExifReader.ParseExifBlob(d, dataAt, (int)length, exif); // raw TIFF blob
                }
                else if (type.SequenceEqual("iTXt"u8))
                {
                    TryReadXmpFromITxt(d.Slice(dataAt, (int)length), ref xmp, warnings);
                }

                pos = dataAt + (int)length + 4; // skip 4-byte CRC
            }
        }

        private static void TryReadXmpFromITxt(ReadOnlySpan<byte> chunk, ref XmpResult xmp, List<string> warnings)
        {
            // iTXt: keyword \0 compressionFlag(1) compressionMethod(1) langTag \0 translatedKeyword \0 text
            int k = chunk.IndexOf((byte)0);
            if (k < 0) return;
            if (!chunk.Slice(0, k).SequenceEqual("XML:com.adobe.xmp"u8)) return; // only the XMP iTXt
            int p = k + 1;
            if (p + 2 > chunk.Length) return;
            byte compressionFlag = chunk[p];
            p += 2; // skip compression flag + method
            int lang = chunk.Slice(p).IndexOf((byte)0); if (lang < 0) return; p += lang + 1;       // language tag
            int trans = chunk.Slice(p).IndexOf((byte)0); if (trans < 0) return; p += trans + 1;     // translated keyword
            if (compressionFlag != 0) { warnings.Add("PNG XMP iTXt is compressed; skipped."); return; }
            if (p > chunk.Length) return;
            xmp = XmpReader.ParseXmlPacket(chunk.Slice(p));
        }
    }

    internal static class WebpReader
    {
        public static bool IsWebp(ReadOnlySpan<byte> d) =>
            d.Length >= 12 && d.Slice(0, 4).SequenceEqual("RIFF"u8) && d.Slice(8, 4).SequenceEqual("WEBP"u8);

        public static void Read(ReadOnlySpan<byte> d, ExifResult exif, out XmpResult xmp, List<string> warnings)
        {
            xmp = new XmpResult();
            int pos = 12;
            while (pos + 8 <= d.Length)
            {
                ReadOnlySpan<byte> fourcc = d.Slice(pos, 4);
                long size = (uint)(d[pos + 4] | (d[pos + 5] << 8) | (d[pos + 6] << 16) | (d[pos + 7] << 24));
                int dataAt = pos + 8;
                if (size < 0 || dataAt + size > d.Length) { warnings.Add("WebP chunk size out of bounds."); break; }

                if (fourcc.SequenceEqual("EXIF"u8))
                {
                    ExifReader.ParseExifBlob(d, dataAt, (int)size, exif); // tolerates optional "Exif\0\0"
                }
                else if (fourcc.SequenceEqual("XMP "u8))
                {
                    xmp = XmpReader.ParseXmlPacket(d.Slice(dataAt, (int)size));
                }

                long advance = size + (size & 1); // chunks are padded to even size
                pos = dataAt + (int)advance;
            }
        }
    }
}
