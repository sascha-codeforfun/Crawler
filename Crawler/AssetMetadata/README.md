# Crawler.AssetMetadata

A native, dependency-free, **clean-room** reader for embedded image metadata —
EXIF, IPTC and XMP — across **JPEG, PNG and WebP**. Built to replace the
`MetadataExtractor` NuGet package (and its transitively-licensed XMP dependency)
so the host project can be distributed under a single MIT licence with a clean
provenance chain.

## Why this exists

The previous metadata path pulled in `MetadataExtractor`, whose XMP handling
descended from an Adobe-licensed base whose licence text is no longer reliably
fetchable from source — an unacceptable gap for redistribution. This component
removes that dependency entirely. It reads everything from first principles
against published, open specifications.

## Clean-room provenance

Every file was written **solely from public specifications**, with no third-party
implementation consulted. Each source file carries an `SPDX-License-Identifier: MIT`
line and a header naming the exact spec(s) it derives from. The specifications used:

| Area | Specification |
|------|---------------|
| JPEG framing | ITU-T T.81, JFIF |
| TIFF / EXIF | Adobe TIFF 6.0, CIPA DC-008 (Exif) |
| IPTC | IPTC-IIM, Adobe Photoshop image-resource (8BIM) structure |
| XMP | ISO 16684-1 / Adobe XMP, W3C RDF/XML |
| PNG | W3C / ISO 15948 (chunked structure, `eXIf`, `iTXt`) |
| WebP | Google WebP Container Specification (RIFF, `EXIF`/`XMP ` chunks) |

XMP is an **open ISO standard** (ISO 16684, RDF/XML); it is parsed here with the
.NET built-in `System.Xml`. The earlier licensing problem was the third-party XMP
*library*, not the *format* — so XMP support is retained with no licence risk.

## What it reports — and what it does not

The output is **descriptive, never a verdict**. It states which fields are present
and their values (e.g. `GPSLocation=48.858222, 2.2945`, `Copyright=© 2024 …`). It
never labels anything a "leak" or "sensitive" — the same GPS coordinates are a
non-issue for a corporate office and an exposure for a private residence, and only
the human reviewing the log has the context to decide. **Silence means "not found
/ could not read here"**, never an assertion that a field is absent.

It reads, per image, the three questions the host cares about: *what metadata is
present*, *is there a copyright notice*, and *is identifying device/location/
software information present*.

## Robustness (untrusted input)

Crawled images are untrusted web content, so the reader is hardened accordingly:

- **Never throws.** Malformed structure degrades to warnings; the caller keeps
  scanning. All multi-byte reads are bounds-checked and endian-correct.
- **XML attack surface closed.** XMP is parsed through an `XmlReader` with
  `DtdProcessing = Prohibit` and no resolver, neutralising entity-expansion
  ("billion laughs") and external-entity (XXE) attacks. An XMP packet over ~1 MB
  is skipped rather than parsed.
- **Volume caps** (applied by `AssetMetadataReportRenderer`): each value is capped
  to ~200 chars, each column to at most 100 fields (with a `(+K more)` marker) and
  a hard ~4 KB ceiling — so one crafted image cannot bloat the log. Every value is
  also passed through the host's field sanitiser before it reaches a log column.

## Public API

```csharp
using Crawler.AssetMetadata;

// 1. Read (never throws; unreadable input yields an empty result):
AssetMetadata meta = AssetMetadataReader.Read(filePath);
//    meta.Format, meta.Exif, meta.Iptc, meta.Xmp, meta.Location,
//    meta.ExifItems / IptcItems / XmpItems  (flat name/value lists)

// 2. Render to log columns (inject the host sanitiser; caps applied internally):
AssetMetadataFinding f = AssetMetadataReportRenderer.Build(
    meta, s => IssueLogWriter.SanitizeField(s).Cleaned);
//    f.HasFinding, f.Detail, f.Context, f.Exif, f.Iptc, f.Xmp
```

A finding is produced only when at least one curated category is present (GPS,
camera make/model, software, author/artist/by-line, copyright, camera owner /
body / lens serial, original timestamp, XMP rights/creator/marked/web-statement,
IPTC by-line/copyright/credit/source). When produced, the `Exif` / `Iptc` / `Xmp`
columns carry the **full** (capped) inventory for the operator to read.

## Integration with AssetQuality

`AssetQuality.cs` is wired to this component in `CheckMetadata`. The asset log
(`25-asset-quality.log`) gains the live URL and the three metadata blocks:

```
Filename | Url | IssueType | Detail | Context | Exif | Iptc | Xmp
```

- **Filename** — on-disk name (`Path.GetFileName`), the match key.
- **Url** — the live URL from the crawl index. Populated for every row; emitted
  empty only in a structurally-impossible index miss (the crawl's integrity check
  halts upstream if disk and index disagree), with no recovery/fallback logic so
  the guarantee stays visible.
- **Detail** — category tokens joined with `+` (also written to the shared ledger's
  `SourceLabel`).
- **Context** — a short presence marker (also the ledger's `Excerpt`).
- **Exif / Iptc / Xmp** — full capped inventory; present-but-empty on
  `ASSET_SIZE` / `ASSET_DIMENSIONS` rows so every row has the same column count
  (append-safe for name-based Power Query / M-Code consumption).

The shared ledger (`IssueTracking.IssueRecord`) is **unchanged** — it still points
to findings via `SourceLabel`/`Excerpt`; the three block columns are log-only.

## Scope notes

- **GIF**: recognised; nothing is extracted (no standard embedded EXIF/IPTC/XMP).
- **XMP**: the main packet is read. **Extended XMP** (a secondary packet split
  across additional JPEG APP1 segments) is not reassembled. JPEG XMP, PNG `iTXt`
  (`XML:com.adobe.xmp`, uncompressed), and WebP `XMP ` are all handled.
- **IPTC**: standard IIM datasets. A Photoshop IRB split across multiple APP13
  segments is reassembled before parsing.
- **MakerNote**: presence/size is reported; proprietary MakerNote contents are not
  decoded.
- Multi-byte text honours the IPTC `1:90` coded-character-set marker (UTF-8 vs
  Latin-1); EXIF ASCII and XMP are UTF-8/ASCII per spec.

## Licence

MIT. Each file carries an SPDX identifier; fold these sources into the host
project's existing MIT `LICENSE`. No separate licence file or third-party notice
is required.
