# UI E2E plan

Goal: drive every user flow through the real UI (Playwright, real Chromium) against a real stack, with real agents on the two VMs where a flow needs one.

Not to be confused with `scripts/e2e/` (older Python API/CLI lab harness with MinIO/Samba/SFTP targets). This directory's harness is `e2e/` (Playwright). Slices may reuse the lab's targets and VM helpers (`scripts/windows/*`, `scripts/e2e/configure-agents.py`).

## Environment

| What | Value |
|---|---|
| Compose project | `bpe2e` (containers `bp-postgres`, `bp-api`, `bp-frontend`) |
| Frontend (UI) | `https://192.168.122.1:5173` (self-signed; `http://…:5174` redirects to it) |
| API for agents | `https://192.168.122.1:5252` (nginx → api:5000; same cert) |
| PostgreSQL | `127.0.0.1:5433` (`postgres`/`postgres`, db `backup_platform_dev`) |
| PlatformAdmin | `admin` / `PlatformAdmin123!` (from `.env.e2e`) |
| Tenant | `E2E Tenant`, one `UserAdmin` member `e2e.useradmin` / `E2e-Passw0rd!2026`, unlimited plan, enrollment enabled |
| Seed state | `e2e/.state.json` (git-ignored; tenant id and accounts) |

`192.168.122.1` is the libvirt bridge address of this laptop (`virbr0`); the VMs reach the stack there. Roles are only **PlatformAdmin** and **UserAdmin** (tenant members).

Bring-up (idempotent, ~1 min warm, ~8 min cold build):

```bash
bash e2e/up.sh          # compose up --build, wait for API, bootstrap admin, seed, write e2e/.state.json
cd e2e && bun run test  # Playwright; Chromium from /usr/bin/chromium (CHROMIUM_PATH to override)
```

Reset to empty: `docker compose -p bpe2e --env-file .env.e2e down -v && bash e2e/up.sh`.

`up.sh` creates `.env.e2e` from `.env.example` (POSTGRES_PORT=5433 because 5432 is often taken). Other ports/host: edit `.env.e2e`.

### Writing tests

- `import { state } from '../state'` gives tenant id and accounts. Sign in through the UI (`tests/auth.spec.ts` has `signIn`).
- One worker, shared tenant: create your own uniquely named objects (`e2e-<slice>-<rand>`) and clean up; do not rely on test order across files.
- A new account must change its initial password at first sign-in (this is a flow to test; seed already completes it for `e2e.useradmin`).
- Seed more data through the API in `beforeAll` with Playwright's `request` (see `seed.ts` for antiforgery + login).

### VMs

| VM | SSH | Address | Notes |
|---|---|---|---|
| Fedora 44 | `ssh virtual-fedora` (user `test-user`, passwordless sudo) | `192.168.122.115` | PostgreSQL 18 server + `psql`/`pg_dump`; no MSSQL, no docker, no agent installed |
| Windows 11 | `ssh virtual-win11` (`windows-test\test-user`) | `192.168.122.218` | MSSQL (`MSSQL$MSSQLSERVER25`) + PostgreSQL 18 running; no `BackupAgent` service; helpers in `scripts/windows/` |

Both reach the stack (verified: UI :5173, API :5252, `/api/agent-dist/install.sh`, `http://…:5174/cert.crt` all answer 200 from Fedora; TCP and cert download OK from Windows). The stack certificate must be trusted on the VM (`https://192.168.122.1:5174/cert.crt`); agent installers are served by the API (`/api/agent-dist/install.sh|install.ps1`, packages built into the API image). Do not delete VM snapshots.

## Flow inventory

Legend: **UI** = doable fully in UI; **API** = needs API/CLI for part; **VM** = needs a real agent on a VM.

### A. Auth and account (no VM)
| Flow | Steps | Expected | UI |
|---|---|---|---|
| Password login | `/login` → login + password | land on `/` (tenant select) or `/tenants/:id` | UI |
| Wrong password | bad password | stays on login, error alert | UI |
| Forced password change | first login of new/temp-password user | change form, ≥12 chars, then in | UI |
| Remember me / logout / session expiry | checkbox, logout, expiry banner | session ends | UI |
| Forgot / reset password, email verification | `/forgot-password`, `/reset-password`, `/verify-email` | needs mail (no SMTP in stack: mailpit/greenmail from lab or notification transport) | UI + mail sink |
| TOTP second factor | `/account/security` enroll, recovery codes regenerate, remove; login with code / recovery code | | UI (code computed in test) |
| Passkeys | `/account/passkeys` register/login/delete | Chromium virtual authenticator (CDP WebAuthn) | UI |
| Sessions | `/account/sessions` list, revoke | | UI |
| Account page | profile, email change | | UI |
| Route guards | unauth → `/login`; UserAdmin → `/platform` redirects; tenant isolation | | UI |
| Language | TR/EN switcher | | UI |

### B. Platform administration (PlatformAdmin, no VM)
| Flow | Steps | Expected |
|---|---|---|
| Tenants | `/platform/tenants` create, detail, agent-enrollment toggle, members add/remove | UI |
| Users | `/platform/users` create (PlatformAdmin/UserAdmin + tenant ids), edit, disable/enable, temporary password | UI; last PlatformAdmin cannot be disabled |
| Plans & subscriptions | `/platform/plans` create/enable/disable; tenant plan assign; storage-limit override | UI |
| Quota policies | `/platform/quota-policies` | UI |
| Platform storage | `/platform/storage`, `/platform/storage/connections` (platform-managed connections, Central root) | UI; Central needs mounted root |
| Agents | `/platform/agents`, `/platform/tenants/:t/agents/:a`, revoke | needs agent for non-empty |
| Agent settings | `/platform/agent-settings` (release publish, update policy) | UI; large upload |
| Notification transports | `/platform/notification-transports` (SMTP/SMS) | UI + mail sink |
| Audit | `/platform/audit` filter/integrity | UI |
| Platform shares | `/platform/shares` | needs artifact |
| Feature flags | `/api/features` (sms, smb, sizeAnomaly, externalSecrets, replicaRepair) change visible UI | API/env restart |

### C. Agent enrollment (VM)
| Flow | Steps | Expected |
|---|---|---|
| Add computer — Linux | `/tenants/:t/enrollment` → token + one-line `install.sh` command → run on Fedora over SSH | agent appears Online in `/agents`; token cleared/consumed; same AgentId after restart |
| Add computer — Windows | same with `install.ps1` (elevated PowerShell) / setup wizard `.exe` (not built on Linux) | Online; service `BackupAgent` Running |
| Token errors | reused/expired token, enrollment disabled | clear error |
| Agent detail | `/agents/:id` profile rename, settings, rescan, revoke, connectivity | UI |
| Database discovery | `/agents/databases`, `/discovery/instances/:id` store credential, verify | UI; PostgreSQL on both VMs, MSSQL on Windows |
| Agent uninstall/reinstall | identity preserved | VM |

### D. Storage connections (tenant; mostly no VM)
| Flow | Steps | Expected |
|---|---|---|
| List/create wizard | `/storage`, `/storage/connections/new` per provider: local (agent disk), central, SFTP, S3, SMB (flag), Google Drive (OAuth, human) | create, test, capacity, enable/disable, secret rotation, delete |
| Connection detail | `/storage/connections/:id` | |
| SFTP/S3 targets | need a target container (lab: `docker-compose.e2e-targets.yml` MinIO/Samba; `atmoz/sftp`) | UI + target |
| Quota view | `/storage` usage, hard quota refusal `quota_size_unknown` | UI |

### E. Backup jobs and runs (VM)
| Flow | Steps | Expected |
|---|---|---|
| First-backup guide | `/first-backup` | UI |
| Job wizard | `/backup-jobs/new`: source (PostgreSQL / MSSQL / files), agent, destinations (1..n), schedule, retention, encryption | job in list |
| Edit/enable/disable/schedule/retention | `/backup-jobs/:id` | UI |
| Run now, pause/resume, cancel, retry | `/backup-runs`, `/backup-runs/:id` | state transitions, progress |
| Schedule occurrences | schedule fires on time | needs a ~1-2 min cron |
| Multi-destination / replication states | Healthy/Degraded/InsufficientReplicas | needs ≥2 destinations |
| File backups | filters, symlink policy, snapshots (Btrfs/LVM/ZFS/VSS) | VM-specific; VSS on Windows |
| Artifacts | `/artifacts`, detail, checksum, delete + cancel-delete, repair replica | UI |
| Health/dashboard | `/` dashboard counters, protection strip | UI |

### F. Restore (VM)
| Flow | Steps | Expected |
|---|---|---|
| Restore wizard (DB) | `/backup-runs/:id/restore`: mode (new database / replace), target, precheck, confirm | `/restores/:id` Succeeded, data compare |
| File restore | `file-restore-wizard` source/what/target | files equal (hash) |
| Restore verification | isolated verify (`restore-verifications`) | Healthy/failed variants |
| Cancel, cleanup retry, rollback removal (`bp_old_*`) | `/restores/:id` | |

### G. Sharing and download (VM for real artifact)
| Flow | Steps | Expected |
|---|---|---|
| Share artifact | `/backup-shares`, create/extend/revoke, download attempts | |
| Download via UI | artifact detail → Download | SHA-256 matches artifact |

### H. Settings (tenant)
| Flow | Steps | Expected |
|---|---|---|
| Notifications | `/settings`: channels email/sms(flag)/webhook create, test, enable/disable, secret; policy; deliveries/failed retry | needs sink (webhook receiver, mailpit) |
| In-app notifications | bell | UI |
| Recovery key / encryption | `/settings/recovery-key`: artifact key, recovery key | UI |
| Upload bandwidth | `/settings/bandwidth` schedules | UI; effect needs agent |
| Audit & history retention | `/api/tenants/:t/audit`, retention settings | UI |

### I. Error states
Backend down banner, session expired mid-action, 403/404 pages (`NotFound`), offline agent warnings, failed run/restore messaging, validation messages on every form.

## Rules (owner decisions, tracked in #228)

1. **Seed through the API, test through the UI.** Users, tenants, plans, agent enrollments, storage connections, jobs and runs the test merely needs are created with the helpers in `e2e/api.ts` (`Api.signIn`, `ensureTenantUser`, `createEnrollmentToken`, ...; add more there, do not inline `fetch` in specs). The flow under test is driven and asserted in the real browser.
2. **Every screen gets the full checklist**, goal is zero open problems including the login screen:
   happy path; validation and error messages; empty / loading / error states; permission and role limits; session expiry and logout; reload and back/forward; responsive (mobile 390 px and desktop 1440 px); keyboard and accessibility (axe, focus order, labels); no browser console errors or warnings; failed network requests (4xx/5xx, offline); text, translation (TR/EN) and UX consistency.
   Helper expectation: each spec file asserts console cleanliness and no unexpected failed requests (`page.on('console' | 'requestfailed' | 'response')`).
3. **Every defect found becomes its own GitHub issue**, linked to #228 (`gh issue create -R mtsarac/backup-platform`, body `Refs #228`). Specs for a known defect use `test.fixme` with the issue number.
4. Agent logging and the log viewer UI (#230) is in scope: its tests are added to the Linux/Windows agent slices (S3/S4) and S5 once the feature lands.

## Slices for parallel workers (Phase 2 sub-issues)

Each slice has its own `e2e/tests/<slice>/` directory and unique object names (`e2e-<slice>-<rand>`) so slices share the one stack. S3 owns the Fedora VM, S4 the Windows VM. Every slice applies the checklist in Rules and files defects separately.

### S1. Auth, account, platform administration
- **Scope:** flows A and B without agent/storage data: login (all states, remember me, logout, expiry), forced password change, forgot/reset password, email verification, TOTP + recovery codes, passkeys (CDP virtual authenticator), sessions, account page, route guards and tenant isolation, language switcher; platform tenants, users (create/edit/disable/enable/temporary password, last-admin rule), plans/subscriptions, quota policies, audit, feature flags.
- **Environment:** docker only + mail sink container (mailpit) wired to a platform notification transport.
- **Depends on:** harness (this PR).
- **Acceptance:** specs for every listed flow pass; checklist items covered for `/login`, `/forgot-password`, `/reset-password`, `/verify-email`, `/account/*`, `/platform/{tenants,users,plans,quota-policies,audit}`; no console errors; defects filed and linked.

### S2. Tenant settings, storage connections, notifications
- **Scope:** flows D and H: storage wizard and detail per provider (local, central, SFTP, S3, SMB behind flag, Google Drive up to the consent URL), test/capacity/enable/disable/secret rotation/delete, quota view; platform storage and notification transports; notification channels (email, webhook, sms behind flag), policy, test send, deliveries and retry; in-app notifications; recovery key; upload bandwidth; audit/history retention.
- **Environment:** docker + target containers (`atmoz/sftp`, MinIO and Samba from `docker-compose.e2e-targets.yml`, mailpit, a webhook sink).
- **Depends on:** harness; S1 only for shared seed.
- **Acceptance:** each provider creates, tests, edits and deletes through the UI against a real target; notification test sends arrive at the sinks; validation and error states per form; checklist covered for `/storage*`, `/settings*`, `/platform/storage*`, `/platform/notification-transports`.

### S3. Linux agent: enrollment, backup, restore, sharing
- **Scope:** flow C (Linux), discovery, E, F, G with PostgreSQL and file sources on Fedora: Add computer dialog and one-line `install.sh`, Online state, restart keeps AgentId, token errors, agent detail/rename/rescan/revoke; job wizard, run now, pause/resume/cancel/retry, schedule occurrence, retention, multi-destination, artifacts; DB and file restore, restore verification, cleanup retry; share + download with SHA-256 check; agent logs in the UI (#230).
- **Environment:** Fedora VM (PG18 present; install no other DB), stack from this PR, central or local storage connection.
- **Depends on:** harness; `createEnrollmentToken` helper.
- **Acceptance:** from empty state a UI-driven run enrolls the Fedora agent, backs up a PostgreSQL database and a directory, restores both into new targets with matching data/hash, downloads an artifact; failure paths (revoked agent, stopped DB, bad credential) show clear UI errors; checklist covered for `/agents*`, `/enrollment`, `/first-backup`, `/backup-jobs*`, `/backup-runs*`, `/restores*`.

### S4. Windows agent: enrollment, backup, restore
- **Scope:** same as S3 for Windows: `install.ps1` (elevated PowerShell over SSH, helpers in `scripts/windows/`), service state, MSSQL and PostgreSQL sources, file backup with VSS, restore, share/download, agent logs in the UI (#230).
- **Environment:** Windows VM (MSSQL + PG18 present, no agent). Trust the stack certificate on the VM (`/cert.crt`). Interactive-session caveats in `scripts/README.md` apply.
- **Depends on:** harness; independent of S3 (distinct job and agent names).
- **Acceptance:** as S3 for MSSQL, PostgreSQL and files on Windows; uninstall/reinstall preserves identity; the agent is removed or left documented at the end.

### S5. Dashboard, artifacts, shares, platform agent views, error states
- **Scope:** dashboard and protection strip, artifacts list/detail/delete/cancel-delete/repair, shares and download attempts, platform agents/shares/audit with real data, offline agent and failed run/restore messaging, backend-down banner, 403/404 pages.
- **Environment:** docker; needs completed backups from S3/S4 or protocol-only fixture data (`scripts/frontend/quota-smoke-fixture.py`).
- **Depends on:** S3 or S4 (or the fixture).
- **Acceptance:** every listed screen passes the checklist with populated and empty data; defects filed.

Order: S1, S2, S3, S4 in parallel; S5 after artifacts exist.

## Known problems found while building the harness

- `docker compose up` (stock compose) could not serve the API through nginx: antiforgery threw `SecurePolicy = Always, but the current request is not an SSL request` because the API trusted no forwarding proxy. Fixed in `docker-compose.yml` (fixed bridge subnet, `Authentication__TrustedProxies__0`).
- Stock compose does not bootstrap a PlatformAdmin although README and `.env.example` say it does; `e2e/up.sh` runs `--bootstrap-user` in the API container.
- `README.md` quick start says the UI is `http://localhost:3000`; the compose file publishes `https://…:5173`.

## S1 results (#235)

Specs: `e2e/tests/s1/` (login, password-change, totp, passkeys, recovery, account, guards, platform-users/-tenants/-plans/-quota-audit, features, z-throttle, screens-checklist). On the isolated `bps1` auth stack 108 pass and 3 are `test.fail` for open defects (#266 #281 #282); the shared stack runs 91 pass and skips 22 by design: 9 passkey specs (no WebAuthn RP on an IP host), 2 mail specs (no account-mail wiring), 11 specs that send wrong credentials (the per-IP throttle never decays, #267 #290). Wrong-TOTP steps in `totp.spec.ts` run only on the isolated stack. Defects #240 #241 #243 #244 #245 #248 #250 #251 #252 #257 #260 #261 #267 #287 are fixed on main and their specs now run as normal tests.

- **Passkeys and e-mail recovery need a second stack.** The main stack has no WebAuthn relying party (`passkeys_not_configured`; the id cannot be an IP) and no account-mail wiring (`Notifications__PublicBaseUrl` unset, so `/api/auth/email-recovery` says `available:false`). Both need an API restart, so `bash e2e/auth-stack.sh` brings up an isolated project `bps1` (overlay `docker-compose.e2e-auth.yml`: localhost RP id, mailpit, own ports and subnet). Run the same specs there with the variables printed at the top of that script; the gated specs then execute instead of skipping.
- **Failed logins are shared state.** The login throttle counts failures per client IP for 24 hours; run the suite only on an isolated stack to avoid locking out other users.
- TOTP codes are replay-protected: `totp()` in `tests/s1/helpers.ts` hands out a strictly later step on every call and waits for the clock if needed.
- Not tested: forced re-login after the real 30-minute reset-token and 24-hour verification-token expiry, the 15-minute max throttle wait, and feature-flag toggling (needs an API restart; only the flag endpoint and one hidden-UI check run).
