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
| Return-schema breaking | Breaking | An output guarantee is lost: a required property becomes optional, a type widens to include null, an enum expands, or a bound relaxes. |
| Return-schema additive | Additive | An output guarantee is strengthened: a property is added, becomes required, or has a narrower type, enum, or bound. |
| Description changed | Risky | “Search orders” becomes “Search invoices”; model selection behavior may change. |
| Approval/safety metadata changed | Risky or Breaking | An explicit approval-required wrapper is added or removed. |
| Unknown schema semantics | Risky and unsupported | A vendor keyword or composition changes in a way the package cannot prove safe. |
| Reference change | Risky and unsupported | A valid `$ref` target or any `$ref` sibling changes. `$ref` is validated as a URI-reference without dereferencing. |
| Schema default or format change | Risky and unsupported | Framework-emitted `default` or `format` metadata changes; the closed model captures it but does not invent compatibility semantics. |

The complete accepted keyword and value-shape surface is the [JSON Schema support matrix](JSON-SCHEMA-SUPPORT.md). The OpenAPI `nullable` keyword is unsupported; standard JSON Schema type arrays express nullability. Numeric comparisons use JSON data-model equality, so `1`, `1.0`, and `1e0` are representation-equivalent in enums and numeric constraints.

`Informational` is reserved for a future non-compatibility note; current structural differences are never downgraded to clean merely because they are unfamiliar. A `Breaking` change is never accepted by `Accept` without the separate `AcceptWithBreakingReview` call.

Input compatibility preserves what existing callers may send. Output compatibility preserves what existing consumers may rely on: newly permitted outputs are breaking. For example, allowing a `null` result where the baseline promised a string is breaking; removing `null` from a return union is additive. `integer` is a subset of `number`. Incomparable types are breaking in either role. Property removal remains conservatively breaking; enum replacements with both additions and removals remain unsupported.

Supported member edits compose: multiple property additions/removals, retained-property edits, and required-name swaps report their component changes without an artificial unsupported parent change. The strongest component determines the diff severity. Nested properties and array items follow the same input/output distinction.

Declaration membership in `properties` and presence membership in `required` are independent components, including required names without declarations. Each meaningful component is reported and counts toward `MaxChanges`. A required name without a declaration already denotes a member whose values are unconstrained: adding its declaration compares the value schema against `{}`, just as an explicit unconstrained declaration does. Making it optional cannot conceal type, enum, bound, nested-required, or unsupported metadata changes. An added unconstrained declaration alone emits no change. Genuinely new optional members retain the additive policy. For example, changing a return schema from `{"type":"object","required":["id"]}` to `{"type":"object","properties":{"id":{"type":"string"}}}` reports an additive type restriction and a breaking loss of required presence: the candidate permits `{}`. Ordinary `Accept` rejects this diff; `AcceptWithBreakingReview` permits it after review. Adding a declaration does not cancel a lost presence guarantee. Declaration removal remains conservatively breaking in both schema roles, even when a simultaneous required removal is additive for inputs.

Each diff is bound to the immutable baseline and candidate instances passed to `Compare`. Both acceptance methods require that exact candidate instance. Reparsed, separately captured, reversed, or unrelated candidates require a fresh comparison against the intended baseline. Unsupported comparisons remain unacceptable through either method.

Public change paths start at `$` for tool/catalog metadata, `$.inputSchema` for input schemas, or `$.returnSchema` for return schemas. Tool description, input-root description, and return-root description changes therefore have distinct paths: `$.description`, `$.inputSchema.description`, and `$.returnSchema.description`. Schema diagnostic locations use the same roots. Fixed keyword segments use dots; dynamic property names use JSON-quoted brackets and array elements use numeric indices. For example, `$.inputSchema.properties["a.properties.b"].enum` differs from `$.inputSchema.properties["a"].properties["b"].enum`. Quoting also distinguishes empty names, quotes, brackets, slashes, tildes, backslashes, control characters, and Unicode names.
