# E2E Slice S3 — Linux Agent (`virtual-fedora`)

Covers `docs/e2e/PLAN.md` Slice S3 (`#236`) and the `#228` screen checklist for `/agents*`, `/enrollment`, `/first-backup`, `/backup-jobs*`, `/backup-runs*`, `/restores*`, and `/backup-shares`.

## Prerequisites & Repeatability

1. Shared stack running (`bash e2e/up.sh`, Docker Compose project `bpe2e` on `192.168.122.1:5173`, one port for UI, API and agents). Do **not** stop or reset the shared stack while other slices run.
2. `virtual-fedora` reachable over SSH (`ssh virtual-fedora`) with passwordless `sudo` and PostgreSQL 18 installed. Do **not** delete VM snapshots.
3. Every spec (`01`–`08`) is idempotent and repeatable:
   - Full slice run (`cd e2e && bunx playwright test tests/s3`): `01-enroll.spec.ts` revokes any old agents in `e2e-s3 tenant`, cleans prior `/opt/backup-agent` + `backup-agent.service` on Fedora, and enrolls `e2e-s3-fedora` from empty state through the UI `Add computer` one-liner (`install.sh`).
   - Standalone file run (`cd e2e && bunx playwright test tests/s3/05-share-download.spec.ts`): `ensureAgent()` in `fixture.ts` reuses the online `e2e-s3-fedora` service or installs it if absent, `pg.ts` recreates deterministic PostgreSQL fixtures, and `files.ts` maintains the `e2e-s3.conf` systemd drop-in (`e2es3files` source + `s3vol2` secondary local volume) and `/srv/e2e-s3/files` tree.
   - Failure tests (`06-failures.spec.ts`) restore `postgresql` and `backup-agent.service` in `finally`/`afterAll` blocks and use transient `/tmp` identities for destructive revoke/offline cases so `e2e-s3-fedora` stays online.
   - Agent log viewer (`08-agent-logs.spec.ts`, `#230` / `#246`) runs an isolated `systemd-run` agent unit in `/tmp` on `virtual-fedora`; if pointed at an older un-rebuilt stack image where `/agent-logs` returns `404`, it skips cleanly with instructions (`E2E_BASE_URL=https://192.168.122.1:6173 E2E_API_URL=https://192.168.122.1:6252`).
