// File-backup fixtures on the Fedora VM: agent-side file source + secondary local volume (systemd drop-in) and a deterministic directory tree.
import { type Api } from '../../api'
import { AGENT_NAME, vm, waitOnline } from './fixture'

export const FILE_SOURCE = 'e2es3files'
export const SOURCE_ROOT = '/srv/e2e-s3/files'
export const REPLICA_VOLUME = 's3vol2'
export const REPLICA_ROOT = '/srv/e2e-s3/replica2'

/** Registers the file source and secondary local volume on the agent (restarts the service only when the drop-in changed) and (re)creates the tree. */
export async function seedFileSource(tenant?: Api, tenantId?: string) {
  const changed = vm(`set -e
sudo -n mkdir -p /etc/systemd/system/backup-platform-agent.service.d ${SOURCE_ROOT}/docs/nested ${REPLICA_ROOT}
sudo -n chown backup-agent:backup-agent ${REPLICA_ROOT}
sudo -n chmod 700 ${REPLICA_ROOT}
want='[Service]
# The packaged unit sandboxes writes (ProtectSystem=strict); restores land inside the source root and replicas in the second volume.
ReadWritePaths=/srv/e2e-s3
Environment="Agent__Backup__FileSources__${FILE_SOURCE}__RootDirectory=${SOURCE_ROOT}"
Environment="Agent__Backup__LocalStorage__Volumes__${REPLICA_VOLUME}=${REPLICA_ROOT}"'
conf=/etc/systemd/system/backup-platform-agent.service.d/e2e-s3.conf
if [ "$(sudo -n cat $conf 2>/dev/null)" != "$want" ]; then printf '%s\\n' "$want" | sudo -n tee $conf >/dev/null; sudo -n systemctl daemon-reload; sudo -n systemctl restart backup-platform-agent; echo restarted; fi
sudo -n rm -rf ${SOURCE_ROOT}/docs ${SOURCE_ROOT}/restored
sudo -n mkdir -p ${SOURCE_ROOT}/docs/nested
cd ${SOURCE_ROOT}/docs
for i in 1 2 3; do head -c 20000 /dev/zero | tr '\\0' "$i" | sudo -n tee f$i.bin >/dev/null; done
echo hello | sudo -n tee readme.txt >/dev/null
echo nested | sudo -n tee nested/deep.txt >/dev/null
sudo -n chmod -R a+rX /srv/e2e-s3/files
sudo -n chown backup-agent:backup-agent ${SOURCE_ROOT}`)
  if (changed.includes('restarted') && tenant && tenantId) await waitOnline(tenant, tenantId, AGENT_NAME)
  return changed.includes('restarted')
}

/** sha256 of every regular file below `dir`, relative paths, sorted. */
export const treeHashes = (dir: string) => vm(`sudo -n sh -c 'cd ${dir} && find . -type f -print0 | sort -z | xargs -0 sha256sum'`).trim()
