<#
.SYNOPSIS
    Stops the API and worker if they are running on this machine.

.DESCRIPTION
    A locally-run service keeps a handle on its own executable, so the next `dotnet build` fails with
    MSB3021 ("the process cannot access the file ... OrderProcessing.Api.exe"). The error names a
    locked file rather than a running service, which sends people looking in the wrong place.

    Note that `pkill -f OrderProcessing.Api` from a Git Bash prompt does NOT stop these - it does not
    match Windows processes the way it appears to - so the kill looks like it worked and the build
    fails anyway.
#>
[CmdletBinding()]
param()

$processes = Get-Process -Name 'OrderProcessing.*' -ErrorAction SilentlyContinue

if (-not $processes) {
    Write-Host 'Nothing running.'
    return
}

foreach ($process in $processes) {
    Write-Host "  stopping $($process.ProcessName) (pid $($process.Id))"
    Stop-Process -Id $process.Id -Force
}

Start-Sleep -Milliseconds 500
$remaining = @(Get-Process -Name 'OrderProcessing.*' -ErrorAction SilentlyContinue).Count
if ($remaining -gt 0) { throw "$remaining process(es) would not stop." }
Write-Host 'Stopped.'
