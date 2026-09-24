# Privacy

KeelMatrix.AiToolContract does not collect or transmit telemetry. It has no network client, hosted service, background activity, or analytics dependency. Omitting telemetry is deliberate: tool names, descriptions, JSON Schemas, parameter names, enum values, return schemas, and approval metadata can reveal proprietary capabilities, and a responsible aggregate activation signal is not available without creating surprising collection risk.

Capture and verification operate on data supplied by the caller and keep it in memory unless the caller explicitly serializes a baseline. The package does not write repository files automatically.

Baselines are source-code-like artifacts. Store them according to the application's normal source and access-control policy, and do not publish them automatically from private applications. Diagnostics describe contract structure but callers should still avoid sending them to public logs when tool metadata is sensitive.

