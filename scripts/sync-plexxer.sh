#!/usr/bin/env bash
# Re-pulls the generated C# client for the AiBuilder Plexxer app and drops it
# into Code/backend/Generated/. Run after every schema publish.
#
# Env vars:
#   PLEXXER_APP_ID      default: 3ovxi6nl1wgdoggvdic64yutd79pdvu8
#   PLEXXER_API_TOKEN   required (must carry the app:client:y grant)
#   PLEXXER_BASE        default: https://api.plexxer.com

set -euo pipefail

APP_ID="${PLEXXER_APP_ID:-3ovxi6nl1wgdoggvdic64yutd79pdvu8}"
BASE="${PLEXXER_BASE:-https://api.plexxer.com}"
TOKEN="${PLEXXER_API_TOKEN:?PLEXXER_API_TOKEN must be set (needs app:client:y grant)}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="$repo_root/backend/Generated"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Fetching $BASE/apps/$APP_ID/client/csharp ..."
http_code=$(curl -sS -o "$tmp/client.zip" -w "%{http_code}" \
  -H "Authorization: Bearer $TOKEN" \
  "$BASE/apps/$APP_ID/client/csharp")

if [[ "$http_code" != "200" ]]; then
  echo "ERROR: HTTP $http_code from client endpoint" >&2
  exit 1
fi

# Snapshot the set of generated entity filenames before we overwrite, so we
# can warn if a previously-present entity disappeared (i.e. was renamed or
# removed from the schema and we might still reference it in code).
prev_entities="$(cd "$dest" 2>/dev/null && find Entities -maxdepth 1 -name '*.cs' -printf '%f\n' 2>/dev/null | sort || true)"

mkdir -p "$dest"
# Wipe only files the generator owns; keep anything else the user dropped here.
find "$dest" -maxdepth 1 -type f \( -name '*.cs' -o -name '*.csproj' -o -name 'README.md' \) -delete
rm -rf "$dest/Entities"

unzip -q "$tmp/client.zip" -d "$dest"

new_entities="$(cd "$dest" && find Entities -maxdepth 1 -name '*.cs' -printf '%f\n' 2>/dev/null | sort || true)"

if [[ -n "$prev_entities" ]]; then
  dropped="$(comm -23 <(echo "$prev_entities") <(echo "$new_entities"))"
  if [[ -n "$dropped" ]]; then
    echo "WARNING: entities present before this sync but not after:" >&2
    echo "$dropped" | sed 's/^/  - /' >&2
    echo "  Backend code that still references these will fail to compile. Grep and fix." >&2
  fi
fi

echo "Done. Generated $(cd "$dest" && find . -type f | wc -l) files in $dest."
