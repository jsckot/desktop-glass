param([string]$Dotnet='dotnet',[string]$InnoCompiler='')
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
Push-Location $project
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
    & $Dotnet run --project tests/DesktopGlass.Tests -c Release
    if($LASTEXITCODE -ne 0){throw '领域测试失败'}
    $output=Join-Path $project '.codex/artifacts/desktop-glass-0.4.1-win-x64'
    & $Dotnet publish src/DesktopGlass.App -c Release -r win-x64 --self-contained true -p:DebugType=None -o $output
    if($LASTEXITCODE -ne 0){throw '发布失败'}
    [IO.File]::WriteAllText((Join-Path $output '.desktop-glass-owner'),'DesktopGlass-52ed77bc-5636-48d9-98f9-548648e153bc')
    $files=@(Get-ChildItem -LiteralPath $output -Force -File -Recurse | Where-Object {$_.Name -ne 'bundle-manifest.json'} | ForEach-Object {[IO.Path]::GetRelativePath($output,$_.FullName)})
    ConvertTo-Json -InputObject $files | Set-Content -LiteralPath (Join-Path $output 'bundle-manifest.json') -Encoding utf8
    if(-not $InnoCompiler){$InnoCompiler=Join-Path $project '.codex/tools/inno-6.7.3/ISCC.exe'}
    if(-not(Test-Path -LiteralPath $InnoCompiler)){throw '请通过 -InnoCompiler 指定 Inno Setup 6.7+ 的 ISCC.exe'}
    & $InnoCompiler ("/DPackageDir="+$output) ("/DOutputDir="+(Join-Path $project '.codex/artifacts')) installer/DesktopGlass.iss
    if($LASTEXITCODE -ne 0){throw 'EXE 安装器编译失败'}
    Write-Output ("安装包："+(Join-Path $project '.codex/artifacts/DesktopGlass-Setup-0.4.1-x64.exe'))
} finally {Pop-Location}
