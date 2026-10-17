# PROJECT KNOWLEDGE BASE - backup-frontend

**Last refreshed:** 2026-10-06  
**Git:** repo on `main`

## OVERVIEW
Vite + React + TypeScript management UI for the Backup Platform backend. It uses the real cookie + antiforgery API (no JWT, nothing in browser storage), explicit tenant routes (`/tenants/:tenantId/...`), TanStack Query, a central ky client, Tailwind/shadcn styling and Base UI dialogs. MVP scope: Dashboard, Agents (+Enrollment), Backup Jobs, Backup Runs, Artifacts. Tenant-level users/settings and PostgreSQL backup are intentionally absent; Storage Providers are not configured freely (only the API's provider catalog and `storage-connections` are shown). A separate PlatformAdmin area lives under `/platform` (tenants, plans, tenant subscription, users, global Agents, audit; read-only for Agents).

## STRUCTURE
```text
backup-frontend/
├── src/
│   ├── App.tsx                 # BrowserRouter + tenant-scoped route tree
│   ├── main.tsx                # StrictMode + Providers entry
│   ├── index.css               # Tailwind, shadcn imports, semantic tokens
│   ├── lib/                    # routes, API client, failure-messages, QueryClient, providers
│   ├── features/               # auth, tenancy, agents, backup-jobs, backups, artifacts, storage, storage-connections
│   ├── components/             # auth guard, layout, theme, data, ui primitives
│   ├── pages/                  # login, tenant picker, dashboard, agents, jobs, runs, artifacts
│   └── test/                   # Vitest setup
├── docs/                       # product spec + superpowers design/plan docs
├── public/                     # static icons/favicon
├── vite.config.ts              # React + Tailwind plugins, @ alias
├── vitest.config.ts            # jsdom + RTL setup
├── components.json             # shadcn config
└── package.json / bun.lock     # bun-managed dependencies
```

## WHERE TO LOOK
| Task | Location | Notes |
|------|----------|-------|
| Routing | `src/App.tsx` | `/login`, `/` tenant picker, everything else under `/tenants/:tenantId/*` inside `TenantProvider` + `AppShell`. |
| HTTP | `src/lib/api/client.ts` | Only place that calls `fetch`: `credentials: include`, antiforgery (`X-CSRF-TOKEN`, refreshed after login/logout), `ApiError`, `tenantPath`, 401 -> session-expired event. `VITE_API_BASE_URL` defaults to `/api`; dev proxy target via `BACKUP_API_PROXY_TARGET` (`BACKUP_API_PROXY_INSECURE=true` for self-signed HTTPS). |
| Failure text | `src/lib/api/failure-messages.ts` | Central safe mapper; unknown codes render `İşlem başarısız oldu (<code>)`. Never show backend exception text. |
| Auth / tenant | `src/features/auth/*`, `src/features/tenancy/*` | `AuthProvider` (me + memberships), `TenantProvider` (tenant-scoped cache clear on switch), `useTenant()` exposes `canAdmin`/`canOperate` (UX only; backend authorizes). PlatformAdmin is a separate badge, not a tenant role. |
| Platform admin | `src/features/platform/*`, `src/pages/platform/*`, `components/layout/platform-{shell,sidebar}.tsx`, `components/auth/platform-admin-route.tsx` | `/platform/*` only for `isPlatformAdmin` (UX guard; backend returns 403). No tenant context there; query keys `['platform', ...]`, cleared on logout/session expiry. Lists are paged (`components/data/pagination.tsx`) with page/filters in URL search params; a 403 renders the authorization state. Plans (`/platform/plans`) and the tenant-detail Subscription section live in `features/platform/plans/`; storage is decimal (1 GB = 1,000,000,000 bytes, `storage.ts`), plan sizes always come from the API, the UI exposes only assign/change, suspend, resume and override. |
| Features | `src/features/{agents,backup-jobs,backups,artifacts}` | Wire DTO types, API functions, hooks (`['tenant', tenantId, resource, ...]` query keys), tests next to code. |
| Pages | `src/pages/*` | Thin screens; `clients/` = Agents and Enrollment, `backups/` = Backup Runs. |
| Shared UI | `src/components/data/*`, `src/components/ui/*`, `src/components/layout/*` | `DataTable` (`wrap` columns), `QueryState`, `StatusPill`, `Badge`, `Button`. |

Platform Support Access (M6) lives in `src/features/platform/support/` and `src/pages/platform/platform-support-{sessions,session}-page.tsx`. Tenant detail opens a time-boxed session with reason, permissions and in-memory idempotency keys; customer metadata/download/NewDatabase restore endpoints are session-scoped with both session and tenant IDs in query keys. Closing, revoking, expiry and detail-route unmount remove customer data caches while retaining session/audit; logout clears the existing `['platform']` root. The read-only Owner/Admin transparency page is `src/pages/support-access/support-access-page.tsx` with tenant-scoped hooks in `src/features/support-access/`. Support restore targets are the customer's real environment, not an isolated support environment, and Start requires a matching successful Precheck plus explicit acknowledgement.


Storage connections (M7a) live in `src/features/storage-connections/` (wire types in `types.ts`, tenant + platform API functions in `api.ts`, hooks in `hooks.ts`). A connection is one destination of one tenant (provider type, ownership `PlatformManaged`/`TenantManaged`, `quotaScope` label, enabled flag, safe configuration, secret status, last test, Replica count/bytes); every tenant has the system defaults `Local` (TenantManaged, scope `tenant`) and `Managed Storage` (PlatformManaged, scope `managed`). Routes: the tenant Storage page `/tenants/:tenantId/storage` renders `TenantDestinations` ('Depolama hedefleri' cards + 'Sağlayıcılar ve yetenekler' panel); PlatformAdmin has `/platform/storage-connections` (sidebar 'Storage Targets', paged table, filters in URL params `ownership` (absent = PlatformManaged, `all` = unfiltered)/`providerType`/`enabled`/`search`/`tenantId`) and a 'Depolama bağlantıları' section on the tenant detail page; both use `PlatformConnectionTable`. Conventions: (1) every row action (`ConnectionActions`) is gated ONLY by the API's `connection.permissions` (computed for the caller's route); `useTenant().canAdmin` is only a hint for the 'Bağlantı ekle' affordance. (2) Create/secret/edit forms are generic over `ProviderDescriptor.configFields`/`secretFields` (`descriptor-fields.tsx`); Planned providers (`availability !== 'Available'`: SFTP, Google Drive) are listed disabled with no create control, and creation is offered only when `availability==='Available' && tenantCreatable` below `maxConnectionsPerTenant` (platform: `platformCreatable`). (3) Secret values are password inputs, sent once in the request body only, wiped from component state at submit, never prefilled, never in query keys (`gcTime: 0` + `reset()` on the secret/create mutations); the UI only shows `hasSecret` ('Kimlik bilgisi kayıtlı' / 'Yok'). (4) Test results are shown as reported: `Succeeded`, `Failed`, `NotTestable` (a warning, never success — Local is AgentSide so the backend cannot test it). (5) Disabling blocks NEW backups (`409 admission_denied` code `storage_connection_disabled`; `storage_connection_unavailable` for a missing/foreign connection) while existing Replicas stay downloadable/restorable and are not deleted: the confirm dialog states this (`disableEffects` in `labels.ts`). (6) Query keys: `['tenant', tenantId, 'storage-connections', 'list'|'providers']` and `['platform', 'storage-connections', 'list'|'tenant'|'providers', ...]`; mutations also refresh the tenant's `storage-quota`/`dashboard` and, on the platform side, `['platform','storage']` and audit. Quota policy scope selectors now include `quotaScope` (labels `[a-z0-9-]{1,64}`) and `storageConnection` (GUIDs, normalised to lowercase), parsed in `features/storage/scope-input.ts`. Backup job `configuration.storageConnectionId` is optional (null = the tenant's default connection for the provider; the wizard resets it when the provider text changes and warns, non-blocking, when the target connection is disabled); `BackupReplica.storageConnectionId` is always present.

Upload bandwidth (Faz 2A) lives in `src/features/upload-bandwidth/` and `src/pages/upload-bandwidth/` (sidebar 'Upload Bandwidth', `/tenants/:tenantId/upload-bandwidth`). One GET document (min/max, tenant limit, agents/jobs that have a limit) plus `PUT .../tenant|agents/{id}|jobs/{id}` with `bytesPerSecond` (`null` = Unlimited); editing is Owner/Admin only, others read-only. Units are decimal and explicit (`units.ts`: 1 Mbit/s = 125,000 B/s, 1 MB/s = 1,000,000 B/s); parsing uses exact BigInt arithmetic against the server-provided min/max.

Database discovery lives in `src/features/discovery/` (wire types, tenant API, hooks, `status.ts` labels/predicates, `database-table.tsx`, `job-prefill.ts`) and `src/pages/discovery/` (sidebar 'Keşfedilen Veritabanları', `/tenants/:tenantId/discovery` and `.../discovery/instances/:instanceId`). Read-only inventory for every role; Operator+ rescans/verifies/binds a credential REFERENCE NAME (never a password), Owner/Admin edits per-agent settings. Rescan is disabled for agents lacking the `discovery.database-inventory` capability. Query keys `['tenant', tenantId, 'discovery', 'agents'|'instances'|'instance', ...]`; while any agent `scanState` is `Requested`/`Running` agents/instances/detail poll every 3 s, then the inventory is refreshed once. `CredentialsRequired` means databases were NOT listed (never rendered as an empty instance). `canCreateJob`/`databaseSelectable` gate selection; the Backup Job Wizard source step ('Keşfedilen veritabanlarından seç', mounted only when opened, plus `?discoveryInstance=&database=` prefill from the detail page) pre-fills the source and creates one job per database (`<job name> – <database>`) through the unchanged create API only after the final 'Kaydet', with per-database results and retry of failed ones. Discovery never creates or changes a job itself.

Notifications (N1) live in `src/features/notifications/` (tenant policy/channels/delivery history, Owner/Admin only: nav entry gated by `canAdmin`, page renders a notice and makes no request otherwise) and `src/features/platform/notifications/` (PlatformAdmin SMTP + SMS gateway settings at `/platform/notification-transports`). Secrets are write-only password inputs cleared on submit, mutations carrying them use `gcTime: 0`, and responses only expose `hasSecret`. SMS is `UYGULANDI — DOĞRULANMADI` (generic HTTP JSON adapter only, no real provider verified) and intentionally has no dedicated tests.

## CODE MAP
| Symbol | Type | Location | Role |
|--------|------|----------|------|
| `apiRequest` / `tenantPath` / `ApiError` | Functions/class | `src/lib/api/client.ts` | Single typed HTTP entry; cookie auth + antiforgery. |
| `failureMessage` / `errorMessage` | Functions | `src/lib/api/failure-messages.ts` | Safe user-facing text for backend codes and HTTP errors. |
| `AuthProvider` | Function | `src/features/auth/auth-provider.tsx` | Real session (`/api/me`, `/api/me/tenants`), login/logout, session-expired state. |
| `TenantProvider` / `useTenant` | Function/hook | `src/features/tenancy/*` | Tenant from route, role permissions, cache clear on switch. |
| `ProtectedRoute` | Function | `src/components/auth/protected-route.tsx` | Redirects unauthenticated users to `/login` with `from` state. |
| `ThemeProvider` | Function | `src/components/theme-provider.tsx` | Persists theme and toggles `.dark` on `<html>`. |
| `queryClient` | Const | `src/lib/query-client.ts` | Query defaults: no focus refetch, one retry for non-4xx, stale 30s. |

## CONVENTIONS
- Toolchain: Bun lockfile, Vite, React, TypeScript, Oxlint, Tailwind, shadcn, Base UI, TanStack Query, ky, Vitest/jsdom/RTL.
- TypeScript strict: `target es2023`, `moduleResolution bundler`, `verbatimModuleSyntax true`, `erasableSyntaxOnly true`, `noUnusedLocals/Params true`, `allowArbitraryExtensions true`, alias `@/* -> src/*`.
- Imports: prefer `@/` alias for cross-folder app imports; use `import type` for types; CSS imported globally from `src/main.tsx` -> `src/index.css`.
- React context lint pattern: keep contexts in `*-context.ts`, providers in `*-provider.tsx`, hooks in `use-*.ts` to satisfy `react/only-export-components`.
- UI language is Turkish; preserve these technical terms in English: `Agent`, `SQL Server Instance`, `Database`, `Backup Job`, `Backup Run`, `Artifact`, `Replica`, `Storage Provider`.
- Keep the MVP small: pages before abstractions, reuse `Button`, `AppShell`, `DataTable`, `QueryState`, `StatusPill`, and the central API client.

## GIT WORKFLOW
- Commit feature and fix work separately. Do not mix unrelated changes or multiple logical units in one commit.
- Commit messages must not include `Co-authored-by`, AI attribution, generated-by footers, or any other AI traces.
- Do not create commits unless the user explicitly asks for a commit.

## ANTI-PATTERNS (THIS PROJECT)
- Do not add `as any`, `@ts-ignore`, `@ts-expect-error`, empty `catch`, or unused underscore params.
- Do not bypass `verbatimModuleSyntax`; use `import type` for type-only imports.
- Do not add a component library or state manager unless existing Tailwind/Base UI/TanStack Query cannot cover the need.
- Do not commit `dist/`, `node_modules`, `.env*`, or generated build outputs.
- Do not reintroduce Vite demo/counter code. `src/App.css` is stale template CSS and is not imported; delete or ignore it when real styles replace it.
- Do not use untested OpenAI model IDs in OpenCode routing; `openai/gpt-5.3-codex-spark` and `openai/gpt-5.4` failed with ChatGPT OAuth. Working tested IDs: `openai/gpt-5.5`, `openai/gpt-5.4-mini`.

## COMMANDS
```bash
bun install        # install/update deps from bun.lock
bun run dev        # Vite dev server + HMR
bun run build      # tsc -b && vite build
bun run lint       # oxlint
bun run preview    # preview production build
bun run test       # vitest run (jsdom + RTL)
```

## CURRENT STATE
- Build, oxlint, and Vitest (`bun run test`) are clean; verified against a real backend + PostgreSQL + Agent.Host + SQL Server in a browser (login, tenant switch, job create/edit, run now, running cancellation, overlap Skip/Queue, manual retry, scheduled runs, artifact delete/cancel, agent-local physical deletion history, enrollment token shown once).
- Job create and edit are ONE request each (`POST`/`PUT backup-jobs/{id}` carry schedule, `removeSchedule`, retention (admin only) and `enabled`); a failing save changes nothing. The list's enable/disable toggle still uses the separate routes.
- Agent capabilities are read from the backend's persisted snapshot (`capabilities`, `capabilitiesRevision`, `capabilitiesReportedAt`); an empty list renders "Henüz bildirilmedi".
- Known backend limit: there is no schedule-occurrence preview endpoint (edit page shows `nextOccurrenceAt` and occurrence history).
- Run progress uses visibility-aware polling (no SignalR for browsers).
