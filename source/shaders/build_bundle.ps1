# Builds tools/gpu_shadow/out/ff5shadow.bundle with the Unity 2019.4.26f1 editor (the game's version) in batch mode,
# then prints the bundle size and the log lines that matter. Usage: powershell -File build_bundle.ps1
param([string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.26f1\Editor\Unity.exe')   # path to Unity.exe 2019.4.26f1
$ErrorActionPreference = 'Stop'
$unity = $Unity
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here 'UnityProject'
$out = Join-Path $here 'out'
$log = Join-Path $here 'build_bundle.log'
# Not Start-Process -Wait: that also waits for Unity's licensing helper, which keeps running (hung 2026-11-13).
$p = Start-Process -FilePath $unity -PassThru -ArgumentList @(
    '-batchmode', '-nographics', '-quit', '-projectPath', "`"$proj`"", '-buildTarget', 'Win64',
    '-executeMethod', 'BuildBundles.Build', '-out', "`"$out`"", '-logFile', "`"$log`"")
$p.WaitForExit()
"exit code: $($p.ExitCode)"
Select-String -Path $log -Pattern 'BuildBundles|error|Shader error|Compiling shader|Exiting batchmode' |
    Select-Object -First 30 | ForEach-Object { $_.Line }
Get-ChildItem $out -Filter 'ff5shadow.bundle*' | ForEach-Object { "{0}  {1} bytes  {2}" -f $_.Name, $_.Length, $_.LastWriteTime }
