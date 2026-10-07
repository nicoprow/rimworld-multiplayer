param(
    [string]$RimWorldExe = "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64.exe",
    [int]$HostPort = 30502,
    [int]$RelayPort = 30503,
    [double]$Delay = 250,
    [double]$Jitter = 50,
    [double]$Loss = 2.5,
    [double]$BurstInterval = 0,
    [double]$BurstLength = 0,
    [Nullable[int]]$Seed = $null,
    [string]$ClientUsername = "Client",
    [string]$ClientDataFolder = (Join-Path $env:USERPROFILE "RimWorldMpTestClient"),
    [string]$HostSave = "",
    [int]$HostStartTimeoutSeconds = 300,
    [int]$ClientExtraDelaySeconds = 0,
    [switch]$NoHostInstance,
    [switch]$NoClientInstance,
    [switch]$NoAutoConnect,
    [switch]$NoRelay
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$conditionerProject = Join-Path $repositoryRoot "Source\NetworkConditioner\NetworkConditioner.csproj"
$mainDataFolder = Join-Path $env:USERPROFILE "AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios"
$mainConfigFolder = Join-Path $mainDataFolder "Config"
$multiplayerSavesFolder = Join-Path $mainDataFolder "MpReplays"

function Format-Invariant([double]$value) {
    return $value.ToString([System.Globalization.CultureInfo]::InvariantCulture)
}

function Copy-ModListToClientDataFolder {
    $clientConfigFolder = Join-Path $ClientDataFolder "Config"
    $clientModsConfig = Join-Path $clientConfigFolder "ModsConfig.xml"
    $mainModsConfig = Join-Path $mainConfigFolder "ModsConfig.xml"

    if (Test-Path $clientModsConfig) { return }
    if (-not (Test-Path $mainModsConfig)) { return }

    New-Item -ItemType Directory -Force $clientConfigFolder | Out-Null
    Copy-Item $mainModsConfig $clientModsConfig
    Write-Host "Copied the mod list to $clientModsConfig"
}

function Start-Relay {
    $relayArguments = @(
        "--listen", $RelayPort,
        "--target", "127.0.0.1:$HostPort",
        "--delay", (Format-Invariant $Delay),
        "--jitter", (Format-Invariant $Jitter),
        "--loss", (Format-Invariant $Loss),
        "--burst-interval", (Format-Invariant $BurstInterval),
        "--burst-length", (Format-Invariant $BurstLength)
    )
    if ($null -ne $Seed) { $relayArguments += @("--seed", $Seed) }

    $relayCommand = "dotnet run --project `"$conditionerProject`" -- $($relayArguments -join ' ')"
    Start-Process powershell -ArgumentList "-NoExit", "-Command", $relayCommand | Out-Null
    Write-Host "Relay: 127.0.0.1:$RelayPort -> 127.0.0.1:$HostPort"
}

function Resolve-HostSavePath {
    $saveNameWithExtension = if ([System.IO.Path]::GetExtension($HostSave) -eq "") { "$HostSave.zip" } else { $HostSave }
    $candidatePaths = @($saveNameWithExtension, (Join-Path $multiplayerSavesFolder $saveNameWithExtension))
    $existingPath = $candidatePaths | Where-Object { Test-Path $_ -PathType Leaf } | Select-Object -First 1

    if ($null -eq $existingPath) {
        throw "Multiplayer save '$HostSave' not found. Pass a .zip path or the name of a save in $multiplayerSavesFolder. Singleplayer .rws saves can't be used; host one once and save it as a multiplayer save."
    }
    if ([System.IO.Path]::GetExtension($existingPath) -ne ".zip") {
        throw "'$existingPath' is not a multiplayer save (.zip)."
    }

    return (Resolve-Path $existingPath).Path
}

function Start-HostInstance {
    $hostArguments = @('"-username=Host"')
    if ($HostSave -ne "") {
        $hostSavePath = Resolve-HostSavePath
        $hostArguments += "`"-mphostreplay=$hostSavePath`""
        $hostArguments += '"-mphostauto"'
    }

    Start-Process $RimWorldExe -ArgumentList $hostArguments | Out-Null
    if ($HostSave -ne "") {
        Write-Host "Host instance started. It hosts $hostSavePath automatically on the port saved in the mod settings."
    } else {
        Write-Host "Host instance started. Host a game on port $HostPort."
    }
}

function Test-HostPortBound {
    $boundEndpoints = Get-NetUDPEndpoint -LocalPort $HostPort -ErrorAction SilentlyContinue
    return $null -ne $boundEndpoints
}

function Wait-ForHostServer {
    Write-Host "Waiting for the host to listen on port $HostPort..."
    $deadline = (Get-Date).AddSeconds($HostStartTimeoutSeconds)

    while (-not (Test-HostPortBound)) {
        if ((Get-Date) -gt $deadline) {
            throw "The host didn't start listening on port $HostPort within $HostStartTimeoutSeconds seconds."
        }
        Start-Sleep -Seconds 2
    }

    Write-Host "Host is listening."
    if ($ClientExtraDelaySeconds -gt 0) { Start-Sleep -Seconds $ClientExtraDelaySeconds }
}

function Start-ClientInstance {
    Copy-ModListToClientDataFolder

    $connectPort = if ($NoRelay) { $HostPort } else { $RelayPort }
    $clientArguments = @(
        "`"-username=$ClientUsername`"",
        "`"-savedatafolder=$ClientDataFolder`""
    )
    if (-not $NoAutoConnect) { $clientArguments += "`"-connect=127.0.0.1:$connectPort`"" }

    Start-Process $RimWorldExe -ArgumentList $clientArguments | Out-Null
    Write-Host "Client instance started. It joins 127.0.0.1:$connectPort."
}

if ($HostSave -ne "" -and -not $NoHostInstance) { Resolve-HostSavePath | Out-Null }
if (-not $NoRelay) { Start-Relay }
if (-not $NoHostInstance) { Start-HostInstance }
$clientJoinsAutomatically = -not $NoClientInstance -and -not $NoAutoConnect
if ($clientJoinsAutomatically -and (Test-HostPortBound)) { Write-Host "Port $HostPort is already in use; assuming the host is running." }
elseif ($clientJoinsAutomatically) { Wait-ForHostServer }
if (-not $NoClientInstance) { Start-ClientInstance }
