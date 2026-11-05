"""WindowsSpecial gap checks on win11-e2e: AGT-08 discovery, AGT-02 service/ACL/DPAPI, AGT-12 reinstall refusal, VSS file backups, RST-11 MSSQL pre-restore, JOB-12 failure injection.
Run: python3 gap_windows.py [name-filter,...]. Scenario names are gap/<id>...; results go to results.jsonl. Secrets are only read in code, never printed."""
import hashlib, json, re, subprocess, sys, time, uuid
import api, lib, flows
from lib import CRED, scenario

PGE = "post" + "gres"  # discovery engine id
MS = CRED["windows11_vm"]["mssql"]
WSQL = f"sqlcmd -S 127.0.0.1,{MS['port']} -U {MS['user']} -P '{MS['password']}' -C -h -1 -W"
PREFIX = "gap-windows-"


def winx(ps, timeout=600):
    """lib.win with UTF-8 output; returns (rc, text)."""
    import base64
    ps = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); $ProgressPreference='SilentlyContinue'; " + ps
    r = subprocess.run(["ssh", "virtual-win11", "powershell -NoProfile -EncodedCommand " + base64.b64encode(ps.encode("utf-16le")).decode()], capture_output=True, timeout=timeout)
    out = (r.stdout + r.stderr).decode("utf-8", "replace")
    return r.returncode, "\n".join(l for l in out.splitlines() if not any(x in l for x in lib.NOISE))


def sql(q, db="master"):
    """Runs T-SQL on the VM instance MSSQLSERVER25 as the lab login; returns trimmed text."""
    rc, out = winx(f"{WSQL} -d {db} -Q \"set nocount on; {q}\"")
    assert rc == 0, out[:300].replace(MS["password"], "***")
    return out.strip()


def win_agent(t):
    return t.agents()["win11-e2e"]


def wait_rescan(t, agent_id, timeout=120):
    """Requests a manual rescan and waits until the scan state is back to Idle with a newer lastScanAt."""
    before = next(a for a in t.call("GET", "/discovery/agents")["items"] if a["agentId"] == agent_id)["lastScanAt"]
    for _ in range(40):
        r = t.raw("POST", f"/discovery/agents/{agent_id}/rescan", {})
        if r.status_code == 202: break
        assert r.status_code == 429, f"rescan -> {r.status_code} {r.text[:200]}"
        time.sleep(int(r.headers.get("Retry-After", "5")) + 1)
    end = time.time() + timeout
    while time.time() < end:
        a = next(a for a in t.call("GET", "/discovery/agents")["items"] if a["agentId"] == agent_id)
        if a["scanState"] == "Idle" and a["lastScanAt"] != before: return a
        time.sleep(2)
    raise TimeoutError("discovery rescan did not finish")


# ---- AGT-08 ----------------------------------------------------------------------------------------------------------------------------------
@scenario("gap/AGT-08/windows-discovery-real-instances")
def agt08(t):
    ag = win_agent(t); scan = wait_rescan(t, ag["agentId"])
    insts = [i for i in t.call("GET", f"/discovery/instances?agentId={ag['agentId']}")["items"]]
    keys = sorted((i["engine"], i["source"], i["instanceName"], i["port"], i["version"], i["edition"]) for i in insts)
    # ground truth from the VM itself
    rc, gt = winx(r"""(Get-Service | ? { $_.Name -like 'MSSQL$*' -or $_.Name -eq 'MSSQLSERVER' -or $_.Name -like 'postgresql*' } | % { $_.Name + '=' + $_.Status }) -join ';'
'psql=' + (& 'C:\Program Files\PostgreSQL\18\bin\postgres.exe' --version)
'listen=' + ((Get-NetTCPConnection -State Listen | ? { $_.LocalPort -in 1433,5432,3306,27017,1521 } | % { $_.LocalPort } | sort -Unique) -join ',')
'docker=' + [bool](Get-Command docker -EA 0)""")
    ms_ver = sql("select cast(serverproperty('ProductVersion') as varchar(40))+'|'+cast(serverproperty('Edition') as varchar(80))+'|'+cast(serverproperty('InstanceName') as varchar(40))").splitlines()[0]
    pgv = re.search(r"PostgreSQL\) ([0-9.]+)", gt).group(1)
    mv, ed, inst = ms_ver.split("|")
    ms = [i for i in insts if i["engine"] == "mssql"]; pg = [i for i in insts if i["engine"] == PGE]
    assert len(insts) == 2 and len(ms) == 1 and len(pg) == 1, f"expected exactly 1 mssql + 1 postgresql on the VM, got {keys}"
    assert ms[0]["instanceName"] == inst == "MSSQLSERVER25" and ms[0]["version"] == mv and ms[0]["edition"].startswith(ed.split(" (")[0]), f"mssql mismatch: discovered {ms[0]['version']}/{ms[0]['edition']}/{ms[0]['instanceName']} vs server {ms_ver}"
    assert pg[0]["version"].split("-")[0] == pgv, f"pg mismatch: {pg[0]['version']} vs postgres.exe {pgv}"
    assert "docker=False" in gt and "3306" not in gt and "27017" not in gt, gt
    assert not [i for i in insts if i["source"] == "Docker"], "a docker instance was invented although the VM has no docker"
    dock = next(s for s in scan["sources"] if s["source"] == "Docker")
    assert dock["status"] == "Unavailable", f"docker source should be Unavailable on the VM, is {dock}"
    return {"discovered": keys, "ground_truth": gt.replace("\n", " | "), "mssqlServer": ms_ver, "dockerSource": dock, "scan": {k: scan[k] for k in ("lastOutcome", "instanceCount", "lastScanAt")}}


@scenario("gap/AGT-08/windows-credential-bind-verify-databases")
def agt08_verify(t):
    """Binds the lab logins to both discovered instances, verifies and compares the database list with the engines themselves."""
    ag = win_agent(t); out = {}
    insts = {i["engine"]: i for i in t.call("GET", f"/discovery/instances?agentId={ag['agentId']}")["items"]}
    pgc = CRED["windows11_vm"]["postgresql"]
    for eng, (user, pwd) in {"mssql": (MS["user"], MS["password"]), PGE: (pgc["user"], pgc["password"])}.items():
        ref = flows.login_ref(t, ag, user, pwd); i = insts[eng]
        t.call("PUT", f"/discovery/instances/{i['id']}/credential", {"credentialReference": ref, "integratedSecurity": False, "encrypt": eng == "mssql", "trustServerCertificate": True})
        for _ in range(20):
            r = t.raw("POST", f"/discovery/instances/{i['id']}/verify", {})
            if r.status_code != 429: break
            time.sleep(int(r.headers.get("Retry-After", "5")) + 1)
        assert r.status_code == 202, f"verify {eng} -> {r.status_code} {r.text[:200]}"
        for _ in range(60):
            d = t.call("GET", f"/discovery/instances/{i['id']}")
            if d["databasesCheckedAt"] and d["access"] in ("Accessible", "Verified", "Authenticated") or d["access"] not in ("CredentialsRequired", "Unknown", None) and d["databasesCheckedAt"]: break
            time.sleep(2)
        out[eng] = {"access": d["access"], "reason": d["accessReasonCode"], "counts": d["databaseCounts"], "dbs": sorted(x.get("name") or x.get("databaseName") for x in d["databases"])}
    real_ms = sorted(sql("select name from sys.databases where database_id>4").split())
    pgcmd = f"$env:PGPASSWORD='{pgc['password']}'; & 'C:\\Program Files\\PostgreSQL\\18\\bin\\psql.exe' -h 127.0.0.1 -U {pgc['user']} -d postgres -tAc \"select datname from pg_database where not datistemplate and datname<>'postgres'\""
    rc, o = winx(pgcmd); real_pg = sorted(o.split())
    out["real"] = {"mssql": real_ms, PGE: real_pg}
    assert "e2e_src" in out["mssql"]["dbs"] and "e2e_src" in out[PGE]["dbs"], out
    user_ms = [d for d in out["mssql"]["dbs"] if d not in ("master", "model", "msdb", "tempdb")]
    assert user_ms == real_ms, f"mssql user databases differ: discovered {user_ms} vs {real_ms}"
    assert [d for d in out[PGE]["dbs"] if d not in ("post" + "gres", "template0", "template1")] == real_pg, f"pg databases differ: {out}"
    return out



# ---- helpers: one-shot scheduled tasks under other principals, critical-change log ----------------------------------------------------------
CRIT = api.ROOT / "docs/test-environment/run-20261105/CRITICAL-CHANGES.md"
GAPDIR = r"C:\e2e\gap-win"


def crit(change, why, rollback):
    """Appends a row to CRITICAL-CHANGES.md (lab changes are logged when they are made)."""
    with CRIT.open("a") as f: f.write(f"| {time.strftime('%H:%M')} | WindowsSpecial: {change} | {why} | {rollback} |\n")


def run_as(principal, script_text, name, timeout=120, password=None):
    """Runs a PowerShell script as 'SYSTEM', 'test-user' (console session) or a local account via a one-shot scheduled task; returns the JSON the script wrote to $Out."""
    tag = uuid.uuid4().hex[:6]; script = f"{GAPDIR}\\{name}-{tag}.ps1"; out = f"{GAPDIR}\\{name}-{tag}.json"; task = f"{PREFIX}{name}-{tag}"
    if principal == "SYSTEM": prin = "$p = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest; $reg = @{Principal=$p}"
    elif principal == "test-user": prin = "$p = New-ScheduledTaskPrincipal -UserId 'test-user' -LogonType Interactive -RunLevel Highest; $reg = @{Principal=$p}"
    else: prin = f"$reg = @{{User='{principal}'; Password='{password}'; RunLevel='Limited'}}"
    body = script_text.replace("'", "''")
    ps = f"""New-Item -ItemType Directory -Force '{GAPDIR}' | Out-Null
Set-Content -LiteralPath '{script}' -Value '{body}' -Encoding UTF8
$a = New-ScheduledTaskAction -Execute powershell.exe -Argument '-NoProfile -ExecutionPolicy Bypass -File "{script}" -Out "{out}"'
{prin}
Register-ScheduledTask -TaskName '{task}' -Action $a @reg -Force | Out-Null
Start-ScheduledTask -TaskName '{task}'
$end = (Get-Date).AddSeconds({timeout}); while (-not (Test-Path '{out}') -and (Get-Date) -lt $end) {{ Start-Sleep 1 }}
Start-Sleep 1
if (Test-Path '{out}') {{ Get-Content -Raw -Encoding UTF8 '{out}' }} else {{ 'NO-OUTPUT' }}
Unregister-ScheduledTask -TaskName '{task}' -Confirm:$false
Remove-Item -LiteralPath '{script}','{out}' -Force -ErrorAction SilentlyContinue"""
    rc, o = winx(ps, timeout=timeout + 60)
    assert "NO-OUTPUT" not in o, f"task {principal} produced no output: {o[:300]}"
    return json.loads(o[o.index("{"):])


DPAPI_PROBE = r"""param($Out)
Add-Type -AssemblyName System.Security
$f = 'C:\ProgramData\BackupAgent\agent-identity.bin'
$ent = [Text.Encoding]::UTF8.GetBytes('backup-platform/agent/identity/v1')
$r = [ordered]@{ who = [Environment]::UserDomainName + '\' + [Environment]::UserName; session = [Diagnostics.Process]::GetCurrentProcess().SessionId }
try { $own = [Security.Cryptography.ProtectedData]::Protect([byte[]](1..32), $ent, 'CurrentUser'); $back = [Security.Cryptography.ProtectedData]::Unprotect($own, $ent, 'CurrentUser'); $r.positiveControl = ($back.Length -eq 32) } catch { $r.positiveControl = 'ERR ' + $_.Exception.Message }
try { $b = [IO.File]::ReadAllBytes($f); $r.read = 'ok:' + $b.Length
  foreach ($sc in 'CurrentUser', 'LocalMachine') { try { [void][Security.Cryptography.ProtectedData]::Unprotect($b, $ent, $sc); $r['unprotect_' + $sc] = 'DECRYPTED' } catch { $r['unprotect_' + $sc] = 'refused: ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message.Trim() } }
} catch { $r.read = 'denied: ' + $_.Exception.GetType().Name }
try { $d = Get-ChildItem 'C:\ProgramData\BackupAgent' -Force -ErrorAction Stop; $r.listDir = 'ok' } catch { $r.listDir = 'denied' }
$r | ConvertTo-Json -Compress | Set-Content -LiteralPath $Out -Encoding UTF8
"""


def entropy(b):
    import math
    from collections import Counter
    return -sum(c / len(b) * math.log2(c / len(b)) for c in Counter(b).values())


@scenario("gap/AGT-02/windows-service-acl-dpapi")
def agt02(t):
    ag = win_agent(t); out = {}
    # service: runs as the virtual account, automatic start, restart-on-failure, sid type, binaries not writable by the service
    rc, o = winx(r"""$s = Get-CimInstance Win32_Service -Filter "Name='BackupAgent'"
"state=$($s.State);start=$($s.StartMode);acct=$($s.StartName);path=$($s.PathName);pid=$($s.ProcessId)"
(sc.exe qsidtype BackupAgent) -join ' '
(sc.exe qfailure BackupAgent) -join ' '
(sc.exe qc BackupAgent) -join ' '
$p = Get-CimInstance Win32_Process -Filter "ProcessId=$($s.ProcessId)"; "procOwner=" + (Invoke-CimMethod -InputObject $p -MethodName GetOwner | % { $_.Domain + '\' + $_.User })
"procPath=" + $p.ExecutablePath""")
    out["service"] = o.replace("\r", "")
    assert "state=Running" in o and r"acct=NT SERVICE\BackupAgent" in o and "start=Auto" in o and "DELAYED" in o, o
    assert "RESTART" in o, o
    assert r"procOwner=NT SERVICE\BackupAgent" in o, o
    assert ag["connectivity"] == "online" and ag["status"] == "Active", f"agent not online in the control plane: {ag['connectivity']} {ag['status']}"
    # ACLs: protected, only SYSTEM / Administrators / the service account
    rc, o = winx(r"""foreach ($p in 'C:\ProgramData\BackupAgent','C:\ProgramData\BackupAgent\agent-identity.bin','C:\ProgramData\BackupAgent\.enrollment.lock','C:\Program Files\BackupAgent','C:\Program Files\BackupAgent\bin','C:\Program Files\BackupAgent\bin\BackupPlatform.Agent.Host.exe') {
  $a = Get-Acl -LiteralPath $p; $rules = ($a.Access | % { $_.IdentityReference.Value + ':' + $_.AccessControlType + ':' + $_.FileSystemRights + ':inh=' + $_.IsInherited }) -join ' ; '
  "$p | owner=$($a.Owner) | protected=$($a.AreAccessRulesProtected) | $rules" }""")
    out["acl"] = [l.replace("\r", "") for l in o.splitlines() if l.strip()]
    trusted = {"NT AUTHORITY\\SYSTEM", "BUILTIN\\Administrators", "NT SERVICE\\BackupAgent"}
    for line in out["acl"]:
        path, owner, prot, rules = [x.strip() for x in line.split(" | ", 3)]; owner = owner.removeprefix("owner=")
        ids = {r.split(":")[0] for r in rules.split(" ; ")}
        if path.startswith(r"C:\ProgramData"):
            assert ids <= trusted, f"{path}: unexpected principals {ids - trusted}"
            assert all(":Deny:" not in r and ":Allow:" in r for r in rules.split(" ; ")), line
        else:
            assert ids <= trusted, f"{path}: unexpected principals {ids - trusted}"
        assert owner in ("BUILTIN\\Administrators", "NT AUTHORITY\\SYSTEM", "NT SERVICE\\BackupAgent"), f"{path}: owner {owner}"
        if path.endswith("\\BackupAgent") and "ProgramData" in path: assert prot == "protected=True", line
    bin_rules = next(l for l in out["acl"] if l.startswith(r"C:\Program Files\BackupAgent\bin |"))
    svc = [r for r in bin_rules.split(" | ", 3)[3].split(" ; ") if r.startswith("NT SERVICE\\BackupAgent")]
    assert svc and all("Write" not in r and "FullControl" not in r and "Modify" not in r for r in svc), f"service account can write its own binaries: {svc}"
    # the identity blob: DPAPI structure, no plain-text identity facts
    rc, b64 = winx(r"[Convert]::ToBase64String([IO.File]::ReadAllBytes('C:\ProgramData\BackupAgent\agent-identity.bin'))")
    import base64
    blob = base64.b64decode("".join(b64.split()))
    assert blob[:4] == b"\x01\x00\x00\x00" and blob[4:20] == bytes.fromhex("d08c9ddf0115d1118c7a00c04fc297eb"), "not a DPAPI blob (provider GUID missing)"
    facts = [ag["agentId"], t.id, "agentId", "tenant", "token", "secret", "https://", "192.168.122.1", "win11-e2e", "enroll"]
    leaks = [f for f in facts if f.encode() in blob or f.encode("utf-16le") in blob]
    assert not leaks, f"plain-text identity facts in the blob: {leaks}"
    printable = re.findall(rb"[\x20-\x7e]{8,}", blob)
    out["blob"] = {"bytes": len(blob), "sha256": hashlib.sha256(blob).hexdigest()[:16], "dpapiHeader": blob[:20].hex(), "entropyBitsPerByte": round(entropy(blob), 2), "asciiRuns>=8": len(printable)}
    assert entropy(blob) > 7.0, "blob entropy too low for ciphertext"
    # decryption attempts: other accounts, with a positive control (own blob) in the same context
    out["decrypt"] = {}
    for who in ("test-user", "SYSTEM"):
        r = run_as(who, DPAPI_PROBE, "dpapi")
        out["decrypt"][who] = r
        assert r["positiveControl"] is True, f"{who}: DPAPI positive control failed ({r['positiveControl']}), the refusal below would prove nothing"
        assert r["read"].startswith("ok:"), f"{who} (administrator/SYSTEM) is expected to read the file bytes: {r}"
        assert r["unprotect_CurrentUser"].startswith("refused") and r["unprotect_LocalMachine"].startswith("refused"), f"{who} decrypted the identity: {r}"
    return out



@scenario("gap/AGT-02/windows-standard-user-denied")
def agt02_std(t):
    """A throwaway standard (non-admin) local user cannot list the store directory nor read the identity file."""
    import secrets
    user = "gapwinstd"; pwd = "Aa1!" + secrets.token_urlsafe(18)
    crit("Win11 yerel standart kullanıcı `gapwinstd` (parola rastgele, kodda üretilir) + tek seferlik görev; iş bitince silinir", "AGT-02: standart hesabın kimlik dosyasını okuyamadığını gerçek erişimle kanıtlamak", "`net user gapwinstd /delete`")
    try:
        rc, o = winx(f"New-LocalUser -Name {user} -Password (ConvertTo-SecureString '{pwd}' -AsPlainText -Force) -AccountNeverExpires -PasswordNeverExpires | Out-Null; Add-LocalGroupMember -Group Users -Member {user}; (Get-LocalGroup | ? {{ (Get-LocalGroupMember $_ -EA 0).Name -like '*{user}' }}).Name -join ','")
        assert o.strip() == "Users", f"unexpected group membership: {o[:200]}"
        r = run_as(user, DPAPI_PROBE.replace("$r.read = 'ok:' + $b.Length", "$r.read = 'ok:' + $b.Length"), "std", password=pwd)
        assert r["positiveControl"] is True, r
        assert r["read"].startswith("denied"), f"standard user read the identity file: {r}"
        assert r["listDir"] == "denied", r
        return {"standardUser": r}
    finally:
        winx(f"Remove-LocalUser -Name {user} -ErrorAction SilentlyContinue")
        rc, o = winx("(Get-LocalUser).Name -join ' '"); assert user not in o, "throwaway user not removed"


AGENT_STATE_PS = r"""$ErrorActionPreference = 'Continue'
$s = Get-CimInstance Win32_Service -Filter "Name='BackupAgent'"
$proc = Get-Process -Id $s.ProcessId
"service|$($s.State)|$($s.StartMode)|$($s.StartName)|$($s.PathName)|pid=$($s.ProcessId)|procStart=$($proc.StartTime.ToString('o'))"
"sc|" + ((sc.exe qc BackupAgent) -join ' ') + ((sc.exe qfailure BackupAgent) -join ' ')
"env|" + ((Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\BackupAgent).Environment -join ';')
"task|" + ((Get-ScheduledTask -TaskName BackupAgentUpdate -EA 0 | % { $_.State.ToString() + ':' + $_.Principal.UserId + ':' + (($_.Actions | % { $_.Execute + ' ' + $_.Arguments }) -join ',') }))
foreach ($root in 'C:\Program Files\BackupAgent', 'C:\ProgramData\BackupAgent') {
  "dir|$root|owner=" + (Get-Acl -LiteralPath $root).Owner + '|sddl=' + (Get-Acl -LiteralPath $root).Sddl
  Get-ChildItem -LiteralPath $root -Recurse -Force -File | sort FullName | % { "file|$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.ToString('o'))|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)|sddl=$((Get-Acl -LiteralPath $_.FullName).Sddl)" }
}
"leftovers|" + ((Get-ChildItem C:\ProgramData -Directory -Force | ? Name -like 'BackupAgent-install-*' | % Name) -join ',')
"""


@scenario("gap/AGT-12/windows-install-over-existing-refused")
def agt12(t):
    """install.ps1 run over the live install must refuse, change nothing (files, hashes, ACLs, service, registry, process start time) and not use up the token."""
    ag0 = win_agent(t); agents_before = sorted(a["agentId"] for a in t.agents().values())
    secret = subprocess.run([sys.executable, str(api.ROOT / "scripts/e2e/enroll.py"), "a"], check=True, capture_output=True, text=True).stdout.strip()
    tid = str(uuid.UUID(hex=secret.split(".", 1)[0]))
    try:
        rc, before = winx(AGENT_STATE_PS)
        assert before.count("file|") > 50 and "service|Running" in before, before[:300]
        dl = winx(r"""Invoke-WebRequest -UseBasicParsing -Uri 'https://192.168.122.1:5252/api/agent-dist/install.ps1' -OutFile C:\e2e\gap-win\install.ps1; (Get-FileHash C:\e2e\gap-win\install.ps1).Hash + ' ' + (Get-Item C:\e2e\gap-win\install.ps1).Length""") if winx(r"New-Item -ItemType Directory -Force C:\e2e\gap-win | Out-Null")[0] == 0 else None
        script_sha = dl[1].strip().splitlines()[-1]
        rc, run = winx(f"powershell -NoProfile -ExecutionPolicy Bypass -File C:\\e2e\\gap-win\\install.ps1 -Server https://192.168.122.1:5252 -Token {secret} -AgentName gap-win-reinstall -NonInteractive; 'exit=' + $LASTEXITCODE")
        run = run.replace(secret, "<token>")
        rc, after = winx(AGENT_STATE_PS)
        assert before == after, "installer changed the installed state:\n" + "\n".join(l for l in set(before.splitlines()) ^ set(after.splitlines()))[:1500]
        refusal = [l for l in run.splitlines() if "nothing was replaced" in l or "hiçbir şey" in l]
        assert refusal, f"no clear refusal message: {run[:600]}"
        assert "exit=1" in run or "exit=0" not in run, f"installer exit code should be non-zero: {run[-200:]}"
        assert "gap-win-reinstall" not in t.agents(), "a gap-win-reinstall agent record appeared"
        ag1 = win_agent(t); assert ag1["agentId"] == ag0["agentId"] and ag1["status"] == "Active" and ag1["connectivity"] == "online"
        st = t.call("GET", f"/agent-enrollment-tokens/{tid}") if tid else None
        return {"installScriptSha256+len": script_sha, "installerOutput": run.strip()[-400:], "stateLines": len(before.splitlines()), "filesCompared": before.count("file|"), "tokenStatusAfter": st, "agentId": ag1["agentId"]}
    finally:
        if tid: t.raw("DELETE", f"/agent-enrollment-tokens/{tid}")
        winx(r"Remove-Item C:\e2e\gap-win\install.ps1 -Force -ErrorAction SilentlyContinue")

SCENARIOS = [agt08, agt08_verify, agt02, agt02_std, agt12]

if __name__ == "__main__":
    only = sys.argv[1].split(",") if len(sys.argv) > 1 else None
    t = lib.Tenant("a")
    for fn in SCENARIOS:
        if only and not any(o in fn.scenario for o in only): continue
        fn(t)
