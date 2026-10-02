# PROJECT KNOWLEDGE BASE - backup-frontend

**Last refreshed:** 2026-09-08  
**Git:** repo on `main` (observed `1caddc2` before this refresh)

## OVERVIEW
Vite + React 19 + TypeScript frontend for Genel Backup Yonetim Sistemi. The app is no longer a vanilla Vite demo: it now has routing, auth/theme providers, TanStack Query, a ky API client, Tailwind v4/shadcn styling, AppShell layout, Login page, and Dashboard placeholder. Business behavior still comes from `docs/Genel_Backup_Yonetim_Sistemi_Kapsam_Dokumani(1).md` plus the design/implementation docs under `docs/superpowers/`.

## STRUCTURE
```text
backup-frontend/
├── src/
│   ├── App.tsx                 # BrowserRouter + protected app routes
│   ├── main.tsx                # StrictMode + Providers entry
│   ├── index.css               # Tailwind v4, shadcn imports, semantic tokens
│   ├── lib/                    # routes, API client, QueryClient, providers, types
│   ├── features/auth/          # AuthContext/AuthProvider/useAuth
│   ├── components/             # auth guard, layout, theme, ui primitives
│   ├── pages/                  # login + dashboard placeholder
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
| Root routing | `src/App.tsx` | `/login` public, everything else protected under `AppShell`; only dashboard implemented. |
| Entry/providers | `src/main.tsx`, `src/lib/providers.tsx` | `QueryClientProvider` -> `ThemeProvider` -> `AuthProvider`. |
| Routes | `src/lib/routes.ts` | Constants for Dashboard, Clients, Backup Jobs, Backups, Restore, Storage, Logs, Users, Settings. |
| Domain types | `src/lib/types/index.ts` | User, Client, Agent, SQL Instance/Database, BackupJob, Backup, StorageProvider, LogEntry. |
| API | `src/lib/api/client.ts` | `ky` client, `VITE_API_BASE_URL` fallback `http://localhost:5000/api`. |
| Auth | `src/features/auth/*`, `src/components/auth/protected-route.tsx` | Temporary in-memory login; no backend auth yet. |
| Theme | `src/components/theme-*`, `src/hooks/use-theme.ts`, `src/index.css` | `light`/`dark`/`system`, localStorage key `backup-theme`. |
| Layout | `src/components/layout/*` | Fixed 240px sidebar, topbar, protected content shell. |
| UI primitives | `src/components/ui/*`, `src/lib/utils.ts` | Base UI button + CVA variants; `cn` re-export from `cn` package. |
| Product spec | `docs/Genel_Backup_Yonetim_Sistemi_Kapsam_Dokumani(1).md` | Source of truth for roles, jobs, storage, dashboard, API. Quote path in shell commands. |
| Design/plan | `docs/superpowers/specs/2026-09-08-backup-frontend-design.md`, `docs/superpowers/plans/2026-09-08-backup-frontend-implementation.md` | Follow before adding MVP pages. |

## CODE MAP
| Symbol | Type | Location | Role |
|--------|------|----------|------|
| `App` | Function | `src/App.tsx` | Wraps `AppRoutes` in `BrowserRouter`. |
| `AppRoutes` | Function | `src/App.tsx` | Auth-aware route tree and protected shell. |
| `Providers` | Function | `src/lib/providers.tsx` | Combines Query, theme, and auth providers. |
| `AuthProvider` | Function | `src/features/auth/auth-provider.tsx` | Temporary in-memory user; `login()` currently accepts any credentials. |
| `ProtectedRoute` | Function | `src/components/auth/protected-route.tsx` | Redirects unauthenticated users to `/login` with `from` state. |
| `ThemeProvider` | Function | `src/components/theme-provider.tsx` | Persists theme and toggles `.dark` on `<html>`. |
| `ROUTES` | Const | `src/lib/routes.ts` | Central route path registry. |
| `api` | Const | `src/lib/api/client.ts` | Shared ky instance for backend calls. |
| `queryClient` | Const | `src/lib/query-client.ts` | Query defaults: no focus refetch, retry 1, stale 30s. |

## CONVENTIONS
- Toolchain: Bun lockfile, Vite 8, React 19, TypeScript 6, Oxlint, Tailwind v4, shadcn, Base UI, TanStack Query, ky, Vitest/jsdom/RTL.
- TypeScript strict: `target es2023`, `moduleResolution bundler`, `verbatimModuleSyntax true`, `erasableSyntaxOnly true`, `noUnusedLocals/Params true`, `allowArbitraryExtensions true`, alias `@/* -> src/*`.
- Imports: prefer `@/` alias for cross-folder app imports; use `import type` for types; CSS imported globally from `src/main.tsx` -> `src/index.css`.
- React context lint pattern: keep contexts in `*-context.ts`, providers in `*-provider.tsx`, hooks in `use-*.ts` to satisfy `react/only-export-components`.
- UI language is Turkish; preserve these technical terms in English: `Client`, `Agent`, `SQL Server Instance`, `Database`, `Backup Job`, `Backup`, `Restore Job`, `Storage Provider`.
- Keep the first MVP small: pages before abstractions, reuse `ROUTES`, `Button`, `AppShell`, domain types, and Query/ky plumbing already present.

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
bunx vitest run    # test runner; package.json has no test script yet
```

## CURRENT STATE
- Build/lint were last reported clean after the MVP shell work; rerun before claiming new changes pass.
- Implemented routes are `/login` and `/`; sidebar links for future pages currently hit the protected fallback (`Sayfa Bulunamadi`) until pages are added.
- Auth is placeholder-only and not persisted; backend integration still needs real login/token handling.
- Next planned MVP work: Clients list/detail + Agent registration, then Backup Jobs wizard following `docs/superpowers/plans/2026-09-08-backup-frontend-implementation.md`.
- `bun run dev` may already be running in the user's terminal; after dependency/config changes, restart it before Playwright/manual QA.
