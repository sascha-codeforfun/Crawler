// SPDX-License-Identifier: MIT
//
// Clean-room implementation — written solely from public specifications, with
// no third-party source consulted. Derived from:
//   - Adobe Photoshop image-resource (8BIM) structure: signature "8BIM",
//     2-byte resource id, padded Pascal name, 4-byte size, even-padded data;
//     IPTC-IIM lives in resource id 0x0404.
//   - IPTC-IIM specification: dataset records of 0x1C, record#, dataset#,
//     2-byte length (high bit => extended length); record 2 application fields;
//     coded character set 1:90 (ESC % G => UTF-8) selecting the text encoding.
//
// Photoshop may split the image-resource block across several APP13 segments at
// arbitrary byte boundaries; the caller concatenates the post-signature bytes of
// every APP13 before calling ParseIrb so a split 0x0404 block reassembles.

using System.Text;

namespace Crawler.AssetMetadata
{
    internal static class IptcReader
    {
        private static readonly Dictionary<(int, int), string> Names = new()
        {
            [(1, 90)] = "CodedCharacterSet",
            [(2, 5)] = "ObjectName", [(2, 25)] = "Keywords", [(2, 40)] = "SpecialInstructions",
            [(2, 55)] = "DateCreated", [(2, 60)] = "TimeCreated",
            [(2, 62)] = "DigitalCreationDate", [(2, 63)] = "DigitalCreationTime",
            [(2, 80)] = "By-line", [(2, 85)] = "By-lineTitle",
            [(2, 90)] = "City", [(2, 95)] = "Province/State", [(2, 101)] = "Country",
            [(2, 105)] = "Headline", [(2, 110)] = "Credit", [(2, 115)] = "Source",
            [(2, 116)] = "CopyrightNotice", [(2, 118)] = "Contact", [(2, 120)] = "Caption/Abstract",
            [(2, 103)] = "OriginalTransmissionReference",
            [(2, 122)] = "Writer/Editor",
        };

        /// <summary>Parse a reassembled Photoshop image-resource block (8BIM stream).</summary>
        public static IptcResult ParseIrb(byte[] irb)
        {
            var res = new IptcResult();
            var seg = (ReadOnlySpan<byte>)irb;
            try
            {
                int p = IndexOf(seg, "8BIM"u8);
                if (p < 0) { res.AddWarning("APP13 present but no 8BIM resource block found."); return res; }

                while (p + 12 <= seg.Length)
                {
                    if (!seg.Slice(p, 4).SequenceEqual("8BIM"u8)) break;
                    int q = p + 4;

                    int resId = (seg[q] << 8) | seg[q + 1];
                    q += 2;

                    int nameLen = seg[q];                 // Pascal name, padded with the length byte to even
                    int nameField = 1 + nameLen;
                    if ((nameField & 1) != 0) nameField++;
                    q += nameField;
                    if (q + 4 > seg.Length) break;

                    int size = (seg[q] << 24) | (seg[q + 1] << 16) | (seg[q + 2] << 8) | seg[q + 3];
                    q += 4;
                    if (size < 0 || q + size > seg.Length) { res.AddWarning("8BIM resource size out of bounds."); break; }

                    if (resId == 0x0404) ParseIptcRecords(seg.Slice(q, size), res);

                    p = q + size + (size & 1);            // data padded to even length
                }

                res.Present = res.Fields.Count > 0;
            }
            catch (Exception ex) { res.AddWarning($"IPTC parse error: {ex.GetType().Name}: {ex.Message}"); }
            return res;
        }

        private static void ParseIptcRecords(ReadOnlySpan<byte> s, IptcResult res)
        {
            var raw = new List<(int rec, int ds, byte[] bytes)>();
            bool utf8 = false;
            int i = 0;
            while (i + 5 <= s.Length)
            {
                if (s[i] != 0x1C) break;                  // dataset tag marker
                int rec = s[i + 1];
                int ds = s[i + 2];
                int length = (s[i + 3] << 8) | s[i + 4];
                int hdr = 5;

                if ((length & 0x8000) != 0)               // extended length: low 15 bits = size of the length field
                {
                    int countBytes = length & 0x7FFF;
                    if (i + hdr + countBytes > s.Length || countBytes > 4) break;
                    long ext = 0;
                    for (int k = 0; k < countBytes; k++) ext = (ext << 8) | s[i + hdr + k];
                    hdr += countBytes;
                    length = (int)ext;
                }

                if (length < 0 || i + hdr + length > s.Length) break;
                var bytes = s.Slice(i + hdr, length).ToArray();
                if (rec == 1 && ds == 90 && IsUtf8Escape(bytes)) utf8 = true;
                raw.Add((rec, ds, bytes));
                i += hdr + length;
            }

            var enc = utf8 ? Encoding.UTF8 : Encoding.Latin1; // Latin1 maps every byte and never throws
            foreach (var (rec, ds, bytes) in raw)
            {
                if (rec == 1 && ds == 90) continue;       // charset marker, not user content
                string name = Names.TryGetValue((rec, ds), out var nm) ? nm : $"{rec}:{ds}";
                res.Add(new IptcField(rec, ds, name, enc.GetString(bytes).Trim()));
            }
        }

        // ESC % G => UTF-8 per IPTC dataset 1:90.
        private static bool IsUtf8Escape(byte[] b) => b.Length >= 3 && b[0] == 0x1B && b[1] == 0x25 && b[2] == 0x47;

        private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
                if (haystack.Slice(i, needle.Length).SequenceEqual(needle)) return i;
            return -1;
        }
    }
}
