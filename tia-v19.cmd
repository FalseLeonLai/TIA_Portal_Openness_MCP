@echo off
chcp 65001 >nul
rem `tia` 命令入口（V19）。把本交付包根目录加入 PATH 后，可直接：tia-v19 gen spec.yaml
rem 仅适用于 TIA Portal V19 引擎（bin-v19）。V20/V21 请用 tia-v20.cmd / tia.cmd。
"%~dp0tools\tiaportal-mcp\src\TiaMcpServer\bin-v19\Release\net48\TiaMcpServer.exe" %*
exit /b %ERRORLEVEL%
