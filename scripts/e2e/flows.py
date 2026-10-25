"""Reusable backup/restore flows against the real API and VM agents."""
import json, lib, uuid
from lib import ST, CRED

def login_ref(t, agent, user, password):
    return t.call("POST", f"/agents/{agent['agentId']}/database-logins", {"userName": user, "password": password})["reference"]

def pg_cfg(t, agent, host_key):
    if host_key == "fedora":
        f = ST["fedora"]; return dict(host="127.0.0.1", port=5432, database=f["pg_db"], ref=login_ref(t, agent, f["pg_user"], f["pg_password"]))
    c = CRED["windows11_vm"]["postgresql"]; return dict(host="127.0.0.1", port=5432, database="e2e_src", ref=login_ref(t, agent, c["user"], c["password"]))

def mssql_cfg(t, agent):
    c = CRED["windows11_vm"]["mssql"]; return dict(host="127.0.0.1", port=c["port"], database="e2e_src", ref=login_ref(t, agent, c["user"], c["password"]))

def db_job_config(engine, cfg, destinations, **extra):
    base = {"engine": engine, "host": cfg["host"], "port": cfg["port"], "database": cfg["database"], "integratedSecurity": False,
            "databaseCredentialReference": cfg["ref"], "trustServerCertificate": True, "encrypt": True, "stagingReference": "default",
            "storageTarget": "default", "storagePrefix": "", "copyOnly": True, "checksumPolicy": "Required", "compressionPolicy": "Preferred",
            "requiredVerifiedReplicaCount": 1, "encryptionPolicy": "Off", "destinations": destinations}
    base.update(extra); return base

def file_job_config(source, destinations, paths=None, **extra):
    base = {"engine": "file-system", "database": source, "host": "localhost", "port": None, "integratedSecurity": True, "databaseCredentialReference": None,
            "encrypt": False, "trustServerCertificate": False, "stagingReference": "default", "storageTarget": "default", "storagePrefix": "", "copyOnly": True,  # copyOnly=False never completes for file jobs (finding FP-1)
           
            "checksumPolicy": "Required", "compressionPolicy": "Preferred", "requiredVerifiedReplicaCount": 1, "encryptionPolicy": "Off",
            "files": {"paths": paths or ["."], "includes": ["**"], "excludes": [], "symlinkPolicy": "Skip"}, "destinations": destinations}
    base.update(extra); return base

def local_dest(target="default", primary=True):
    return {"storageProvider": "local", "storageConnectionId": None, "storageExecutionLocation": "Agent", "isPrimary": primary, "storageTarget": target}

def run_backup(t, agent, name, config, timeout=900):
    job = t.call("POST", "/backup-jobs", {"name": f"{name} {uuid.uuid4().hex[:6]}", "agentId": agent["agentId"], "overlapPolicy": "Skip", "configuration": config})
    run = t.call("POST", f"/backup-jobs/{job['id']}/runs", {})
    rid = run.get("id", run.get("run", {}).get("id"))
    detail = t.wait(f"/backup-runs/{rid}", timeout=timeout)
    if detail["status"] != "Succeeded":
        raise AssertionError(f"backup run {detail['status']}: {json.dumps(detail)[:900]}")
    arts = [a for at in detail["attempts"] for a in at["artifacts"]]
    assert len(arts) == 1, f"expected 1 artifact, got {len(arts)}"
    return job, rid, arts[0], detail

def restore(t, agent, artifact_id, target, dbname, mode="NewDatabase", options=None, precheck=True):
    req = {"agentId": agent["agentId"], "target": target, "databaseName": dbname, "mode": mode}
    if options: req["options"] = options
    if precheck:
        p = t.call("POST", f"/backup-artifacts/{artifact_id}/restore-precheck", req)
        d = t.wait(f"/restores/{p.get('id', p.get('restoreId'))}", timeout=300)
        assert d["status"] == "Succeeded", f"precheck {d['status']}: {json.dumps(d)[:600]}"
    s = t.call("POST", f"/backup-artifacts/{artifact_id}/restore", req)
    d = t.wait(f"/restores/{s.get('id', s.get('restoreId'))}", timeout=900)
    assert d["status"] == "Succeeded", f"restore {d['status']}: {json.dumps(d)[:900]}"
    return d
