<#
.SYNOPSIS
  Collects a local, sanitized performance report for Photos Windows. Nothing is sent anywhere.

.DESCRIPTION
  Run it while Photos Windows is open and the problem is happening (for example, the viewer says
  "This is taking longer than usual"). For 60 seconds it samples the app's CPU, memory, handles, threads and disk
  I/O, then collects Windows/CPU/RAM, the HEIF/HEVC codec packages and the app's recent viewer and thumbnail log
  lines, with paths and file names removed. You see the result before anything is written to the ZIP.

  The report contains no photos, thumbnails, paths, file names, GPS data, people names or face data.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Collect-PhotosWindowsPerformance.ps1
#>
param(
    [int]$Seconds = 60,
    [string]$OutFolder = [Environment]::GetFolderPath('Desktop')
)

$ErrorActionPreference = 'Stop'
$proc = Get-Process -Name PhotosWindows -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host 'Photos Windows is not running. Open it, reproduce the problem and run this script again.' -ForegroundColor Yellow; exit 1 }

# Removes anything that looks like a path or a file name.
function Remove-Private([string]$text) {
    $text = [regex]::Replace($text, '(?i)[a-z]:\\[^\s"''<>|]*', '<path>')
    $text = [regex]::Replace($text, '\\\\[^\s"''<>|]+', '<path>')
    $text = [regex]::Replace($text, '(?i)\b[\w\-. ()]+\.(heic|heif|jpe?g|png|tiff?|dng|cr2|cr3|nef|arw|mov|mp4|avif|xmp|webp|gif|bmp)\b', '<file>')
    return $text
}

Write-Host "Sampling Photos Windows for $Seconds s (do not close it)..."
$samples = @()
$cores = [Environment]::ProcessorCount
$prev = $proc.TotalProcessorTime; $prevT = Get-Date
$io0 = Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.Id)" | Select-Object ReadTransferCount, WriteTransferCount
for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep -Seconds 1
    $proc.Refresh()
    $now = Get-Date
    $cpu = ($proc.TotalProcessorTime - $prev).TotalMilliseconds / ($now - $prevT).TotalMilliseconds / $cores * 100
    $prev = $proc.TotalProcessorTime; $prevT = $now
    $samples += [pscustomobject]@{
        t = $i + 1; cpuPercent = [math]::Round($cpu, 1)
        workingSetMiB = [math]::Round($proc.WorkingSet64 / 1MB); privateMiB = [math]::Round($proc.PrivateMemorySize64 / 1MB)
        handles = $proc.HandleCount; threads = $proc.Threads.Count
    }
}
$io1 = Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.Id)" | Select-Object ReadTransferCount, WriteTransferCount
$totalCpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average

$os = Get-CimInstance Win32_OperatingSystem
$cpuInfo = Get-CimInstance Win32_Processor | Select-Object -First 1
$disks = Get-PhysicalDisk -ErrorAction SilentlyContinue | ForEach-Object { "$($_.MediaType) $($_.BusType) $([math]::Round($_.Size / 1GB)) GB" }
# HEIC needs both: HEIF Image Extensions (container) and an HEVC decoder (the image data).
$codecs = foreach ($n in '*HEIF*', '*HEVC*', '*RawImage*', '*AV1*') { Get-AppxPackage -Name $n -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name) $($_.Version)" } }
if (-not ($codecs -match 'HEVC')) { $codecs += 'HEVC decoder: NOT INSTALLED (HEIC photos cannot be decoded)' }
$exe = $proc.Path
$appVersion = if ($exe) { (Get-Item $exe).VersionInfo.ProductVersion } else { 'unknown' }

$logDir = Join-Path $env:LOCALAPPDATA 'PhotosWindows\logs'
$logLines = @()
if (Test-Path $logDir) {
    $latest = Get-ChildItem $logDir -Filter 'photoswindows-*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 2
    foreach ($f in $latest) {
        $logLines += Get-Content $f.FullName -Tail 4000 | Where-Object {
            $_ -match 'Viewer load|Thumbnail|thumbnail|Perceptual hash|Background|still running|decod|OutOfMemory'
        } | ForEach-Object { Remove-Private $_ }
    }
    $logLines = $logLines | Select-Object -Last 400
}

$report = [ordered]@{
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    app = [ordered]@{ version = $appVersion }
    windows = "$($os.Caption) $($os.Version) build $($os.BuildNumber)"
    cpu = "$($cpuInfo.Name.Trim()) ($($cpuInfo.NumberOfCores) cores / $cores threads)"
    ramGiB = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
    disks = $disks
    codecs = $codecs
    sampleSeconds = $Seconds
    cpuPercentAvg = [math]::Round(($samples | Measure-Object cpuPercent -Average).Average, 1)
    cpuPercentMax = ($samples | Measure-Object cpuPercent -Maximum).Maximum
    systemCpuPercentNow = $totalCpu
    workingSetMiBMax = ($samples | Measure-Object workingSetMiB -Maximum).Maximum
    privateMiBStart = $samples[0].privateMiB
    privateMiBEnd = $samples[-1].privateMiB
    handlesStartEnd = "$($samples[0].handles) -> $($samples[-1].handles)"
    threadsStartEnd = "$($samples[0].threads) -> $($samples[-1].threads)"
    diskReadMiB = [math]::Round(($io1.ReadTransferCount - $io0.ReadTransferCount) / 1MB, 1)
    diskWriteMiB = [math]::Round(($io1.WriteTransferCount - $io0.WriteTransferCount) / 1MB, 1)
    samples = $samples
    logLines = $logLines
}

$json = $report | ConvertTo-Json -Depth 4
Write-Host ''
Write-Host '---- Summary (review before saving) ----' -ForegroundColor Cyan
$report.GetEnumerator() | Where-Object { $_.Key -notin 'samples', 'logLines' } | ForEach-Object { Write-Host ("{0,-22} {1}" -f $_.Key, ($_.Value -join '; ')) }
Write-Host ''
Write-Host "Last log lines included ($($logLines.Count)), paths and file names replaced:" -ForegroundColor Cyan
$logLines | Select-Object -Last 15 | ForEach-Object { Write-Host "  $_" }
Write-Host ''
$answer = Read-Host 'Save this report to a ZIP on your Desktop? (y/n)'
if ($answer -notmatch '^(y|s)') { Write-Host 'Nothing saved.'; exit 0 }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$tmp = Join-Path $env:TEMP "pw-report-$stamp"
New-Item -ItemType Directory -Path $tmp | Out-Null
$json | Out-File -Encoding utf8 (Join-Path $tmp 'performance-report.json')
$zip = Join-Path $OutFolder "PhotosWindows-performance-$stamp.zip"
Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $zip
Remove-Item $tmp -Recurse -Force
Write-Host "Saved: $zip  (open it and check it before sharing)" -ForegroundColor Green
