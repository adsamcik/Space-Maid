#!/usr/bin/env python3
"""Project the supplied registry CSVs into deterministic filename-only resources.

Usage: python tools/Build-FileFormatRegistry.py extensions.csv formats.csv
       format_extensions.csv [--output-directory DIRECTORY]

The projection keeps association-level evidence and unresolved references. It
does not infer missing formats, naming rules, confidence scores or reuse terms.
"""

import argparse
import csv
import gzip
import hashlib
import io
import json
from pathlib import Path
import sys


SCHEMAS = {
    "extensions.csv": (
        "extension_id", "extension", "is_compound", "lexical_kind", "semantic_kinds",
        "original_case_forms", "format_ids", "source_record_ids", "has_multiple_formats",
        "distinct_meaning_count", "identification_limitations",
    ),
    "formats.csv": (
        "format_id", "canonical_name", "description", "description_source_record_id",
        "record_kind", "version", "primary_category_id", "primary_family_id", "representation",
        "mime_types", "typical_applications", "operating_systems", "domains", "lifecycle_status",
        "specification_status", "specification_disclosure", "source_record_ids", "identity_basis",
        "identity_review_status", "limitations",
    ),
    "format_extensions.csv": (
        "association_id", "format_id", "extension_id", "association_kinds", "evidence",
        "typical_role_ids", "notes",
    ),
}
ARRAY_FIELDS = {
    "extensions.csv": (
        "semantic_kinds", "original_case_forms", "format_ids", "source_record_ids", "identification_limitations",
    ),
    "formats.csv": (
        "representation", "mime_types", "typical_applications", "operating_systems", "domains",
        "specification_disclosure", "source_record_ids", "identity_basis", "limitations",
    ),
    "format_extensions.csv": ("association_kinds", "evidence", "typical_role_ids", "notes"),
}
SEMANTIC_KINDS = {
    "format_association", "family_hint", "classification_hint", "language_syntax",
    "filename_convention", "declaration_convention", "reviewed_collection_association",
    "composition", "composition_inner", "composition_outer",
}
ASSOCIATION_KINDS = SEMANTIC_KINDS - {"family_hint", "classification_hint", "composition"}
COMPOSITION_KINDS = {"composition_inner", "composition_outer"}
EVIDENCE_FIELDS = (
    "source_id", "source_record_id", "source_url", "evidence_locator", "original_suffix",
    "case_sensitive", "original_glob_rules", "upstream_forbids_extension_only_identification",
    "content_verified", "verification_status",
)


class InputError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise InputError(message)


def ascii_fold(value):
    return value.translate(str.maketrans("ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz"))


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"Duplicate JSON property: {key}")
        result[key] = value
    return result


def reject_constant(value):
    raise InputError(f"Non-finite JSON value: {value}")


def read_csv(path, name):
    raw = path.read_bytes()
    reader = csv.DictReader(io.StringIO(raw.decode("utf-8-sig"), newline=""), strict=True)
    require(reader.fieldnames is not None and len(reader.fieldnames) == len(SCHEMAS[name])
            and set(reader.fieldnames) == set(SCHEMAS[name]), f"{name}: unexpected columns")
    rows = []
    for number, row in enumerate(reader, 2):
        require(None not in row and all(value is not None for value in row.values()),
                f"{name}, record {number}: wrong field count")
        for field in ARRAY_FIELDS[name]:
            value = json.loads(row[field], object_pairs_hook=unique_object, parse_constant=reject_constant)
            require(isinstance(value, list), f"{name}, record {number}: {field} must be a JSON array")
            if field != "evidence":
                require(all(isinstance(item, str) and item for item in value),
                        f"{name}, record {number}: {field} must contain nonempty strings")
                require(len(value) == len(set(value)), f"{name}, record {number}: duplicate {field} values")
            row[field] = value
        rows.append(row)
    require(rows, f"{name}: no records")
    return rows, hashlib.sha256(raw).hexdigest()


def keyed(rows, key, name):
    result = {}
    for row in rows:
        identity = row[key]
        require(identity and not any(ord(char) < 32 for char in identity), f"{name}: invalid {key}")
        require(identity not in result, f"{name}: duplicate {key} {identity}")
        result[identity] = row
    return result


def csv_bool(value, label):
    require(value in ("true", "false"), f"{label}: expected true or false")
    return value == "true"


def validate_suffix(suffix):
    require(suffix.startswith(".") and len(suffix) > 1 and not suffix.endswith(".")
            and not any(char in '\\/:*?"<>|' or char.isspace() or ord(char) < 32 for char in suffix),
            f"Invalid literal suffix: {suffix!r}")
    require(suffix == ascii_fold(suffix), f"Suffix is not ASCII-case-normalized: {suffix!r}")


def project(extension_rows, format_rows, association_rows, input_hashes):
    extensions = keyed(extension_rows, "extension_id", "extensions.csv")
    formats = keyed(format_rows, "format_id", "formats.csv")
    associations = keyed(association_rows, "association_id", "format_extensions.csv")
    suffixes = set()
    for row in extension_rows:
        suffix = row["extension"]
        validate_suffix(suffix)
        require(row["extension_id"] == "ext:" + suffix, f"Extension ID does not match suffix: {suffix}")
        require(suffix not in suffixes, f"Duplicate suffix: {suffix}")
        suffixes.add(suffix)
        compound = csv_bool(row["is_compound"], suffix)
        require(compound == (suffix.count(".") > 1), f"Incorrect compound flag: {suffix}")
        require(row["lexical_kind"] == ("compound_dotted_suffix" if compound else "single_dotted_suffix"),
                f"Incorrect lexical kind: {suffix}")
        require(set(row["semantic_kinds"]) <= SEMANTIC_KINDS, f"Unrecognized semantic kind: {suffix}")
        require(all(ascii_fold(value) == suffix for value in row["original_case_forms"]),
                f"Original case spelling does not match suffix: {suffix}")
        require(csv_bool(row["has_multiple_formats"], suffix) == (len(row["format_ids"]) > 1),
                f"Incorrect multiple-format flag: {suffix}")
        require(row["distinct_meaning_count"].isdigit(), f"Invalid meaning count: {suffix}")
        require(all(identity in formats for identity in row["format_ids"]), f"Unknown format reference: {suffix}")
    for row in format_rows:
        require(row["canonical_name"].strip() and row["primary_category_id"].strip(),
                f"Format name or category missing: {row['format_id']}")

    by_extension = {identity: [] for identity in extensions}
    pairs = set()
    for row in association_rows:
        require(row["format_id"] in formats and row["extension_id"] in extensions,
                f"Association has an unknown foreign key: {row['association_id']}")
        pair = (row["format_id"], row["extension_id"])
        require(pair not in pairs, f"Duplicate format/extension pair: {pair}")
        pairs.add(pair)
        require(row["association_kinds"] and set(row["association_kinds"]) <= ASSOCIATION_KINDS,
                f"Unrecognized association kinds: {row['association_id']}")
        require(row["evidence"], f"Association has no evidence: {row['association_id']}")
        extension = extensions[row["extension_id"]]
        source_ids = set(extension["source_record_ids"])
        for evidence in row["evidence"]:
            require(isinstance(evidence, dict)
                    and set(evidence) == set(EVIDENCE_FIELDS) | {"association_kind", "upstream_rule_weights"},
                    f"Unexpected evidence schema: {row['association_id']}")
            for field in ("source_id", "source_record_id", "source_url", "evidence_locator", "original_suffix", "verification_status", "association_kind"):
                require(isinstance(evidence[field], str) and evidence[field], f"Missing evidence {field}: {row['association_id']}")
            require(evidence["source_record_id"] in source_ids,
                    f"Evidence is not referenced by its extension: {row['association_id']}")
            require(ascii_fold(evidence["original_suffix"]) == extension["extension"],
                    f"Evidence spelling does not match suffix: {row['association_id']}")
            require(evidence["case_sensitive"] is None or isinstance(evidence["case_sensitive"], bool),
                    f"Invalid evidence case policy: {row['association_id']}")
            require(isinstance(evidence["upstream_forbids_extension_only_identification"], bool)
                    and evidence["content_verified"] is False, f"Evidence is not filename-only: {row['association_id']}")
            require(isinstance(evidence["original_glob_rules"], list)
                    and all(isinstance(rule, dict) and isinstance(rule.get("pattern"), str) and rule["pattern"]
                            for rule in evidence["original_glob_rules"]), f"Invalid source glob metadata: {row['association_id']}")
            require(isinstance(evidence["upstream_rule_weights"], list)
                    and all(isinstance(weight, str) for weight in evidence["upstream_rule_weights"]),
                    f"Invalid source weight metadata: {row['association_id']}")
        by_extension[row["extension_id"]].append(row)

    ordered_formats = sorted(format_rows, key=lambda row: row["format_id"])
    format_indexes = {row["format_id"]: index for index, row in enumerate(ordered_formats)}
    lookup = []
    for extension in sorted(extension_rows, key=lambda row: ascii_fold(row["extension"])):
        rows = by_extension[extension["extension_id"]]
        require(set(extension["format_ids"]) == {row["format_id"] for row in rows},
                f"Forward/reverse format mapping differs: {extension['extension']}")
        candidates, inner, outer, case_rules, joined_sources = set(), set(), set(), set(), set()
        forbids = False
        for row in rows:
            index = format_indexes[row["format_id"]]
            kinds = set(row["association_kinds"])
            if kinds - COMPOSITION_KINDS:
                candidates.add(index)
            if "composition_inner" in kinds:
                inner.add(index)
            if "composition_outer" in kinds:
                outer.add(index)
            for evidence in row["evidence"]:
                joined_sources.add(evidence["source_record_id"])
                forbids |= evidence["upstream_forbids_extension_only_identification"]
                if evidence["case_sensitive"] is True:
                    case_rules.add((evidence["original_suffix"], index))
        lookup.append({
            "Suffix": extension["extension"], "CandidateIndexes": sorted(candidates),
            "InnerIndexes": sorted(inner), "OuterIndexes": sorted(outer),
            "HasClassificationHints": "classification_hint" in extension["semantic_kinds"],
            "HasUnjoinedEvidence": bool(set(extension["source_record_ids"]) - joined_sources),
            "ForbidsExtensionOnly": forbids,
            "Conventions": sorted(set(extension["semantic_kinds"]) & {"declaration_convention", "filename_convention"}),
            "CaseRules": [{"Spelling": spelling, "FormatIndex": index} for spelling, index in sorted(case_rules)],
        })
    fast = {
        "SchemaVersion": 1, "ExtensionCount": len(extension_rows), "FormatCount": len(format_rows),
        "AssociationCount": len(association_rows), "InputHashes": input_hashes,
        "Formats": [{"Id": row["format_id"], "Name": row["canonical_name"], "CategoryId": row["primary_category_id"]}
                    for row in ordered_formats], "Extensions": lookup,
    }
    provenance = {
        "SchemaVersion": 1, "InputHashes": input_hashes,
        "Formats": [{"format_id": row["format_id"], "source_record_ids": sorted(row["source_record_ids"])}
                    for row in ordered_formats],
        "Associations": [{
            "association_id": row["association_id"], "format_id": row["format_id"], "extension_id": row["extension_id"],
            "association_kinds": sorted(row["association_kinds"]), "notes": sorted(row["notes"]),
            "typical_role_ids": sorted(row["typical_role_ids"]),
            "evidence": sorted(({field: evidence[field] for field in EVIDENCE_FIELDS} for evidence in row["evidence"]),
                               key=lambda evidence: json.dumps(evidence, ensure_ascii=False, sort_keys=True)),
        } for row in sorted(association_rows, key=lambda row: row["association_id"])],
    }
    return fast, provenance


def compressed_json(document):
    raw = json.dumps(document, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")
    output = io.BytesIO()
    with gzip.GzipFile(fileobj=output, mode="wb", filename="", mtime=0, compresslevel=9) as stream:
        stream.write(raw)
    return output.getvalue(), len(raw)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("extensions", type=Path)
    parser.add_argument("formats", type=Path)
    parser.add_argument("format_extensions", type=Path)
    parser.add_argument("--output-directory", type=Path,
                        default=Path(__file__).resolve().parents[1] / "src/SpaceMaid.Core/Analysis/Data")
    args = parser.parse_args()
    csv.field_size_limit(64 * 1024 * 1024)
    inputs = {"extensions.csv": args.extensions, "formats.csv": args.formats, "format_extensions.csv": args.format_extensions}
    rows, hashes = {}, {}
    for name, path in inputs.items():
        rows[name], hashes[name] = read_csv(path, name)
    fast, provenance = project(rows["extensions.csv"], rows["formats.csv"], rows["format_extensions.csv"], hashes)
    resources = {}
    for name, document in (("FileFormatRegistry.json.gz", fast), ("FileFormatRegistry.provenance.json.gz", provenance)):
        resources[name] = compressed_json(document)
    require(hashes == {name: hashlib.sha256(path.read_bytes()).hexdigest() for name, path in inputs.items()},
            "An input CSV changed during generation; no output was written.")
    output = args.output_directory.resolve()
    output.mkdir(parents=True, exist_ok=True)
    receipt = {"ExtensionCount": fast["ExtensionCount"], "FormatCount": fast["FormatCount"],
               "AssociationCount": fast["AssociationCount"], "InputHashes": hashes, "Outputs": {}}
    for name, (compressed, uncompressed_size) in resources.items():
        (output / name).write_bytes(compressed)
        receipt["Outputs"][name] = {"Bytes": len(compressed), "JsonBytes": uncompressed_size,
                                    "Sha256": hashlib.sha256(compressed).hexdigest()}
    print(json.dumps(receipt, indent=2, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (InputError, OSError, UnicodeError, csv.Error, json.JSONDecodeError) as error:
        print(f"Registry generation failed: {error}", file=sys.stderr)
        sys.exit(1)
