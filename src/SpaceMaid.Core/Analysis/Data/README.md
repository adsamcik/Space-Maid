# Built-in filename registry

The user supplied the 10 October 2026 *File Format and Extension Registry* research report and three companion CSVs: `extensions.csv`, `formats.csv`, and `format_extensions.csv`. The generated lookup contains all 9,263 normalized suffixes, 17,051 catalog identities (including variants and syntaxes), and 22,163 format/suffix associations. Catalog identities are not a verified count of globally unique underlying formats.

`FileFormatRegistry.json.gz` is embedded in Core and prepared once in memory. It contains stable format IDs, short labels, taxonomy categories, suffix relationships, documented composition constituents, naming conventions, case assertions and uncertainty flags. `FileFormatRegistry.provenance.json.gz` stays in the repository, outside the runtime lookup. It preserves input hashes, format/source-record references and short association evidence, including source URLs, restrictions, names and contextual roles. Long format descriptions, external source code and acquisition archives are omitted.

The three CSVs do not include `extension_lookup`, `classification_hints`, `filename_rules`, `case_rules`, `compositions`, `source_records` or `sources`. Consequently, this is a conservative projection, not a reproduction of every status or naming rule in the complete research package:

- Zero concrete candidates remain Unknown, including the 583 hint-only suffixes. Supplied hints are not promoted to a guessed format.
- Multiple concrete interpretations remain Ambiguous, even when versions share a useful category. Missing classification-hint details also preserve ambiguity; a sole PSB candidate does not establish certainty.
- A candidate category describes the known candidates, without claiming to cover an unresolved hint. Unclassified documented formats remain distinct from wholly unknown names.
- One documented inner/outer pair is a composed interpretation; independent alternatives stay visible. No arbitrary compression combinations are generated.
- Explicit case evidence can annotate a naming convention, but does not erase independently documented alternatives. Ordinary keys use ASCII folding only.
- A small supplement implements the report's Dockerfile, Makefile, `.gitignore`, `.env`, LICENSE and SQLite sidecar examples. It does not claim to implement the unavailable 522-row filename-rule table. Dotenv and license names are purpose hints with unknown format. No broad glob or regex rules run.

Lookup uses the provided Windows basename and longest registered literal ending. A leading dot is part of a basename, and trailing dots are preserved. The public inference result exposes the complete candidate list; tooltips show at most eight alphabetically presented candidate labels and the remaining count. Alphabetical presentation is not a preference ranking. Filename evidence never verifies file contents, encoding, codec availability, safety, ownership or deletability. Photos eligibility and cleanup detection use their existing independent rules.

Regenerate with Python's standard library; Python is not a build or runtime dependency:

```powershell
python tools/Build-FileFormatRegistry.py E:/Downloads/extensions.csv E:/Downloads/formats.csv E:/Downloads/format_extensions.csv
```

The generator validates schema, JSON arrays, unique keys and pairs, foreign references, literal suffixes and forward/reverse mappings. It rechecks input hashes before writing. Deterministic ordering and gzip headers permit byte-for-byte regeneration.

Input SHA-256:

| File | SHA-256 |
| --- | --- |
| extensions.csv | f5cf664f0fdbf3c31dd6ff991dc3cd7060ffca6853b8ea8dbcfc88c4e878c22e |
| formats.csv | d1e7b29e2fa5fe95cc9e07f948ebf3724a34faf7d3cc0fde8113797f1630d44e |
| format_extensions.csv | ae166739762420fa4c7405aa5cf2d6759dcd192d7f47caf53f8f9d9ca04ea28e |

## Attribution and reuse

The generated lookup and provenance sidecar are excluded from Space Maid's MIT license. See the project's `THIRD_PARTY_NOTICES.md` for the distinction between the authored implementation and this externally sourced collection.

The supplied research has mixed upstream provenance and no blanket MIT or CC0 license. The factual labels and relationships remain attributed through the provenance sidecar. The report identifies ArchiveTeam (CC0), dexvert (Unlicense with exceptions), PRONOM JSON (MIT), Tika (Apache-2.0), Linguist and mime-db (MIT), libmagic (BSD), Shared MIME-info (GPL-2.0-or-later), EDAM (CC-BY-SA-4.0), and specialist sources with per-record or unknown documentation terms. Consult the original source URLs and the report's source appendix for those terms; this projection does not establish new permission to redistribute upstream prose or relicense the whole collection. The omitted `sources` and source-record tables also mean this is not the original complete license/notice bundle.

The small naming supplement follows [SQLite's sidecar formats](https://sqlite.org/fileformat2.html), [TypeScript declaration conventions](https://www.typescriptlang.org/docs/handbook/modules/theory.html), [W3C SVG compression naming](https://www.w3.org/TR/SVG/mimereg.html), [Git ignore documentation](https://git-scm.com/docs/gitignore), and the examples in the supplied report. The implementation uses authored lookup code and factual registry projections; it does not execute imported source patterns or format parsers.
