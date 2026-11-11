#!/usr/bin/env bash
# Manually post @ofac/telegram-adapter to npmjs.org — the CI publish path
# (OIDC trusted publisher) is not available from a dev machine, so this is
# the token-auth fallback.
#
# The registry law this script obeys: pnpm rewrites `workspace:*` to the
# dependency's LOCAL VERSION at publish time. So every @ofac package in
# telegram's dependency chain is stamped to the SAME version — stamping
# only telegram would emit deps pinned to an unpublished 0.0.0. The chain
# comes from tools/npm/publishable-packages.mjs --only (deps before
# dependents); versions already on the registry are skipped, so the default
# 0.0.1 run posts only the telegram tarball on top of the placeholder line
# its deps already have. Stamps are always restored — the tree stays at its
# committed version.
#
# Usage: NPM_TOKEN=*** ./tools/npm/publish-telegram-on-npmjs.sh [version]
#   version defaults to 0.0.1 (the placeholder line already on the registry);
#   a token in ~/.npmrc also works and takes precedence.
#   DRY_RUN=1 …                     # rehearse: stamp+build+publish --dry-run
set -euo pipefail
cd "$(dirname "$0")/../.."

VERSION="${1:-0.0.1}"
NAME="@ofac/telegram-adapter"
DIR="src/adapters/communication/telegram"
NPM_PUBLISH_FLAGS=(--no-git-checks --access public)
[ -n "${DRY_RUN:-}" ] && NPM_PUBLISH_FLAGS+=(--dry-run)

mapfile -t closure < <(node tools/npm/publishable-packages.mjs --only "$DIR")

if [ -z "${DRY_RUN:-}" ] &&
  [ "$(npm view "${NAME}@${VERSION}" version 2>/dev/null || true)" = "$VERSION" ]; then
  echo "${NAME}@${VERSION} is already on npmjs.org — nothing to do."
  exit 0
fi

# One-off project .npmrc so the token never rides on the command line.
npmrc=""
if [ -n "${NPM_TOKEN:-}" ] && [ ! -f .npmrc ]; then
  npmrc=.npmrc
  printf '//registry.npmjs.org/:_authToken=%s\n' "$NPM_TOKEN" >.npmrc
fi
restore() {
  [ -z "$npmrc" ] || rm -f "$npmrc"
  for d in "${closure[@]}"; do
    git restore -- "$d/package.json" 2>/dev/null || true
  done
}
trap restore EXIT

for d in "${closure[@]}"; do
  echo "stamp $d -> $VERSION"
  npm pkg set version="$VERSION" -C "$d"
done

echo "build $NAME and its workspace deps"
pnpm --filter "${NAME}..." build

for d in "${closure[@]}"; do
  name=$(node -p "require('./$d/package.json').name")
  [ "$name" = "$NAME" ] || [ -n "${PUBLISH_DEPS:-}" ] || continue
  if [ -z "${DRY_RUN:-}" ] &&
    [ "$(npm view "${name}@${VERSION}" version 2>/dev/null || true)" = "$VERSION" ]; then
    echo "skip $name@$VERSION: already on the registry"
    continue
  fi
  echo "publish $name@$VERSION"
  # No --provenance: attestations need CI's OIDC identity — a manual
  # token publish is integrity-hashed only (npm audit signatures still
  # verify the tarball).
  pnpm --dir "$d" publish "${NPM_PUBLISH_FLAGS[@]}"
done

echo
if [ -n "${DRY_RUN:-}" ]; then
  echo "DRY_RUN: nothing published."
else
  echo "done: https://www.npmjs.com/package/${NAME}/v/${VERSION}"
  echo "note: the full-closure stamping this script does is also the Makefile"
  echo "'release' target's law — one stamped version line across the set."
fi
