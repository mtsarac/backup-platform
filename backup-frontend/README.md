# React + TypeScript + Vite

This template provides a minimal setup to get React working in Vite with HMR and some Oxlint rules.

Currently, two official plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react) uses [Oxc](https://oxc.rs)
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react-swc) uses [SWC](https://swc.rs/)

## React Compiler

The React Compiler is not enabled on this template because of its impact on dev & build performances. To add it, see [this documentation](https://react.dev/learn/react-compiler/installation).

## Expanding the Oxlint configuration

If you are developing a production application, we recommend enabling type-aware lint rules by installing `oxlint-tsgolint` and editing `.oxlintrc.json`:

```json
{
  "$schema": "./node_modules/oxlint/configuration_schema.json",
  "plugins": ["react", "typescript", "oxc"],
  "options": {
    "typeAware": true
  },
  "rules": {
    "react/rules-of-hooks": "error",
    "react/only-export-components": ["warn", { "allowConstantExport": true }]
  }
}
```

See the [Oxlint rules documentation](https://oxc.rs/docs/guide/usage/linter/rules) for the full list of rules and categories.

## Secure Backup downloads

The existing tenant Backup Browser detail screen supports Owner/Admin-only downloads for Central and Agent-local replicas. It shows destination availability, expected SHA256 and size, offers checksum copying, prepares a scoped download session and polls its status. Ready sessions use a native cookie-authenticated browser download; file bytes are never buffered as a JavaScript Blob. Agent offline, unavailable replicas, denied access and integrity/transfer failures use safe central error messages. Storage keys and physical paths are not displayed.

Availability and status query keys include the tenant and artifact. Changing tenant, artifact, download eligibility or administrative permissions remounts the download controller so an earlier pending session cannot start in the new scope.

## Dashboard status scope

The status banner reports successful backups in the last 24 hours, not a guarantee of overall system safety. Its warning count is the number of nonzero categories (failed backups, storage failures, insufficient replication, failed artifact deletions), not their sum: these counters may overlap. Agent connectivity and scheduled jobs remain separate indicators.

## Hardening API compatibility

Backup job storage configuration uses only `destinations` and `requiredVerifiedReplicaCount`; the primary target is selected by `isPrimary`, not array position. Removed top-level storage fields are not submitted. `Quarantined` replicas require ownership review and are not verified restore/download sources; `TargetConflict` is explained without exposing provider exception details.

Text size (100%, 125%, 150%) and high contrast persist in browser local storage and are applied before React mounts. Restore request retries retain an idempotency key for unchanged input; correcting target, mode or options creates a new request identity.
