# Frontend

The web UI for Backup Platform. React 19, TypeScript, Vite, Tailwind CSS 4, TanStack Query, and Base UI components. It talks to the backend through same-origin `/api` requests.

## Layout

```text
src/
  features/    One folder per area: agents, backup-jobs, backups, artifacts,
               restores, storage, notifications, discovery, platform admin, ...
  components/  Shared UI: layout, data tables, auth, storage, ui primitives
  pages/       Route-level pages
  lib/         API client, i18n (Turkish and English), providers, accessibility prefs
  hooks/       Shared hooks
  test/        Test setup
```

## Run it

You need [bun](https://bun.sh) 1.4+.

```bash
bun install --frozen-lockfile
bun run dev          # proxies /api to BACKUP_API_PROXY_TARGET (default https://localhost:5001)
bun run typecheck
bun run lint         # oxlint
bun run test         # vitest
bun run build
```

The `Dockerfile` serves the production build with nginx. `docker-compose.yml` in the repository root runs it next to the API.

## Tests

`bun run test` runs the whole suite on two `vmThreads` workers. To check one area while you work, pass a path:

```bash
bun run test src/features/platform/audit-table.test.tsx
```

Before opening a PR, run typecheck, lint, the full test suite, and the build.

## Behavior worth knowing

- **Downloads.** Backup Browser downloads are for tenant Owners and Admins. The UI prepares a download session and polls it. Once ready, the browser downloads the file natively with the session cookie, so file bytes never pass through a JavaScript Blob. Storage keys and physical paths are never shown. The availability and status query keys include the tenant and artifact, so switching either one starts a fresh session.
- **Dashboard banner.** It reports successful backups in the last 24 hours. It does not claim the whole system is safe. The warning count is the number of non-zero categories (failed backups, storage failures, insufficient replication, failed artifact deletions), not their sum, because those counters can overlap.
- **Job storage configuration.** Jobs send only `destinations` and `requiredVerifiedReplicaCount`. The primary target is the one with `isPrimary`, not the first in the array. `Quarantined` replicas need an ownership review and are not offered as restore or download sources.
- **Database discovery.** Each backup computer has one rescan action and a plain scan status. Server pages list database backup availability and why a row cannot be selected. System databases and databases already covered by a job are unavailable. Selecting databases opens the backup-job wizard with the computer, server address, engine, saved credential reference and database names pre-filled; jobs are created only on the wizard's final confirmation. Scan settings, identifiers, versions and raw reason codes are under collapsed, copyable technical details.
- **Restore retries.** A retry with unchanged input keeps its idempotency key. Changing the target, mode, or options creates a new request.
- **Tenant notifications.** Only the selected tab mounts and loads data. Policy thresholds and delivery diagnostics are in collapsed, copyable technical details; channels and deliveries are cards. Channel deletion requires its exact name and keeps delivery history. Saving webhook authentication off requires a consequence confirmation because the stored secret is deleted. Secrets remain write-only and are cleared after submission, including failed requests.
- **Accessibility.** The shared tenant/platform top bar has one **Görünüm / Display** menu. Its first action increases text size; decrease, high contrast and the dark-theme toggle are alongside it without nested menus. Text size (100%, 125%, 150%), contrast and theme keep their existing local-storage keys; size and contrast apply before React mounts. Escape closes the menu and restores trigger focus. Shared buttons, text fields, selects and details summaries have a minimum 44 px target at the default font size on desktop and mobile; keyboard focus has a visible outline.
