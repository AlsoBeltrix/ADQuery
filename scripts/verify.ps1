[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'ADQuery.sln'
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$testResultsPath = Join-Path $artifactsRoot 'test-results'
$dependencyAuditPath = Join-Path $artifactsRoot 'dependency-audit'
$publishPath = Join-Path $artifactsRoot 'publish'
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)

function Write-Stage {
    param([Parameter(Mandatory)][string]$Message)

    Write-Output "`n==> $Message"
}

function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Native command failed with exit code ${exitCode}: $FilePath $($Arguments -join ' ')"
    }
}

function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $output = & $FilePath @Arguments | Out-String
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Native command failed with exit code ${exitCode}: $FilePath $($Arguments -join ' ')"
    }

    return $output
}

function Reset-ArtifactDirectory {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullArtifactsRoot = [IO.Path]::GetFullPath($artifactsRoot)
    $artifactsPrefix = $fullArtifactsRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset an artifact directory outside '$fullArtifactsRoot': $fullPath"
    }

    if ($PSCmdlet.ShouldProcess($fullPath, 'Reset artifact directory')) {
        if (Test-Path -LiteralPath $fullPath) {
            Remove-Item -LiteralPath $fullPath -Recurse -Force
        }

        $null = New-Item -ItemType Directory -Path $fullPath -Force
    }
}

function Assert-RepositoryContract {
    if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
        throw "Repository solution was not found: $solutionPath"
    }

    $globalJsonPath = Join-Path $repositoryRoot 'global.json'
    if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
        throw "SDK contract was not found: $globalJsonPath"
    }

    $sdkContract = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
    if ($sdkContract.sdk.version -ne '10.0.300' -or
        $sdkContract.sdk.rollForward -ne 'latestPatch' -or
        $sdkContract.sdk.allowPrerelease -ne $false) {
        throw 'global.json must pin 10.0.300/latestPatch with prerelease SDKs disabled.'
    }

    $selectedSdk = (Invoke-NativeCapture -FilePath 'dotnet' -Arguments @('--version')).Trim()
    if ($selectedSdk -notmatch '^10\.0\.3\d{2}$') {
        throw "Selected SDK '$selectedSdk' is outside the approved stable 10.0.3xx feature band."
    }

    Write-Output "Selected SDK: $selectedSdk"

    $projectPaths = @(
        'csharp/AdQueryOrchestrator.csproj',
        'tests/AdQueryOrchestrator.Tests/AdQueryOrchestrator.Tests.csproj'
    )

    foreach ($relativeProjectPath in $projectPaths) {
        $projectPath = Join-Path $repositoryRoot $relativeProjectPath
        [xml]$project = Get-Content -LiteralPath $projectPath -Raw
        $targetFramework = @($project.Project.PropertyGroup.TargetFramework) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -First 1

        if ($targetFramework -ne 'net10.0-windows') {
            throw "Project '$relativeProjectPath' must target net10.0-windows; found '$targetFramework'."
        }
    }
}

function Assert-TestArtifact {
    $trxFiles = @(Get-ChildItem -LiteralPath $testResultsPath -Filter '*.trx' -File -Recurse)
    if ($trxFiles.Count -eq 0) {
        throw "No TRX test result was produced beneath '$testResultsPath'."
    }

    $executedTests = 0
    foreach ($trxFile in $trxFiles) {
        [xml]$trx = Get-Content -LiteralPath $trxFile.FullName -Raw
        $executedTests += [int]$trx.TestRun.ResultSummary.Counters.executed
    }

    if ($executedTests -le 0) {
        throw 'The test run reported zero executed tests.'
    }

    $coverageFiles = @(
        Get-ChildItem -LiteralPath $testResultsPath -Filter 'coverage.cobertura.xml' -File -Recurse
    )
    if ($coverageFiles.Count -eq 0) {
        throw "No Cobertura coverage result was produced beneath '$testResultsPath'."
    }

    Write-Output "Executed tests: $executedTests"
    Write-Output "TRX files: $($trxFiles.Count); Cobertura files: $($coverageFiles.Count)"
}

function Assert-PublishedRuntimeContract {
    $runtimeConfigPath = Join-Path $publishPath 'AdQueryOrchestrator.runtimeconfig.json'
    $dependenciesPath = Join-Path $publishPath 'AdQueryOrchestrator.deps.json'
    if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) {
        throw "Published runtime contract was not found: $runtimeConfigPath"
    }
    if (-not (Test-Path -LiteralPath $dependenciesPath -PathType Leaf)) {
        throw "Published dependency contract was not found: $dependenciesPath"
    }

    $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
    if ($runtimeConfig.runtimeOptions.tfm -ne 'net10.0') {
        throw "Published application must target net10.0; found '$($runtimeConfig.runtimeOptions.tfm)'."
    }

    $frameworks = @($runtimeConfig.runtimeOptions.frameworks)
    foreach ($frameworkName in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
        $framework = @($frameworks | Where-Object { $_.name -eq $frameworkName })
        if ($framework.Count -ne 1 -or $framework[0].version -notmatch '^10\.0\.\d+$') {
            throw "Published application does not declare one .NET 10 '$frameworkName' framework."
        }
    }

    $dependencies = Get-Content -LiteralPath $dependenciesPath -Raw | ConvertFrom-Json
    if ($dependencies.runtimeTarget.name -ne '.NETCoreApp,Version=v10.0') {
        throw "Published dependency graph targets '$($dependencies.runtimeTarget.name)' instead of .NET 10."
    }
}

function Invoke-PublishedStartupSmoke {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()

    $standardOutputPath = Join-Path $publishPath 'startup.stdout.log'
    $standardErrorPath = Join-Path $publishPath 'startup.stderr.log'
    $priorUrls = $env:ASPNETCORE_URLS
    $priorEnvironment = $env:ASPNETCORE_ENVIRONMENT
    $applicationProcess = $null

    try {
        $env:ASPNETCORE_URLS = "http://127.0.0.1:$port"
        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        $applicationProcess = Start-Process `
            -FilePath 'dotnet' `
            -ArgumentList @('AdQueryOrchestrator.dll') `
            -WorkingDirectory $publishPath `
            -RedirectStandardOutput $standardOutputPath `
            -RedirectStandardError $standardErrorPath `
            -WindowStyle Hidden `
            -PassThru

        $response = $null
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            Start-Sleep -Milliseconds 250
            $applicationProcess.Refresh()
            if ($applicationProcess.HasExited) {
                throw "Published application exited during startup with code $($applicationProcess.ExitCode)."
            }

            try {
                $response = Invoke-WebRequest `
                    -Uri "http://127.0.0.1:$port/api/user/info" `
                    -SkipHttpErrorCheck `
                    -TimeoutSec 1
                break
            }
            catch {
                # The port can refuse connections briefly while Kestrel starts.
            }
        }

        if ($null -eq $response) {
            throw 'Published application did not answer a local startup request.'
        }
        if ([int]$response.StatusCode -ne 401) {
            throw "Published protected endpoint returned $([int]$response.StatusCode) instead of 401."
        }

        Write-Output 'Published startup smoke passed: protected endpoint returned 401.'
    }
    finally {
        if ($null -ne $applicationProcess) {
            $applicationProcess.Refresh()
            if (-not $applicationProcess.HasExited) {
                Stop-Process -Id $applicationProcess.Id
                $null = $applicationProcess.WaitForExit(10000)
            }
        }

        $env:ASPNETCORE_URLS = $priorUrls
        $env:ASPNETCORE_ENVIRONMENT = $priorEnvironment
    }
}

function Assert-AuditDocument {
    param(
        [Parameter(Mandatory)][object]$Audit,
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string[]]$ExpectedProjectPaths
    )

    $versionProperty = $Audit.PSObject.Properties['version']
    if ($null -eq $versionProperty -or $versionProperty.Value -ne 1) {
        throw "Dependency audit for '$Target' did not use supported JSON schema version 1."
    }

    $sourcesProperty = $Audit.PSObject.Properties['sources']
    $sources = @()
    if ($null -ne $sourcesProperty) {
        $sources = @($sourcesProperty.Value)
    }
    if ($sources.Count -eq 0 -or @($sources | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw "Dependency audit for '$Target' did not report a valid package source."
    }

    $projectsProperty = $Audit.PSObject.Properties['projects']
    $projects = @()
    if ($null -ne $projectsProperty) {
        $projects = @($projectsProperty.Value)
    }
    if ($projects.Count -eq 0) {
        throw "Dependency audit for '$Target' did not report any projects."
    }

    $reportedProjectPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($project in $projects) {
        $pathProperty = $project.PSObject.Properties['path']
        if ($null -eq $pathProperty -or [string]::IsNullOrWhiteSpace($pathProperty.Value)) {
            throw "Dependency audit for '$Target' contained a project without a path."
        }

        $reportedPath = if ([IO.Path]::IsPathRooted($pathProperty.Value)) {
            [IO.Path]::GetFullPath($pathProperty.Value)
        }
        else {
            [IO.Path]::GetFullPath((Join-Path $repositoryRoot $pathProperty.Value))
        }
        $null = $reportedProjectPaths.Add($reportedPath)
    }

    foreach ($expectedProjectPath in $ExpectedProjectPaths) {
        $expectedPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $expectedProjectPath))
        if (-not $reportedProjectPaths.Contains($expectedPath)) {
            throw "Dependency audit for '$Target' omitted expected project '$expectedProjectPath'."
        }
    }

    $findings = @(
        foreach ($project in $projects) {
            $frameworksProperty = $project.PSObject.Properties['frameworks']
            if ($null -eq $frameworksProperty) {
                continue
            }

            foreach ($framework in @($frameworksProperty.Value)) {
                $frameworkProperty = $framework.PSObject.Properties['framework']
                if ($null -eq $frameworkProperty -or
                    [string]::IsNullOrWhiteSpace($frameworkProperty.Value)) {
                    throw "Dependency audit for '$Target' contained a framework without a name."
                }

                $topLevelPackages = $framework.PSObject.Properties['topLevelPackages']
                $transitivePackages = $framework.PSObject.Properties['transitivePackages']
                $packages = @()
                if ($null -ne $topLevelPackages) {
                    $packages += @($topLevelPackages.Value)
                }
                if ($null -ne $transitivePackages) {
                    $packages += @($transitivePackages.Value)
                }

                foreach ($package in $packages) {
                    if ($null -eq $package) {
                        continue
                    }

                    $vulnerabilitiesProperty = $package.PSObject.Properties['vulnerabilities']
                    if ($null -eq $vulnerabilitiesProperty) {
                        continue
                    }

                    foreach ($vulnerability in @($vulnerabilitiesProperty.Value)) {
                        if ($null -eq $vulnerability) {
                            continue
                        }

                        [pscustomobject]@{
                            Project  = $project.path
                            Framework = $frameworkProperty.Value
                            Package  = $package.id
                            Version  = $package.resolvedVersion
                            Severity = $vulnerability.severity
                            Advisory = $vulnerability.advisoryUrl
                        }
                    }
                }
            }
        }
    )

    if ($findings.Count -gt 0) {
        $details = $findings | Format-Table -AutoSize | Out-String
        throw "Vulnerable packages were found for '$Target':`n$details"
    }
}

function Assert-NoVulnerablePackage {
    param(
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string[]]$ExpectedProjectPaths
    )

    $auditJson = Invoke-NativeCapture -FilePath 'dotnet' -Arguments @(
        'list',
        $Target,
        'package',
        '--vulnerable',
        '--include-transitive',
        '--format',
        'json',
        '--no-restore'
    )

    [IO.File]::WriteAllText($OutputPath, $auditJson.Trim() + [Environment]::NewLine, $utf8WithoutBom)
    $audit = $auditJson | ConvertFrom-Json
    Assert-AuditDocument -Audit $audit -Target $Target -ExpectedProjectPaths $ExpectedProjectPaths

    Write-Output "Vulnerability audit passed: $Target"
}

function Invoke-RepositoryVerification {
    Push-Location $repositoryRoot
    try {
        Write-Stage 'Validate repository and SDK contract'
        Assert-RepositoryContract

        Reset-ArtifactDirectory -Path $testResultsPath
        Reset-ArtifactDirectory -Path $dependencyAuditPath
        Reset-ArtifactDirectory -Path $publishPath

        Write-Stage 'Restore locked dependencies'
        Invoke-Native -FilePath 'dotnet' -Arguments @(
            'restore', 'ADQuery.sln', '--locked-mode', '--nologo'
        )

        Write-Stage 'Verify formatting'
        Invoke-Native -FilePath 'dotnet' -Arguments @(
            'format', 'ADQuery.sln', 'whitespace', '--verify-no-changes', '--no-restore'
        )

        Write-Stage 'Build Release with warnings as errors'
        Invoke-Native -FilePath 'dotnet' -Arguments @(
            'build', 'ADQuery.sln', '-c', 'Release', '--no-restore', '--nologo', '-warnaserror'
        )

        Write-Stage 'Run tests with TRX and Cobertura output'
        Invoke-Native -FilePath 'dotnet' -Arguments @(
            'test',
            'ADQuery.sln',
            '-c',
            'Release',
            '--no-build',
            '--no-restore',
            '--nologo',
            '--logger',
            'trx;LogFilePrefix=verification',
            '--results-directory',
            $testResultsPath,
            '--collect',
            'XPlat Code Coverage'
        )
        Assert-TestArtifact

        Write-Stage 'Publish and start the Release application'
        Invoke-Native -FilePath 'dotnet' -Arguments @(
            'publish',
            'csharp/AdQueryOrchestrator.csproj',
            '-c',
            'Release',
            '--no-restore',
            '--nologo',
            '--output',
            $publishPath
        )
        Assert-PublishedRuntimeContract
        Invoke-PublishedStartupSmoke

        Write-Stage 'Audit direct and transitive dependencies'
        Assert-NoVulnerablePackage `
            -Target 'csharp/AdQueryOrchestrator.csproj' `
            -OutputPath (Join-Path $dependencyAuditPath 'application.json') `
            -ExpectedProjectPaths @('csharp/AdQueryOrchestrator.csproj')
        Assert-NoVulnerablePackage `
            -Target 'ADQuery.sln' `
            -OutputPath (Join-Path $dependencyAuditPath 'solution.json') `
            -ExpectedProjectPaths @(
                'csharp/AdQueryOrchestrator.csproj',
                'tests/AdQueryOrchestrator.Tests/AdQueryOrchestrator.Tests.csproj'
            )

        Write-Output "`nVerification passed."
    }
    finally {
        Pop-Location
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-RepositoryVerification
}
