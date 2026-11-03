@echo off
rem Build ZaxLet; output goes to dist\ZaxLet.exe (needs the .NET 8 SDK to build, the .NET 8 Desktop Runtime to run).
rem Uses the portable SDK in .dotnet when present, otherwise dotnet from PATH.
set DN=dotnet
if exist "%~dp0..\.dotnet\dotnet.exe" set DN="%~dp0..\.dotnet\dotnet.exe"
%DN% publish "%~dp0..\src\ZaxLet\ZaxLet.csproj" -c Release -o "%~dp0..\dist" --no-self-contained
