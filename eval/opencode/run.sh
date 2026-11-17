#!/bin/bash
# Runs the benchmark with OpenCode instead of pelly's own orchestrator.
#   run.sh <base|mcp|mcp-agents> <out-dir> [--kind cs|sql] [other eval arguments, e.g. --tasks cs-modern-001]
# base        OpenCode without MCP
# mcp         OpenCode with the pelly MCP server (pelly_impact, pelly_verify)
# mcp-agents  as mcp, and the suite overlay puts the recommended AGENTS.md sentence in each root
# Env: OC_MODEL (default ollama/qwen3.6:35b-a3b-q4_K_M), PELLY_MCP_DLL (default: the Release build of src/PellyCode.McpServer).
# SQL tasks need PELLY_SQL_CONNECTION (a person starts those runs).
set -eu
variant=${1:?usage: run.sh <base|mcp|mcp-agents> <out-dir> [eval arguments]}
out=${2:?usage: run.sh <base|mcp|mcp-agents> <out-dir> [eval arguments]}
shift 2
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
mkdir -p "$out"
out=$(cd "$out" && pwd)

case "$variant" in
  base) template=opencode.base.json ;;
  mcp|mcp-agents) template=opencode.mcp.json ;;
  *) echo "unknown variant: $variant (base|mcp|mcp-agents)" >&2; exit 2 ;;
esac

dll=${PELLY_MCP_DLL:-$repo/src/PellyCode.McpServer/bin/Release/net10.0/PellyCodeMcpServer.dll}
if [ "$template" = opencode.mcp.json ] && [ ! -f "$dll" ]; then
  dotnet build "$repo/src/PellyCode.McpServer" -c Release -v q >&2
fi
sed "s|__PELLY_MCP_DLL__|$dll|" "$here/$template" > "$out/opencode.json"

# A suite next to the results: the builtin suite's roots and tasks, plus the AGENTS.md overlay for mcp-agents.
suite="$out/suite"
rm -rf "$suite"; mkdir -p "$suite"
cp -R "$repo/eval/benchmark/tasks" "$suite/tasks"
[ -d "$repo/eval/benchmark/judges" ] && cp -R "$repo/eval/benchmark/judges" "$suite/judges"
overlay=""
[ "$variant" = mcp-agents ] && overlay=", \"overlay\": \"$here/overlay\""
cat > "$suite/suite.json" <<JSON
{
  "formatVersion": 1,
  "name": "opencode-$variant",
  "roots": [
    { "name": "Modern", "path": "$repo/eval/corpus/Modern"$overlay },
    { "name": "Legacy", "path": "$repo/eval/corpus/Legacy"$overlay },
    { "name": "Sql", "path": "$repo/eval/corpus/Sql"$overlay }
  ],
  "supportFiles": ["$repo/eval/corpus/Directory.Build.props", "$repo/eval/corpus/Directory.Packages.props", "$repo/global.json"],
  "resultsDir": "$out/results"
}
JSON

export OC_CONFIG="$out/opencode.json"
export OC_MODEL=${OC_MODEL:-ollama/qwen3.6:35b-a3b-q4_K_M}
export PELLY_EVAL_AGENT="$here/opencode-agent.sh"
cd "$repo"
exec dotnet run --project eval/PellyCode.Eval -- --suite "$suite" --out "$out/results" "$@"
