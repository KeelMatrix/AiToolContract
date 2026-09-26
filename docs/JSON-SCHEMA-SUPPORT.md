# JSON Schema support matrix

This document defines the closed JSON Schema surface accepted by baseline version 1. Capture and baseline parsing normalize every schema into one internal model owned by this repository. Comparison operates only on that model; raw schema text is never used to decide equality. A construct that the model cannot represent fails closed at normalization with an `UnsupportedClassification` diagnostic.

The subset is derived from probes of `AIFunctionFactory` with `Microsoft.Extensions.AI.Abstractions` `10.0.0`, `10.10.0`, and `10.10.1`. The probes cover required and optional parameters, parameter descriptions, defaults, `DateTime`, `Uri`, `Guid`, enums, nullable values, arrays, and nested objects.

Transition coverage is model-derived. The rule table in `SchemaTransitionRules` is keyed by every property of the closed `NormalizedSchema` model and by its supported transition directions; the test suite reflects the model and asserts equality with both rule-table key sets. A missing model property, direction, or rule is reported as an unsupported classification rather than a clean result. The generator exercises every rule at the root and at depth two for both input and return schemas, so nested property, enum, constraint, type, and item transitions cannot be omitted from coverage by forgetting a hand-written matrix row.

## Closed keyword surface

| Keyword | Normalized value | Comparison behavior |
| --- | --- | --- |
| `$ref` | Valid URI-reference string; never dereferenced | Unchanged references are preserved; target or sibling changes are unsupported and require review |
| `description` | String | Changes are `Risky` because model behavior may change |
| `default` | Any JSON data-model value | Capture and round-trip are supported; changes are unsupported and require review |
| `format` | String | Capture and round-trip are supported; changes are unsupported and require review |
| `type` | Recognized type string or non-empty unique array of type strings | Union narrowing is `Breaking`; union widening is `Additive`; incomparable type changes are `Breaking` |
| `enum` | Non-empty array of unique JSON data-model values | Narrowing is `Breaking`; expansion is `Additive`; mixed changes require review |
| `items` | One schema object | Nested changes are classified; presence changes are unsupported |
| `properties` | Object whose values are schemas | Added/removed and nested changes are classified |
| `required` | Non-empty array of unique strings | Required input additions are `Breaking`; optional input additions are `Additive` |
| `minimum`, `exclusiveMinimum` | JSON number | Bound narrowing is `Breaking`; widening is `Additive` |
| `maximum`, `exclusiveMaximum` | JSON number | Bound narrowing is `Breaking`; widening is `Additive` |
| `minLength`, `maxLength`, `minItems`, `maxItems` | Non-negative integer JSON number | Bound narrowing is `Breaking`; widening is `Additive` |

Framework output differs between the tested endpoints: `10.10.0` may express nullable reference, object, and array values as a type union containing `null`, while `10.0.0` may use the non-null type with a `null` default. Both forms are represented by the same closed model and round-trip without a clean-comparison accident.

The OpenAPI `nullable` keyword is not part of this JSON Schema subset. Use a standard JSON Schema type array such as `["string", "null"]`. An explicit `nullable` member fails closed as unsupported.

## Fail-closed boundary

Boolean schemas, tuple-form or boolean `items`, composition keywords such as `allOf`/`anyOf`/`oneOf`, `$defs` and `definitions`, `additionalProperties`, `not`, `contains`, vendor keywords, and every other unlisted keyword are outside the normalized model. They fail at capture or baseline parsing at any depth. Wrong value kinds, duplicate members, empty `enum`/`required` arrays, duplicate set members, malformed JSON, and invalid `$ref` URI-reference text fail closed as well.

JSON numbers are parsed as a coefficient plus arbitrary-precision decimal exponent. Lexical forms such as `1`, `1.0`, and `1e0` are equal by JSON data-model value. The configured schema-byte limit bounds the input; exponent values are not truncated to `long`. If a future representation limit is reached, the result is an explicit `CanonicalizationOrResourceLimit` diagnostic, never `MalformedBaseline`.
