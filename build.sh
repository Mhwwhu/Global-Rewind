#!/usr/bin/env sh
# 跨平台构建入口（薄壳）：真正的探测 / 编译 / 部署逻辑在 build/Program.cs，
# 这样 Windows / Linux / macOS 只维护一份实现，不会各写一套慢慢漂移。
#
#   bash build.sh               # 编译 + 部署到 <游戏>/mods/GlobalRewind
#   bash build.sh --no-deploy   # 只编译
#   bash build.sh --help        # 全部选项
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

exec dotnet run --project "$ROOT/build/Build.csproj" -- "$@"
