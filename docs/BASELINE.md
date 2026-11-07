# Baseline format and versioning

The baseline is a KeelMatrix-owned JSON document. Version one has this shape:

```json
{
  "schemaVersion": 1,
  "tools": [
    {
      "name": "search_orders",
      "description": "Search orders",
      "inputSchema": {
        "type": "object",
        "properties": {
          "query": { "type": "string" }
        },
        "required": ["query"]
      },
      "returnSchema": null,
      "requiresApproval": false
    }
  ]
}
```

`schemaVersion` is mandatory. Version one is the only accepted version; malformed and unrecognized versions fail closed with distinct diagnostics. The root must contain exactly `schemaVersion` and `tools`. Each tool must contain exactly `name`, `description`, `inputSchema`, `returnSchema`, and `requiresApproval`; `description` and `returnSchema` may be explicit JSON `null`, while `requiresApproval` must be an explicit boolean. Missing, extra, duplicate, or wrongly typed envelope members use the malformed-baseline diagnostic family. A future version must define its migration and compatibility rules before it is accepted.

Object properties are written in ordinal order. JSON Schema `required`, `enum`, and multi-valued `type` arrays are canonicalized as semantic sets because their order is not meaningful to JSON Schema. JSON numbers are canonicalized by data-model value, so lexical forms such as `1` and `1.0` are equivalent. Whitespace does not affect the baseline. A valid `$ref` URI-reference is preserved without dereferencing or networking; target and sibling changes are unsupported and cannot be accepted by either verifier acceptance method. The complete v1 surface is defined in the [JSON Schema support matrix](JSON-SCHEMA-SUPPORT.md); malformed schemas and valid forms whose semantics are not represented by the closed normalized model fail at normalization with a diagnostic.

Tool identities use ordinal, exact matching. Duplicate identities are an explicit error. Names are not normalized, case-folded, or guessed. A rename therefore appears as a removal and an addition. All persisted strings must contain valid Unicode scalar values. Unmatched UTF-16 surrogates in names, descriptions, schema strings, or property names fail capture; literal and escaped malformed baseline strings use `MalformedBaseline`. Valid supplementary-character pairs and U+FFFD are preserved without normalization, so successful capture round-trips without identity or description drift.

The default limits are 256 tools, 1 MiB per schema, depth 64, 10,000 object properties, 10,000 array items, and 5,000 reported changes. Supply `AiToolContractLimits` to tighten them. Limits fail safely with a resource diagnostic before unbounded comparison work. Parsing and serialization also apply one derived aggregate UTF-8 budget to the complete baseline envelope, tool names/descriptions, and canonical JSON. It is derived from the configured tool and per-schema limits, allows the complete per-schema allowance for each configured tool, and is applied to both raw input and canonical output.

`MaxChanges` counts emitted components. Meaningful value/declaration changes and a required-membership change for the same name count separately. Adding an unconstrained declaration for an already required name does not invent a new obligation or emit a representation-only component. Exceeding the limit throws a resource diagnostic; comparison never returns a partial diff for acceptance.

The supplied limits are re-enforced at every public capture, baseline-parse, comparison, and serialization entry point; a previously normalized or deserialized baseline does not bypass them. Parse and serialize round trips use the same aggregate baseline budget, so canonical expansion from escaping or normalization cannot make a parsed baseline fail later during serialization. `MaxProperties` counts normalized schema members, every actual JSON Schema `properties` entry, and object members in supported enum and default values. `MaxArrayItems` counts every supported array-valued schema or default family, including enum entries, required names, type unions, and arrays inside nested enum/default values. `MaxSchemaBytes` and `MaxSchemaDepth` apply to each schema, including schemas reached through nested properties and items. Boundary values are accepted and the first item beyond any bound returns a resource diagnostic, including when a depth calculation would otherwise overflow.

Logical depth counts the root schema as level one. A property schema or item schema adds one level; each enum entry or default value adds one, and each nested JSON array/object value adds one more. The `properties` map and outer `enum` array are representation containers rather than extra logical schema levels. JSON parsing allows the representation containers and baseline envelope separately, then enforces the same logical limit. A stack-safety ceiling of 256 logical levels applies even when a larger configured limit is supplied.

Canonical string encoding is measured cumulatively before allocation, including escape expansion in tool names and descriptions. Oversized output stops at the budget boundary. Existing `JsonElement` schema inputs are measured from their raw UTF-8 data before text copying. Enum values use reusable canonical semantic keys with sorting for uniqueness and linear merging for membership; numeric equality remains exact for nested values as well. Number coefficients lose leading and trailing zeros lexically before arbitrary-precision integer construction, preserving exact equality without a division per trailing zero.

Diagnostic locations use the escaped path convention in the [compatibility guide](COMPATIBILITY.md).

Arbitrary raw schema member names are quoted even when an early property-count check fails before keyword validation. Duplicate and unsupported-keyword failures use the same convention; unrecognized keywords remain rejected.

Schema property/array counter failures, enum-key encoding failures, and malformed string decoding retain the input or return root and the affected member or array index. If a property name cannot be decoded, the location identifies its containing object. Tightened-limit comparison and serialization retain that context too; complete-schema byte failures identify the schema root. Duplicate members are rejected by envelope validation and schema normalization, where their context is available.

Property and array counters use the normalized representation: an empty `properties` map is omitted, and a one-element `type` array becomes a scalar type. Neither contributes an extra container count after that normalization. Raw schema and baseline input still have independent UTF-8 byte limits before parsing.
