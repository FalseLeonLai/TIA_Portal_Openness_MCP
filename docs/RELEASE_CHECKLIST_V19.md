# V19 分支维护与发版清单

> 适用：`FalseLeonLai/TIA_Portal_Openness_MCP` 的 `v19` 分支。
> 本仓库为**独立维护**，不再向上游 `bulaofen0036-coder/TIA_Portal_Openness_MCP` 提交 PR；上游 `origin` 仅作 fetch 参考。

## 0. 分支/远端约定

| 项 | 值 |
|---|---|
| 仓库 | `FalseLeonLai/TIA_Portal_Openness_MCP`（public） |
| 默认分支 | `v19` |
| `v19` 跟踪 | `fork/v19`（不是 `origin/v19`） |
| 发版 tag | `v<version>-v19`，例如 `v2.7.8.4-v19` |
| 完整包 | `TIA_MCP_V19_<version>.zip` |
| 热修包 | `TIA_MCP_V19_<version>_<scope>-hotfix.zip`，只含 `TiaMcpServer.exe` |
| 校验 | `TIA_MCP_V19_<version>_SHA256SUMS.txt` |

## 1. 代码与版本

```bash
cd ~/csharp-learning/port/repo
git switch v19
git status --short          # 必须是干净的
```

- 改 `TiaMcpServer.V19.csproj` 的 `<AssemblyVersion>`。
- 在 `CHANGELOG.md` 顶部加 `## [<version>] - <date> - <title>`。
- 更新 `README.V19.md` 状态行、修复段、维护策略。
- 工具增删/描述变化后重跑：

```bash
python3 scripts/generate-tools-reference.py
```

## 2. 构建与离线测试

```bash
dotnet build tools/tiaportal-mcp/src/TiaMcpServer/TiaMcpServer.V19.csproj \
  -c Release -p:SiemensEngineeringDirectory=/home/li/csharp-learning/port/siemens19/V19

DOTNET_ROLL_FORWARD=LatestMajor dotnet run \
  --project tools/tiaportal-mcp/tests/TiaMcpServer.Tests -c Release
```

门槛：构建 `0 errors`（允许既有 2 warnings），测试 `0 failed`。

## 3. 提交与 tag

```bash
git add -A
git commit -m "docs(v19): release <version> ..."
git tag -a v<version>-v19 -m "V19 引擎 <version>：<一句话范围>"

git push fork v19
git push fork v<version>-v19
```

## 4. 打包

**完整包**：从上一个大版本完整包复制，避免漏 DLL/templates。

```bash
cd ~/csharp-learning/port/pkg
cp -a TIA_MCP_V19_<previous> TIA_MCP_V19_<version>
cp ../repo/tools/tiaportal-mcp/src/TiaMcpServer/bin-v19/Release/net48/TiaMcpServer.exe TIA_MCP_V19_<version>/
cp ../repo/CHANGELOG.md ../repo/README.V19.md TIA_MCP_V19_<version>/
cp ../repo/docs/TOOLS_REFERENCE.md ../repo/docs/tool-capability-matrix.md \
   ../repo/docs/tools-index.csv TIA_MCP_V19_<version>/docs/
zip -r -q TIA_MCP_V19_<version>.zip TIA_MCP_V19_<version>
```

**热修包**：只放 exe + README-HOTFIX.txt。

```bash
mkdir -p TIA_MCP_V19_<version>-hotfix
cp .../TiaMcpServer.exe TIA_MCP_V19_<version>-hotfix/
# 写 README-HOTFIX.txt（适用版本、改动、验证、回滚）
zip -r -q TIA_MCP_V19_<version>_<scope>-hotfix.zip TIA_MCP_V19_<version>-hotfix
```

**校验和**：

```bash
sha256sum TIA_MCP_V19_<version>.zip TIA_MCP_V19_<version>_<scope>-hotfix.zip \
  | tee TIA_MCP_V19_<version>_SHA256SUMS.txt
```

发版前 `unzip -l` 对一遍 README/release notes 点名的每个文件；资产名只用 ASCII。

## 5. GitHub Release

```bash
gh release create v<version>-v19 \
  --repo FalseLeonLai/TIA_Portal_Openness_MCP \
  --title "V19 引擎 <version>（<摘要>）" \
  --notes-file release-notes-<version>.md \
  --latest \
  TIA_MCP_V19_<version>.zip \
  TIA_MCP_V19_<version>_<scope>-hotfix.zip \
  TIA_MCP_V19_<version>_SHA256SUMS.txt
```

验证：

```bash
gh release list --repo FalseLeonLai/TIA_Portal_Openness_MCP --limit 3
gh release view v<version>-v19 --repo FalseLeonLai/TIA_Portal_Openness_MCP
```

- 最新一条必须带 `Latest`。
- 三个资产的名称/大小与本地一致；走代理时上传可能先 EOF 后继续，必须核对。

## 6. 收尾

- 删除已合并的临时分支（本地 + `fork`）；保留 `v19-lts` 作回退。
- 更新 `V19-TOPIC-SNAPSHOT.md`：分支 SHA、tag、Release URL、验证计数、未真机项。
- 更新 `MAINTENANCE-TODO.md`（如适用）。
- 汇报分三档：真机已验证 / 仅编译+离线 / 未验证。未真机的修复写进 Release notes 的「验证」段。

## 7. 不做的事

- 不向上游开 v19 PR（用户已明确；上游已转向闭源 agent/最新引擎）。
- 不把 `master` 当默认分支；默认分支固定 `v19`。
- 不把 Siemens 的 DLL 打进仓库或发布包。
- 不在 Release 资产里放本地路径、工程名、用户名或测试报告原文。
