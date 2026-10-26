#!/usr/bin/env bash
# Stage the official release layout under the fixed installer download names.
set -euo pipefail
# shellcheck source=scripts/dev/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"
dev_prepare_state
dist_dir="$BP_DEV_HOME/agent-dist"
mkdir -p "$dist_dir"
make_runtime_dir
# Enrollment currently accepts numeric System.Version values, not prerelease labels.
version="${BP_AGENT_VERSION:-0.0.0}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail 'BP_AGENT_VERSION must be a numeric major.minor.patch version.'
for platform in win-x64 linux-x64; do
    bash "$REPO_ROOT/.github/scripts/package-release.sh" "agent-$platform" "$version" "$RUNTIME_DIR"
    extension=tar.gz
    [[ "$platform" != win-x64 ]] || extension=zip
    source_archive="$RUNTIME_DIR/backup-agent-$platform-$version.$extension"
    staging="$(mktemp -d "$dist_dir/.staging.XXXXXXXX")"
    register_cleanup_dir "$staging"
    staged="$staging/backup-agent-$platform.$extension"
    cp -- "$source_archive" "$staged"
    chmod 644 "$staged"
    mv -f -- "$staged" "$dist_dir/backup-agent-$platform.$extension"
done
printf 'Distribution packages published to %s\n' "$dist_dir"
