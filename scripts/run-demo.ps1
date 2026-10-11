#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateRange(0, 65535)][int]$GatewayPort = 0,
    [ValidateRange(0, 65535)][int]$DemoServerPort = 0,
    [string]$PolicyPath,
    [switch]$ShowAudit,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$owned = [System.Collections.Generic.List[object]]::new()
$exitCode = 0

function Get-DemoPort([int]$Requested) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Requested)
    try {
        $listener.Start()
        return $listener.LocalEndpoint.Port
    }
    catch { throw 'A requested loopback port is unavailable. Choose another port; no existing process will be stopped.' }
    finally { $listener.Stop() }
}

function Start-OwnedProcess([string]$Name, [string[]]$ProcessArguments) {
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = (Get-Command dotnet -ErrorAction Stop).Source
    $info.WorkingDirectory = $repoRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $ProcessArguments) { $info.ArgumentList.Add($argument) }
    $child = [System.Diagnostics.Process]::new()
    $child.StartInfo = $info
    $null = $child.Start()
    $record = @{ Name = $Name; Process = $child; Stderr = $child.StandardError.ReadToEndAsync(); Stdout = $null; Prefix = '' }
    $owned.Add($record)
    return $record
}

function Wait-Ready($Child) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 30) {
        $line = $Child.Process.StandardOutput.ReadLineAsync()
        $remaining = [int][Math]::Max(1, 30000 - $watch.Elapsed.TotalMilliseconds)
        if (-not $line.Wait($remaining)) { throw "$($Child.Name) readiness timed out." }
        $value = $line.GetAwaiter().GetResult()
        if ($null -eq $value) { throw "$($Child.Name) failed before readiness. Check ports and policy configuration." }
        if ($value -eq 'MCP_SENTINEL_READY') {
            $Child.Stdout = $Child.Process.StandardOutput.ReadToEndAsync()
            return
        }
        $Child.Prefix += $value + "`n"
    }
    throw "$($Child.Name) readiness timed out."
}

function Stop-OwnedProcesses {
    # Reverse dependency order. Signal graceful shutdown over our private stdin channel.
    $cleanupFailed = $false
    for ($index = $owned.Count - 1; $index -ge 0; $index--) {
        $record = $owned[$index]
        $child = $record.Process
        try {
            if (-not $child.HasExited) {
                try { $child.StandardInput.WriteLine('stop'); $child.StandardInput.Flush() } catch { }
                if (-not $child.WaitForExit(10000)) {
                    $child.Kill($true)
                    if (-not $child.WaitForExit(10000)) { throw 'Owned child did not exit.' }
                }
            }
        }
        catch { $cleanupFailed = $true }
    }
    if ($cleanupFailed) { throw 'One or more owned processes could not be stopped.' }
}

function Read-Events($Child) {
    $output = $Child.Prefix
    if ($null -ne $Child.Stdout) { $output += $Child.Stdout.GetAwaiter().GetResult() }
    foreach ($line in ($output -split "`n")) {
        if ($line.TrimStart().StartsWith('{')) {
            try { ConvertFrom-Json -InputObject $line -AsHashtable } catch { }
        }
    }
}

try {
    if (-not $SkipBuild) {
        & dotnet build (Join-Path $repoRoot 'McpSentinel.slnx') --configuration Release
        if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    }
    $GatewayPort = Get-DemoPort $GatewayPort
    $DemoServerPort = Get-DemoPort $DemoServerPort
    if ($GatewayPort -eq $DemoServerPort) { throw 'Gateway and DemoServer need distinct ports. Retry or select explicit ports.' }
    if ([string]::IsNullOrWhiteSpace($PolicyPath)) { $PolicyPath = Join-Path $repoRoot 'policies/demo-policy.json' }
    $gatewayUrl = "http://127.0.0.1:$GatewayPort"
    $demoUrl = "http://127.0.0.1:$DemoServerPort"
    $demoDll = Join-Path $repoRoot 'samples/McpSentinel.DemoServer/bin/Release/net10.0/McpSentinel.DemoServer.dll'
    $gatewayDll = Join-Path $repoRoot 'src/McpSentinel.Gateway/bin/Release/net10.0/McpSentinel.Gateway.dll'
    $clientDll = Join-Path $repoRoot 'samples/McpSentinel.DemoClient/bin/Release/net10.0/McpSentinel.DemoClient.dll'
    foreach ($dll in @($demoDll, $gatewayDll, $clientDll)) { if (-not (Test-Path -LiteralPath $dll)) { throw 'Build outputs missing; run without -SkipBuild.' } }
    $demo = Start-OwnedProcess 'DemoServer' @($demoDll, '--contentRoot', (Split-Path -Parent $demoDll), '--urls', $demoUrl, '--Demo:Controlled', 'true')
    Wait-Ready $demo
    $gateway = Start-OwnedProcess 'Gateway' @($gatewayDll, '--contentRoot', (Split-Path -Parent $gatewayDll), '--urls', $gatewayUrl, '--Demo:Controlled', 'true',
        '--Gateway:UpstreamUrl', "$demoUrl/mcp", '--Gateway:PolicyPath', $PolicyPath)
    Wait-Ready $gateway
    Write-Output "Gateway: $gatewayUrl/mcp"
    $client = Start-OwnedProcess 'DemoClient' @($clientDll, "$gatewayUrl/mcp")
    $client.Stdout = $client.Process.StandardOutput.ReadToEndAsync()
    if (-not $client.Process.WaitForExit(45000)) { throw 'DemoClient timed out.' }
    Write-Output $client.Stdout.GetAwaiter().GetResult().TrimEnd()
    if ($client.Process.ExitCode -ne 0) { throw 'DemoClient scenario failed.' }
    Stop-OwnedProcesses
    if ($demo.Process.ExitCode -ne 0 -or $gateway.Process.ExitCode -ne 0) { throw 'Infrastructure did not shut down successfully.' }
    $audit = @(Read-Events $gateway | Where-Object { $_.Category -eq 'McpSentinel.Gateway.SecurityAudit' })
    $terminal = @($audit | Where-Object { $_.EventId -eq 1001 })
    if ($audit.Count -ne 6 -or $terminal.Count -ne 3) { throw 'Expected one policy and one terminal audit event per invocation.' }
    $expected = @{
        'customer.read' = @('Allow', 'Forwarded', 'Succeeded')
        'customer.delete' = @('Deny', 'Blocked', 'NotAttempted')
        'invoice.refund' = @('RequireApproval', 'Blocked', 'NotAttempted')
    }
    foreach ($tool in $expected.Keys) {
        $match = @($terminal | Where-Object { $_.State.ToolName -eq $tool })
        if ($match.Count -ne 1 -or $match[0].State.PolicyDecision -ne $expected[$tool][0] -or
            $match[0].State.EnforcementOutcome -ne $expected[$tool][1] -or $match[0].State.UpstreamStatus -ne $expected[$tool][2]) {
            throw 'Unexpected enforcement audit outcome.'
        }
    }
    $counts = @(Read-Events $demo | Where-Object { $_.EventId -eq 2000 })
    if ($counts.Count -ne 1 -or $counts[0].State.ToolName -ne 'customer.read' -or $counts[0].State.InvocationCount -ne 1) {
        throw 'Mock upstream invocation counters did not match the expected scenario.'
    }
    $auditJson = ConvertTo-Json -InputObject $audit -Depth 20
    foreach ($sensitive in @('customer-1', 'invoice-1', 'Ada Example', 'Customer not found')) {
        if ($auditJson.Contains($sensitive)) { throw 'Sensitive demo payload found in audit output.' }
    }
    Write-Output 'Upstream counters: customer.read=1; customer.delete=0; invoice.refund=0'
    Write-Output 'Audit verified: 3 policy decisions, 3 terminal outcomes; no demo arguments or response content.'
    if ($ShowAudit) {
        foreach ($event in $terminal) { Write-Output (ConvertTo-Json -InputObject $event.State -Depth 10 -Compress) }
    }
    Write-Output 'Demo passed (mock scenario counts, not an exactly-once guarantee).'
}
catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    $exitCode = 1
}
finally {
    try {
        Stop-OwnedProcesses
        if ($owned.Count -gt 0) { Write-Output "Cleanup verified: $($owned.Count) owned child processes exited." }
    }
    catch { Write-Error 'Owned process cleanup failed.' -ErrorAction Continue; $exitCode = 1 }
    foreach ($record in $owned) { $record.Process.Dispose() }
}
exit $exitCode
