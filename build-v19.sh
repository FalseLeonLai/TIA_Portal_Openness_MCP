#!/usr/bin/env bash
# 在 Linux/CI 上交叉编译 V19 引擎。
# Windows 上直接用 build-v19.cmd 即可（NuGet 包会从注册表找 PublicAPI）。
#
# 用法：
#   ./build-v19.sh "/path/to/Portal V19/PublicAPI/V19"
set -euo pipefail

PUBLIC_API="${1:-}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$ROOT/tools/tiaportal-mcp/src/TiaMcpServer/TiaMcpServer.V19.csproj"

if [ -n "$PUBLIC_API" ]; then
  dotnet build "$PROJ" -c Release -p:SiemensEngineeringDirectory="$PUBLIC_API"
else
  dotnet build "$PROJ" -c Release
fi
