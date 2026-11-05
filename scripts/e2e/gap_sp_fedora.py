"""Private SFTP service on the Fedora VM for the StoragePolicyGaps tests: a second sshd (port 2222, own host key, own authorized_keys, systemd unit gap-sp-sshd) that the
tests may stop, restart and re-key without touching the shared SFTP on port 22, plus a 200 MiB ext4 loop file system (/srv/gap-sp/cap) used for deterministic fill levels.
Usage: python3 gap_sp_fedora.py up|down|status. Every change is listed in docs/test-environment/run-20261105/CRITICAL-CHANGES.md."""
import re, sys
import lib

PORT = 2222
CFG = "/etc/ssh/gap-sp/sshd_config"
UNIT = "gap-sp-sshd"
SFTP_USER = "e2e-sftp"          # the lab's SFTP account (password in state.json); only this instance's own base directories are used
BASE = "/srv/gap-sp/sftp"       # remoteBaseDirectory of the plain private SFTP connection
CAP = "/srv/gap-sp/cap"         # mount point of the 200 MiB ext4 loop file system
CAP_BASE = CAP + "/sftp"        # remoteBaseDirectory of the capacity test connection
KEY = "/etc/ssh/gap-sp/host_ed25519"


def fed(script):
    rc, out = lib.fedora(script); return rc, out


def up():
    rc, out = fed(f"""set -e
sudo mkdir -p /etc/ssh/gap-sp {BASE} {CAP}
[ -f {KEY} ] || sudo ssh-keygen -q -t ed25519 -N '' -f {KEY}
sudo touch /etc/ssh/gap-sp/authorized_keys
sudo tee {CFG} >/dev/null <<'EOT'
Port {PORT}
HostKey {KEY}
PidFile /run/gap-sp-sshd.pid
AllowUsers {SFTP_USER}
PasswordAuthentication yes
PubkeyAuthentication yes
AuthorizedKeysFile /etc/ssh/gap-sp/authorized_keys
UsePAM yes
# The tests make many deliberately failing logins from one address; OpenSSH 9.8+ would drop the following (valid) connections as a penalty and the run would pause.
PerSourcePenalties no
Subsystem sftp /usr/libexec/openssh/sftp-server
EOT
sudo chown {SFTP_USER}: {BASE}
sudo semanage port -l | grep -q 'ssh_port_t.*{PORT}' || sudo semanage port -a -t ssh_port_t -p tcp {PORT}
sudo firewall-cmd --add-port={PORT}/tcp >/dev/null
systemctl is-active {UNIT} >/dev/null 2>&1 || sudo systemd-run --unit={UNIT} --collect /usr/sbin/sshd -D -e -f {CFG} >/dev/null
sleep 1; systemctl is-active {UNIT}
# capacity file system: 200 MiB ext4 without reserved blocks (free == available)
if ! mountpoint -q {CAP}; then
  [ -f /srv/gap-sp/cap.img ] || sudo truncate -s 200M /srv/gap-sp/cap.img
  sudo blkid /srv/gap-sp/cap.img >/dev/null 2>&1 || sudo mkfs.ext4 -q -m 0 -F /srv/gap-sp/cap.img
  sudo mount -o loop /srv/gap-sp/cap.img {CAP}
fi
sudo mkdir -p {CAP_BASE}; sudo chown {SFTP_USER}: {CAP_BASE}
df -B1 --output=size,used,avail {CAP} | tail -1
""")
    return rc, out


def down():
    return fed(f"""sudo systemctl stop {UNIT} 2>&1; sudo umount {CAP} 2>&1; sudo rm -rf /srv/gap-sp /etc/ssh/gap-sp
sudo semanage port -d -t ssh_port_t -p tcp {PORT} 2>&1; sudo firewall-cmd --remove-port={PORT}/tcp 2>&1; echo cleaned""")


def stop_sshd(): return fed(f"sudo systemctl stop {UNIT}")
def start_sshd(): return fed(f"sudo systemd-run --unit={UNIT} --collect /usr/sbin/sshd -D -e -f {CFG} >/dev/null; sleep 1; systemctl is-active {UNIT}")


def fingerprint():
    rc, out = fed(f"sudo ssh-keygen -lf {KEY}.pub"); return out.split()[1]


def rotate_hostkey():
    """Stops the private sshd, replaces its host key and starts it again; returns the new fingerprint."""
    stop_sshd(); fed(f"sudo rm -f {KEY} {KEY}.pub && sudo ssh-keygen -q -t ed25519 -N '' -f {KEY}"); start_sshd(); return fingerprint()


def stat_fs():
    """(block size, total blocks, available blocks) as the SFTP statvfs probe sees them."""
    rc, o = fed(f"stat -f -c '%S %b %a' {CAP}"); return tuple(int(x) for x in o.split()[-3:])


def fill_to_avail(avail_blocks):
    """Leaves exactly `avail_blocks` blocks available (a root-owned filler takes the rest); returns (block size, total blocks, available blocks)."""
    fed(f"sudo rm -f {CAP}/filler; sync")
    bs, total, avail = stat_fs(); want = avail - avail_blocks
    assert want >= 0, f"cannot leave {avail_blocks} blocks (only {avail} available)"
    if want: fed(f"sudo fallocate -l {want * bs} {CAP}/filler && sync")
    return stat_fs()


if __name__ == "__main__":
    print({"up": up, "down": down}[sys.argv[1]]())
