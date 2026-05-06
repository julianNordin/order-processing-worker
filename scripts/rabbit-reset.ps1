<#
.SYNOPSIS
    Deletes every exchange and queue this project declares, so the next startup recreates them.

.DESCRIPTION
    You will need this. Declaring a queue that already exists with DIFFERENT arguments fails with
    PRECONDITION_FAILED and takes the channel down with it, and changing a retry TTL is exactly that
    change. There is no "declare or alter" in AMQP - the queue has to go first.

    Note that `docker compose down` WITHOUT -v keeps the broker's volume, so the old topology comes
    straight back and the error repeats. This script is the narrower fix; `docker compose down -v` is
    the wider one.

.PARAMETER PurgeOnly
    Empty the queues but leave them declared. Use this between manual experiments, when the shape is
    fine and only the contents are in the way.
#>
[CmdletBinding()]
param(
    [switch]$PurgeOnly,
    [string]$ManagementUrl = 'http://localhost:15672',
    [string]$UserName = $env:RABBITMQ_USER,
    [string]$Password = $env:RABBITMQ_PASSWORD
)

$ErrorActionPreference = 'Stop'

if (-not $UserName -or -not $Password) {
    # Fall back to .env, which is what a developer actually has sitting there.
    $envFile = Join-Path $PSScriptRoot '..\.env'
    if (Test-Path $envFile) {
        Get-Content $envFile | ForEach-Object {
            if ($_ -match '^\s*RABBITMQ_USER\s*=\s*(.+)$')     { $UserName = $Matches[1].Trim() }
            if ($_ -match '^\s*RABBITMQ_PASSWORD\s*=\s*(.+)$') { $Password = $Matches[1].Trim() }
        }
    }
}
if (-not $UserName -or -not $Password) {
    throw "No broker credentials. Copy .env.example to .env, or pass -UserName and -Password."
}

$pair    = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${UserName}:${Password}"))
$headers = @{ Authorization = "Basic $pair" }
$vhost   = [Uri]::EscapeDataString('/')

$queues    = @('orders.placed', 'orders.retry.5s', 'orders.retry.30s', 'orders.retry.2m', 'orders.dlq')
$exchanges = @('orders', 'orders.retry', 'orders.dlx')

foreach ($queue in $queues) {
    $verb = if ($PurgeOnly) { 'Purging' } else { 'Deleting' }
    $url  = if ($PurgeOnly) { "$ManagementUrl/api/queues/$vhost/$queue/contents" }
            else            { "$ManagementUrl/api/queues/$vhost/$queue" }
    try {
        Invoke-RestMethod -Uri $url -Method Delete -Headers $headers | Out-Null
        Write-Host "  $verb queue $queue"
    } catch {
        # A queue that is not there is the state we wanted anyway.
        Write-Host "  $queue not present"
    }
}

if (-not $PurgeOnly) {
    foreach ($exchange in $exchanges) {
        try {
            Invoke-RestMethod -Uri "$ManagementUrl/api/exchanges/$vhost/$exchange" -Method Delete -Headers $headers | Out-Null
            Write-Host "  Deleting exchange $exchange"
        } catch {
            Write-Host "  $exchange not present"
        }
    }
}

Write-Host ''
Write-Host 'Done. The topology is redeclared the next time either service starts.'
