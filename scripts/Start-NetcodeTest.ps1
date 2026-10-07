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
    [switch]$NoRelay,
    [switch]$NoWindowLayout,
    [int]$WindowAppearTimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace NetcodeTest -Name WindowPlacement -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetWindowPos(System.IntPtr hWnd, System.IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int command);
"@
[NetcodeTest.WindowPlacement]::SetProcessDPIAware() | Out-Null

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

function Get-HalfScreenArea([string]$side) {
    $workArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $halfWidth = [int][Math]::Floor($workArea.Width / 2)
    $left = if ($side -eq "Left") { $workArea.Left } else { $workArea.Left + $halfWidth }
    return @{ X = $left; Y = $workArea.Top; Width = $halfWidth; Height = $workArea.Height }
}

function Get-WindowedLaunchArguments([string]$side) {
    if ($NoWindowLayout) { return @() }
    $area = Get-HalfScreenArea $side
    return @("-screen-fullscreen", "0", "-screen-width", $area.Width, "-screen-height", $area.Height)
}

function Set-ClientWindowPrefs {
    if ($NoWindowLayout) { return }

    $clientConfigFolder = Join-Path $ClientDataFolder "Config"
    $clientPrefsPath = Join-Path $clientConfigFolder "Prefs.xml"
    $area = Get-HalfScreenArea "Right"

    if (-not (Test-Path $clientPrefsPath)) {
        New-Item -ItemType Directory -Force $clientConfigFolder | Out-Null
        Set-Content -Path $clientPrefsPath -Encoding UTF8 -Value "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<PrefsData />"
    }

    $prefsDocument = New-Object System.Xml.XmlDocument
    $prefsDocument.Load($clientPrefsPath)
    $screenSettings = [ordered]@{ screenWidth = $area.Width; screenHeight = $area.Height; fullscreen = "False" }
    foreach ($settingName in $screenSettings.Keys) {
        $settingNode = $prefsDocument.DocumentElement.SelectSingleNode($settingName)
        if ($null -eq $settingNode) {
            $settingNode = $prefsDocument.CreateElement($settingName)
            $prefsDocument.DocumentElement.AppendChild($settingNode) | Out-Null
        }
        $settingNode.InnerText = [string]$screenSettings[$settingName]
    }
    $prefsDocument.Save($clientPrefsPath)
}

function Wait-ForMainWindow([System.Diagnostics.Process]$process) {
    $deadline = (Get-Date).AddSeconds($WindowAppearTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { return [IntPtr]::Zero }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { return $process.MainWindowHandle }
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}

function Move-WindowToHalfScreen([System.Diagnostics.Process]$process, [string]$side) {
    if ($NoWindowLayout) { return }

    $windowHandle = Wait-ForMainWindow $process
    if ($windowHandle -eq [IntPtr]::Zero) {
        Write-Host "Couldn't find the $side window to arrange it."
        return
    }

    $restoreWindowCommand = 9
    $noZOrderChangeFlag = 0x0004
    $area = Get-HalfScreenArea $side
    [NetcodeTest.WindowPlacement]::ShowWindow($windowHandle, $restoreWindowCommand) | Out-Null
    [NetcodeTest.WindowPlacement]::SetWindowPos($windowHandle, [IntPtr]::Zero, $area.X, $area.Y, $area.Width, $area.Height, $noZOrderChangeFlag) | Out-Null
}

function Start-HostInstance {
    $hostArguments = @('"-username=Host"') + (Get-WindowedLaunchArguments "Left")
    if ($HostSave -ne "") {
        $hostSavePath = Resolve-HostSavePath
        $hostArguments += "`"-mphostreplay=$hostSavePath`""
        $hostArguments += '"-mphostauto"'
    }

    $hostProcess = Start-Process $RimWorldExe -ArgumentList $hostArguments -PassThru
    Move-WindowToHalfScreen $hostProcess "Left"
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
    Set-ClientWindowPrefs

    $connectPort = if ($NoRelay) { $HostPort } else { $RelayPort }
    $clientArguments = @(
        "`"-username=$ClientUsername`"",
        "`"-savedatafolder=$ClientDataFolder`""
    ) + (Get-WindowedLaunchArguments "Right")
    if (-not $NoAutoConnect) { $clientArguments += "`"-connect=127.0.0.1:$connectPort`"" }

    $clientProcess = Start-Process $RimWorldExe -ArgumentList $clientArguments -PassThru
    Move-WindowToHalfScreen $clientProcess "Right"
    Write-Host "Client instance started. It joins 127.0.0.1:$connectPort."
}

if ($HostSave -ne "" -and -not $NoHostInstance) { Resolve-HostSavePath | Out-Null }
if (-not $NoRelay) { Start-Relay }
if (-not $NoHostInstance) { Start-HostInstance }
$clientJoinsAutomatically = -not $NoClientInstance -and -not $NoAutoConnect
if ($clientJoinsAutomatically -and (Test-HostPortBound)) { Write-Host "Port $HostPort is already in use; assuming the host is running." }
elseif ($clientJoinsAutomatically) { Wait-ForHostServer }
if (-not $NoClientInstance) { Start-ClientInstance }
