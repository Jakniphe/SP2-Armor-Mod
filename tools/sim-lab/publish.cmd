@echo off
rem Builds the desktop program as ONE self-contained .exe (nothing to install to run it) in the "publish" folder.
rem Needs the .NET 10 SDK to build (https://dotnet.microsoft.com/download/dotnet/10.0).
cd /d "%~dp0"
dotnet publish Desktop\SimLab.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if errorlevel 1 (
  echo.
  echo The build failed. Is the .NET 10 SDK installed?  https://dotnet.microsoft.com/download/dotnet/10.0
  pause
  exit /b 1
)
echo.
echo Done: %~dp0publish\ArmorImpactLab.exe
pause
