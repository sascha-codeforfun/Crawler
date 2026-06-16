// SPDX-License-Identifier: MIT
//
// Clean-room implementation — written solely from public specifications, with
// no third-party source consulted. Derived from:
//   - ISO 16684-1 / Adobe XMP specification: the JPEG APP1 signature
//     "http://ns.adobe.com/xap/1.0/\0", the RDF/XML packet, and the dc:,
//     xmpRights:, photoshop: namespaces used for rights/attribution.
//   - W3C RDF/XML and XML: rdf:Alt/Seq/Bag with rdf:li members.
//
// The XML is parsed through an XmlReader with DtdProcessing=Prohibit and no
// resolver, which neutralises entity-expansion ("billion laughs") and external
// entity (XXE) attacks. A size ceiling caps how much XML is parsed at all.
// Only curated rights/attribution properties are extracted (not arbitrary XMP).

using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Crawler.AssetMetadata
{
    internal static class XmpReader
    {
        // JPEG APP1 XMP segments begin with this NUL-terminated namespace URI.
        private static readonly byte[] Header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        public static ReadOnlySpan<byte> Signature => Header;

        // Poisoning defence: never build an XML tree from an oversized packet.
        private const int MaxXmlChars = 1_000_000;

        /// <summary>Parse a JPEG APP1 XMP payload (strips the namespace-URI signature).</summary>
        public static XmpResult ParseJpegSegment(ReadOnlySpan<byte> d, int payloadStart, int payloadLen)
        {
            var res = new XmpResult();
            if (payloadLen <= Header.Length) { res.AddWarning("XMP segment too short."); return res; }
            int xmlStart = payloadStart + Header.Length;
            return ParseXmlPacket(d.Slice(xmlStart, payloadLen - Header.Length), res);
        }

        /// <summary>Parse a bare XMP packet (PNG iTXt / WebP "XMP " chunk carry it without the JPEG signature).</summary>
        public static XmpResult ParseXmlPacket(ReadOnlySpan<byte> utf8Bytes, XmpResult? into = null)
        {
            var res = into ?? new XmpResult();
            try
            {
                string xml = Encoding.UTF8.GetString(utf8Bytes);
                int lt = xml.IndexOf('<');
                if (lt < 0) { res.AddWarning("XMP packet contains no XML."); return res; }
                xml = xml.Substring(lt);
                if (xml.Length > MaxXmlChars) { res.AddWarning($"XMP packet too large ({xml.Length} chars); skipped."); return res; }

                var doc = LoadHardened(xml);

                res.Rights = Collect(doc, "rights");
                res.Creators = Collect(doc, "creator");
                res.Titles = Collect(doc, "title");
                res.Descriptions = Collect(doc, "description");
                res.Keywords = Collect(doc, "subject");
                res.UsageTerms = Collect(doc, "UsageTerms");
                res.Marked = Collect(doc, "Marked").FirstOrDefault();
                res.WebStatement = Collect(doc, "WebStatement").FirstOrDefault();
                res.Credit = Collect(doc, "Credit").FirstOrDefault();
                res.Source = Collect(doc, "Source").FirstOrDefault();
                res.Headline = Collect(doc, "Headline").FirstOrDefault();

                res.Present = res.Rights.Count > 0 || res.Creators.Count > 0 || res.Titles.Count > 0
                           || res.Descriptions.Count > 0 || res.Keywords.Count > 0
                           || res.Marked != null || res.WebStatement != null || res.Credit != null;
            }
            catch (Exception ex) { res.AddWarning($"XMP parse error: {ex.GetType().Name}: {ex.Message}"); }
            return res;
        }

        private static XDocument LoadHardened(string xml)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,   // no DTDs => no entity-expansion bombs
                XmlResolver = null,                       // no external entity / network fetch (XXE)
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,      // skips the <?xpacket?> wrappers cleanly
                IgnoreWhitespace = true,
            };
            using var sr = new StringReader(xml);
            using var xr = XmlReader.Create(sr, settings);
            return XDocument.Load(xr);
        }

        /// <summary>
        /// Collect values for a property by local name, namespace-agnostic. Handles the
        /// element form (rdf:Alt/Seq/Bag &gt; rdf:li) and the attribute form (property
        /// carried as an attribute on an rdf:Description).
        /// </summary>
        private static List<string> Collect(XDocument doc, string localName)
        {
            var results = new List<string>();

            foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == localName))
            {
                var lis = el.Descendants()
                            .Where(x => x.Name.LocalName == "li")
                            .Select(x => x.Value.Trim())
                            .Where(s => s.Length > 0)
                            .ToList();
                if (lis.Count > 0) results.AddRange(lis);
                else { var v = el.Value.Trim(); if (v.Length > 0) results.Add(v); }
            }

            foreach (var el in doc.Descendants())
                foreach (var attr in el.Attributes())
                    if (attr.Name.LocalName == localName)
                    {
                        var v = attr.Value.Trim();
                        if (v.Length > 0) results.Add(v);
                    }

            var seen = new HashSet<string>();
            return results.Where(seen.Add).ToList(); // distinct, order-preserving
        }
    }
}
