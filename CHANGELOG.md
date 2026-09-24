# Changelog

This file records consumer-facing changes to KeelMatrix.AiToolContract.

## [Unreleased]

### Fixed

- Closed unsupported JSON Schema keywords and malformed shapes by construction, including unknown members and empty applicator arrays.
- Kept reference and definition changes unsupported even when another schema change is classified, and canonicalized representation-equivalent JSON numbers without reporting drift.

## [0.1.0-rc.1]

### Added

- Deterministic capture and verification of Microsoft.Extensions.AI tool names, descriptions, JSON Schemas, return schemas, and approval metadata.
- Versioned baseline JSON with bounded canonicalization, duplicate-member rejection, resource-limit diagnostics, and compatibility classifications for supported contract changes.
- Offline package-consumer workflow using the real Microsoft.Extensions.AI abstractions without model, provider, API-key, or network access.
