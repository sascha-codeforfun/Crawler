// SPDX-License-Identifier: MIT
//
// Clean-room implementation — written solely from public specifications, with
// no third-party source consulted. EXIF/TIFF parsing derived from:
//   - Adobe TIFF 6.0 (byte-order mark II/MM, magic 42, IFD entry layout:
//     tag/type/count/value-or-offset, the 12 field types and their sizes,
//     inline-vs-offset rule, offsets relative to the TIFF header start).
//   - CIPA DC-008 (Exif): the Exif sub-IFD pointer 0x8769, GPS IFD pointer
//     0x8825, Interop pointer 0xA005, and GPS coordinate encoding.
//
// Non-throwing: every multi-byte read is bounds-checked and endian-correct;
// malformed structure degrades to warnings. Reports facts only.

using System.Globalization;
using System.Text;

namespace Crawler.AssetMetadata
{
    internal static class ExifReader
    {
        /// <summary>Parse a JPEG APP1 payload that begins with the "Exif\0\0" signature.</summary>
        public static void ParseExifPayload(ReadOnlySpan<byte> d, int payloadStart, int payloadLen, ExifResult result)
            => ParseExifBlob(d, payloadStart, payloadLen, result);

        /// <summary>
        /// Parse an EXIF blob. Tolerates an optional leading "Exif\0\0" (present in JPEG
        /// APP1, sometimes prepended in PNG/WebP); otherwise the blob is a bare TIFF block
        /// starting at its byte-order mark — which is how PNG eXIf and WebP EXIF store it.
        /// </summary>
        public static void ParseExifBlob(ReadOnlySpan<byte> d, int start, int len, ExifResult result)
        {
            try
            {
                if (len < 4) { result.AddWarning("EXIF blob too small."); return; }
                int tiffStart = start;
                if (len >= 6 && IsExifHeader(d.Slice(start, len))) tiffStart = start + 6; // skip "Exif\0\0"
                ParseTiff(d, tiffStart, start + len, result);
                result.HasExif = true;
            }
            catch (Exception ex)
            {
                result.AddWarning($"EXIF parse error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool IsExifHeader(ReadOnlySpan<byte> seg) =>
            seg.Length >= 6 && seg[0] == (byte)'E' && seg[1] == (byte)'x' && seg[2] == (byte)'i' &&
            seg[3] == (byte)'f' && seg[4] == 0x00 && seg[5] == 0x00;

        private static void ParseTiff(ReadOnlySpan<byte> d, int tiffStart, int blockEnd, ExifResult r)
        {
            if (tiffStart + 8 > blockEnd) { r.AddWarning("EXIF block too small for a TIFF header."); return; }

            ushort bom = (ushort)((d[tiffStart] << 8) | d[tiffStart + 1]);
            bool little = bom switch
            {
                0x4949 => true,   // "II"
                0x4D4D => false,  // "MM"
                _ => throw new SkipTiff($"Unknown TIFF byte order 0x{bom:X4}."),
            };

            var rd = new Tiff(d, tiffStart, blockEnd, little);
            if (rd.U16(tiffStart + 2) != 42) { r.AddWarning("Invalid TIFF magic number (expected 42)."); return; }

            uint ifd0 = rd.U32(tiffStart + 4);
            var visited = new HashSet<uint>();
            uint next = ParseIfd(rd, ifd0, ExifIfd.Primary, r, visited);
            if (next != 0) ParseIfd(rd, next, ExifIfd.Thumbnail, r, visited);
        }

        private static uint ParseIfd(Tiff rd, uint ifdOffset, ExifIfd ifd, ExifResult r, HashSet<uint> visited)
        {
            if (ifdOffset == 0) return 0;
            if (!visited.Add(ifdOffset)) { r.AddWarning($"{ifd}: IFD cycle at offset {ifdOffset}, stopping."); return 0; }

            int abs = rd.Abs(ifdOffset);
            if (!rd.InBounds(abs, 2)) { r.AddWarning($"{ifd}: IFD offset out of bounds."); return 0; }

            int count = rd.U16(abs);
            int entriesStart = abs + 2;
            int maxEntries = (rd.End - entriesStart) / 12;
            if (count > maxEntries)
            {
                r.AddWarning($"{ifd}: IFD claims {count} entries but only {maxEntries} fit; clamping.");
                count = Math.Max(maxEntries, 0);
            }

            uint exifPtr = 0, gpsPtr = 0, interopPtr = 0;
            for (int i = 0; i < count; i++)
            {
                int e = entriesStart + i * 12;
                try
                {
                    ushort tag = rd.U16(e);
                    ushort type = rd.U16(e + 2);
                    uint cnt = rd.U32(e + 4);
                    int valueField = e + 8;

                    // Sub-directory pointers (no user data): recurse rather than store.
                    if (ifd == ExifIfd.Primary && tag == 0x8769) { exifPtr = rd.U32(valueField); continue; }
                    if (ifd == ExifIfd.Primary && tag == 0x8825) { gpsPtr = rd.U32(valueField); continue; }
                    if (ifd == ExifIfd.Exif && tag == 0xA005) { interopPtr = rd.U32(valueField); continue; }

                    var (value, display) = ReadValue(rd, type, cnt, valueField);
                    r.AddTag(new ExifTag
                    {
                        Ifd = ifd, Id = tag, Name = ExifTagNames.Resolve(ifd, tag),
                        Type = (ExifType)type, Count = (int)cnt, Value = value, Display = display,
                    });
                }
                catch (SkipTiff ex) { r.AddWarning($"{ifd}: entry {i} skipped ({ex.Message})."); }
            }

            if (exifPtr != 0) ParseIfd(rd, exifPtr, ExifIfd.Exif, r, visited);
            if (gpsPtr != 0) ParseIfd(rd, gpsPtr, ExifIfd.Gps, r, visited);
            if (interopPtr != 0) ParseIfd(rd, interopPtr, ExifIfd.Interop, r, visited);

            int nextPtrAbs = entriesStart + count * 12;
            return rd.InBounds(nextPtrAbs, 4) ? rd.U32(nextPtrAbs) : 0;
        }

        private static (object? value, string display) ReadValue(Tiff rd, ushort type, uint count, int valueField)
        {
            int size = TypeSize(type);
            if (size == 0) return (null, $"<unsupported type {type}>");
            if (count > 200_000) throw new SkipTiff($"implausible element count {count}");

            long total = (long)size * count;
            int dataAbs = total <= 4 ? valueField : rd.Abs(rd.U32(valueField));
            if (!rd.InBounds(dataAbs, (int)total)) throw new SkipTiff("value runs out of bounds");

            int n = (int)count;
            switch ((ExifType)type)
            {
                case ExifType.Ascii:
                {
                    string s = rd.Ascii(dataAbs, n);
                    return (s, s);
                }
                case ExifType.Byte:
                case ExifType.Short:
                case ExifType.Long:
                {
                    var a = new long[n];
                    for (int i = 0; i < n; i++) a[i] = rd.UInt(dataAbs + i * size, size);
                    return (a, Join(a));
                }
                case ExifType.SByte:
                case ExifType.SShort:
                case ExifType.SLong:
                {
                    var a = new long[n];
                    for (int i = 0; i < n; i++) a[i] = rd.SInt(dataAbs + i * size, size);
                    return (a, Join(a));
                }
                case ExifType.Rational:
                {
                    var a = new Rational[n];
                    for (int i = 0; i < n; i++)
                        a[i] = new Rational((uint)rd.UInt(dataAbs + i * 8, 4), (uint)rd.UInt(dataAbs + i * 8 + 4, 4));
                    return (a, string.Join(", ", a));
                }
                case ExifType.SRational:
                {
                    var a = new SRational[n];
                    for (int i = 0; i < n; i++)
                        a[i] = new SRational((int)rd.SInt(dataAbs + i * 8, 4), (int)rd.SInt(dataAbs + i * 8 + 4, 4));
                    return (a, string.Join(", ", a));
                }
                case ExifType.Float:
                {
                    var a = new double[n];
                    for (int i = 0; i < n; i++) a[i] = BitConverter.Int32BitsToSingle((int)rd.UInt(dataAbs + i * 4, 4));
                    return (a, Join(a));
                }
                case ExifType.Double:
                {
                    var a = new double[n];
                    for (int i = 0; i < n; i++) a[i] = BitConverter.Int64BitsToDouble((long)rd.U64(dataAbs + i * 8));
                    return (a, Join(a));
                }
                case ExifType.Undefined:
                default:
                {
                    var bytes = rd.Slice(dataAbs, (int)total);
                    return (bytes, FormatUndefined(bytes));
                }
            }
        }

        // TIFF 6.0 field-type sizes (bytes per component).
        private static int TypeSize(ushort t) => t switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 => 4,
            5 or 10 or 12 => 8,
            _ => 0,
        };

        // Numeric Display values feed a machine-parsed log, so they must never
        // vary with the ambient locale (e.g. a comma decimal would corrupt the
        // comma-joined GPS "lat, long" string). Always format invariant.
        private static string Join(long[] a) => a.Length == 1
            ? a[0].ToString(CultureInfo.InvariantCulture)
            : string.Join(", ", a.Select(x => x.ToString(CultureInfo.InvariantCulture)));
        private static string Join(double[] a) => a.Length == 1
            ? a[0].ToString("0.######", CultureInfo.InvariantCulture)
            : string.Join(", ", a.Select(x => x.ToString("0.######", CultureInfo.InvariantCulture)));

        private static string FormatUndefined(byte[] b)
        {
            bool printable = b.Length > 0 && Array.TrueForAll(b, x => x >= 0x20 && x < 0x7F);
            if (printable && b.Length <= 64) return Encoding.ASCII.GetString(b);
            return $"<{b.Length} bytes>";
        }

        private sealed class SkipTiff : Exception { public SkipTiff(string m) : base(m) { } }

        /// <summary>Endian-aware, bounds-checked window over the TIFF block.</summary>
        private readonly ref struct Tiff
        {
            private readonly ReadOnlySpan<byte> _d;
            private readonly int _tiffStart;
            private readonly bool _little;
            public readonly int End;

            public Tiff(ReadOnlySpan<byte> d, int tiffStart, int end, bool little)
            {
                _d = d; _tiffStart = tiffStart; End = Math.Min(end, d.Length); _little = little;
            }

            // TIFF offsets are relative to the TIFF header start.
            public int Abs(uint tiffOffset) => _tiffStart + (int)tiffOffset;
            public bool InBounds(int abs, int len) => len >= 0 && abs >= _tiffStart && abs + len <= End;

            public ushort U16(int abs)
            {
                Require(abs, 2);
                return _little ? (ushort)(_d[abs] | (_d[abs + 1] << 8))
                               : (ushort)((_d[abs] << 8) | _d[abs + 1]);
            }

            public uint U32(int abs)
            {
                Require(abs, 4);
                return _little ? (uint)(_d[abs] | (_d[abs + 1] << 8) | (_d[abs + 2] << 16) | (_d[abs + 3] << 24))
                               : (uint)((_d[abs] << 24) | (_d[abs + 1] << 16) | (_d[abs + 2] << 8) | _d[abs + 3]);
            }

            public ulong U64(int abs)
            {
                Require(abs, 8);
                ulong lo = U32(abs); ulong hi = U32(abs + 4);
                return _little ? (hi << 32) | lo : (lo << 32) | hi;
            }

            public long UInt(int abs, int size) => size switch
            {
                1 => Byte(abs), 2 => U16(abs), 4 => U32(abs),
                _ => throw new SkipTiff($"bad uint size {size}"),
            };

            public long SInt(int abs, int size) => size switch
            {
                1 => (sbyte)Byte(abs), 2 => (short)U16(abs), 4 => (int)U32(abs),
                _ => throw new SkipTiff($"bad sint size {size}"),
            };

            public byte Byte(int abs) { Require(abs, 1); return _d[abs]; }

            public string Ascii(int abs, int len)
            {
                Require(abs, len);
                // ASCII fields are NUL-terminated; Copyright (0x8298) may hold two
                // NUL-separated strings. Collect every non-empty run; join with " / ".
                var parts = new List<string>();
                int start = abs, limit = abs + len;
                for (int i = abs; i <= limit; i++)
                {
                    if (i == limit || _d[i] == 0)
                    {
                        if (i > start)
                        {
                            var s = Encoding.ASCII.GetString(_d.Slice(start, i - start)).Trim();
                            if (s.Length > 0) parts.Add(s);
                        }
                        start = i + 1;
                    }
                }
                return string.Join(" / ", parts);
            }

            public byte[] Slice(int abs, int len) { Require(abs, len); return _d.Slice(abs, len).ToArray(); }

            private void Require(int abs, int len)
            {
                if (!InBounds(abs, len)) throw new SkipTiff($"read of {len} byte(s) at 0x{abs:X} out of bounds");
            }
        }
    }
}
