#Requires -Version 7.0

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [uri]$CoordinatorUrl,

    [Parameter(Mandatory)]
    [hashtable]$PathMappings,

    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$WorkerId = ((($env:COMPUTERNAME).ToLowerInvariant()) -replace '[^a-z0-9_.-]', '-'),

    [string]$DisplayName = $env:COMPUTERNAME,

    [ValidateSet('PersonalDesktop', 'DedicatedRenderNode')]
    [string]$Mode = 'PersonalDesktop',

    [string]$HandBrakePath = '',

    [string[]]$Capabilities = @('handbrake', 'encode:x264', 'encode:x265'),

    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'BackBurner\Worker'),

    [switch]$ReplaceConfiguration,
    [switch]$NoStartAtLogin,
    [switch]$DoNotStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-HandBrakeCli {
    param([string]$RequestedPath)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates.Add($RequestedPath)
    }

    $command = Get-Command HandBrakeCLI.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $candidates.Add($command.Source)
    }

    $candidates.Add((Join-Path $env:ProgramFiles 'HandBrake\HandBrakeCLI.exe'))
    if (${env:ProgramFiles(x86)}) {
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'HandBrake\HandBrakeCLI.exe'))
    }

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate) -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $resolved = (Resolve-Path -LiteralPath $candidate).Path
        $versionOutput = & $resolved --version 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "HandBrakeCLI failed its version check at '$resolved'."
        }
        if (-not ($versionOutput | Select-Object -First 1)) {
            throw "HandBrakeCLI returned no version information at '$resolved'."
        }
        return $resolved
    }

    throw 'HandBrakeCLI.exe was not found. Download the Windows CLI from handbrake.fr, extract it locally, and rerun with -HandBrakePath.'
}

if (-not $IsWindows) {
    throw 'This installer is for an interactive Windows worker. Use the CLI host and systemd on Linux.'
}
if ($CoordinatorUrl.Scheme -notin @('http', 'https')) {
    throw 'CoordinatorUrl must use HTTP or HTTPS.'
}
if ([string]::IsNullOrWhiteSpace($DisplayName)) {
    throw 'DisplayName cannot be empty.'
}
if ($PathMappings.Count -eq 0) {
    throw 'At least one logical path mapping is required.'
}

$normalizedMappings = [ordered]@{}
foreach ($entry in $PathMappings.GetEnumerator()) {
    $logicalRoot = [string]$entry.Key
    $physicalRoot = [string]$entry.Value
    if ($logicalRoot -notmatch '^[A-Za-z0-9_.-]+$') {
        throw "Logical root '$logicalRoot' may contain only letters, digits, dot, dash, and underscore."
    }
    if (-not (Test-Path -LiteralPath $physicalRoot -PathType Container)) {
        throw "Path mapping '$logicalRoot' is not currently reachable as a directory. Fix its local SMB mapping or credentials first."
    }
    $normalizedMappings[$logicalRoot] = (Resolve-Path -LiteralPath $physicalRoot).Path
}

$normalizedCapabilities = @(
    $Capabilities |
        ForEach-Object { $_.Trim().ToLowerInvariant() } |
        Where-Object { $_ } |
        Sort-Object -Unique
)
if ('handbrake' -notin $normalizedCapabilities) {
    throw "Capabilities must include 'handbrake'."
}

$resolvedHandBrake = Resolve-HandBrakeCli -RequestedPath $HandBrakePath
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw 'The .NET 10 SDK is required to build this checkout. Install it and rerun.'
}
$sdks = & $dotnet.Source --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($sdks -match '^10\.')) {
    throw 'The .NET 10 SDK is required to build this checkout.'
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workerProject = Join-Path $repositoryRoot 'src\BackBurner.Worker.Windows\BackBurner.Worker.Windows.csproj'
if (-not (Test-Path -LiteralPath $workerProject -PathType Leaf)) {
    throw "The Windows worker project was not found beneath '$repositoryRoot'."
}

$revision = ''
$git = Get-Command git -ErrorAction SilentlyContinue
if ($null -ne $git) {
    $revision = (& $git.Source -C $repositoryRoot rev-parse --short=12 HEAD 2>$null | Select-Object -First 1)
}
if ([string]::IsNullOrWhiteSpace($revision)) {
    $revision = Get-Date -Format 'yyyyMMdd-HHmmss'
}
$revision = $revision -replace '[^A-Za-z0-9_.-]', '-'

$releaseRoot = Join-Path $InstallRoot 'releases'
$releaseDirectory = Join-Path $releaseRoot $revision
$configurationPath = Join-Path $InstallRoot 'worker.local.json'
$executablePath = Join-Path $releaseDirectory 'BackBurner.Worker.Windows.exe'
$startupDirectory = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startupDirectory 'BackBurner Worker.lnk'

if ((Test-Path -LiteralPath $configurationPath) -and -not $ReplaceConfiguration) {
    throw "A local configuration already exists at '$configurationPath'. Rerun with -ReplaceConfiguration only after reviewing the new values."
}

if (-not $PSCmdlet.ShouldProcess($InstallRoot, "Publish revision $revision, write local configuration, and register the worker")) {
    return
}

if ((Get-Process -Name 'BackBurner.Worker.Windows' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'A BackBurner Windows worker is already running. Exit it from the notification-area menu before installing or upgrading.'
}

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $releaseDirectory)) {
    $stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("backburner-worker-" + [guid]::NewGuid().ToString('N'))
    try {
        & $dotnet.Source publish $workerProject -c Release -r win-x64 --self-contained true -o $stagingRoot
        if ($LASTEXITCODE -ne 0) {
            throw 'dotnet publish failed.'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $stagingRoot 'BackBurner.Worker.Windows.exe') -PathType Leaf)) {
            throw 'The published worker executable was not produced.'
        }
        Move-Item -LiteralPath $stagingRoot -Destination $releaseDirectory
    }
    finally {
        if (Test-Path -LiteralPath $stagingRoot) {
            $resolvedStaging = [System.IO.Path]::GetFullPath($stagingRoot)
            $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
            if (-not $resolvedStaging.StartsWith($resolvedTemporaryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to clean unexpected staging path '$resolvedStaging'."
            }
            Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
        }
    }
}

$gpuNames = @(
    Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Name -Unique
)
$computerSystem = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
$ramGiB = if ($null -ne $computerSystem) {
    [math]::Round(([double]$computerSystem.TotalPhysicalMemory / 1GB), 1).ToString([Globalization.CultureInfo]::InvariantCulture) + ' GiB'
} else {
    'unknown'
}

$configuration = [ordered]@{
    coordinatorUrl = $CoordinatorUrl.AbsoluteUri.TrimEnd('/')
    workerId = $WorkerId
    displayName = $DisplayName
    workerApiKey = ''
    handBrakePath = $resolvedHandBrake
    mode = $Mode
    pollIntervalSeconds = 5
    idleThresholdSeconds = 900
    humanActiveGraceSeconds = 30
    quietWindowSeconds = 900
    preflightSeconds = 30
    detectWindowsHumanIdle = ($Mode -eq 'PersonalDesktop')
    detectCodexProcessActivity = ($Mode -eq 'PersonalDesktop')
    codexCpuBusyPercent = 1
    systemCpuBusyPercent = 20
    gameWorkerLeaseFile = $null
    gameWorkerQueueFile = $null
    codyWorkerBrokerPath = '/usr/local/bin/cody-workerctl'
    codyWorkerProfile = 'cpu'
    codyWorkerLeaseTtlSeconds = 60
    codyWorkerRenewSeconds = 20
    capabilities = $normalizedCapabilities
    inhibitFiles = @()
    inhibitDirectories = @('%LOCALAPPDATA%\BackBurner\inhibits')
    profile = [ordered]@{
        gpu = if ($gpuNames.Count -gt 0) { $gpuNames -join '; ' } else { 'unknown' }
        ram = $ramGiB
    }
    paths = $normalizedMappings
}

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
$temporaryConfiguration = Join-Path $InstallRoot ('.worker.local.' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [System.IO.File]::WriteAllText(
        $temporaryConfiguration,
        (($configuration | ConvertTo-Json -Depth 8) + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryConfiguration -Destination $configurationPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryConfiguration) {
        Remove-Item -LiteralPath $temporaryConfiguration -Force
    }
}

if (-not $NoStartAtLogin) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $executablePath
    $shortcut.Arguments = '"' + $configurationPath + '"'
    $shortcut.WorkingDirectory = $releaseDirectory
    $shortcut.IconLocation = "$executablePath,0"
    $shortcut.Description = 'BackBurner idle media worker'
    $shortcut.Save()
}

if (-not $DoNotStart) {
    Start-Process -FilePath $executablePath -ArgumentList ('"{0}"' -f $configurationPath) -WorkingDirectory $releaseDirectory
}

[pscustomobject]@{
    WorkerId = $WorkerId
    Mode = $Mode
    Release = $revision
    Executable = $executablePath
    Configuration = $configurationPath
    StartsAtLogin = -not $NoStartAtLogin
    StartedNow = -not $DoNotStart
}
