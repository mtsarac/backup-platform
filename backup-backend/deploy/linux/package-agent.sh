#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 5 ]]; then
  printf 'Usage: bash package-agent.sh deb|rpm /absolute/publish version x64|arm64 /absolute/output\n' >&2
  exit 1
fi
format=$1; payload=$2; version=$3; architecture=$4; output=$5
[[ "$format" = deb || "$format" = rpm ]] || exit 1
[[ "$payload" = /* && "$output" = /* && -x "$payload/BackupPlatform.Agent.Host" ]] || { printf 'Absolute payload/output paths and a published executable are required.\n' >&2; exit 1; }
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || { printf 'Package version must be canonical numeric major.minor.patch.\n' >&2; exit 1; }
case "$architecture" in x64) deb_arch=amd64; rpm_arch=x86_64 ;; arm64) deb_arch=arm64; rpm_arch=aarch64 ;; *) exit 1 ;; esac
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$output"
root="$work/root"
installer="$root/usr/lib/backup-platform-agent-installer"
mkdir -p "$installer/payload"
cp -a -- "$payload/." "$installer/payload/"
cp -- "$script_dir/install-agent.sh" "$script_dir/backup-platform-agent.service" "$script_dir/agent-environment.example" "$installer/"
chmod -R go-w "$root"
if [[ "$format" = deb ]]; then
  command -v dpkg-deb >/dev/null
  mkdir -p "$root/DEBIAN"
  cat > "$root/DEBIAN/control" <<CONTROL
Package: backup-platform-agent
Version: $version
Section: admin
Priority: optional
Architecture: $deb_arch
Maintainer: Backup Platform Operations
Depends: libc6, libgcc-s1, libstdc++6, libgssapi-krb5-2, libicu72 | libicu74 | libicu76 | libicu78, libssl3 | libssl3t64, zlib1g, systemd, passwd, util-linux
Description: Backup Platform Linux Agent
 Native agent, secure enrollment and versioned systemd service installation.
CONTROL
  cat > "$root/DEBIAN/postinst" <<POSTINST
#!/bin/sh
set -e
if [ "\$1" = configure ] && [ ! -d /opt/backup-platform-agent/releases/$version ]; then
  /bin/bash /usr/lib/backup-platform-agent-installer/install-agent.sh /usr/lib/backup-platform-agent-installer/payload $version
fi
POSTINST
  cat > "$root/DEBIAN/prerm" <<'PRERM'
#!/bin/sh
set -e
if [ "$1" = remove ]; then systemctl disable --now backup-platform-agent.service; fi
PRERM
  chmod 0755 "$root/DEBIAN/postinst" "$root/DEBIAN/prerm"
  dpkg-deb --root-owner-group --build "$root" "$output/backup-platform-agent_${version}_${deb_arch}.deb"
else
  command -v rpmbuild >/dev/null
  mkdir -p "$work/rpm/BUILD" "$work/rpm/RPMS" "$work/rpm/SOURCES" "$work/rpm/SPECS" "$work/rpm/SRPMS"
  tar -C "$root" -czf "$work/rpm/SOURCES/agent.tar.gz" .
  cat > "$work/rpm/SPECS/agent.spec" <<SPEC
Name: backup-platform-agent
Version: $version
Release: 1
Summary: Backup Platform Linux Agent
License: Proprietary
BuildArch: $rpm_arch
Source0: agent.tar.gz
Requires: glibc, libgcc, libstdc++, krb5-libs, libicu, openssl-libs, zlib, systemd, shadow-utils, util-linux, bash, coreutils
AutoReqProv: no
%description
Native agent, secure enrollment and versioned systemd service installation.
%prep
%build
%install
mkdir -p %{buildroot}
tar -xzf %{SOURCE0} -C %{buildroot}
%post
if [ ! -d /opt/backup-platform-agent/releases/$version ]; then
  /bin/bash /usr/lib/backup-platform-agent-installer/install-agent.sh /usr/lib/backup-platform-agent-installer/payload $version
fi
%preun
if [ "\$1" = 0 ]; then systemctl disable --now backup-platform-agent.service; fi
%files
/usr/lib/backup-platform-agent-installer
SPEC
  rpmbuild --define "_topdir $work/rpm" --target "$rpm_arch" -bb "$work/rpm/SPECS/agent.spec"
  find "$work/rpm/RPMS" -name '*.rpm' -exec cp -- {} "$output/" \;
fi
