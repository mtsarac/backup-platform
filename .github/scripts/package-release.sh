#!/usr/bin/env bash
# Builds one release archive. Run from anywhere; needs the .NET 10 SDK (and bun for "frontend").
#
#   package-release.sh <component> <version> <output-dir>
#
# component   agent-win-x64 | agent-linux-x64 | backend-linux-x64 | frontend
# version     semantic version without the leading v, e.g. 0.2.0 or 0.2.0-rc.1. It is stamped into the assemblies
#             (-p:Version), which is what Agent.Host reports as its software version in every heartbeat.
#
# Output: <output-dir>/backup-<component>-<version>.zip (Windows agent) or .tar.gz (everything else).
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <agent-win-x64|agent-linux-x64|backend-linux-x64|frontend> <version> <output-dir>" >&2
  exit 2
fi
component="$1"
version="$2"
mkdir -p "$3"
output="$(cd "$3" && pwd)"

if ! [[ "${version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "version '${version}' is not a semantic version (no leading v, no build metadata)" >&2
  exit 2
fi

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
backend="${repo}/backup-backend"
templates="${backend}/deploy/release"
name="backup-${component}-${version}"
work="$(mktemp -d)"
trap 'rm -rf "${work}"' EXIT
stage="${work}/${name}"
mkdir -p "${stage}"

publish() { # <project> <rid> [extra msbuild args...]
  local project="$1" rid="$2"
  shift 2
  dotnet publish "${backend}/${project}" -c Release -r "${rid}" --self-contained true \
    -p:Version="${version}" -p:ContinuousIntegrationBuild=true -p:SatelliteResourceLanguages=en "$@" -o "${stage}/bin"
  # Development-only logging overrides do not belong in a deployment.
  rm -f "${stage}/bin/appsettings.Development.json"
}

# Agent.Host reports this value as its software version; a release archive must carry exactly the release version.
check_version() { # <assembly>
  dotnet run "${repo}/.github/scripts/check-assembly-version.cs" -- "$1" "${version}"
}

readme() { # <template>
  sed "s/@VERSION@/${version}/g" "${templates}/$1" > "${stage}/README.txt"
}

case "${component}" in
  agent-win-x64)
    publish src/hosts/agent/BackupPlatform.Agent.Host.csproj win-x64 -p:AgentPlatform=Windows
    check_version "${stage}/bin/BackupPlatform.Agent.Host.dll"
    cp "${backend}/deploy/windows/Install-BackupAgentService.ps1" "${backend}/deploy/windows/Uninstall-BackupAgentService.ps1" "${stage}/"
    mkdir "${stage}/config"
    cp "${templates}/agent.settings.example.json" "${stage}/config/"
    readme README-agent-windows.txt
    archive=zip
    ;;
  agent-linux-x64)
    publish src/hosts/agent/BackupPlatform.Agent.Host.csproj linux-x64 -p:AgentPlatform=Linux
    check_version "${stage}/bin/BackupPlatform.Agent.Host.dll"
    mkdir "${stage}/config"
    cp "${templates}/agent.settings.example.json" "${stage}/config/"
    readme README-agent-linux.txt
    archive=tar
    ;;
  backend-linux-x64)
    publish src/hosts/api/BackupPlatform.Api.csproj linux-x64
    # The backend never migrates by itself, so the release carries the schema for exactly this version.
    mkdir "${stage}/sql" "${stage}/config"
    (
      cd "${backend}"
      dotnet tool restore >/dev/null
      # The design-time factory only requires the variable to exist; an idempotent script never connects.
      BACKUP_PLATFORM_CONNECTION_STRING='Host=unused;Database=unused' \
        dotnet ef migrations script --idempotent --configuration Release \
        --project src/core/infrastructure --output "${stage}/sql/migrations.sql" \
        -- -p:ContinuousIntegrationBuild=true >/dev/null
    )
    cp "${backend}/.env.example" "${stage}/config/backend.env.example"
    readme README-backend.txt
    archive=tar
    ;;
  frontend)
    (
      cd "${repo}/backup-frontend"
      bun install --frozen-lockfile
      bun run build
    )
    cp -r "${repo}/backup-frontend/dist" "${stage}/dist"
    readme README-frontend.txt
    archive=tar
    ;;
  *)
    echo "unknown component '${component}'" >&2
    exit 2
    ;;
esac

if [ "${archive}" = zip ]; then
  (cd "${work}" && zip -qr -X "${output}/${name}.zip" "${name}")
  echo "${output}/${name}.zip"
else
  tar -C "${work}" --sort=name --owner=0 --group=0 --numeric-owner -czf "${output}/${name}.tar.gz" "${name}"
  echo "${output}/${name}.tar.gz"
fi
