#!/usr/bin/env bash
# Builds a plugin package and adds it to the repository manifest in dist/.
#
# Usage: scripts/release.sh 1.0.1.0
#
# Afterwards, commit and push dist/. Jellyfin picks the new version up on its next
# repository refresh, and the plugin shows an update in the dashboard.
set -euo pipefail

VERSION="${1:?usage: scripts/release.sh <version>   e.g. 1.0.1.0}"
GITHUB_USER="mcgrizzz"
GITHUB_REPO="jimaku-jellyfin"
BRANCH="main"

RAW="https://${GITHUB_USER}.github.io/${GITHUB_REPO}/dist"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$HOME/.local/bin:$PATH"

echo "==> Testing"
dotnet test -c Release --nologo

echo "==> Packaging ${VERSION}"
mkdir -p artifacts dist
jprm plugin build . --output ./artifacts --version "$VERSION" --dotnet-framework net9.0

ZIP="jimaku_${VERSION}.zip"
cp "artifacts/${ZIP}" "dist/${ZIP}"

echo "==> Updating manifest"
[ -f dist/manifest.json ] || jprm repo init ./dist/manifest.json

# -U pins the exact URL; without it jprm invents a directory layout raw hosting does not have.
jprm repo add ./dist/manifest.json "dist/${ZIP}" -U "${RAW}/${ZIP}"

echo
echo "Done. dist/${ZIP} and dist/manifest.json are ready."
echo "Now:  git add dist && git commit -m \"Release ${VERSION}\" && git push"
echo
echo "GitHub Pages republishes within a minute or so of the push, and Jellyfin"
echo "picks the new version up on its next repository refresh."
