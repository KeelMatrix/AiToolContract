# Changelog

This file records consumer-facing changes to KeelMatrix.AiToolContract.

## [Unreleased]

## [0.1.0-rc.1] - 2026-09-26

### Added

- Deterministic capture and verification of Microsoft.Extensions.AI tool names, descriptions, JSON Schemas, return schemas, and approval metadata.
- Versioned baseline JSON with bounded canonicalization, duplicate-member rejection, resource-limit diagnostics, and compatibility classifications for supported contract changes.
- Offline package-consumer workflow using the real Microsoft.Extensions.AI abstractions without model, provider, API-key, or network access.
- A closed normalized schema model for capture, baseline parsing, and comparison, with explicit review results for unsupported semantics.
- Compatibility classifications for enum, required-set, and type-union transitions in input and return schemas, together with validated metadata such as descriptions, defaults, formats, and URI-reference values.
- Input compatibility and output-guarantee classifications, independent declaration and required-membership changes, and acceptance bound to the compared candidate.
- Recursive unsupported-transition protection for newly required declarations, with schema locations retained in counter, enum-encoding, and malformed-string diagnostics.
- Distinct metadata/input/return diagnostic paths, lossless Unicode persistence, and bounded schema/enum processing and canonical output, including nested enum/default values and tool metadata.
