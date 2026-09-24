# Compatibility categories and change examples

The verifier compares contracts by tool identity. It does not infer renames: a renamed tool is one removed tool and one added tool.

| Change | Category | Example |
| --- | --- | --- |
| Tool added | Additive | `search_orders` is added to the catalog. |
| Tool removed | Breaking | `delete_order` disappears. |
| Required parameter added | Breaking | `region` is added to `required`. |
| Parameter removed | Breaking | `query` is removed from `properties`. |
| Optional parameter added | Additive | `limit` is added to `properties` but not `required`. |
| Type changed | Breaking | `limit` changes from `integer` to `string`. |
| Standard type union narrowed | Breaking | `type: ["string", "null"]` becomes `type: "string"`. |
| Standard type union widened | Additive | `type: "string"` becomes `type: ["string", "null"]`. |
| Enum narrowed | Breaking | `new, open, closed` becomes `new, open`. |
| Enum expanded | Additive | `new, open` becomes `new, open, closed`. |
| Constraint narrowed | Breaking | `minimum: 1` becomes `minimum: 10`. |
| Constraint widened | Additive | `maximum: 100` becomes `maximum: 1000`. |
| Return-schema breaking | Breaking | A return property is removed or its type narrows. |
| Return-schema additive | Additive | An optional return property is added. |
| Description changed | Risky | “Search orders” becomes “Search invoices”; model selection behavior may change. |
| Approval/safety metadata changed | Risky or Breaking | An explicit approval-required wrapper is added or removed. |
| Unknown schema semantics | Risky and unsupported | A vendor keyword or composition changes in a way the package cannot prove safe. |
| Reference change | Risky and unsupported | A valid `$ref` target or any `$ref` sibling changes. `$ref` is validated as a URI-reference without dereferencing. |
| Schema default or format change | Risky and unsupported | Framework-emitted `default` or `format` metadata changes; the closed model captures it but does not invent compatibility semantics. |

The complete accepted keyword and value-shape surface is the [JSON Schema support matrix](JSON-SCHEMA-SUPPORT.md). The OpenAPI `nullable` keyword is unsupported; standard JSON Schema type arrays express nullability. Numeric comparisons use JSON data-model equality, so `1`, `1.0`, and `1e0` are representation-equivalent in enums and numeric constraints.

`Informational` is reserved for a future non-compatibility note; current structural differences are never downgraded to clean merely because they are unfamiliar. A `Breaking` change is never accepted by `Accept` without the separate `AcceptWithBreakingReview` call.
