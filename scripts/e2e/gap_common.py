"""Shared helpers for the gap_* scripts (JobRestoreGaps and its sub-tasks): tenant/agent context, host database access, throwaway PostgreSQL containers.
Scenario names are `gap/<ID>[/detail]`; every scenario appends a PASS/FAIL row to results.jsonl through lib.scenario. Throwaway objects are prefixed gap-jr-/gap_jr_."""
import hashlib, json, os, subprocess, sys, time, uuid
import api, lib, flows
from lib import ST, CRED, scenario

PFX = "gap-jr-"
HOST_PG = "postgres"          # host PostgreSQL container (127.0.0.1:5432), db e2e_src is the lab source
HOST_MSSQL = "bpe2e-host-mssql"
PGQ = "select count(*)||'|'||md5(string_agg(name||payload, ',' order by id)) from items"  # deterministic content digest of the lab table shape
MSQ = matrix_msq = ("select cast(count(*) as varchar(20))+'|'+convert(varchar(64),hashbytes('SHA2_256',(select string_agg(cast(name as nvarchar(max))+payload, ',') within group (order by id) from items)),2) from items")
HOST_STORE = os.path.expanduser("~/.local/share/bpe2e-host-agent")  # host agent: local-store/, staging/


class Ctx:
    """Tenant A client, agents and credentials references on the host agent."""
    def __init__(self):
        self.t = lib.Tenant("a"); self.ag = self.t.agents()
        self.ha, self.wa, self.fa = (self.ag[n] for n in ("host-e2e", "win11-e2e", "fedora-e2e"))
        self.hpg = ST["host"]["pg"]
        self._refs = {}
    def ref(self, agent, user, password):
        k = (agent["agentId"], user, hashlib.sha256(password.encode()).hexdigest())
        if k not in self._refs: self._refs[k] = flows.login_ref(self.t, agent, user, password)
        return self._refs[k]
    def host_pg_cfg(self, database="e2e_src", host="127.0.0.1", port=5432, user=None, password=None):
        return dict(host=host, port=port, database=database, ref=self.ref(self.ha, user or self.hpg["user"], password or self.hpg["password"]))
    def host_pg_target(self, host="127.0.0.1", port=5432, user=None, password=None):
        return {"host": host, "port": port, "databaseCredentialReference": self.ref(self.ha, user or self.hpg["user"], password or self.hpg["password"]), "encrypt": False, "trustServerCertificate": True}
    def host_mssql_cfg(self, database="e2e_src"):
        return dict(host="127.0.0.1", port=1433, database=database, ref=self.ref(self.ha, "sa", ST["host"]["mssql_sa_password"]))
    def host_mssql_target(self):
        return {"host": "127.0.0.1", "port": 1433, "databaseCredentialReference": self.ref(self.ha, "sa", ST["host"]["mssql_sa_password"]), "encrypt": True, "trustServerCertificate": True}
    def win_pg_cfg(self, database="e2e_src"): return flows.pg_cfg(self.t, self.wa, "win")


def sh(*cmd, inp=None, check=False, timeout=600):
    r = subprocess.run(list(cmd), input=inp, capture_output=True, text=True, timeout=timeout)
    if check and r.returncode: raise RuntimeError(f"{cmd[:3]} -> {r.returncode}: {(r.stderr or r.stdout)[:300]}")
    return r.returncode, (r.stdout + r.stderr).strip()


def pg_exec(container, user, password, sql, db="postgres", host="127.0.0.1"):
    """Runs SQL in a PostgreSQL container, tuples-only; returns stdout. Raises on error."""
    rc, out = sh("docker", "exec", "-e", f"PGPASSWORD={password}", container, "psql", "-h", host, "-U", user, "-d", db, "-tA", "-v", "ON_ERROR_STOP=1", "-c", sql)
    if rc: raise RuntimeError(f"psql: {out[:300]}")
    return out.strip()


def host_pg(sql, db="postgres"):
    c = ST["host"]["pg"]; return pg_exec(HOST_PG, c["user"], c["password"], sql, db)


def host_pg_digest(db): return host_pg(PGQ, db)
def host_pg_drop(db): host_pg(f"drop database if exists \"{db}\" with (force)")
def host_pg_exists(db): return host_pg(f"select count(*) from pg_database where datname='{db}'") == "1"


def host_mssql(sql, db="master"):
    rc, out = sh("docker", "exec", HOST_MSSQL, "/opt/mssql-tools18/bin/sqlcmd", "-S", "127.0.0.1,1433", "-U", "sa", "-P", ST["host"]["mssql_sa_password"], "-C", "-h", "-1", "-W", "-d", db, "-Q", "set nocount on; " + sql)
    if rc: raise RuntimeError(f"sqlcmd: {out[:300]}")
    return out.strip()


def host_mssql_digest(db): return host_mssql(MSQ, db).splitlines()[0].strip()
def host_mssql_drop(db): host_mssql(f"if db_id('{db}') is not null begin alter database [{db}] set single_user with rollback immediate; drop database [{db}] end")


class Throwaway:
    """A throwaway PostgreSQL container on its own docker network (10.77.<octet>.0/24); host agent and this script reach it by container IP."""
    def __init__(self, name, octet, image="postgres:18", volume=False):
        self.name, self.net, self.octet, self.image = f"gap-jr-{name}", f"gap-jr-net{octet}", octet, image
        self.ip = f"10.77.{octet}.2"; self.password = uuid.uuid4().hex; self.user = "postgres"; self.volume = f"{self.name}-data" if volume else None
    def up(self):
        sh("docker", "rm", "-f", self.name)
        sh("docker", "network", "inspect", self.net)[0] and sh("docker", "network", "create", "--subnet", f"10.77.{self.octet}.0/24", self.net, check=True)
        args = ["docker", "run", "-d", "--name", self.name, "--network", self.net, "--ip", self.ip, "-e", f"POSTGRES_PASSWORD={self.password}", "--shm-size=256m"]
        if self.volume: args += ["-v", f"{self.volume}:/var/lib/postgresql"]
        sh(*args, self.image, check=True)
        self.wait()
        return self
    def wait(self, timeout=90):
        end = time.time() + timeout
        while time.time() < end:
            try:
                if pg_exec(self.name, self.user, self.password, "select 1") == "1": return
            except Exception: pass
            time.sleep(1)
        raise TimeoutError(self.name)
    def sql(self, q, db="postgres"): return pg_exec(self.name, self.user, self.password, q, db)
    def digest(self, db): return self.sql(PGQ, db)
    def exists(self, db): return self.sql(f"select count(*) from pg_database where datname='{db}'") == "1"
    def dbs(self): return self.sql("select datname from pg_database where not datistemplate order by 1").splitlines()
    def seed(self, db="gap_jr_src", rows=50000):
        """Same items(id,name,payload) shape as the lab source; payload is pseudo-random so it does not compress away."""
        self.sql(f'create database "{db}"')
        self.sql(f"create table items(id serial primary key, name text not null, payload text not null); insert into items(name,payload) select 'name-'||g, md5(g::text)||md5((g*7)::text)||md5((g*13)::text) from generate_series(1,{rows}) g", db)
    def down(self):
        sh("docker", "rm", "-f", self.name)
        if self.volume: sh("docker", "volume", "rm", "-f", self.volume)
        sh("docker", "network", "rm", self.net)
    def target(self, ctx, superuser=True, user=None, password=None):
        return ctx.host_pg_target(self.ip, 5432, user or self.user, password or self.password)
    def cfg(self, ctx, database):
        return ctx.host_pg_cfg(database, self.ip, 5432, self.user, self.password)


def stage_files():
    """Files left in the host agent's staging directory (a finished restore/backup/verification must leave none)."""
    d = os.path.join(HOST_STORE, "staging")
    return sorted(os.path.join(r, f) for r, _, fs in os.walk(d) for f in fs) if os.path.isdir(d) else []


def local_store_path(replica):
    """Physical path of a host-agent local replica."""
    return os.path.join(HOST_STORE, "local-store", replica["key"])


def run_job(t, agent, name, config, timeout=900, wait=True, **job_extra):
    job = t.call("POST", "/backup-jobs", {"name": f"{PFX}{name}-{uuid.uuid4().hex[:5]}", "agentId": agent["agentId"], "overlapPolicy": job_extra.pop("overlapPolicy", "Skip"), "configuration": config, **job_extra})
    run = t.call("POST", f"/backup-jobs/{job['id']}/runs", {}); rid = run.get("id", run.get("run", {}).get("id"))
    return job, rid, (t.wait(f"/backup-runs/{rid}", timeout=timeout) if wait else None)


def arts(detail): return [a for at in detail["attempts"] for a in at["artifacts"]]


def backup_ok(t, agent, name, config, **kw):
    job, rid, d = run_job(t, agent, name, config, **kw)
    assert d["status"] == "Succeeded", f"backup {d['status']}: {json.dumps(d['attempts'], default=str)[:600]}"
    a = arts(d); assert len(a) == 1, len(a)
    return job, rid, t.call("GET", f"/backup-artifacts/{a[0]['id']}"), d


def restore_req(agent, target, dbname, mode="NewDatabase", options=None, replica_id=None, confirm=None, precheck_id=None):
    r = {"agentId": agent["agentId"], "target": target, "databaseName": dbname, "mode": mode}
    for k, v in (("options", options), ("replicaId", replica_id), ("confirmOverwrite", confirm), ("precheckId", precheck_id)):
        if v is not None: r[k] = v
    return r


def start_restore(t, art_id, req, kind="restore", expect=(200, 201, 202)):
    """POST restore / restore-precheck / restore-verifications. Returns (http status, body)."""
    r = t.raw("POST", f"/backup-artifacts/{art_id}/{kind}", req)
    assert r.status_code in expect, f"{kind} -> {r.status_code} {r.text[:300]}"
    return r.status_code, r.json() if r.content else None


def wait_restore(t, rid, timeout=900, stop=("Succeeded", "Failed", "Cancelled", "AgentLost")):
    return t.wait(f"/restores/{rid}", timeout=timeout, stop_on=stop)


def fail_view(d): return {"status": d["status"], "failureCode": d.get("failureCode"), "targetState": d.get("targetState"), "progress": (d.get("progress") or {}).get("stage")}
