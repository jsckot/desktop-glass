param([string]$Dotnet='dotnet')
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
Push-Location $project
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
    & $Dotnet run --project tests/DesktopGlass.Tests -c Release
    if($LASTEXITCODE -ne 0){throw '领域测试失败'}
    $output=Join-Path $project '.codex/artifacts/desktop-glass-mvp-win-x64'
    & $Dotnet publish src/DesktopGlass.App -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.13 -p:DebugType=None -o $output
    if($LASTEXITCODE -ne 0){throw '发布失败'}
    $files=@(Get-ChildItem -LiteralPath $output -File -Recurse | Where-Object {$_.Name -ne 'bundle-manifest.json'} | ForEach-Object {[IO.Path]::GetRelativePath($output,$_.FullName)})
    ConvertTo-Json -InputObject $files | Set-Content -LiteralPath (Join-Path $output 'bundle-manifest.json') -Encoding utf8
    Compress-Archive -Path "$output/*" -DestinationPath (Join-Path $project '.codex/artifacts/DesktopGlass-0.1.0-win-x64.zip') -Force
    Write-Output "安装程序：$output/DesktopGlass.exe"
} finally {Pop-Location}
