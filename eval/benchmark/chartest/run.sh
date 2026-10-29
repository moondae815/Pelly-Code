#!/usr/bin/env bash
# Runs the Modern characterization-test cases (eval/benchmark/chartest/m*.yaml) against fresh corpus copies.
# Legacy cases (l*.yaml, platform: windows) need Windows and are skipped here.
# Usage: run.sh [--repeat N] [--out DIR] [--model TAG] [--endpoint URL]
set -u
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
repeat=3
out="${TMPDIR:-/tmp}/chartest-premeasure"
model="qwen3.6:35b-a3b-q4_K_M"
endpoint="http://localhost:11434"
while [ $# -gt 0 ]; do
  case "$1" in
    --repeat) repeat="$2"; shift 2;;
    --out) out="$2"; shift 2;;
    --model) model="$2"; shift 2;;
    --endpoint) endpoint="$2"; shift 2;;
    *) echo "unknown option: $1" >&2; exit 4;;
  esac
done
pelly="$repo/src/PellyCoder.Cli/bin/Debug/net10.0/pelly.dll"
[ -f "$pelly" ] || { echo "build first: $pelly missing" >&2; exit 4; }
mkdir -p "$out"
field() { sed -n "s/^$2: *//p" "$1" | head -1; }
sha() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
mismatches=0

for yaml in "$here"/m*.yaml; do
  id="$(field "$yaml" id)"; root="$(field "$yaml" root)"; file="$(field "$yaml" file)"
  method="$(field "$yaml" method)"; count="$(field "$yaml" count)"
  for i in $(seq 1 "$repeat"); do
    dest="$out/$id-$i"
    work="$(mktemp -d "${TMPDIR:-/tmp}/chartest-work.XXXXXX")"
    cp -R "$repo/eval/corpus/$root/." "$work/"
    find "$work" -type d \( -name bin -o -name obj -o -name .pelly \) -prune -exec rm -rf {} +
    mkdir -p "$dest"
    ( cd "$work" && dotnet restore "$root.sln" --nologo --disable-parallel > "$dest/restore.log" 2>&1 ) || { echo "$id#$i restore failed"; echo 4 > "$dest/exit"; rm -rf "$work"; continue; }
    before="$(sha "$work/$file")"
    start=$(date +%s)
    ( cd "$work" && dotnet "$pelly" test "$file" --method "$method" --count "$count" --yes \
        --model "$model" --endpoint "$endpoint" > "$dest/console.log" 2>&1 )
    code=$?
    end=$(date +%s)
    echo "$code" > "$dest/exit"; echo $((end - start)) > "$dest/seconds"
    run="$(ls -d "$work"/.pelly/runs/*/ 2>/dev/null | sort | tail -1)"
    if [ -n "$run" ]; then cp -R "$run" "$dest/run"; [ -f "$run/summary.json" ] && cp "$run/summary.json" "$dest/summary.json"; fi
    # The target file must end byte-identical: a mismatch fails the run (and the script).
    after="$(sha "$work/$file" 2>/dev/null || echo missing)"
    if [ "$before" != "$after" ]; then
      echo "$before $after" > "$dest/target-changed"; mismatches=$((mismatches + 1))
      echo "$id#$i exit=$code seconds=$((end - start)) TARGET CHANGED"
    else
      echo "$id#$i exit=$code seconds=$((end - start)) target unchanged"
    fi
    rm -rf "$work"
  done
done
echo "results: $out"
[ "$mismatches" -eq 0 ] || { echo "target changed in $mismatches run(s)" >&2; exit 1; }
