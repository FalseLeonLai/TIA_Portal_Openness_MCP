# TIA Portal V19 MCP 引擎（2.8.0）

> 本仓库默认分支为 **`v19`**，独立维护，不再向上游提交 PR。  
> 完整安装、工具清单、能力边界与发版说明见 **[README.V19.md](README.V19.md)**；上游 V20/V21 文档保留在 **[README.UPSTREAM.md](README.UPSTREAM.md)**（英文）与 **[README.UPSTREAM.zh-CN.md](README.UPSTREAM.zh-CN.md)**。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE) [![Release](https://img.shields.io/github/v/release/FalseLeonLai/TIA_Portal_Openness_MCP)](https://github.com/FalseLeonLai/TIA_Portal_Openness_MCP/releases) [![validate-bundle](https://github.com/FalseLeonLai/TIA_Portal_Openness_MCP/actions/workflows/validate.yml/badge.svg)](https://github.com/FalseLeonLai/TIA_Portal_Openness_MCP/actions/workflows/validate.yml)

通过 **MCP（stdio 或 HTTP）** 在 **TIA Portal V19** 里读写工程：建项目、加硬件、生成 PLC（Tag / UDT / DB / SCL / LAD）、导出块、编译诊断、保存；不要求会 Openness 编码。

- **239 个工具**（V19 编译面），默认 **lite** 档 71 个；其余用 `FindTools` + `CallTool` 按需触达。
- 同一个 `TiaMcpServer.exe` 也是命令行 `tia`：`gen` / `patch` / `compile` / `describe` / `export` / `import` / `prewarm` / `schema` / `version`。
- 交付包内含预编译运行时、DLL、Skill、工具清单、能力矩阵、PLC/HMI 模板、项目蓝图与手册；**不含**西门子官方 DLL。

## 2.8.0 新增：Trust & Batch

| 工具 | 作用 |
|---|---|
| `GetToolCapabilities` | 每个工具的 access / status / requires / evidence；`FindTools` 行尾附 `[access\|status]` |
| `PlanBatch` | 批次静态预检：工具存在、版本支持、DirectOnly、确认门、参数绑定；不执行、不碰 Portal |
| `RunBatch` | 复跑预检后在进程级 Portal 锁内顺序执行；逐操作写 JSONL 账本，支持 `continueOnError` |
| `GetOpsLedger` | 读账本尾部；参数只存 SHA-256，错误文本脱敏，不落明文 |

详见 [`docs/TRUST-LAYER.md`](docs/TRUST-LAYER.md)。

## 快速开始

要求：Windows、TIA Portal **V19**、.NET Framework 4.8；运行账号在 **Siemens TIA Openness** 用户组。

```bat
rem 版本与用法（不需要启动 TIA）
tia-v19.cmd version
tia-v19.cmd help

rem 离线冒烟；加 --with-tia 会启动 TIA 做连接自检
smoke-v19.cmd
smoke-v19.cmd --with-tia
```

MCP 客户端（Cursor / VS Code / Claude Desktop）示例（全量工具）：

```json
{
  "mcpServers": {
    "tia-portal-v19": {
      "command": "C:\\path\\to\\TiaMcpServer.exe",
      "args": [
        "--tia-portal-location", "C:\\Program Files\\Siemens\\Automation\\Portal V19",
        "--tia-major-version", "19",
        "--profile", "full"
      ]
    }
  }
}
```

完整参数、HTTP 模式、工具档位与已知限制见 **[README.V19.md](README.V19.md)**。

## 文档

- [README.V19.md](README.V19.md) — 安装、运行、构建、V19 能力边界
- [docs/AGENT_COOKBOOK.md](docs/AGENT_COOKBOOK.md) — 给 AI agent 的决策表与黄金路径
- [docs/TOOLS_REFERENCE.md](docs/TOOLS_REFERENCE.md) — 239 个工具说明
- [docs/TRUST-LAYER.md](docs/TRUST-LAYER.md) — 能力矩阵 / 批次 / 账本设计
- [docs/full-project-generation-runbook.md](docs/full-project-generation-runbook.md) — 从空项目生成 PLC + WinCC Unified

## 验证状态

- V19 Release 构建 **0 errors**（2 个既有 warnings）
- 离线测试 **698 passed / 0 failed / 0 skipped**
- `runtime/v21/TiaMcpServer.exe` 与包内 V19 引擎均为 **2.8.0.0**
- 真实 TIA 启动冒烟需在 Windows 上执行；构建机为 Linux

## 发版

最新版：https://github.com/FalseLeonLai/TIA_Portal_Openness_MCP/releases/latest  
完整包：`TIA_MCP_V19_<version>.zip`；发版流程见 [docs/RELEASE_CHECKLIST_V19.md](docs/RELEASE_CHECKLIST_V19.md)。

## License

MIT。上游项目：`bulaofen0036-coder/TIA_Portal_Openness_MCP`；本分支为 V19 独立维护版。
