#!/bin/bash
# One benchmark task through `opencode run`. Called by eval's ExternalAgent (env PELLY_EVAL_AGENT).
# Args: workdir file kind method line object request.
# Env: OC_CONFIG (OpenCode config file), OC_MODEL (provider/model).
# Every variable that is directly followed by Hangul is written ${var}: under some locales bash reads the Hangul bytes
# as part of the name ($target를 -> an unset variable).
set -u
dir=$1 file=$2 kind=$3 method=$4 line=$5 object=$6 request=$7

# pelly_verify compares against HEAD, so the work copy (which eval copies without .git) must be a repository.
if [ ! -d "$dir/.git" ]; then
  git -C "$dir" init -q
  git -C "$dir" add -A
  git -C "$dir" -c user.name=pelly-eval -c user.email=pelly-eval@localhost commit -q -m baseline --no-gpg-sign
fi
# Project rules (the suite overlay's AGENTS.md lands in the root folder) are read from the folder OpenCode starts in.
root=${file%%/*}
if [ -f "$dir/$root/AGENTS.md" ] && [ ! -f "$dir/AGENTS.md" ]; then cp "$dir/$root/AGENTS.md" "$dir/AGENTS.md"; fi

if [ "$kind" = sql ]; then
  if [ -n "$object" ]; then target="\`${object}\` 객체"; else target="SQL 객체"; fi
  prompt="\`${file}\` 파일의 ${target}를 다음 요청대로 고쳐 줘: ${request}"
elif [ -n "$method" ]; then
  prompt="\`${file}\` 파일의 \`${method}\` 메서드를 다음 요청대로 고쳐 줘: ${request}"
else
  prompt="\`${file}\` 파일 ${line}번째 줄이 있는 메서드를 다음 요청대로 고쳐 줘: ${request}"
fi
echo "PROMPT: ${prompt}"
# stdin from /dev/null: `opencode run` hangs when stdin is not a terminal.
cd "$dir" && OPENCODE_CONFIG="$OC_CONFIG" opencode run --pure --auto -m "$OC_MODEL" "${prompt}" < /dev/null
