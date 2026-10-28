#!/usr/bin/env bash
# Create reference/.env from .env.example with generated secrets when no .env
# exists. Existing files are never touched.
set -euo pipefail

cd -- "$(dirname -- "$0")/.."
[ -f .env ] && exit 0
[ -f .env.example ] || { echo "generate-secrets: .env.example missing" >&2; exit 1; }

secret() { openssl rand -hex 16 2>/dev/null || od -An -tx1 -N16 /dev/urandom | tr -d ' \n'; }

umask 077
cp .env.example .env
# Replace every change-me placeholder value with generated hex.
while IFS= read -r line; do
  key=${line%%=*}
  tmp=$(mktemp)
  sed "s|^${key}=.*$|${key}=$(secret)|" .env >"$tmp" && mv "$tmp" .env
done < <(grep -E '^[A-Z0-9_]+=change-?me' .env)
echo "generated reference/.env (rotated $(grep -cE '^[A-Z0-9_]+=change-?me' .env.example || true) placeholders)"
