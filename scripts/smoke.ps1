<#
.SYNOPSIS
    Places an order, waits for the worker to process it, and downloads the receipt.

.DESCRIPTION
    The end-to-end check in one command: HTTP in, PDF out, across two processes and a broker. If this
    passes, the whole pipeline is working - the outbox published, the worker consumed, the receipt
    was rendered and stored, and the API can serve it back.

    Requires the API and the worker to be running, and the compose stack to be up.
#>
[CmdletBinding()]
param(
    [string]$ApiUrl = 'http://127.0.0.1:8080',
    [int]$TimeoutSeconds = 30,
    [string]$OutputPath = (Join-Path ([IO.Path]::GetTempPath()) 'orderprocessing-receipt.pdf')
)

$ErrorActionPreference = 'Stop'

$body = @{
    customerEmail = 'buyer@example.com'
    lines = @(
        @{ sku = 'SKU-1'; description = 'Blue widget'; quantity = 3; unitPrice = 13.99 }
        @{ sku = 'SKU-2'; description = 'Red widget';  quantity = 1; unitPrice = 5.00  }
    )
} | ConvertTo-Json -Depth 5

Write-Host 'Placing an order...'
$accepted = Invoke-RestMethod -Uri "$ApiUrl/api/orders" -Method Post -Body $body -ContentType 'application/json'
$orderId = $accepted.orderId
Write-Host "  accepted as $orderId (status $($accepted.status))"

Write-Host 'Waiting for the worker...'
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$status = $null

while ((Get-Date) -lt $deadline) {
    $order = Invoke-RestMethod -Uri "$ApiUrl/api/orders/$orderId"
    $status = $order.status

    if ($status -eq 'Completed') { break }
    if ($status -eq 'Failed') { throw "Order $orderId failed: $($order.failureReason)" }

    Start-Sleep -Milliseconds 500
}

if ($status -ne 'Completed') {
    throw "Order $orderId was still '$status' after $TimeoutSeconds seconds. Is the worker running?"
}

Write-Host "  completed"

Write-Host 'Downloading the receipt...'
Invoke-WebRequest -Uri "$ApiUrl/api/orders/$orderId/receipt" -OutFile $OutputPath

# A PDF starts with %PDF. Checking the magic bytes rather than merely the status code, because a
# proxy or an error page returning 200 with HTML would otherwise pass silently.
$magic = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($OutputPath)[0..3])
if ($magic -ne '%PDF') { throw "Downloaded file is not a PDF (starts with '$magic')." }

$size = (Get-Item $OutputPath).Length
Write-Host "  $OutputPath  ($size bytes, $magic)"
Write-Host ''
Write-Host 'Smoke test passed: order -> queue -> worker -> receipt.'
