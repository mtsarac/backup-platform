import { spawnSync } from 'node:child_process'

// Failed logins count per source IP for 24 h and back off up to 15 min, so deliberate wrong-password tests lock later sign-ins out. Best effort: only the local bpe2e stack.
export default function globalSetup() {
  spawnSync('docker', ['exec', 'bp-postgres', 'psql', '-U', 'postgres', '-d', 'backup_platform_dev', '-c', 'DELETE FROM login_throttle_entries'], { stdio: 'ignore' })
}
