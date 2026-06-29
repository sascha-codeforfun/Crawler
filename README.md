# Crawler

A site crawler and content-quality analyzer for your **own** website. Point it at a
site, let it download a snapshot, and it runs a battery of checks over the captured
pages, scripts, PDFs and images — then writes the findings to per-topic logs you can
read, filter, or pull into a spreadsheet.

It is built to *find and report*, not to judge or fix: every check states what it
found and where, and leaves the decision to you. Findings can be triaged
interactively, tracked across runs, and turned into ready-to-send tickets for
whoever fixes them. It runs unattended, completes a
full pass over a mid-size site in minutes, and ships as a single self-contained
executable that needs nothing installed on the target machine.

---

## What it actually does

### Spell-check — the core

Multi-language spell-checking is the heart of the tool, and it reaches places most
checkers don't:

- **Page text**, across **as many dictionaries as you care to configure** — tested
  with up to **26 languages** in a single run.
- **Inline JavaScript string literals** — the user-visible strings embedded in a
  page's own `<script>` blocks, which ordinary spell-checkers never see.
- **String literals inside `.js` files** — the same, for external scripts.
- **Per-element language** — each run of text is checked against the language
  declared closest to it (its nearest `lang` attribute, with the page language as the
  floor), so a foreign-language island inside an otherwise-English page is checked
  against the right dictionary instead of drowning in false positives — at virtually
  no added cost.

Dictionaries are Hunspell `.dic`/`.aff` files. They are **operator-supplied** (none
ship with the app); on a fresh install the app points you at where to put them and
how to wire them up. An optional **foreign-words list** lets you allow researched,
comment-justified words from any script without loosening your main dictionaries.

### Content quality — the other stronghold

A battery of checks for the small, easy-to-miss defects that slip into published
HTML and quietly degrade text quality. Each is reported with the file and the
surrounding context. What it looks for:

- **Typographic quotes** — an opener with no closer, a closer with no opener, an
  opener and closer from different quote systems, or a page mixing more than one
  quote system. A lower-confidence "ambiguous" tier is flagged separately for review.
- **Ligature characters** found in visible text.
- **Invisible characters** — control, bidirectional, zero-width characters and stray
  byte-order marks, detected across the whole page (with `<title>`, `<meta>` and
  editor-authored body text each called out specifically).
- **Mixed alphabets (homoglyphs)** — a single word built from more than one script,
  where look-alike letters stand in for each other — e.g. a Latin `A` where the
  Cyrillic `А` was meant. Same glyph, different character, so it silently matches no
  dictionary; reported with the impostor character pinpointed.
- **Confusable punctuation in code** — a non-ASCII look-alike of an ASCII syntax
  character wedged between letters (e.g. a stray full-width or prolonged-sound mark
  inside what should read `padding-top`), reported with the plain-ASCII form it
  should have been.
- **Decomposed & mixed-normalization text** — visible text stored in decomposed
  Unicode form (a base letter plus a separate combining mark) where the composed form
  was meant: visually identical, but byte-fragile for site search, assistive
  technology, AI and anything else that compares without normalizing. Flags both
  fully decomposed text and pages that carry the same word in two different
  encodings. Scripts whose marks have no composed form (e.g. Arabic) are exempt.
- **Bleed** — an inline element butting against bare text with no separator, so two
  words run together into one (e.g. `helloWorld` where `hello World` was meant).
- **Anchor defects** — anchors with no visible text, anchors that close mid-word
  (a stray letter after the closing tag), and adjacent anchors with no separation.
- **Bare text in containers** — text sitting directly inside a container with no
  block wrapper.
- **Configured "unwanted" patterns** — your own list of strings/patterns that
  shouldn't appear in the raw HTML.
- **Malformed HTML** — content before the doctype, and HTML parse errors.
- **CMS template authoring defects** — structural mistakes that come from the
  template, not the content, including unbalanced `{{ }}` binding fences.
- **Language mismatch** — a page whose declared language doesn't match its text.

---

## Broken links & missing pages

Finds links that lead to **404s** and other dead ends across the crawled site — and,
crucially, reports **which page(s) link to each broken target**, so you know not just
*what* is broken but *where to fix it*. No configuration and nothing for the page to
declare: it walks what's actually there and tells you what's dead. For most site
owners this is the first thing they want, and it needs no setup to get it.

---

## The side battery

Smaller, focused checks that run alongside the two strongholds:

- **Image asset metadata** — flags images that were published with embedded
  metadata still attached: device, author, copyright and location details that often
  shouldn't ship on a public site, and agency/credit fields that can indicate a
  licensed asset. (Read with the app's own metadata reader — no third-party
  dependency.)
- **PDF quality markers** — reports the metadata and accessibility flags each PDF
  *declares about itself*: title, language, tags, structure tree, alt-text presence,
  and PDF/A and PDF/UA markers. It reports what the file claims, not whether those
  claims actually hold — so its real value is showing you which documents are
  *missing* the markers entirely. (Read with the app's own PDF reader — no
  third-party dependency.)
- **Redirect analysis** — surfaces redirect problems, including circular redirects.
- **Self-linking pages** — pages that link to themselves.
- **Canonical declaration issues** — problems with a page's declared canonical URL.
- **Resource bloat** — pages and assets heavier than they should be, against a
  configurable baseline — and for assets bloated by embedded Base64 data (a common
  cause), it decodes and saves the embedded files so you can see what they actually are.
- **Sitemap** — generates a sitemap from the data your pages declare.
- **SEO fields** — reports the SEO data your pages have set. This **reflects what's
  already on the page** — it does not generate, score, or optimize anything; if a
  page declares nothing, there is nothing to report.

…and a few more, each writing to its own log.

---

## Running it

- **Self-contained, single file.** The published executable bundles the .NET runtime
  and all dependencies, so it runs on a plain Windows 10 machine with **nothing
  installed** — copy the folder and run.
- A full pass over a mid-size site completes in minutes; it has been run end-to-end
  on low-end hardware (a 4-core Celeron) without trouble.
- Findings are written to per-topic log files in the snapshot folder, designed to be
  read directly or imported into a spreadsheet.

### Configuration

Settings live in `config.json`, which is **heavily commented** and acts as the
reference template. **Don't edit it directly** — copy it to `config.private.json`
and make your changes there. The app prefers `config.private.json` automatically,
keeps your real paths out of the shared template, and won't overwrite your settings
when you update.

### Dictionaries

Spell-check needs Hunspell `.dic`/`.aff` files, which you supply yourself (a good
starting point is the [LibreOffice dictionaries](https://github.com/LibreOffice/dictionaries)
collection). On first run the app creates a `dictionaries` folder with a readme
explaining exactly how to add and configure them.

---

## License

Crawler is released under the **MIT License** (see `LICENSE.txt`). It bundles a small
number of third-party components; their licences and notices are in the `licenses`
folder and summarised in `THIRD-PARTY-NOTICES.txt`. Dictionaries are not distributed
with the app and carry their own licences.
