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

Tool identities use ordinal, exact matching. Duplicate identities are an explicit error. Names are not normalized, case-folded, or guessed. A rename therefore appears as a removal and an addition.

The default limits are 256 tools, 1 MiB per schema, depth 64, 10,000 object properties, 10,000 array items, and 5,000 reported changes. Supply `AiToolContractLimits` to tighten them. Limits fail safely with a resource diagnostic before unbounded comparison work.
