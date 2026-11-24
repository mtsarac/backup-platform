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

## Slices for parallel workers

Each slice uses its own spec files (`e2e/tests/<slice>/*.spec.ts`) and unique object names, so they can share the one stack. Slices needing VMs must not run the same VM operations at once: S3 owns Fedora, S4 owns Windows.

| # | Slice | Scope (flows) | Environment | Depends on |
|---|---|---|---|---|
| S1 | **Auth, account, platform admin** | A, B (except storage/agents/shares needing data), I (guards, 403/404), language | docker only (mail sink container for reset/verify) | Phase 1 harness |
| S2 | **Tenant settings, storage, notifications** | D (local/central/SFTP/S3/SMB-flag; Drive only up to consent URL), H, platform storage & transports, quota/plans effects | docker + target containers (sftp, minio, samba, mailpit, webhook sink) | S1 seed only |
| S3 | **Linux agent: enrollment → backup → restore** | C (Linux), discovery, E and F, G for PostgreSQL + file sources on Fedora; schedules, multi-destination, pause/cancel/retry, replica repair | Fedora VM (+ S2 storage targets: use local/central connection to stay independent) | C first, then E, F, G |
| S4 | **Windows agent: enrollment → backup → restore** | C (Windows install.ps1, service), MSSQL + PostgreSQL + file/VSS sources, E, F, G on Windows | Windows VM | independent of S3; use unique job names |
| S5 | **Dashboard, artifacts, shares, platform agent views, error states** | `/` dashboard, artifacts list/detail/delete/cancel-delete, shares, platform agents/shares/audit with real data, I (agent offline, failed run) | docker; needs ≥1 completed backup — seed through a protocol-only fixture (`scripts/frontend/quota-smoke-fixture.py`) or wait for S3/S4 | S3 or fixture |

Order: S1, S2, S3, S4 in parallel; S5 after S3/S4 produce artifacts (or on the fixture).

## Known problems found while building the harness

- `docker compose up` (stock compose) could not serve the API through nginx: antiforgery threw `SecurePolicy = Always, but the current request is not an SSL request` because the API trusted no forwarding proxy. Fixed in `docker-compose.yml` (fixed bridge subnet, `Authentication__TrustedProxies__0`).
- Stock compose does not bootstrap a PlatformAdmin although README and `.env.example` say it does; `e2e/up.sh` runs `--bootstrap-user` in the API container.
- `README.md` quick start says the UI is `http://localhost:3000`; the compose file publishes `https://…:5173`.
