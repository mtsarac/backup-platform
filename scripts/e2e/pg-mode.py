"""Switch the PostgreSQL backup mode of an agent: python3 pg-mode.py <Logical|Physical|Streaming>  (Fedora agent only).
BackupMode is agent-wide (Agent__Backup__PostgreSql__BackupMode), so every PostgreSQL job of that agent follows it: put it back to Logical before running the matrix.
Fedora: first use installs postgresql-contrib (pg_waldump), gives bp_e2e the REPLICATION attribute and adds a pg_hba replication rule."""
import subprocess, sys
mode = sys.argv[1] if len(sys.argv) > 1 else sys.exit(__doc__)
assert mode in ("Logical", "Physical", "Streaming"), mode
def run(*cmd, inp=None):
    r = subprocess.run(cmd, input=inp, text=True, capture_output=True)
    print("\n".join(l for l in (r.stdout + r.stderr).splitlines() if not any(x in l for x in ("quantum", "vulnerable", "upgraded", "**")))[-600:])
    r.returncode and sys.exit(r.returncode)
run("ssh", "virtual-fedora", "sudo bash -s", inp=f"""set -e
rpm -q postgresql-contrib >/dev/null || dnf install -y -q postgresql-contrib
sudo -u postgres psql -qc "alter role bp_e2e replication"
grep -q 'bp_e2e.*e2e-replication' /var/lib/pgsql/data/pg_hba.conf || sed -i '1i host replication bp_e2e 127.0.0.1/32 scram-sha-256 # e2e-replication' /var/lib/pgsql/data/pg_hba.conf
sudo -u postgres psql -qc "select pg_reload_conf()" >/dev/null
f=/etc/systemd/system/backup-agent.service.d/pgmode.conf
printf '[Service]\\nEnvironment="Agent__Backup__PostgreSql__BackupMode={mode}"\\n' > $f
systemctl daemon-reload && systemctl restart backup-agent && sleep 2 && systemctl is-active backup-agent
""")
