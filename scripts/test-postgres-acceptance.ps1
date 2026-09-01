[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$containerName = "prisstyrning-pg-acceptance-$suffix"
$databaseName = "prisstyrning_acceptance_$suffix"
$started = $false

function Assert-DockerReady {
    $dockerPath = (Get-Command docker -CommandType Application -ErrorAction Stop |
            Select-Object -First 1).Source
    $process = Start-Process `
        -FilePath $dockerPath `
        -ArgumentList @('version', '--format', '{{.Server.Version}}') `
        -WindowStyle Hidden `
        -PassThru
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

    docker run --detach --rm `
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
        $health = docker inspect --format '{{.State.Health.Status}}' $containerName
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

    $portMapping = docker port $containerName 5432/tcp
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
        docker stop --time 10 $containerName | Out-Null
    }
}
