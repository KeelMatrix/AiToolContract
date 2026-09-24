# Changelog

This file records consumer-facing changes to KeelMatrix.AiToolContract.

## [Unreleased]

### Fixed

- Replaced the open-ended keyword comparison path with one closed normalized schema model shared by capture, baseline parsing, and comparison.
- Captured the `description`, `default`, and `format` fields emitted by the supported `AIFunctionFactory` dependency endpoints, validated `$ref` URI-references without dereferencing, and represented arbitrary JSON numeric exponents without `long` exhaustion.
- Required every version-one root and tool envelope member and rejected the OpenAPI `nullable` extension in favor of standard JSON Schema type arrays.

## [0.1.0-rc.1]

### Added

- Deterministic capture and verification of Microsoft.Extensions.AI tool names, descriptions, JSON Schemas, return schemas, and approval metadata.
- Versioned baseline JSON with bounded canonicalization, duplicate-member rejection, resource-limit diagnostics, and compatibility classifications for supported contract changes.
- Offline package-consumer workflow using the real Microsoft.Extensions.AI abstractions without model, provider, API-key, or network access.
