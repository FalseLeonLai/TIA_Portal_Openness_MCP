@echo off
chcp 65001 >nul
setlocal
rem 构建 V19 引擎。TIA Portal 装在默认位置时不用带参数（NuGet 包会从注册表找 PublicAPI）；
rem 非标准安装路径请传安装根目录，例如：
rem   build-v19.cmd "D:\app\TIA19\Portal V19"
set "TIA_ROOT=%~1"
if not "%TIA_ROOT%"=="" (
  dotnet build "%~dp0tools\tiaportal-mcp\src\TiaMcpServer\TiaMcpServer.V19.csproj" -c Release -p:SiemensEngineeringDirectory="%TIA_ROOT%\PublicAPI\V19"
) else (
  dotnet build "%~dp0tools\tiaportal-mcp\src\TiaMcpServer\TiaMcpServer.V19.csproj" -c Release
)
exit /b %ERRORLEVEL%
