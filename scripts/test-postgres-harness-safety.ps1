[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$harnessPath = Join-Path $PSScriptRoot 'test-postgres-acceptance.ps1'
$checks = 0
$savedDockerEnvironment = @{}
foreach ($name in @('DOCKER_CONTEXT', 'DOCKER_HOST')) {
    $savedDockerEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Assert-Check([bool] $condition, [string] $description) {
    if (-not $condition) { throw "Harness safety check failed: $description" }
    $script:checks++
}

function Assert-Rejected([string] $endpoint, [string] $description) {
    $rejected = $false
    try {
        & $harnessPath -DockerEndpoint $endpoint -SafetyCheckOnly | Out-Null
    }
    catch {
        $rejected = $true
        Assert-Check (-not $_.Exception.Message.Contains('synthetic-private-value')) 'rejection must not echo an endpoint or environment value'
    }
    Assert-Check $rejected $description
}

try {
    # Only this test process is changed; inherited values are restored in finally.
    [Environment]::SetEnvironmentVariable('DOCKER_CONTEXT', $null, 'Process')
    [Environment]::SetEnvironmentVariable('DOCKER_HOST', $null, 'Process')

    foreach ($endpoint in @(
        'npipe:////./pipe/dockerDesktopLinuxEngine',
        'npipe:////./pipe/docker_engine',
        'unix:///var/run/docker.sock',
        'unix:///run/docker.sock'
    )) {
        $result = & $harnessPath -DockerEndpoint $endpoint -SafetyCheckOnly
        Assert-Check ($result.Endpoint -ceq $endpoint) 'an allowed socket must remain exact'
        Assert-Check ($result.DockerArguments.Count -eq 2 -and
            $result.DockerArguments[0] -ceq '--host' -and
            $result.DockerArguments[1] -ceq $endpoint) 'every operation must use an explicit socket argument'
        Assert-Check ($result.EngineContacted -eq $false) 'preflight must not contact a Docker engine'
    }

    foreach ($endpoint in @(
        '', ' ', 'tcp://127.0.0.1:2375', 'tcp://203.0.113.20:2376',
        'ssh://synthetic-private-value@example.invalid',
        'npipe:////server/pipe/docker_engine', 'npipe:////./pipe/other_engine',
        'unix:///tmp/forwarded-docker.sock', 'http://127.0.0.1:2375',
        ' npipe:////./pipe/docker_engine', 'unix:///var/run/docker.sock ',
        'UNIX:///var/run/docker.sock', '--context=production'
    )) {
        Assert-Rejected $endpoint 'unlisted, remote or ambiguous endpoints must be rejected'
    }

    foreach ($name in @('DOCKER_CONTEXT', 'DOCKER_HOST')) {
        [Environment]::SetEnvironmentVariable($name, 'synthetic-private-value', 'Process')
        Assert-Rejected 'npipe:////./pipe/dockerDesktopLinuxEngine' 'environment-selected endpoints must be rejected without disclosure'
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($harnessPath, [ref] $tokens, [ref] $parseErrors)
    Assert-Check ($parseErrors.Count -eq 0) 'the harness must parse without errors'
    $commands = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))
    $dockerCommands = @($commands | Where-Object { $_.CommandElements[0].Extent.Text -ceq '$dockerPath' })
    Assert-Check ($dockerCommands.Count -eq 4) 'run, inspect, port and stop must all use the resolved Docker executable'
    foreach ($command in $dockerCommands) {
        Assert-Check ($command.CommandElements[1].Extent.Text -ceq '@dockerArguments') 'each Docker operation, including stop, must include the pinned socket'
    }
    Assert-Check (@($commands | Where-Object { $_.GetCommandName() -ieq 'docker' }).Count -eq 0) 'no implicit-context Docker command is permitted'
    $assignments = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true))
    $prefixAssignments = @($assignments | Where-Object { $_.Left.Extent.Text -ceq '$dockerArguments' })
    Assert-Check ($prefixAssignments.Count -eq 1) 'the pinned Docker arguments must not be rebound'
    $startup = @($assignments | Where-Object { $_.Left.Extent.Text -ceq '$startParameters' })
    Assert-Check ($startup.Count -eq 1 -and $startup[0].Right.Extent.Text.Contains('ArgumentList = $dockerArguments +')) 'the readiness subprocess must use the same pinned socket'

    Write-Output "PostgreSQL harness safety: $checks checks passed; no Docker engine or database contacted."
}
finally {
    foreach ($name in @('DOCKER_CONTEXT', 'DOCKER_HOST')) {
        [Environment]::SetEnvironmentVariable($name, $savedDockerEnvironment[$name], 'Process')
    }
}
