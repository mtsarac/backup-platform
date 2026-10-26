#!/usr/bin/env python3
"""Parse private local credentials as data, never as executable shell input."""
import json
import os
import re
import sys
from pathlib import Path

import yaml


def read_sections(path):
    sections = {"postgres": {}, "mssql": {}, "windows": {}, "windows_postgres": {}}
    data = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
    local, vm = data.get("local") or {}, data.get("windows11_vm") or {}
    pg_section, windows_pg_section = (next(k for k in sections if match(k)) for match in (
        lambda k: k not in ("mssql", "windows") and not k.startswith("windows_"),
        lambda k: k.startswith("windows_")))
    # YAML names the PostgreSQL block after the engine; sections use the same names as before.
    local_pg = next((v for k, v in local.items() if k != "mssql"), None)
    vm_pg = next((v for k, v in vm.items() if isinstance(v, dict) and k != "mssql"), None)

    def fill(section, source, fields):
        for legacy, field in fields.items():
            if (value := (source or {}).get(field)) is not None:
                sections[section][legacy] = str(value)

    fill(pg_section, local_pg, {"HOST": "host", "PORT": "port", "POSTGRES_USER": "user", "POSTGRES_PASSWORD": "password"})
    fill("mssql", local.get("mssql"), {"HOST": "host", "PORT": "port", "USER": "user", "MSSQL_SA_PASSWORD": "password"})
    fill("windows", vm.get("mssql"), {"HOST": "host", "PORT": "port", "USER": "user", "MSSQL_SA_PASSWORD": "password"})
    if vm.get("ssh"):
        sections["windows"]["SSH"] = str(vm["ssh"])
    fill(windows_pg_section, vm_pg, {"USER": "user", "PASSWORD": "password"})
    return sections


def quote_connection(value):
    return '"' + value.replace('"', '""') + '"'


def configuration(sections):
    pg = sections["postgres"]
    sql = sections["windows" if os.environ.get("BP_MSSQL_PROFILE") == "windows" else "mssql"]
    vm = sections["windows"]
    values = {}
    if all(pg.get(key) for key in ("HOST", "PORT", "POSTGRES_USER", "POSTGRES_PASSWORD")):
        values["BACKUP_PLATFORM_TEST_CONNECTION_STRING"] = ";".join(
            key + "=" + quote_connection(pg[field]) for key, field in
            (("Host", "HOST"), ("Port", "PORT"), ("Username", "POSTGRES_USER"), ("Password", "POSTGRES_PASSWORD")))
        values["BACKUP_PLATFORM_TEST_POSTGRES_PASSWORD"] = pg["POSTGRES_PASSWORD"]
    for suffix, key in (("HOST", "HOST"), ("PORT", "PORT"), ("USER", "USER"), ("PASSWORD", "MSSQL_SA_PASSWORD")):
        if sql.get(key):
            values["BACKUP_PLATFORM_TEST_MSSQL_" + suffix] = sql[key]
    values["BACKUP_PLATFORM_TEST_MSSQL_TRUST_SERVER_CERTIFICATE"] = "true"
    for suffix, key in (("USER", "USER"), ("PASSWORD", "PASSWORD")):
        if value := sections.get("windows_postgres", {}).get(key):
            values["BACKUP_PLATFORM_TEST_WINDOWS_POSTGRES_" + suffix] = value
    ssh = vm.get("SSH", "")
    match = re.fullmatch(r"ssh\s+([\w.@-]+)", ssh)
    host = os.environ.get("WINDOWS_SSH_HOST", vm.get("HOST", "192.168.122.218"))
    user = os.environ.get("WINDOWS_SSH_USER", "")
    alias = match[1] if match else ""
    # Prefer the configured OpenSSH alias/key; an explicit user selects the host fallback.
    if user:
        values["WINDOWS_SSH_TARGET"] = f"{user}@{host}"
    elif alias:
        values["WINDOWS_SSH_TARGET"] = alias
    else:
        values["WINDOWS_SSH_TARGET"] = host
    values["WINDOWS_SSH_PORT"] = "22"
    values["WINDOWS_SSH_CONNECT_TIMEOUT"] = "10"
    tools_dir = Path.home() / ".local/pg18/bin"
    if tools_dir.is_dir() and "BACKUP_PLATFORM_TEST_POSTGRES_TOOLS_DIRECTORY" not in os.environ:
        values["BACKUP_PLATFORM_TEST_POSTGRES_TOOLS_DIRECTORY"] = str(tools_dir)
    old_tools_dir = Path.home() / ".local/pg16/bin"
    if old_tools_dir.is_dir() and "BACKUP_PLATFORM_TEST_POSTGRES_OLD_TOOLS_DIRECTORY" not in os.environ:
        values["BACKUP_PLATFORM_TEST_POSTGRES_OLD_TOOLS_DIRECTORY"] = str(old_tools_dir)
    lib_dir = Path.home() / ".local/pg18/lib"
    if lib_dir.is_dir():
        current_ld = os.environ.get("LD_LIBRARY_PATH", "")
        if str(lib_dir) not in current_ld.split(":"):
            values["LD_LIBRARY_PATH"] = f"{lib_dir}:{current_ld}".rstrip(":")
    return values

def redact(sections):
    secrets = {value for section in sections.values() for key, value in section.items()
               if re.search(r"password|secret|token", key, re.IGNORECASE) and value}
    secrets.update(value for key, value in os.environ.items()
                   if re.search(r"password|secret|token|connection_string", key, re.IGNORECASE) and value)
    # Also cover escaped secrets in JSON and connection strings.
    secrets.update(json.dumps(value)[1:-1] for value in list(secrets))
    secrets.update(value.replace('"', '""') for value in list(secrets))
    ordered = sorted(secrets, key=len, reverse=True)
    for line in sys.stdin:
        for secret in ordered:
            line = line.replace(secret, "[REDACTED]")
        line = re.sub(r"(::add-mask::).*", r"\1[REDACTED]", line)
        sys.stdout.write(line)
        sys.stdout.flush()


def main():
    if len(sys.argv) != 3 or sys.argv[1] not in ("load", "redact"):
        raise ValueError("Expected load|redact and private config path")
    path = Path(sys.argv[2])
    sections = read_sections(path) if path.is_file() else {key: {} for key in ("postgres", "mssql", "windows")}
    if sys.argv[1] == "redact":
        redact(sections)
        return
    if os.environ.get("BP_MSSQL_PROFILE", "local") not in ("local", "windows"):
        raise ValueError("BP_MSSQL_PROFILE must be local or windows")
    for key, value in configuration(sections).items():
        value = os.environ.get(key) or value
        sys.stdout.buffer.write(key.encode() + b"\0" + value.encode() + b"\0")


if __name__ == "__main__":
    try:
        main()
    except BrokenPipeError:
        pass
    except (OSError, ValueError):
        print("Local configuration operation failed; check file format and permissions (values withheld).", file=sys.stderr)
        sys.exit(2)
