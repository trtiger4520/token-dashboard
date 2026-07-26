param(
    [string]$PublishDirectory = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $repositoryRoot 'artifacts/publish/win-x64'
}

$executable = Join-Path $PublishDirectory 'TokenDashboard.Api.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Published executable was not found: $executable"
}

$databasePath = Join-Path ([IO.Path]::GetTempPath()) ('token-dashboard-smoke-' + [Guid]::NewGuid().ToString('N') + '.db')
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $executable
$startInfo.WorkingDirectory = $PublishDirectory
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $false
$startInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'Development'
$startInfo.Environment['TokenDashboard__OpenBrowser'] = 'false'
$startInfo.Environment['TokenDashboard__EmitStartupDiagnostics'] = 'true'
$startInfo.Environment['TokenDashboard__ConnectionString'] = "Data Source=$databasePath"

$process = [Diagnostics.Process]::new()
$process.StartInfo = $startInfo
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$http = [Net.Http.HttpClient]::new($handler)
$outputTask = $null
$baseUrl = $null
$key = $null

try {
    if (-not $process.Start()) { throw 'Published process did not start' }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    $outputTask = $process.StandardOutput.ReadLineAsync()
    while ([DateTimeOffset]::UtcNow -lt $deadline -and $null -eq $baseUrl) {
        if ($outputTask.IsCompleted) {
            $line = $outputTask.GetAwaiter().GetResult()
            if ($null -ne $line -and $line.StartsWith('TOKEN_DASHBOARD_STARTUP_URL=', [StringComparison]::Ordinal)) {
                $startupUrl = $line.Substring('TOKEN_DASHBOARD_STARTUP_URL='.Length)
                $fragmentIndex = $startupUrl.IndexOf('#key=', [StringComparison]::Ordinal)
                if ($fragmentIndex -lt 0) { throw 'Startup diagnostics did not contain a URL fragment key' }
                $baseUrl = $startupUrl.Substring(0, $fragmentIndex).TrimEnd('/')
                $key = [Uri]::UnescapeDataString($startupUrl.Substring($fragmentIndex + '#key='.Length))
                break
            }

            $outputTask = $process.StandardOutput.ReadLineAsync()
        }
        else {
            Start-Sleep -Milliseconds 100
        }
    }

    if ($null -eq $baseUrl -or [string]::IsNullOrWhiteSpace($key)) { throw 'Timed out waiting for startup diagnostics' }
    $root = $http.GetAsync("$baseUrl/").GetAwaiter().GetResult()
    if ([int]$root.StatusCode -ne 200) { throw "SPA root returned $([int]$root.StatusCode)" }
    $health = $http.GetAsync("$baseUrl/health").GetAwaiter().GetResult()
    if ([int]$health.StatusCode -ne 200) { throw "Health returned $([int]$health.StatusCode)" }
    $unauthorized = $http.GetAsync("$baseUrl/api/overview").GetAwaiter().GetResult()
    if ([int]$unauthorized.StatusCode -ne 401) { throw "Unauthenticated API returned $([int]$unauthorized.StatusCode)" }

    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, "$baseUrl/api/overview")
    $request.Headers.Add('X-Token-Dashboard-Key', $key)
    $authorized = $http.SendAsync($request).GetAwaiter().GetResult()
    if ([int]$authorized.StatusCode -ne 200) { throw "Authenticated API returned $([int]$authorized.StatusCode)" }

    Write-Output "Smoke passed: $baseUrl"
}
finally {
    $http.Dispose()
    $handler.Dispose()
    if ($process -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    $process.Dispose()
    if (Test-Path -LiteralPath $databasePath) { Remove-Item -LiteralPath $databasePath -Force }
    foreach ($suffix in @('-shm', '-wal')) {
        $sidecar = $databasePath + $suffix
        if (Test-Path -LiteralPath $sidecar) { Remove-Item -LiteralPath $sidecar -Force }
    }
}
