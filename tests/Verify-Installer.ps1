param([string]$PackageDir,[string]$InnoCompiler)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
if(-not $PackageDir){$PackageDir=Join-Path $project '.codex/artifacts/desktop-glass-0.4.0-win-x64'}
if(-not $InnoCompiler){$InnoCompiler=Join-Path $project '.codex/tools/inno-6.7.3/ISCC.exe'}
$work=Join-Path $project ('.codex/tmp/installer-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$data=Join-Path $work '隔离 配置'
$target=Join-Path $work '中文 自选目录'
$product='DesktopGlass-52ed77bc-5636-48d9-98f9-548648e153bc'
$results=[Collections.Generic.List[string]]::new()
function Check($Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Run([string]$Exe,[string[]]$Arguments){
    $process=Start-Process -FilePath $Exe -WindowStyle Hidden -ArgumentList $Arguments -PassThru
    if(-not $process.WaitForExit(60000)){throw ('隔离检查超时，保留责任现场，进程 '+$process.Id)}
    return $process.ExitCode
}
function Setup([string]$Name,[string]$Location){
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $work ($Name+'.log'))+'"'))
    if($Location){$arguments+=('/DIR="'+$Location+'"')}
    Run $setup $arguments
}
function Uninstall([string]$Name,[string]$Location){
    Run (Join-Path $Location 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $work ($Name+'.log'))+'"'))
}
try {
    & $InnoCompiler ("/DPackageDir="+$PackageDir) ("/DOutputDir="+$work) ("/DSandboxData="+$data) (Join-Path $project 'installer/DesktopGlass.iss') *> (Join-Path $work 'compile.log')
    Check ($LASTEXITCODE -eq 0) '隔离安装器编译失败'
    $setup=Join-Path $work 'DesktopGlass-Setup-0.4.0-x64-sandbox.exe'
    Check ((Setup 'custom-install' $target) -eq 0) '中文空格目录安装失败'
    Check ((Test-Path (Join-Path $target 'DesktopGlass.exe')) -and -not(Test-Path (Join-Path $target '.maintenance'))) '安装负载或维护标记错误'
    $results.Add('自选中文空格目录安装')
    $foreign=Join-Path $work '非产品目录'; New-Item -ItemType Directory -Path $foreign | Out-Null
    [IO.File]::WriteAllText((Join-Path $foreign '用户文件.txt'),'keep')
    Check ((Setup 'foreign-directory-block' $foreign) -ne 0) '没有拒绝已有未知内容的目录'
    Check (([IO.File]::ReadAllText((Join-Path $foreign '用户文件.txt')) -eq 'keep') -and -not(Test-Path (Join-Path $foreign '.desktop-glass-owner'))) '未知目录遭到改写'
    $results.Add('拒绝已有未知内容的安装目录')

    # 文件占用来自自建文本 fixture；不访问旧的真实安装目录。
    $old=Join-Path $work '旧 安装位置'; New-Item -ItemType Directory -Path $old | Out-Null
    [IO.File]::WriteAllText((Join-Path $old '.desktop-glass-owner'),$product)
    [IO.File]::WriteAllText((Join-Path $old 'bundle-manifest.json'),'["DesktopGlass.exe","locked.dll"]')
    [IO.File]::WriteAllText((Join-Path $old 'DesktopGlass.exe'),'owned-fixture')
    [IO.File]::WriteAllText((Join-Path $old 'locked.dll'),'owned-fixture')
    $bridge=Join-Path $target 'DesktopGlass.exe'
    $shared=@('--sandbox-data',('"'+$data+'"'))
    $locked=[IO.File]::Open((Join-Path $old 'locked.dll'),'Open','Read','None')
    try {
        $code=Run $bridge (@('--setup-complete','--previous',('"'+$old+'"'),'--report',('"'+(Join-Path $work 'migration-block.json')+'"'))+$shared)
        Check ($code -ne 0) '旧负载被占用时错误报告成功'
        Check ((Test-Path (Join-Path $target '.previous-installations.json')) -and (Test-Path (Join-Path $old 'bundle-manifest.json'))) '迁移失败丢失重试责任'
        $third=Join-Path $work '不得再次迁移'
        $code=Run $bridge (@('--setup-prepare','--target',('"'+$third+'"'),'--previous',('"'+$target+'"'),'--report',('"'+(Join-Path $work 'nested-migration-block.json')+'"'))+$shared)
        Check (($code -ne 0) -and -not(Test-Path $third)) '待清理期间更换位置没有在写入前拒绝'
    } finally {$locked.Dispose()}
    Check ((Run $bridge (@('--setup-complete','--report',('"'+(Join-Path $work 'migration-retry.json')+'"'))+$shared)) -eq 0) '未传旧路径的收尾重试失败'
    Check (-not(Test-Path $old) -and -not(Test-Path (Join-Path $target '.previous-installations.json'))) '旧目录清理未完成'
    $results.Add('旧负载占用保留责任并可重试')
    $results.Add('未完成迁移时禁止再次换目录')

    $prior=@(1..3 | ForEach-Object {Join-Path $work ('多位置-'+$_)})
    foreach($location in $prior){
        New-Item -ItemType Directory -Path $location | Out-Null
        [IO.File]::WriteAllText((Join-Path $location '.desktop-glass-owner'),$product)
        [IO.File]::WriteAllText((Join-Path $location 'bundle-manifest.json'),'["DesktopGlass.exe"]')
        [IO.File]::WriteAllText((Join-Path $location 'DesktopGlass.exe'),'owned-fixture')
    }
    [IO.File]::WriteAllText((Join-Path $prior[0] '用户文件.txt'),'keep')
    [IO.File]::WriteAllText((Join-Path $target '.previous-installations.json'),(ConvertTo-Json -InputObject $prior -Compress))
    $locked=[IO.File]::Open((Join-Path $prior[2] 'DesktopGlass.exe'),'Open','Read','None')
    try {
        $code=Run $bridge (@('--setup-complete','--report',('"'+(Join-Path $work 'multiple-migration-block.json')+'"'))+$shared)
        Check ($code -ne 0) '多位置占用没有触发失败'
        $remaining=@(Get-Content (Join-Path $target '.previous-installations.json') -Raw | ConvertFrom-Json)
        Check (($remaining.Count -eq 1) -and ($remaining[0] -eq $prior[2])) '已经清理的旧位置被重新写入责任记录'
    } finally {$locked.Dispose()}
    Check ((Run $bridge (@('--setup-complete','--report',('"'+(Join-Path $work 'multiple-migration-retry.json')+'"'))+$shared)) -eq 0) '多位置部分成功后重试失败'
    Check (([IO.File]::ReadAllText((Join-Path $prior[0] '用户文件.txt')) -eq 'keep') -and -not(Test-Path $prior[2])) '多位置清理结果不正确'
    $results.Add('多位置逐项完成后只重试剩余责任')

    New-Item -ItemType Directory -Path $data | Out-Null
    [IO.File]::WriteAllText((Join-Path $data '.desktop-glass-owner'),$product)
    [IO.File]::WriteAllText((Join-Path $data 'workspace.json'),'{"Version":2,"Groups":[]}')
    [IO.File]::WriteAllText((Join-Path $data 'recovery.json'),'owned-recovery-fixture')
    Check ((Uninstall 'uninstall-recovery-block' $target) -ne 0) '未完成恢复未阻止卸载'
    Check ((Test-Path $bridge) -and (Test-Path (Join-Path $data 'workspace.json'))) '恢复失败后删除了负载或配置'
    Remove-Item -LiteralPath (Join-Path $data 'recovery.json')
    $results.Add('恢复责任阻止卸载')

    $mutex=[Threading.Mutex]::new($false,'Local\DesktopGlass-installer'); $owned=$false
    try {
        try {$owned=$mutex.WaitOne(0)} catch [Threading.AbandonedMutexException] {$owned=$true}
        Check $owned '当前有其他真实安装操作，停止隔离互斥测试'
        Check ((Uninstall 'uninstall-mutex-block' $target) -ne 0) '并行安装未阻止卸载'
        Check ((Setup 'install-mutex-block' (Join-Path $work '不应创建')) -ne 0) '并行卸载未阻止安装'
        Check (Test-Path $bridge) '互斥拒绝后负载丢失'
    } finally {if($owned){$mutex.ReleaseMutex()};$mutex.Dispose()}
    $results.Add('安装卸载共享互斥')

    [IO.File]::WriteAllText((Join-Path $data '用户文件.txt'),'keep')
    [IO.File]::WriteAllText((Join-Path $target '用户文件.txt'),'keep')
    Check ((Uninstall 'uninstall-success' $target) -eq 0) '正常卸载失败'
    Check (-not(Test-Path $bridge) -and -not(Test-Path (Join-Path $target '.maintenance')) -and -not(Test-Path (Join-Path $data 'workspace.json'))) '正常卸载残留程序或配置'
    Check (([IO.File]::ReadAllText((Join-Path $data '用户文件.txt')) -eq 'keep') -and ([IO.File]::ReadAllText((Join-Path $target '用户文件.txt')) -eq 'keep')) '卸载删除了未知文件'
    $results.Add('卸载清理自有文件并保留未知内容')

    Check ((Setup 'default-install' '') -eq 0) '默认目录安装失败'
    Check ((Uninstall 'default-uninstall' ($data+'-install')) -eq 0) '默认目录卸载失败'
    # Inno 的启动器可先于临时卸载子进程退出；等待实际目录清理，而非假定启动器退出即完成。
    $wait=[Diagnostics.Stopwatch]::StartNew()
    while((Test-Path ($data+'-install')) -and $wait.Elapsed.TotalSeconds -lt 8){Start-Sleep -Milliseconds 100}
    Check (-not(Test-Path ($data+'-install'))) '无未知内容的默认安装目录有残留'
    $results.Add('默认目录安装和完整卸载')
    @{Passed=$true;Checks=$results.ToArray();Work=$work}|ConvertTo-Json -Depth 4 | Tee-Object -FilePath (Join-Path $work 'result.json')
} catch {
    @{Passed=$false;Checks=$results.ToArray();Error=$_.ToString();Work=$work}|ConvertTo-Json -Depth 4 | Tee-Object -FilePath (Join-Path $work 'result.json')
    exit 1
}
