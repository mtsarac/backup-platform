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

## Design system

One visual language, defined in `src/index.css` tokens and the shared components in `src/components/ui` and `src/components/data`. New and redesigned screens use only these; do not hand-write `rounded-xl border ...` surfaces or raw inputs.

- **Tokens.** Semantic colors (`bg-surface`, `text-text-muted`, `border-border`, `bg-primary`), status pairs (`success|warning|error|info` with `-subtle` fills), `shadow-card` (flat on purpose; only overlays use `shadow-overlay`), the type scale (`text-sm` hints, `text-body` content, `text-base` titles, `text-2xl` page title), and a minimum target for every control: 36 px with a mouse (24 px for links inside table rows), 44 px on touch (`pointer-coarse:`). Light, dark and high-contrast are all token-driven; never use a literal color.
- **Page structure.** `PageHeader` (one `h1`, crumbs, primary actions) then `Section` (a heading and its content, no box; `flush` frames a table or list, `tone="danger"` frames the whole section) or `Card` (plain surface; `as="section|article|li|form"`, `tone="default|subtle|danger"`, `padding="none|md|lg"`). `Facts`/`Fact` for read-only label/value pairs, `BrandMark` for the product mark.
- **Forms.** `Field` binds label, hint and error to one control; put `TextInput`, a `selectClass` select or `SecretField` inside it and spread the `control` props. `CheckField`, `SearchablePicker`, `Stepper`, `FormDialog` and `ConfirmDialog` cover the rest.
- **Data.** `DataList` (table on desktop, cards on small screens), `QueryState` for loading/empty/error, `StatusPill` for every status (colour + icon + text; plain in lists, a tinted pill only in a `PageHeader` meta row), `Badge` for plain tags, `KpiCard`, `ProgressBar`, `Pagination`.
- **Messages.** `Notice` for non-failures, `ErrorNotice` for failures (what happened, effect on data, what to do, raw code under technical details), `EmptyState` for empty lists, `TechnicalDetails` for ids and raw values.
- **Restraint.** One primary button per page; actions repeated per row are `outline`. No decorative icons on figures or empty states, no icon bubbles, no question as a section heading (a wizard step or a confirmation may ask one). Collapsed content is a quiet text toggle (`Disclosure`, `TechnicalDetails`), never nested.
- **Rules.** Every user-visible string goes through `tx()`. State is never colour alone. Every screen, frozen and platform ones included, uses these components; the old per-screen input and surface styles are gone. The login page is the reference screen.

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

- **Downloads.** Backup Browser downloads are for UserAdmin accounts with tenant membership; PlatformAdmin accounts cannot download artifact content. The UI prepares a download session and polls it. Once ready, the browser downloads the file natively with the session cookie, so file bytes never pass through a JavaScript Blob. Storage keys and physical paths are never shown. The availability and status query keys include the tenant and artifact, so switching either one starts a fresh session.
- **Tenant dashboard.** The verdict uses backend health without recomputing it from counters. Missing-copy recommendations and the tenant-wide last-24-hour replication-shortfall count share one verified-copies section: historical job health and the recent aggregate remain distinct, with links to both job settings and replication results. Enabled and scheduled job counts are separate facts in the plain sections below the recent runs; nothing on the dashboard is behind a toggle except each run's technical details.
- **Platform dashboard.** One status line, whose heading states the result (\"3 konu ilgi bekliyor\"), uses only the existing attention conditions: failed backup attempts in the last 24 hours, offline computers, unknown connections, or disabled computers. No conditions means Safe; any means Attention, with at most three linked items in that existing order. There is no invented urgent threshold. The figures and the bounded storage preview follow as plain content.
- **Plans, quota policies and usage.** System plans are read-only. Saving a plan edit requires its original name and warns that the limit applies to every subscriber. Policy edits require a consequence confirmation showing the affected workspace cohort, old/new thresholds, counting scope and enforcement. Storage usage shows workspace, used-plus-reserved versus limit, and quota state, with copyable policy internals collapsed on desktop and mobile. Rebuilding usage from either the list or tenant detail requires the exact workspace name; it recalculates accounting records without deleting backup files.
- **Tenant storage eligibility.** Usage shows the API's `newBackupsAllowed` answer once, independently of subscription and quota state. Notices explain restrictions and data safety; they do not replace the explicit admission result.
- **Platform tenant detail.** URL-addressable sections cover the overview, people, subscription, storage, backup computers and security. Only the active section mounts its queries. Security includes wrap keys and tenant recovery keys; platform administrators can manage tenant recovery keys without a membership.
- **Job storage configuration.** Jobs send only `destinations` and `requiredVerifiedReplicaCount`. The primary target is the one with `isPrimary`, not the first in the array. `Quarantined` replicas need an ownership review and are not offered as restore or download sources.
- **Unsaved job forms.** The create wizard and edit page protect unsaved values on Cancel, sidebar/internal links, workspace switching, and browser Back/Forward. Staying keeps the form; discarding resumes the originally requested destination. Reloading or closing a dirty form retains the browser's native warning. A successful save clears the saved snapshot before navigating; failed or partially successful saves retain protection. The app uses React Router's data router, so tests mounting these forms must use `createMemoryRouter` with `RouterProvider`, not `MemoryRouter`.
- **Database discovery.** Each backup computer has one rescan action and a plain scan status. Server pages list database backup availability and why a row cannot be selected. System databases and databases already covered by a job are unavailable. Selecting databases opens the backup-job wizard with the computer, server address, engine, saved credential reference and database names pre-filled; jobs are created only on the wizard's final confirmation. Scan settings, identifiers, versions and raw reason codes are under collapsed, copyable technical details.
- **Restore retries.** A retry with unchanged input keeps its idempotency key. Changing the target, mode, or options creates a new request.
- **Tenant notifications.** Only the selected tab mounts and loads data. Policy thresholds and delivery diagnostics are in collapsed, copyable technical details; channels and deliveries are cards. Channel deletion requires its exact name and keeps delivery history. Saving webhook authentication off requires a consequence confirmation because the stored secret is deleted. Secrets remain write-only and are cleared after submission, including failed requests.
- **Accessibility.** The shared tenant/platform top bar has one **Görünüm / Display** menu. Its first action increases text size; decrease, high contrast and the dark-theme toggle are alongside it without nested menus. Text size (100%, 125%, 150%), contrast and theme keep their existing local-storage keys; size and contrast apply before React mounts. Escape closes the menu and restores trigger focus. Shared buttons, text fields, selects and details summaries have a minimum 44 px target on touch screens and 36 px with a mouse (WCAG 2.2 AA asks for 24 px); keyboard focus has a visible outline.
