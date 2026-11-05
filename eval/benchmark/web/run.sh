#!/usr/bin/env bash
# Runs the pelly js and pelly css cases (eval/benchmark/web/js-*.yaml, css-*.yaml) against fresh copies of eval/corpus/Asp.
# A case passes when pelly exits 0 and the edited file has every mustContain string, every mustKeep string and no mustNotContain string.
# mustContain must name at least one string the original corpus file does not have (evidence of the request); a case whose
# mustContain strings are all already in the original is a bad case definition: it is reported, not run, and fails the script.
# mustKeep lists strings already in the original that the edit must not drop (preservation checks).
# A run that does not exit 0 must leave the target file byte-identical (pelly restores it); a mismatch fails the script.
# Needs python3 (reads the yaml lists and summary.json). Nothing runs the JS or CSS in a browser.
# Usage: run.sh [--repeat N] [--out DIR] [--model TAG] [--endpoint URL] [--only ID] [--check-cases]
#   --check-cases only runs the case-definition guard (no model needed).
set -u
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
repeat=3
out="${TMPDIR:-/tmp}/web-premeasure"
model="qwen3.6:35b-a3b-q4_K_M"
endpoint="http://localhost:11434"
only=""
check_only=0
while [ $# -gt 0 ]; do
  case "$1" in
    --repeat) repeat="$2"; shift 2;;
    --out) out="$2"; shift 2;;
    --model) model="$2"; shift 2;;
    --endpoint) endpoint="$2"; shift 2;;
    --only) only="$2"; shift 2;;
    --check-cases) check_only=1; shift;;
    *) echo "unknown option: $1" >&2; exit 4;;
  esac
done
pelly="$repo/src/PellyCoder.Cli/bin/Debug/net10.0/pelly.dll"
command -v python3 >/dev/null 2>&1 || { echo "python3 is required" >&2; exit 4; }
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
# Case-definition guard: prints "<id> ok new:<s>" or "<id> BAD …"; returns 1 when no mustContain string is new.
check_case() {
  local yaml="$1" id file original new="" s
  id="$(field "$yaml" id)"; file="$(field "$yaml" file)"
  original="$(text "$repo/eval/corpus/Asp/$file")"
  while IFS= read -r s; do [ -z "$s" ] && continue; printf '%s' "$original" | grep -qF -- "$s" || new="$new${new:+,}$s"; done < <(field "$yaml" mustContain)
  if [ -z "$new" ]; then echo "$id BAD CASE: every mustContain string is already in $file"; return 1; fi
  while IFS= read -r s; do [ -z "$s" ] && continue
    printf '%s' "$original" | grep -qF -- "$s" || { echo "$id BAD CASE: mustKeep '$s' is not in $file"; return 1; }
  done < <(field "$yaml" mustKeep)
  echo "$id ok new:$new"
}
bad_cases=0
for yaml in "$here"/js-*.yaml "$here"/css-*.yaml; do
  [ -n "$only" ] && [ "$only" != "$(field "$yaml" id)" ] && continue
  check_case "$yaml" || bad_cases=$((bad_cases + 1))
done
if [ "$check_only" -eq 1 ]; then [ "$bad_cases" -eq 0 ]; exit $?; fi
[ -f "$pelly" ] || { echo "build first: $pelly missing" >&2; exit 4; }
mkdir -p "$out"
results="$out/results.tsv"
printf 'id\trun\texit\tseconds\tattempts\tfailureKind\tcontains\tlevelRejections\twarnings\tchecks\n' > "$results"
mismatches=0
total_start=$(date +%s)

for yaml in "$here"/js-*.yaml "$here"/css-*.yaml; do
  id="$(field "$yaml" id)"
  [ -n "$only" ] && [ "$only" != "$id" ] && continue
  check_case "$yaml" > /dev/null || { echo "$id skipped: bad case definition"; continue; }
  file="$(field "$yaml" file)"; fn="$(field "$yaml" function)"; sel="$(field "$yaml" selector)"; lines="$(field "$yaml" lines)"
  request="$(field "$yaml" request)"
  case "$file" in *.css) cmd=css;; *) cmd=js;; esac
  if [ -n "$fn" ]; then target=(--function "$fn"); elif [ -n "$sel" ]; then target=(--selector "$sel"); else target=(--lines "$lines"); fi
  for i in $(seq 1 "$repeat"); do
    dest="$out/$id-$i"
    work="$(mktemp -d "${TMPDIR:-/tmp}/web-work.XXXXXX")"
    cp -R "$repo/eval/corpus/Asp/." "$work/"
    rm -rf "$work/.pelly"
    mkdir -p "$work/.git" "$dest"   # .git marks the copy as the workspace root, so its pelly.json applies
    before="$(sha "$work/$file")"
    start=$(date +%s)
    ( cd "$work" && dotnet "$pelly" "$cmd" "$file" "$request" "${target[@]}" --yes \
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
      while IFS= read -r s; do [ -z "$s" ] && continue; printf '%s' "$body" | grep -qF -- "$s" || contains="dropped:$s"; done < <(field "$yaml" mustKeep)
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
    print("-\t-\t-\t-\t-"); sys.exit()
web = s.get("web") or {}
checks = ",".join(f"{k}={v}" for k, v in sorted((web.get("checks") or {}).items()))
warnings = "name=%s,api=%s,property=%s" % (web.get("nameWarnings", "-"), web.get("apiWarnings", "-"), web.get("propertyWarnings", "-"))
print("\t".join([str(s.get("attempts", "-")), s.get("failureKind") or "-", str(web.get("levelRejections", "-")), warnings, checks or "-"]))
' "$dest/summary.json")"
    attempts="$(printf '%s' "$meta" | cut -f1)"; kind="$(printf '%s' "$meta" | cut -f2)"
    level="$(printf '%s' "$meta" | cut -f3)"; warnings="$(printf '%s' "$meta" | cut -f4)"; checks="$(printf '%s' "$meta" | cut -f5)"
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$id" "$i" "$code" "$((end - start))" "$attempts" "$kind" "$contains" "$level" "$warnings" "$checks" >> "$results"
    echo "$id#$i exit=$code seconds=$((end - start)) attempts=$attempts kind=$kind contains=$contains levelRejections=$level"
    rm -rf "$work"
  done
done
echo "total seconds: $(( $(date +%s) - total_start ))"
echo "results: $out (results.tsv)"
[ "$bad_cases" -eq 0 ] || echo "bad case definition(s): $bad_cases (not run)" >&2
[ "$mismatches" -eq 0 ] || { echo "target changed in $mismatches failed run(s)" >&2; exit 1; }
[ "$bad_cases" -eq 0 ] || exit 1
