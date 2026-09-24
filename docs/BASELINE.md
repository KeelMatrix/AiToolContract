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

`schemaVersion` is mandatory. Version one is the only accepted version; malformed and unrecognized versions fail closed with distinct diagnostics. A future version must define its migration and compatibility rules before it is accepted.

Object properties are written in ordinal order. JSON Schema `required` and `enum` arrays are canonicalized as semantic sets because their order is not meaningful to JSON Schema; arrays such as `allOf`, `anyOf`, and `oneOf` retain their order. Whitespace does not affect the baseline. `$ref`, `$defs`, and `definitions` are preserved and compared; unsupported semantic changes require review.

Tool identities use ordinal, exact matching. Duplicate identities are an explicit error. Names are not normalized, case-folded, or guessed. A rename therefore appears as a removal and an addition.

The default limits are 256 tools, 1 MiB per schema, depth 64, 10,000 object properties, 10,000 array items, and 5,000 reported changes. Supply `AiToolContractLimits` to tighten them. Limits fail safely with a resource diagnostic before unbounded comparison work.

