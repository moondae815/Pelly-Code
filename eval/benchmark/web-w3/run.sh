#!/usr/bin/env bash
# Runs the W3a spec cases (eval/benchmark/web-w3/w3-*.md with w3-*.yaml expectations) with `pelly apply` against fresh
# copies of eval/corpus/Asp (the site root), like eval/benchmark/asp-a2/run.sh: js and css spec tasks, JS signature changes
# and the callers they leave behind (W3 design §4).
# Per case: (1) `pelly spec check --impact` must exit 0 and print every checkMustContain string and no checkMustNotContain
# string; (2) `pelly apply --yes` must exit with the expected code, the named files must contain the strings
# ("path::text", read as UTF-8 or CP949), the console output must (not) contain the strings, and apply.json must list at
# least jsCallersNotUpdatedMin jsCallersNotUpdated entries. No case needs a database.
# Needs python3. Usage: run.sh [--repeat N] [--out DIR] [--model TAG] [--endpoint URL] [--only ID]
set -u
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
repeat=3
out="${TMPDIR:-/tmp}/web-w3-premeasure"
model="qwen3.6:35b-a3b-q4_K_M"
endpoint="http://localhost:11434"
only=""
while [ $# -gt 0 ]; do
  case "$1" in
    --repeat) repeat="$2"; shift 2;;
    --out) out="$2"; shift 2;;
    --model) model="$2"; shift 2;;
    --endpoint) endpoint="$2"; shift 2;;
    --only) only="$2"; shift 2;;
    *) echo "unknown option: $1" >&2; exit 4;;
  esac
done
pelly="$repo/src/PellyCoder.Cli/bin/Debug/net10.0/pelly.dll"
# Build first so the run never measures a stale pelly (an up-to-date build takes a few seconds).
build_log="$(dotnet build "$repo/src/PellyCoder.Cli" -nologo -v q 2>&1)" || { printf '%s\n' "$build_log" >&2; echo "pelly build failed" >&2; exit 4; }
[ -f "$pelly" ] || { echo "pelly.dll missing after build: $pelly" >&2; exit 4; }
command -v python3 >/dev/null 2>&1 || { echo "python3 is required" >&2; exit 4; }
mkdir -p "$out"
# Prints one yaml field: a scalar (quotes removed) or, for a [..] list, one item per line.
field() {
  python3 -I -c '
import json, sys
key = sys.argv[2] + ":"
for line in open(sys.argv[1], encoding="utf-8"):
    if line.startswith(key):
        v = line[len(key):].strip()
        if v.startswith("["):
            for item in json.loads(v): print(item)
        elif v.startswith("\""):
            print(json.loads(v))
        else:
            print(v)
        break
' "$1" "$2"
}
# Prints the file as UTF-8 text (UTF-8 files as they are, anything else read as CP949), newlines joined to spaces.
text() { { iconv -f UTF-8 -t UTF-8 "$1" 2>/dev/null || iconv -f CP949 -t UTF-8 "$1"; } | tr '\r\n' '  '; }
results="$out/results.tsv"
printf 'id\trun\tcheck\tapply\texit\tseconds\tdetail\n' > "$results"
failures=0
total_start=$(date +%s)

for yaml in "$here"/w3-*.yaml; do
  id="$(field "$yaml" id)"
  [ -n "$only" ] && [ "$only" != "$id" ] && continue
  want_exit="$(field "$yaml" exit)"; min_callers="$(field "$yaml" jsCallersNotUpdatedMin)"
  for i in $(seq 1 "$repeat"); do
    dest="$out/$id-$i"; work="$(mktemp -d "${TMPDIR:-/tmp}/web-w3-work.XXXXXX")"
    mkdir -p "$dest" "$work/.git" "$work/changes"
    cp -R "$repo/eval/corpus/Asp/." "$work/"; rm -rf "$work/.pelly"
    cp "$here/$id.md" "$work/changes/$id.md"
    detail=""; check="ok"; apply="-"; code="-"; secs=0
    # (1) spec check with the impact search (no model, no DB needed for the ASP warnings)
    ( cd "$work" && dotnet "$pelly" spec check "changes/$id.md" --impact > "$dest/check.log" 2>&1 ); ccode=$?
    ctext="$(tr '\r\n' '  ' < "$dest/check.log")"
    [ "$ccode" -eq 0 ] || { check="FAIL"; detail="$detail spec-check-exit=$ccode"; }
    while IFS= read -r s; do [ -z "$s" ] && continue; printf '%s' "$ctext" | grep -qF -- "$s" || { check="FAIL"; detail="$detail check-missing:$s"; }; done < <(field "$yaml" checkMustContain)
    while IFS= read -r s; do [ -z "$s" ] && continue; printf '%s' "$ctext" | grep -qF -- "$s" && { check="FAIL"; detail="$detail check-present:$s"; }; done < <(field "$yaml" checkMustNotContain)
    # (2) apply
    start=$(date +%s)
    ( cd "$work" && dotnet "$pelly" apply "changes/$id.md" --yes --model "$model" --endpoint "$endpoint" > "$dest/console.log" 2>&1 ); code=$?
    secs=$(( $(date +%s) - start ))
    apply="ok"
    [ "$code" -eq "$want_exit" ] || { apply="FAIL"; detail="$detail exit=$code(want $want_exit)"; }
    otext="$(tr '\r\n' '  ' < "$dest/console.log")"
    while IFS= read -r s; do [ -z "$s" ] && continue
      f="${s%%::*}"; t="${s#*::}"
      text "$work/$f" | grep -qF -- "$t" || { apply="FAIL"; detail="$detail file-missing:$f::$t"; }
    done < <(field "$yaml" fileMustContain)
    while IFS= read -r s; do [ -z "$s" ] && continue; printf '%s' "$otext" | grep -qF -- "$s" || { apply="FAIL"; detail="$detail output-missing:$s"; }; done < <(field "$yaml" outputMustContain)
    while IFS= read -r s; do [ -z "$s" ] && continue; printf '%s' "$otext" | grep -qF -- "$s" && { apply="FAIL"; detail="$detail output-present:$s"; }; done < <(field "$yaml" outputMustNotContain)
    run="$(ls -d "$work"/.pelly/runs/apply-*/ 2>/dev/null | sort | tail -1)"
    [ -n "$run" ] && cp -R "$run" "$dest/run"
    if [ -n "$min_callers" ]; then
      n="$(python3 -I -c '
import json, sys
try:
    print(len(json.load(open(sys.argv[1], encoding="utf-8")).get("jsCallersNotUpdated") or []))
except Exception:
    print(-1)
' "${run}apply.json")"
      [ "$n" -ge "$min_callers" ] || { apply="FAIL"; detail="$detail jsCallersNotUpdated=$n(want>=$min_callers)"; }
    fi
    [ "$check" = "ok" ] && [ "$apply" = "ok" ] || failures=$((failures + 1))
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$id" "$i" "$check" "$apply" "$code" "$secs" "${detail:--}" >> "$results"
    echo "$id#$i check=$check apply=$apply exit=$code seconds=$secs${detail:+ }$detail"
    rm -rf "$work"
  done
done
echo "total seconds: $(( $(date +%s) - total_start ))"
echo "results: $out (results.tsv)"
[ "$failures" -eq 0 ] || { echo "$failures failing run(s)" >&2; exit 1; }
