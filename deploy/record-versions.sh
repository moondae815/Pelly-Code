#!/usr/bin/env bash
# 개발 PC의 Ollama에서 Ollama 버전과 모델 식별 정보를 읽어 deploy/version-record.json에 쓴다 (R8).
# CI에는 모델이 없으므로 이 스크립트는 반입 전에 사람이 개발 PC에서 실행하고 결과를 커밋한다.
#   사용: deploy/record-versions.sh [endpoint] [model]
#   기본값: http://localhost:11434, qwen3.6:35b-a3b-coding-nvfp4
# 모델 정보는 /api/tags 에서 그 모델 행의 digest · details.format · details.quantization_level 을 읽는다.
# 개발 PC 모델은 safetensors(nvfp4)라 GGUF 파일 해시가 없다. 현장 모델의 GGUF SHA-256은 M4에서 기록한다.
# 모델이 서버에 없으면 파일을 쓰지 않고 종료 3. 기록 파일에는 접속 주소를 넣지 않는다.
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

row="$(curl -fsS --max-time 30 "$endpoint/api/tags" | jq -c --arg m "$model" '[.models[] | select(.name == $m or .model == $m)] | first // empty')"
if [[ -z "$row" ]]; then
  echo "모델이 Ollama에 없습니다: $model (/api/tags)" >&2
  exit 3
fi

digest="$(printf '%s' "$row" | jq -er '.digest')"
[[ "$digest" =~ ^[0-9a-f]{64}$ ]] || { echo "digest 형식이 예상과 다릅니다: $digest" >&2; exit 3; }
format="$(printf '%s' "$row" | jq -er '.details.format')"
quant="$(printf '%s' "$row" | jq -er '.details.quantization_level')"

jq -n \
  --arg pelly "$pelly_version" \
  --arg ollama "$ollama_version" \
  --arg model "$model" \
  --arg digest "$digest" \
  --arg format "$format" \
  --arg quant "$quant" \
  --arg at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  '{pellyVersion: $pelly, ollamaVersion: $ollama, model: $model, modelDigest: $digest, modelFormat: $format, modelQuantization: $quant, recordedAt: $at}' > "$out"
echo "wrote $out"
cat "$out"
