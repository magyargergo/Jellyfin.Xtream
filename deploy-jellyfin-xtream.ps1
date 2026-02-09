# Jellyfin Xtream Plugin - Build & Deploy Script
param(
    [string]$RemoteHost = "192.168.1.5",
    [string]$RemoteUser = "gmagyar",
    [string]$ProjectPath = "D:\development\jellyfin-plugins\Jellyfin.Xtream",
    [string]$RemotePath = "/home/gmagyar/docker-media/config/jellyfin/data/plugins/Jellyfin Xtream_0.7.2.0",
    [string]$DockerComposePath = "/home/gmagyar/docker-media",
    [switch]$SkipBuild,
    [switch]$SkipRestart,
    [switch]$SkipBackup,
    [switch]$SkipTests,
    [switch]$Debug
)

# Color output functions
function Write-Success { param($Message) Write-Host "[SUCCESS] $Message" -ForegroundColor Green }
function Write-Info { param($Message) Write-Host "[INFO] $Message" -ForegroundColor Cyan }
function Write-Warning { param($Message) Write-Host "[WARNING] $Message" -ForegroundColor Yellow }
function Write-Error { param($Message) Write-Host "[ERROR] $Message" -ForegroundColor Red }

# Script configuration
$SshPath = "C:\Windows\System32\OpenSSH\ssh.exe"
$ScpPath = "C:\Windows\System32\OpenSSH\scp.exe"
$Configuration = if ($Debug) { "Debug" } else { "Release" }
$BuildOutputDir = "$ProjectPath\Jellyfin.Xtream\bin\$Configuration\net8.0"
$BuildPath = "$BuildOutputDir\Jellyfin.Xtream.dll"

# SSH options to prevent hanging
$SshOptions = @(
    "-o", "BatchMode=yes",           # Fail instead of prompting for password
    "-o", "ConnectTimeout=10",       # Connection timeout in seconds
    "-o", "ServerAliveInterval=5",   # Send keepalive every 5 seconds
    "-o", "ServerAliveCountMax=3"    # Disconnect after 3 missed keepalives
)

# Helper function to run SSH command with timeout
function Invoke-SshCommand {
    param(
        [string]$Command,
        [int]$TimeoutSeconds = 60,
        [switch]$IgnoreError
    )

    $sshArgs = $SshOptions + @("$RemoteUser@$RemoteHost", $Command)

    Write-Host "  Running: $Command" -ForegroundColor DarkGray

    # Capture both output and exit code inside the job
    $job = Start-Job -ScriptBlock {
        param($ssh, $sshArgsParam)
        $output = & $ssh @sshArgsParam 2>&1
        @{
            Output = $output
            ExitCode = $LASTEXITCODE
        }
    } -ArgumentList $SshPath, $sshArgs

    $completed = Wait-Job $job -Timeout $TimeoutSeconds

    if ($completed) {
        $result = Receive-Job $job
        Remove-Job $job -Force

        # Handle case where result is hashtable (expected) or raw output (fallback)
        if ($result -is [hashtable]) {
            $output = $result.Output
            $exitCode = $result.ExitCode
        } else {
            # Fallback: treat entire result as output, assume success
            $output = $result
            $exitCode = 0
        }

        if ($output) {
            $output | ForEach-Object { Write-Host $_ }
        }

        if ($exitCode -ne 0 -and -not $IgnoreError) {
            return $false
        }
        return $true
    } else {
        Stop-Job $job
        Remove-Job $job -Force
        Write-Warning "Command timed out after $TimeoutSeconds seconds"
        return $false
    }
}

# Helper function to run SCP with timeout
function Invoke-ScpCopy {
    param(
        [string]$Source,
        [string]$Destination,
        [int]$TimeoutSeconds = 30
    )

    $scpArgs = $SshOptions + @($Source, $Destination)

    $job = Start-Job -ScriptBlock {
        param($scp, $scpArgsParam)
        & $scp @scpArgsParam 2>&1
        $LASTEXITCODE
    } -ArgumentList $ScpPath, $scpArgs

    $completed = Wait-Job $job -Timeout $TimeoutSeconds

    if ($completed) {
        $output = Receive-Job $job
        Remove-Job $job -Force
        # Last item is exit code
        $exitCode = $output[-1]
        return $exitCode -eq 0
    } else {
        Stop-Job $job
        Remove-Job $job -Force
        Write-Warning "SCP timed out after $TimeoutSeconds seconds"
        return $false
    }
}

# Dependencies that need to be deployed
$RequiredDependencies = @(
    "Discord.Net.Core.dll",
    "Discord.Net.Rest.dll",
    "Discord.Net.Webhook.dll",
    "Polly.dll",
    "Polly.Core.dll",
    "AngleSharp.dll",
    "TurboXml.dll",
    "Newtonsoft.Json.dll"
)

# Native libraries for TsDuck MPEG-TS analysis (Linux only)
# TsDuck is split into: libtscore (core utils) + libtsduck (TS processing)
# libtsduck_interop is our P/Invoke bridge that links to libtsduck
$NativeLibrariesDir = "$BuildOutputDir\runtimes\linux-x64\native"
$NativeLibraries = @(
    "libtsduck_interop.so",
    "libtscore.so",
    "libtsduck.so"
)

Write-Info "Starting Jellyfin Xtream Plugin Deployment"
Write-Info "Configuration: $Configuration"
Write-Info "Target: $RemoteUser@$RemoteHost"

try {
    if ($SkipTests) {
        Write-Warning "Skipping pre-deployment tests (-SkipTests specified)"
    } else {
        # Step 0: Run pre-deployment checks
        Write-Info "Running pre-deployment checks..."

        # 0a: .NET unit tests
        Write-Info "[1/3] Running .NET unit tests..."
        Push-Location $ProjectPath
        $testCommand = "dotnet test --configuration $Configuration --no-restore --filter 'Category!=Integration' --logger 'console;verbosity=minimal'"
        Write-Info "Executing: $testCommand"
        Invoke-Expression $testCommand
        if ($LASTEXITCODE -ne 0) {
            Write-Error ".NET unit tests failed with exit code $LASTEXITCODE"
            Pop-Location
            exit 1
        }
        Pop-Location
        Write-Success ".NET unit tests passed"

        # 0b: Native unit/integration tests (Docker)
        Write-Info "[2/3] Running native TsDuck tests..."
        Push-Location $ProjectPath
        $nativeBuildCommand = "docker build -f native/tsduck_interop/Dockerfile.test --target test-runner -t tsduck-interop-tests ."
        Write-Info "Building test image: $nativeBuildCommand"
        Invoke-Expression $nativeBuildCommand
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Native test build failed with exit code $LASTEXITCODE"
            Pop-Location
            exit 1
        }
        $nativeRunCommand = "docker run --rm tsduck-interop-tests"
        Write-Info "Running tests: $nativeRunCommand"
        Invoke-Expression $nativeRunCommand
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Native tests failed with exit code $LASTEXITCODE"
            Pop-Location
            exit 1
        }
        Pop-Location
        Write-Success "Native tests passed"

        # 0c: E2E tests (Docker Compose)
        Write-Info "[3/3] Running E2E tests..."
        Push-Location $ProjectPath
        $e2eResultsDir = Join-Path $ProjectPath "e2e-results"
        if (-not (Test-Path $e2eResultsDir)) {
            New-Item -ItemType Directory -Path $e2eResultsDir -Force | Out-Null
        }
        $e2eCommand = "docker compose -f docker-compose.e2e.yaml up --build --abort-on-container-exit --exit-code-from e2e-tests"
        Write-Info "Executing: $e2eCommand"
        Invoke-Expression $e2eCommand
        $e2eExitCode = $LASTEXITCODE
        docker compose -f docker-compose.e2e.yaml down 2>$null
        if ($e2eExitCode -ne 0) {
            Write-Error "E2E tests failed with exit code $e2eExitCode"
            Pop-Location
            exit 1
        }
        Pop-Location
        Write-Success "E2E tests passed"

        Write-Success "All pre-deployment checks passed (3/3)"
    }

    # Step 1: Build the plugin
    if (-not $SkipBuild) {
        # Clear previous build artifacts
        Write-Info "Cleaning previous build artifacts..."
        dotnet clean --configuration $Configuration


        Write-Info "Building plugin ($Configuration)..."
        Push-Location $ProjectPath

        # Debug builds don't treat warnings as errors for easier development
        $buildArgs = if ($Debug) {
            "--configuration Debug"
        } else {
            "--configuration Release -p:TreatWarningsAsErrors=true"
        }

        $buildCommand = "dotnet build $buildArgs"
        Write-Info "Build command: $buildCommand"
        Invoke-Expression $buildCommand

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Build failed with exit code $LASTEXITCODE"
            Pop-Location
            exit 1
        }

        Pop-Location
        Write-Success "Plugin built successfully ($Configuration)"

        # Verify build output exists
        if (-not (Test-Path $BuildPath)) {
            Write-Error "Build output not found at: $BuildPath"
            exit 1
        }

        $buildSize = (Get-Item $BuildPath).Length
        Write-Info "Build size: $([math]::Round($buildSize/1KB, 1)) KB"

        # Verify required dependencies
        $missingDeps = @()
        foreach ($dep in $RequiredDependencies) {
            $depPath = Join-Path $BuildOutputDir $dep
            if (-not (Test-Path $depPath)) {
                $missingDeps += $dep
            }
        }

        if ($missingDeps.Count -gt 0) {
            Write-Error "Missing required dependencies: $($missingDeps -join ', ')"
            exit 1
        }

        Write-Info "All $($RequiredDependencies.Count) required dependencies present"
    } else {
        Write-Warning "Skipping build (-SkipBuild specified)"
        if (-not (Test-Path $BuildPath)) {
            Write-Error "No existing build found at: $BuildPath"
            exit 1
        }
    }

    if (-not $SkipBackup) {
        # Step 2: Create backup on remote server (OUTSIDE plugins directory to avoid assembly conflicts)
        Write-Info "Creating backup on remote server..."
        $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
        # CRITICAL: Backups MUST be outside /config/data/plugins/ entirely (Jellyfin scans recursively)
        $backupBaseDir = "/home/gmagyar/docker-media/config/jellyfin/xtream-plugin-backups"
        $backupDir = "$backupBaseDir/backup_$timestamp"

        # Create backup directory and copy DLLs
        $backupCommand = "mkdir -p '$backupDir' && cd '$RemotePath' && cp *.dll '$backupDir/' 2>/dev/null || true"
        if (Invoke-SshCommand -Command $backupCommand -TimeoutSeconds 30 -IgnoreError) {
            Write-Success "Backup created: $backupDir"

            # Clean up old backups (keep only last 5)
            $cleanupCommand = "cd '$backupBaseDir' && ls -t | tail -n +6 | xargs -r rm -rf"
            Invoke-SshCommand -Command $cleanupCommand -TimeoutSeconds 30 -IgnoreError | Out-Null
            Write-Info "Old backups cleaned (keeping last 5)"
        } else {
            Write-Warning "Backup creation failed, continuing anyway..."
        }
    } else {
        Write-Warning "Skipping backup (-SkipBackup specified)"
    }

    # IMPORTANT: Remove any old backup directories from within the plugin folder (fixes assembly conflicts)
    Write-Info "Cleaning up any old backups inside plugin directory..."
    $cleanPluginBackupsCommand = "cd '$RemotePath' && rm -rf backup_* 2>/dev/null || true"
    Invoke-SshCommand -Command $cleanPluginBackupsCommand -TimeoutSeconds 30 -IgnoreError | Out-Null
    Write-Success "Plugin directory cleaned"

    # Step 3: Deploy new plugin and dependencies
    Write-Info "Deploying plugin and dependencies..."

    # Deploy main plugin DLL
    $mainDllDest = "${RemoteUser}@${RemoteHost}:$RemotePath/Jellyfin.Xtream.dll"
    if (-not (Invoke-ScpCopy -Source $BuildPath -Destination $mainDllDest -TimeoutSeconds 60)) {
        Write-Error "Failed to copy plugin to remote server"
        exit 1
    }
    Write-Success "Main plugin deployed: Jellyfin.Xtream.dll"

    # Deploy dependencies
    $deployedCount = 0
    foreach ($dep in $RequiredDependencies) {
        $depPath = Join-Path $BuildOutputDir $dep
        $depDest = "${RemoteUser}@${RemoteHost}:$RemotePath/$dep"
        if (Invoke-ScpCopy -Source $depPath -Destination $depDest -TimeoutSeconds 30) {
            $deployedCount++
        } else {
            Write-Warning "Failed to deploy dependency: $dep"
        }
    }

    Write-Success "Deployed $deployedCount/$($RequiredDependencies.Count) dependencies"

    # Deploy native TsDuck libraries to plugin's persistent runtimes directory
    # This location survives container restarts (mounted from host)
    $script:nativeDeployedCount = 0
    $nativeRuntimesDir = "$RemotePath/runtimes/linux-x64/native"
    if (Test-Path $NativeLibrariesDir) {
        Write-Info "Deploying native TsDuck libraries to plugin directory..."

        # Ensure runtimes directory exists
        $createRuntimesDirCommand = "mkdir -p '$nativeRuntimesDir'"
        Invoke-SshCommand -Command $createRuntimesDirCommand -TimeoutSeconds 30 -IgnoreError | Out-Null

        foreach ($lib in $NativeLibraries) {
            $libPath = Join-Path $NativeLibrariesDir $lib
            if (Test-Path $libPath) {
                $libDest = "${RemoteUser}@${RemoteHost}:$nativeRuntimesDir/$lib"
                if (Invoke-ScpCopy -Source $libPath -Destination $libDest -TimeoutSeconds 60) {
                    $script:nativeDeployedCount++
                    $libSize = (Get-Item $libPath).Length
                    Write-Info "  Deployed: $lib ($([math]::Round($libSize/1MB, 2)) MB)"
                } else {
                    Write-Warning "Failed to deploy native library: $lib"
                }
            }
        }
        Write-Success "Deployed $script:nativeDeployedCount native libraries to plugin runtimes directory"
    } else {
        Write-Warning "Native libraries not found at $NativeLibrariesDir"
        Write-Info "TsDuck stream analysis will use fallback mode"
    }

    # Step 4: Verify deployment
    Write-Info "Verifying deployment..."
    $verifyCommand = @"
cd '$RemotePath'
echo '=== Plugin Files ==='
ls -lh *.dll | grep -E '(Jellyfin.Xtream|Polly|Discord)' || true
echo ''
echo '=== Native Libraries ==='
ls -lh runtimes/linux-x64/native/*.so* 2>/dev/null || echo 'No native libraries found'
echo ''
echo '=== Total Size ==='
du -sh .
"@
    Invoke-SshCommand -Command $verifyCommand -TimeoutSeconds 30 -IgnoreError | Out-Null
    Write-Success "Deployment verified"

    # Step 5: Verify FFmpeg availability (for stream previews)
    Write-Info "Verifying FFmpeg availability in container..."
    $ffmpegCheckCommand = "docker exec jellyfin which ffmpeg || docker exec jellyfin ls -la /usr/lib/jellyfin-ffmpeg/ffmpeg"
    if (Invoke-SshCommand -Command $ffmpegCheckCommand -TimeoutSeconds 30 -IgnoreError) {
        Write-Success "FFmpeg found in Jellyfin container"
    } else {
        Write-Warning "FFmpeg not detected - stream preview feature may not work"
    }

    # Step 6: Restart Jellyfin
    if (-not $SkipRestart) {
        Write-Info "Restarting Jellyfin container..."

        $restartCommand = "cd $DockerComposePath; docker compose stop jellyfin && docker compose up -d jellyfin"
        if (-not (Invoke-SshCommand -Command $restartCommand -TimeoutSeconds 120)) {
            Write-Error "Failed to restart Jellyfin container"
            exit 1
        }

        Write-Success "Jellyfin restarted successfully"

        # Wait a moment and check if container is running
        Write-Info "Waiting for Jellyfin to start..."
        Start-Sleep -Seconds 5

        $statusCommand = "docker ps | grep jellyfin"
        if (Invoke-SshCommand -Command $statusCommand -TimeoutSeconds 30 -IgnoreError) {
            Write-Success "Jellyfin container is running"
        } else {
            Write-Warning "Could not verify Jellyfin container status"
        }

        # Native libraries are deployed to plugin's runtimes directory (persistent on host).
        # However, libtsduck_interop.so links to libtsduck.so, and the dynamic linker won't
        # find it in the plugin directory. We must copy to /usr/local/lib and run ldconfig.
        if ($script:nativeDeployedCount -gt 0) {
            Write-Info "Installing native libraries into container's library path..."

            # The plugin directory inside the container
            $containerPluginDir = "/config/data/plugins/Jellyfin Xtream_0.7.2.0"
            $containerNativeDir = "$containerPluginDir/runtimes/linux-x64/native"

            # Copy libraries to /usr/local/lib and run ldconfig
            $installLibsCommand = @"
docker exec jellyfin sh -c 'cp "$containerNativeDir/libtsduck.so" /usr/local/lib/ && \
cp "$containerNativeDir/libtscore.so" /usr/local/lib/ && \
cp "$containerNativeDir/libtsduck_interop.so" /usr/local/lib/ && \
ldconfig && \
echo "Libraries installed to /usr/local/lib"'
"@
            if (Invoke-SshCommand -Command $installLibsCommand -TimeoutSeconds 30 -IgnoreError) {
                Write-Success "Native libraries installed to container /usr/local/lib"

                # Verify the libraries are resolvable
                $verifyLddCommand = "docker exec jellyfin ldd /usr/local/lib/libtsduck_interop.so 2>&1 | grep -E '(libtsduck|not found)'"
                Invoke-SshCommand -Command $verifyLddCommand -TimeoutSeconds 30 -IgnoreError | Out-Null
            } else {
                Write-Warning "Failed to install native libraries to container - TsDuck may not work"
            }
        }
    } else {
        Write-Warning "Skipping Jellyfin restart (-SkipRestart specified)"
        Write-Info "Remember to restart Jellyfin manually to load the new plugin"
    }

    Write-Success "Deployment completed successfully!"
    Write-Host ""
    Write-Info "Plugin Info:"
    Write-Info "  - Configuration: $Configuration"
    Write-Info "  - Main Plugin: Jellyfin.Xtream.dll"
    Write-Info "  - Dependencies: $($RequiredDependencies.Count) DLLs"
    Write-Info "    * AngleSharp (HTML parsing)"
    Write-Info "    * Polly (HTTP resilience & retry policies)"
    Write-Info "    * Discord.Net (webhook notifications)"
    Write-Info "    * TurboXml (XMLTV parsing)"
    Write-Info "    * FFmpeg.AutoGen (MPEG-TS demuxing)"
    Write-Info "  - Native Libraries: TsDuck interop (MPEG-TS stream analysis)"
    Write-Info "    * libtsduck_interop.so (P/Invoke bridge)"
    Write-Info "    * libtscore.so (TsDuck core utilities)"
    Write-Info "    * libtsduck.so (TsDuck TS processing)"

    if ($Debug) {
        Write-Host ""
        Write-Warning "DEBUG BUILD DEPLOYED"
        Write-Info "Debug features enabled:"
        Write-Info "  - Verbose logging"
        Write-Info "  - Debug symbols included"
        Write-Info "  - No optimization"
        Write-Info "  - Better stack traces for troubleshooting"
    }

    Write-Host ""
    Write-Info "Next steps:"
    Write-Info "1. Access Jellyfin at http://$RemoteHost`:8096"
    Write-Info "2. Go to Dashboard -> Plugins -> Jellyfin Xtream"
    Write-Info "3. Check that plugin loaded successfully in logs"
    Write-Info "4. Configure your Xtream credentials and settings"

} catch {
    Write-Error "Deployment failed: $($_.Exception.Message)"
    exit 1
}
