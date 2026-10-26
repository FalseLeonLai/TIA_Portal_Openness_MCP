@echo off
chcp 65001 >nul
rem TIA Portal V19 MCP entry point (bin-v19 build).
rem Usage: tia-v19 version | tia-v19 help | tia-v19 schema
"%~dp0tools\tiaportal-mcp\src\TiaMcpServer\bin-v19\Release\net48\TiaMcpServer.exe" %*
exit /b %ERRORLEVEL%
