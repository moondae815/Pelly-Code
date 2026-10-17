#!/usr/bin/env bash
# S1 보조: raw 모드에서 stop 옵션이 지켜지는지 확인. 실행: ./stop-check.sh > stop-check-output.txt
set -euo pipefail
model="${PELLY_SPIKE_MODEL:-qwen3.6:35b-a3b-coding-nvfp4}"
endpoint="${PELLY_SPIKE_OLLAMA:-http://localhost:11434}"
for stop in '"</think>"' '"\n</think>"' '"</think>\n"' '"IsNullOrEmpty"'; do
  nonce=$(uuidgen | cut -c1-8)
  prompt="<|im_start|>system\nYou are a C# coding assistant. Think briefly. [$nonce]<|im_end|>\n<|im_start|>user\nC#에서 문자열이 null이거나 비었는지 확인하는 메서드 이름 하나만 말해줘.<|im_end|>\n<|im_start|>assistant\n<think>\n"
  body="{\"model\":\"$model\",\"prompt\":\"$prompt\",\"raw\":true,\"stream\":false,\"options\":{\"num_predict\":3000,\"temperature\":0,\"stop\":[$stop]}}"
  curl -s "$endpoint/api/generate" -d "$body" | python3 -c "
import json,sys; r=json.load(sys.stdin); resp=r['response']
print('stop=' + sys.argv[1], 'done_reason=' + r['done_reason'], 'eval_count=' + str(r['eval_count']), 'contains_close=' + str('</think>' in resp), 'tail=' + repr(resp[-50:]))" "$stop"
done
