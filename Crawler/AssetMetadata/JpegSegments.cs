// SPDX-License-Identifier: MIT
//
// Clean-room implementation — written solely from public specifications, with
// no third-party source consulted. JPEG marker framing derived from ITU-T T.81
// (JPEG) and the JFIF specification: SOI 0xFFD8, APPn marker segments each with
// a 2-byte big-endian length, standalone markers without length, and SOS 0xFFDA
// beginning entropy-coded data (after which application metadata cannot appear).

namespace Crawler.AssetMetadata
{
    /// <summary>A located APPn segment (payload excludes the marker and 2-byte length).</summary>
    public readonly record struct JpegSegment(byte Marker, int PayloadStart, int PayloadLength);

    internal static class JpegSegmentScanner
    {
        public static bool Scan(ReadOnlySpan<byte> d, List<JpegSegment> segments, List<string> warnings)
        {
            if (d.Length < 2 || d[0] != 0xFF || d[1] != 0xD8)
            {
                warnings.Add("Not a JPEG: missing SOI marker (0xFFD8).");
                return false;
            }

            int pos = 2;
            while (pos + 1 < d.Length)
            {
                if (d[pos] != 0xFF)
                {
                    warnings.Add($"Expected segment marker at offset 0x{pos:X}, found 0x{d[pos]:X2}.");
                    break;
                }

                byte marker = d[pos + 1];
                if (marker == 0xFF) { pos++; continue; }                               // fill byte
                if (marker == 0xD9 || marker == 0xDA) break;                            // EOI / SOS
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { pos += 2; continue; } // standalone

                if (pos + 4 > d.Length) { warnings.Add("Truncated segment header."); break; }
                int segLen = (d[pos + 2] << 8) | d[pos + 3];                            // big-endian, incl. these 2 bytes
                if (segLen < 2) { warnings.Add($"Invalid segment length {segLen} at 0x{pos:X}."); break; }

                int dataStart = pos + 4;
                int dataLen = segLen - 2;
                if (dataStart + dataLen > d.Length)
                {
                    warnings.Add("Segment length runs past end of file (truncated image).");
                    break;
                }

                if (marker >= 0xE0 && marker <= 0xEF)
                    segments.Add(new JpegSegment(marker, dataStart, dataLen));

                pos = dataStart + dataLen;
            }
            return true;
        }

        public static bool PayloadStartsWith(ReadOnlySpan<byte> d, JpegSegment seg, ReadOnlySpan<byte> signature)
            => seg.PayloadLength >= signature.Length
               && d.Slice(seg.PayloadStart, signature.Length).SequenceEqual(signature);
    }
}
