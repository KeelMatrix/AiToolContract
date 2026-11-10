# Changelog

This file records consumer-facing changes to KeelMatrix.AiToolContract.

## [Unreleased]

### Fixed

- Adds repository classification metadata for website discovery of existing releases and matching tags for future packages, with source and archive validation.

## [0.1.0] - 2026-11-08

### Added

- Captures Microsoft.Extensions.AI tool names, descriptions, input and return JSON Schemas, and approval metadata without invoking tools or models.
- Provides deterministic, versioned JSON baselines with explicit acceptance of contract changes; verification never overwrites a baseline or automatically approves breaking changes.
- Classifies supported input and return contract changes as Breaking, Risky, Additive, or Informational, including description and approval metadata changes.
- Rejects malformed, duplicate, ambiguous, and unsupported contracts with explicit diagnostics and configurable resource limits.
- Operates offline without provider SDKs, API keys, telemetry, or background activity. Structural compatibility does not guarantee unchanged model tool-selection behavior.
