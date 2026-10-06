# PROJECT KNOWLEDGE BASE - backup-frontend

**Last refreshed:** 2026-10-06  
**Git:** repo on `main`

## OVERVIEW
Vite + React 19 + TypeScript management UI for the Backup Platform backend. It uses the real cookie + antiforgery API (no JWT, nothing in browser storage), explicit tenant routes (`/tenants/:tenantId/...`), TanStack Query, a central ky client, Tailwind v4/shadcn styling and Base UI dialogs. MVP scope: Dashboard, Agents (+Enrollment), Backup Jobs, Backup Runs, Artifacts. Restore, storage-provider admin, users/settings and PostgreSQL backup are intentionally absent.

## STRUCTURE
```text
backup-frontend/
├── src/
│   ├── App.tsx                 # BrowserRouter + tenant-scoped route tree
│   ├── main.tsx                # StrictMode + Providers entry
│   ├── index.css               # Tailwind v4, shadcn imports, semantic tokens
│   ├── lib/                    # routes, API client, failure-messages, QueryClient, providers
│   ├── features/               # auth, tenancy, agents, backup-jobs, backups, artifacts
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
| Features | `src/features/{agents,backup-jobs,backups,artifacts}` | Wire DTO types, API functions, hooks (`['tenant', tenantId, resource, ...]` query keys), tests next to code. |
| Pages | `src/pages/*` | Thin screens; `clients/` = Agents and Enrollment, `backups/` = Backup Runs. |
| Shared UI | `src/components/data/*`, `src/components/ui/*`, `src/components/layout/*` | `DataTable` (`wrap` columns), `QueryState`, `StatusPill`, `Badge`, `Button`. |

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
- Toolchain: Bun lockfile, Vite 8, React 19, TypeScript 6, Oxlint, Tailwind v4, shadcn, Base UI, TanStack Query, ky, Vitest/jsdom/RTL.
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
- Build, oxlint, and Vitest (`bun run test`) are clean; verified against a real backend + PostgreSQL + Agent.Host + SQL Server in a browser (login, tenant switch, job create/edit, run now, manual retry, scheduled runs, artifact delete/cancel, physical deletion history, enrollment token shown once).
- Known backend limits surfaced in the UI: agent capabilities are not persisted by the backend yet (shown as "Bildirilmedi"); there is no schedule-occurrence preview endpoint (edit page shows `nextOccurrenceAt` and occurrence history); job edit is several backend calls (general, schedule, retention, enable), so a later step may fail after earlier ones applied.
- Run progress uses visibility-aware polling (no SignalR for browsers).
