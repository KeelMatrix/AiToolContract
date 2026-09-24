# JSON Schema support matrix

This document defines the JSON Schema surface accepted by baseline version 1. The implementation and its enumerative tests use the same matrix. A keyword not listed here, at any schema depth, fails closed with an unsupported diagnostic. A listed keyword with the wrong value shape is malformed or unsupported and never produces a clean result.

| Keyword | Required value shape | Change handling |
| --- | --- | --- |
| `$defs` | Object whose values are schemas | Preserved; any change is unsupported |
| `$ref` | String | Preserved; any target or sibling change is unsupported |
| `additionalProperties` | Schema or boolean; boolean form is unsupported | Any change is unsupported |
| `allOf` | Non-empty array of schemas | Any change is unsupported |
| `anyOf` | Non-empty array of schemas | Any change is unsupported |
| `contains` | Schema or boolean; boolean form is unsupported | Any change is unsupported |
| `definitions` | Object whose values are schemas | Preserved; any change is unsupported |
| `enum` | Non-empty array of unique JSON data-model values | Narrowing and expansion are classified |
| `exclusiveMaximum` | JSON number | Narrowing and expansion are classified |
| `exclusiveMinimum` | JSON number | Narrowing and expansion are classified |
| `items` | A schema or boolean; tuple-form arrays and boolean form are unsupported | Nested classified changes are supported; presence changes are unsupported |
| `maxItems` | Non-negative integer | Narrowing and expansion are classified |
| `maxLength` | Non-negative integer | Narrowing and expansion are classified |
| `maximum` | JSON number | Narrowing and expansion are classified |
| `minItems` | Non-negative integer | Narrowing and expansion are classified |
| `minLength` | Non-negative integer | Narrowing and expansion are classified |
| `minimum` | JSON number | Narrowing and expansion are classified |
| `not` | Schema or boolean; boolean form is unsupported | Any change is unsupported |
| `nullable` | Boolean | Widening and narrowing are classified |
| `oneOf` | Non-empty array of schemas | Any change is unsupported |
| `properties` | Object whose values are schemas | Property and nested changes are classified |
| `required` | Non-empty array of unique strings | Additions and removals are classified |
| `type` | A recognized type string or non-empty array of unique type strings | Type changes are classified |

JSON numbers are canonicalized and compared by JSON data-model value, so representation-only forms such as `1`, `1.0`, and `1e0` do not create drift. Array order remains meaningful for applicators; `required`, `enum`, and multi-valued `type` are treated as semantic sets.

Boolean schemas, tuple-form `items`, empty applicator arrays, empty `enum`/`required` arrays, duplicate set members, malformed JSON, and unknown members are rejected or reported unsupported rather than accepted as clean.
