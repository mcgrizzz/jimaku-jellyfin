#!/usr/bin/env bash
# jsDelivr caches branch-pinned URLs for up to 12 hours. After pushing a new release, purge the
# manifest (and the new zip) or Jellyfin will keep seeing the previous version.
set -euo pipefail

GITHUB_USER="mcgrizzz"
GITHUB_REPO="jimaku-jellyfin"
BRANCH="main"
BASE="https://purge.jsdelivr.net/gh/${GITHUB_USER}/${GITHUB_REPO}@${BRANCH}/dist"

for file in "$@" manifest.json; do
    echo "purging ${file}"
    curl -sS "${BASE}/${file}" | head -c 200
    echo
done
