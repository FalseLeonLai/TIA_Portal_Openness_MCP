@echo off
chcp 65001 >nul
setlocal
rem Build the V19 engine. No argument needed when TIA Portal is installed in the default path.
rem Non-standard install root: build-v19.cmd "D:\app\TIA19\Portal V19"
set "TIA_ROOT=%~1"
if not "%TIA_ROOT%"=="" (
  dotnet build "%~dp0tools\tiaportal-mcp\src\TiaMcpServer\TiaMcpServer.V19.csproj" -c Release -p:SiemensEngineeringDirectory="%TIA_ROOT%\PublicAPI\V19"
) else (
  dotnet build "%~dp0tools\tiaportal-mcp\src\TiaMcpServer\TiaMcpServer.V19.csproj" -c Release
)
exit /b %ERRORLEVEL%
