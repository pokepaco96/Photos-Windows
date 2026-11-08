<#
.SYNOPSIS
  Start-up smoke check: launches each given PhotosWindows.exe with an isolated profile, waits, and records whether it
  stays alive, its log, the files that matter for XAML/resources, and Windows Application event-log errors.
  Diagnostic only; it never touches real user data.
.EXAMPLE
  ./startup-smoke.ps1 -Exe publish\PhotosWindows.exe,bin\PhotosWindows.exe -Output artifacts\qa\run\logs
#>
param(
    [Parameter(Mandatory)] [string[]] $Exe,
    [Parameter(Mandatory)] [string] $Output,
    [int] $Seconds = 10
)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$report = Join-Path $Output 'startup-smoke.txt'
"Startup smoke $(Get-Date -Format o)" | Set-Content $report
$start = Get-Date
$i = 0
foreach ($path in $Exe) {
    $i++
    "`n=== [$i] $path" | Add-Content $report
    if (-not (Test-Path $path)) { "  MISSING" | Add-Content $report; continue }
    $dir = Split-Path $path
    "  files: " + ((Get-ChildItem $dir -File | Where-Object { $_.Extension -in '.pri', '.xbf', '.winmd' -or $_.Name -like 'Microsoft.UI.Xaml*' -or $_.Name -like 'PhotosWindows*' } |
        ForEach-Object { "$($_.Name) ($($_.Length))" }) -join ', ') | Add-Content $report
    "  xbf files (recursive): " + ((Get-ChildItem $dir -Recurse -Filter *.xbf | ForEach-Object { $_.FullName.Substring($dir.Length + 1) }) -join ', ') | Add-Content $report
    $tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
    $profile = Join-Path $tempRoot "smoke-profile-$i"
    New-Item -ItemType Directory -Force -Path $profile | Out-Null
    $psi = New-Object System.Diagnostics.ProcessStartInfo $path
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $dir
    $psi.Environment['PHOTOSWINDOWS_DATA_DIR'] = $profile
    $p = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Seconds $Seconds
    if ($p.HasExited) {
        "  RESULT: exited after start (exit code $($p.ExitCode))" | Add-Content $report
    } else {
        $p.Refresh()
        "  RESULT: alive after $Seconds s; main window title: '$($p.MainWindowTitle)'" | Add-Content $report
        $p.Kill()
    }
    Get-ChildItem (Join-Path $profile 'logs') -ErrorAction SilentlyContinue | ForEach-Object { "  --- $($_.Name)"; Get-Content $_.FullName } | Add-Content $report
}
"`n=== Application event log (errors since start)" | Add-Content $report
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $start } -ErrorAction SilentlyContinue |
    Where-Object { $_.LevelDisplayName -in 'Error', 'Critical' -or $_.ProviderName -in 'Application Error', '.NET Runtime', 'Windows Error Reporting' } |
    ForEach-Object { "--- $($_.TimeCreated) $($_.ProviderName) $($_.Id)`n$($_.Message)" } | Add-Content $report
Get-Content $report
