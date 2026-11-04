#!/usr/bin/env bash
# Runs the pelly asp cases (eval/benchmark/asp/asp-*.yaml) against fresh copies of eval/corpus/Asp.
# A case passes when pelly exits 0 and the edited file has every mustContain string and no mustNotContain string.
# A run that does not exit 0 must leave the target file byte-identical (pelly restores it); a mismatch fails the script.
# Needs python3 (reads the yaml lists and summary.json). The VBScript compile check is skipped off Windows.
# Usage: run.sh [--repeat N] [--out DIR] [--model TAG] [--endpoint URL] [--only ID]
set -u
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
repeat=3
out="${TMPDIR:-/tmp}/asp-premeasure"
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
[ -f "$pelly" ] || { echo "build first: $pelly missing" >&2; exit 4; }
command -v python3 >/dev/null 2>&1 || { echo "python3 is required" >&2; exit 4; }
mkdir -p "$out"
sha() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
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
# Prints the file as UTF-8 text (UTF-8 files as they are, anything else read as CP949).
text() { iconv -f UTF-8 -t UTF-8 "$1" 2>/dev/null || iconv -f CP949 -t UTF-8 "$1"; }
results="$out/results.tsv"
printf 'id\trun\texit\tseconds\tattempts\tfailureKind\tcontains\tchecks\n' > "$results"
mismatches=0
total_start=$(date +%s)

for yaml in "$here"/asp-*.yaml; do
  id="$(field "$yaml" id)"
  [ -n "$only" ] && [ "$only" != "$id" ] && continue
  file="$(field "$yaml" file)"; proc="$(field "$yaml" proc)"; lines="$(field "$yaml" lines)"
  request="$(field "$yaml" request)"
  if [ -n "$proc" ]; then target=(--proc "$proc"); else target=(--lines "$lines"); fi
  for i in $(seq 1 "$repeat"); do
    dest="$out/$id-$i"
    work="$(mktemp -d "${TMPDIR:-/tmp}/asp-work.XXXXXX")"
    cp -R "$repo/eval/corpus/Asp/." "$work/"
    rm -rf "$work/.pelly"
    mkdir -p "$work/.git" "$dest"   # .git marks the copy as the workspace root, so its pelly.json applies
    before="$(sha "$work/$file")"
    start=$(date +%s)
    ( cd "$work" && dotnet "$pelly" asp "$file" "$request" "${target[@]}" --yes \
        --model "$model" --endpoint "$endpoint" > "$dest/console.log" 2>&1 )
    code=$?
    end=$(date +%s)
    echo "$code" > "$dest/exit"; echo $((end - start)) > "$dest/seconds"
    cp "$work/$file" "$dest/result.${file##*.}"
    run="$(ls -d "$work"/.pelly/runs/*/ 2>/dev/null | sort | tail -1)"
    if [ -n "$run" ]; then cp -R "$run" "$dest/run"; [ -f "$run/summary.json" ] && cp "$run/summary.json" "$dest/summary.json"; fi
    contains="-"
    if [ "$code" -eq 0 ]; then
      contains="ok"
      body="$(text "$work/$file")"
      while IFS= read -r s; do [ -z "$s" ] && continue; printf '%s' "$body" | grep -qF -- "$s" || contains="missing:$s"; done < <(field "$yaml" mustContain)
      while IFS= read -r s; do [ -z "$s" ] && continue; printf '%s' "$body" | grep -qF -- "$s" && contains="present:$s"; done < <(field "$yaml" mustNotContain)
    else
      after="$(sha "$work/$file" 2>/dev/null || echo missing)"
      if [ "$before" != "$after" ]; then
        echo "$before $after" > "$dest/target-changed"; mismatches=$((mismatches + 1)); contains="TARGET CHANGED"
      fi
    fi
    meta="$(python3 -I -c '
import json, sys
try:
    s = json.load(open(sys.argv[1], encoding="utf-8"))
except Exception:
    print("-\t-\t-"); sys.exit()
checks = ",".join(f"{k}={v}" for k, v in sorted(((s.get("asp") or {}).get("checks") or {}).items()))
print("\t".join([str(s.get("attempts", "-")), s.get("failureKind") or "-", checks or "-"]))
' "$dest/summary.json")"
    attempts="$(printf '%s' "$meta" | cut -f1)"; kind="$(printf '%s' "$meta" | cut -f2)"; checks="$(printf '%s' "$meta" | cut -f3)"
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$id" "$i" "$code" "$((end - start))" "$attempts" "$kind" "$contains" "$checks" >> "$results"
    echo "$id#$i exit=$code seconds=$((end - start)) attempts=$attempts kind=$kind contains=$contains"
    rm -rf "$work"
  done
done
echo "total seconds: $(( $(date +%s) - total_start ))"
echo "results: $out (results.tsv)"
[ "$mismatches" -eq 0 ] || { echo "target changed in $mismatches failed run(s)" >&2; exit 1; }
