[CmdletBinding()]
param(
    [string] $DockerEndpoint = 'npipe:////./pipe/dockerDesktopLinuxEngine',
    [switch] $SafetyCheckOnly
)

$ErrorActionPreference = 'Stop'
$allowedEndpoints = @(
    'npipe:////./pipe/dockerDesktopLinuxEngine',
    'npipe:////./pipe/docker_engine',
    'unix:///var/run/docker.sock',
    'unix:///run/docker.sock'
)
if ($allowedEndpoints -cnotcontains $DockerEndpoint) {
    throw 'PostgreSQL acceptance requires an explicitly allowed local Docker socket. TCP, SSH, remote pipes and custom endpoints are not permitted.'
}
if (-not [string]::IsNullOrEmpty($env:DOCKER_CONTEXT) -or
    -not [string]::IsNullOrEmpty($env:DOCKER_HOST)) {
    throw 'Remove DOCKER_CONTEXT and DOCKER_HOST overrides from this test process before running PostgreSQL acceptance. Their values are not logged.'
}

# Pin every operation, including cleanup, to the checked socket. Never depend
# on a mutable selected context or inherit an environment-selected remote host.
$dockerArguments = @('--host', $DockerEndpoint)
if ($SafetyCheckOnly) {
    [pscustomobject]@{
        Endpoint = $DockerEndpoint
        DockerArguments = $dockerArguments
        EngineContacted = $false
    }
    return
}

$dockerPath = (Get-Command docker -CommandType Application -ErrorAction Stop |
        Select-Object -First 1).Source
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$containerName = "prisstyrning-pg-acceptance-$suffix"
$databaseName = "prisstyrning_acceptance_$suffix"
$started = $false

function Assert-DockerReady {
    $startParameters = @{
        FilePath = $dockerPath
        ArgumentList = $dockerArguments + @('version', '--format', '{{.Server.Version}}')
        PassThru = $true
    }
    if ($PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows) {
        $startParameters.WindowStyle = 'Hidden'
    }
    $process = Start-Process @startParameters
    try {
        if (-not $process.WaitForExit(15000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Docker engine did not respond within 15 seconds.'
        }
        if ($process.ExitCode -ne 0) {
            throw 'Docker engine is not available.'
        }
    }
    finally {
        $process.Dispose()
    }
}

try {
    Assert-DockerReady

    & $dockerPath @dockerArguments run --detach --rm `
        --name $containerName `
        --publish '127.0.0.1::5432' `
        --env POSTGRES_HOST_AUTH_METHOD=trust `
        --env POSTGRES_DB=$databaseName `
        --health-cmd "pg_isready -U postgres -d $databaseName" `
        --health-interval 1s `
        --health-timeout 3s `
        --health-retries 30 `
        postgres:17-alpine | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'The isolated PostgreSQL container could not start.'
    }
    $started = $true

    $deadline = (Get-Date).AddSeconds(45)
    do {
        $health = & $dockerPath @dockerArguments inspect --format '{{.State.Health.Status}}' $containerName
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not read the isolated PostgreSQL container health.'
        }
        if ($health -eq 'healthy') {
            break
        }
        if ($health -eq 'unhealthy' -or (Get-Date) -ge $deadline) {
            throw "The isolated PostgreSQL container did not become ready (status: $health)."
        }
        Start-Sleep -Milliseconds 500
    } while ($true)

    $portMapping = & $dockerPath @dockerArguments port $containerName 5432/tcp
    if ($LASTEXITCODE -ne 0 -or $portMapping -notmatch ':(\d+)$') {
        throw 'Could not read the random local PostgreSQL port.'
    }
    $port = $Matches[1]
    $env:PRISSTYRNING_TEST_POSTGRES =
        "Host=127.0.0.1;Port=$port;Database=$databaseName;Username=postgres;Pooling=false"

    dotnet test Prisstyrning.Tests\Prisstyrning.Tests.csproj `
        --configuration Release `
        --filter 'Category=PostgreSqlAcceptance' `
        --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) {
        throw 'PostgreSQL acceptance failed.'
    }
}
finally {
    Remove-Item Env:\PRISSTYRNING_TEST_POSTGRES -ErrorAction SilentlyContinue
    if ($started) {
        & $dockerPath @dockerArguments stop --time 10 $containerName | Out-Null
    }
}
