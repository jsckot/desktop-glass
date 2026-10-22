param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
foreach ($mode in @('diagnose','smoke','crash-test','ui-test','safety-tests','install-test')) {
    $output = Join-Path $EvidenceDirectory "$mode.json"
    $process = Start-Process -FilePath $Executable -ArgumentList @("--$mode", '--output', "`"$output`"") -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "$mode failed with exit code $($process.ExitCode). See $output" }
    $result = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    if (-not $result.Passed) { throw "$mode failed: $($result.Error)" }
    if($mode -eq 'install-test') {
        $timer=[Diagnostics.Stopwatch]::StartNew()
        while((Test-Path -LiteralPath $result.Sandbox) -and $timer.Elapsed.TotalSeconds -lt 15){Start-Sleep -Milliseconds 200}
        if(Test-Path -LiteralPath $result.Sandbox){throw "卸载清理未完成：$($result.Sandbox)"}
    }
    Write-Output "PASS $mode"
}
