param (
    [switch]$Stop,
    [switch]$Build,
    [switch]$Publish,
    [switch]$Deploy,
    [switch]$Install,
    [switch]$Start,
    [switch]$MoreStates,
    [string]$DeployPath = "D:\Scripts\MqttAgent.exe"
)

$RootDir = Split-Path -Parent $PSScriptRoot
$ConfigPath = Join-Path $RootDir "appsettings.json"

# Load config from JSON if exists
$Token = $env:MQTTAGENT_TOKEN
$Port = if ($env:MQTTAGENT_PORT) { [int]$env:MQTTAGENT_PORT } else { 23482 }

if (Test-Path $ConfigPath) {
    try {
        $json = Get-Content $ConfigPath -Raw | ConvertFrom-Json
        if ($json.MqttAgent.Token) { $Token = $json.MqttAgent.Token }
        if ($json.MqttAgent.Port) { $Port = $json.MqttAgent.Port }
    } catch {
        Write-Warning "Failed to parse $ConfigPath."
    }
}

if (-not $Token) {
    Write-Error "CRITICAL: MQTTAGENT_TOKEN not found in config or environment. Deployment aborted for security."
    exit 1
}

Write-Host "Loaded config (Token: ...$($Token.Substring($Token.Length - 4)), Port: $Port)" -ForegroundColor Gray

$CsprojPath = Join-Path $RootDir "MqttAgent.csproj"
$PublishDir = Join-Path $RootDir "publish"
$ExePath = Join-Path $PublishDir "MqttAgent.exe"
$ServiceName = "MqttAgent"

function Bump-Version {
    $content = Get-Content $CsprojPath -Raw
    if ($content -match '<Version>(?<version>.*)</Version>') {
        $version = [version]$Matches['version']
        $newVersion = "{0}.{1}.{2}" -f $version.Major, $version.Minor, ($version.Build + 1)
        $content = $content -replace "<Version>.*</Version>", "<Version>$newVersion</Version>"
        $content | Set-Content $CsprojPath
        Write-Host "Bumped version to $newVersion" -ForegroundColor Magenta
        return $newVersion
    }
    return "1.0.0"
}

if ($Stop) {
    Write-Host "Stopping MQTT Agent Service..." -ForegroundColor Cyan
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        Write-Host "Stopping service via sudo..." -ForegroundColor Gray
        sudo sc.exe stop $ServiceName
        Start-Sleep -Seconds 2
    }

    Write-Host "Killing MQTT Agent processes..." -ForegroundColor Cyan
    # Kill any process with the name
    Get-Process MqttAgent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    # Fallback aggressive kill
    taskkill /F /IM MqttAgent.exe /T 2>$null

    Write-Host "Waiting for file locks to release..." -ForegroundColor Gray
    $retry = 10
    while ($retry -gt 0) {
        try {
            $testStream = [System.IO.File]::Open($DeployPath, 'Open', 'Write', 'None')
            $testStream.Close()
            break
        } catch {
            Start-Sleep -Seconds 1
            $retry--
        }
    }
}

if ($Build) {
    Write-Host "Building project (Warnings as Errors)..." -ForegroundColor Cyan
    dotnet build -c Release $RootDir /p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed with errors or warnings."
        exit $LASTEXITCODE
    }
}

if ($Deploy) {
    Write-Host "Deploying single-file to $DeployPath..." -ForegroundColor Cyan
    dotnet publish $RootDir -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$PublishDir\single"
    $SingleExe = Join-Path "$PublishDir\single" "MqttAgent.exe"
    if (Test-Path $SingleExe) {
        if (Test-Path $DeployPath) {
            try {
                $oldPath = $DeployPath + ".old"
                if (Test-Path $oldPath) { Remove-Item $oldPath -Force -ErrorAction SilentlyContinue }
                Rename-Item $DeployPath $oldPath -Force -ErrorAction SilentlyContinue
            } catch {
                Write-Warning "Could not rename $DeployPath. Attempting direct overwrite..."
            }
        }
        try {
            Copy-Item $SingleExe $DeployPath -Force -ErrorAction Stop
            $DeployDir = Split-Path $DeployPath
            Copy-Item $ConfigPath $DeployDir -Force -ErrorAction SilentlyContinue
            Write-Host "Deployed to $DeployPath (with config)" -ForegroundColor Green
        } catch {
            Write-Error "CRITICAL: Failed to copy to $DeployPath. File is likely still locked.`n$($_.Exception.Message)"
            exit 1
        }
    }
}

if ($Publish) {
    $newVersion = Bump-Version
    Write-Host "Publishing Release $newVersion to GitHub..." -ForegroundColor Cyan
    
    # Git operations
    git add .
    git commit -m "v$newVersion"
    git push

    # GH Release
    dotnet publish $RootDir -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$PublishDir\release"
    $ReleaseExe = Join-Path "$PublishDir\release" "MqttAgent.exe"
    gh release create "v$newVersion" $ReleaseExe --title "Release v$newVersion" --notes "Automated release via update.ps1"
}

if ($Install) {
    Write-Host "Registering service and persistence via the agent itself..." -ForegroundColor Cyan
    $TargetExe = if ($Deploy) { $DeployPath } else { $ExePath }
    
    # Use the agent's native install logic
    sudo $TargetExe --install
    # --more-states
    
    Write-Host "Ensuring firewall rule for port $Port..." -ForegroundColor Cyan
    $RuleName = "MQTT Agent"
    sudo powershell -Command "if (Get-NetFirewallRule -DisplayName '$RuleName' -ErrorAction SilentlyContinue) { Remove-NetFirewallRule -DisplayName '$RuleName' }; New-NetFirewallRule -DisplayName '$RuleName' -Direction Inbound -Program '$TargetExe' -Action Allow -LocalPort $Port -Protocol TCP"

    if ($MoreStates) {
        Write-Host "Enabling MoreStates in service configuration..." -ForegroundColor Gray
        sudo sc.exe config $ServiceName binPath= "$TargetExe --service --more-states"
    }
}

if ($Start) {
    Write-Host "Starting MQTT Agent Service via sudo..." -ForegroundColor Cyan
    sudo sc.exe start $ServiceName
    Start-Sleep -Seconds 3
    $TargetExe = if (Test-Path $DeployPath) { $DeployPath } else { $ExePath }
    Write-Host $TargetExe
    $StartArgs = @("-tray", "-token", $Token)
    if ($MoreStates) { $StartArgs += "-more-states" }
    Start-Process $TargetExe -ArgumentList $StartArgs
}

Write-Host "Done!" -ForegroundColor Green
