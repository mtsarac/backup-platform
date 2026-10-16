# Backup Platform UI design system

One system for the tenant console and the PlatformAdmin area. Turkish UI copy; the technical terms `Agent`, `SQL Server Instance`, `PostgreSQL`, `Database`, `Backup Job`, `Backup Run`, `Artifact`, `Replica`, `Storage Provider` stay English.

## Principles
- **State first.** Every list/detail leads with what is healthy, failing or needs a decision; configuration comes second.
- **One thing per screen.** Long pages are split into route tabs, in-page tabs or steps. A page has one primary action.
- **State never relies on color.** `StatusPill`/`Badge dot` carry a text label; `Notice` carries an icon.
- **Accessible by default.** 44px primary targets, visible 3px focus ring, text scale (100/125/150 %), high-contrast mode, reduced motion, keyboard-complete dialogs/tabs/wizards, tables become labelled cards below `md`.
- Keep the API contract and permissions untouched: UI gating only mirrors `useTenant()`/`permissions`; the backend decides.

## Tokens (`src/index.css`)
| Role | Light | Dark |
|---|---|---|
| canvas (page) | `#eef2f1` | `#0a1011` |
| surface (cards) | `#ffffff` | `#131d1e` |
| surface-2 / surface-3 (inset, hover) | `#f5f8f7` / `#e9eeed` | `#0f1819` / `#1d2b2c` |
| text / text-muted | `#101b1a` / `#46544f` | `#e8efee` / `#aebdba` |
| primary (deep teal) / hover | `#0f5e63` / `#0b4c50` | `#5ed0d6` / `#86dde1` |
| border / border-strong | `#cfd8d5` / `#8a9a96` | `#2a3b3c` / `#6f8683` |
| sidebar (both themes dark ink) | `#0f1f1e` | `#0c1516` |
| success / warning / error / info | `#17653a` `#8f4f06` `#b3261e` `#2557a7` | lighter tints, same hues |

Tailwind utilities: `bg-canvas bg-surface bg-surface-2 bg-surface-3 text-text text-text-heading text-text-muted border-border border-border-strong bg-primary hover:bg-primary-hover text-success bg-success-subtle …`, `shadow-card`, `shadow-overlay`. Type: Geist Variable, base 16px, body `text-[0.9375rem]`, page title `text-2xl font-semibold`. Radius: controls `rounded-lg`, surfaces `rounded-xl`. Spacing: 4px grid; page `space-y-6`, section body `p-5`.

## Building blocks
| Need | Use |
|---|---|
| Page heading | `ui/page-header` `PageHeader` (crumbs, title, description, actions, meta) |
| Titled surface | `ui/section` `Section` (`flush` for tables); read-only facts `Facts`/`Fact` |
| Sub-pages | `ui/tabs` `RouteTabs` (URL-backed) or `Tabs` (state) |
| Multi-step flow | `ui/stepper` `Stepper` + the flow's own Back/Next buttons |
| Messages | `ui/notice` `Notice` (error → alert) |
| Empty list | `ui/empty-state` `EmptyState` (say what's missing, offer the action) |
| Form control | `ui/field` `Field`; classes `inputClass`/`selectClass` from `features/platform/styles` |
| List filters | `ui/filters` `Filters`/`FilterField`/`filterControlClass` |
| Tables | `data/data-table` `DataTable` (cards below `md`); `wrap: true` for long text columns |
| Loading/error/empty | `data/query-state` `QueryState` |
| Status | `data/status-pill` `StatusPill`, `ui/badge` `Badge` (`dot`) |
| Paging | `data/pagination` `Pagination` |
| Dialogs | `features/platform/plans/form-dialog` `FormDialog`, `features/agents/confirm-dialog` `ConfirmDialog` |

## Layout
Fixed dark sidebar (grouped navigation) from `lg`; modal drawer below. Slim sticky top bar: tenant switcher (tenant shell) / title (platform shell) on the left; role badges, text-size + contrast tools, theme and sign-out on the right. Content is centred, max 88rem. A skip link jumps to `#main`.
## Screen Organisation and Information Architecture
- **Storage Hub (`/tenants/:tenantId/storage`):** split into route tabs (`Bağlantılar`, `Kullanım ve kota`, `Sağlayıcılar`).
  - Destinations list shows health, last test date, stored size share and direct test / manage actions.
  - Connection detail (`/storage/connections/:connectionId`) separates `Genel bakış`, safe `Yapılandırma`, `Kimlik bilgisi` (secret status, OAuth re-auth/disconnect) and destructive `Yönetim` (enable/disable, delete).
  - Connection creation (`/storage/connections/new`) is a four-step linear flow (Sağlayıcı → Bağlantı → Kimlik bilgisi → Özet ve test). Creates via single POST and automatically invokes provider test / OAuth.
- **Tenant detail (`/platform/tenants/:tenantId`):** tabs for `Genel`, `Üyeler`, `Abonelik`, `Depolama`, `Agent'lar`, and `Destek erişimi`.
- **Agents:** list displays connectivity, operational status and capability count; details moved to tabs.
- **Backup Jobs:** table with pagination and status filters; six-step creation/edit wizard (Genel → Kaynak → Depolama → Zamanlama → Saklama & seçenekler → Özet).
- **Restores & Backup Runs:** multi-tab details keeping raw JSON/details secondary to operational status.
