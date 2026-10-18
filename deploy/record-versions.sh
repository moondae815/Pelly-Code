#!/usr/bin/env bash
# 개발 PC의 Ollama에서 Ollama 버전과 모델 GGUF SHA-256을 읽어 deploy/version-record.json에 쓴다 (R8).
# CI에는 모델이 없으므로 이 스크립트는 반입 전에 사람이 개발 PC에서 실행하고 결과를 커밋한다.
#   사용: deploy/record-versions.sh [endpoint] [model]
#   기본값: http://localhost:11434, qwen3.6:35b-a3b-coding-nvfp4
# 모델 해시는 /api/show 의 modelfile FROM 줄에 있는 blob 경로(sha256-<hex>)에서 읽는다.
# FROM 줄에 blob이 없으면(예: safetensors 모델은 태그만 나온다) 파일을 쓰지 않고 종료 3.
set -euo pipefail

endpoint="${1:-http://localhost:11434}"
model="${2:-qwen3.6:35b-a3b-coding-nvfp4}"
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(dirname "$here")"
out="$here/version-record.json"

command -v jq >/dev/null || { echo "jq가 필요합니다" >&2; exit 4; }

pelly_version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$repo/Directory.Build.props" | head -n 1)"
[[ -n "$pelly_version" ]] || { echo "Directory.Build.props에서 Version을 찾지 못했습니다" >&2; exit 4; }

ollama_version="$(curl -fsS --max-time 10 "$endpoint/api/version" | jq -er '.version')"

show="$(jq -n --arg m "$model" '{model: $m}' | curl -fsS --max-time 30 "$endpoint/api/show" -H 'Content-Type: application/json' -d @-)"
from_line="$(printf '%s' "$show" | jq -er '.modelfile' | grep -m 1 '^FROM ' || true)"
if [[ ! "$from_line" =~ sha256-([0-9a-f]{64}) ]]; then
  echo "modelfile FROM 줄에 sha256 blob이 없습니다: '${from_line}'" >&2
  echo "format: $(printf '%s' "$show" | jq -r '.details.format // "?"'), quantization: $(printf '%s' "$show" | jq -r '.details.quantization_level // "?"')" >&2
  exit 3
fi
gguf_sha="${BASH_REMATCH[1]}"

jq -n \
  --arg pelly "$pelly_version" \
  --arg ollama "$ollama_version" \
  --arg model "$model" \
  --arg sha "$gguf_sha" \
  --arg at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  '{pellyVersion: $pelly, ollamaVersion: $ollama, model: $model, modelGgufSha256: $sha, recordedAt: $at}' > "$out"
echo "wrote $out"
cat "$out"
