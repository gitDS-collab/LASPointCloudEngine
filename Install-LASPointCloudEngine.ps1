[CmdletBinding()]
param(
    [ValidateSet('2027')]
    [string]$RevitVersion = '2027',

    # Reserved for automated validation. Normal installs always use the current user's AppData folder.
    [string]$ApplicationDataRoot
)

$ErrorActionPreference = 'Stop'

function Get-AvailablePath {
    param([Parameter(Mandatory = $true)][string]$BasePath)

    $candidate = $BasePath
    $suffix = 1
    while (Test-Path -LiteralPath $candidate) {
        $candidate = "$BasePath-$suffix"
        $suffix++
    }

    return $candidate
}

function Test-Payload {
    param([Parameter(Mandatory = $true)][string]$Folder)

    foreach ($fileName in @('LASPointCloudEngine.dll', 'LASPointCloudEngine.deps.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Folder $fileName) -PathType Leaf)) {
            throw "The release payload is incomplete: $fileName is missing. Extract the whole ZIP and run this script again."
        }
    }

    $iconsPath = Join-Path $Folder 'Resources\Icons'
    $icons = @(Get-ChildItem -LiteralPath $iconsPath -Filter '*.png' -File -ErrorAction SilentlyContinue)
    if ($icons.Count -ne 8) {
        throw "The release payload is incomplete: expected 8 ribbon icon PNG files under Resources\\Icons, found $($icons.Count)."
    }
}

function Test-PackageChecksums {
    param(
        [Parameter(Mandatory = $true)][string]$ReleaseFolder,
        [Parameter(Mandatory = $true)][string]$ChecksumsPath
    )

    if (-not (Test-Path -LiteralPath $ChecksumsPath -PathType Leaf)) {
        throw 'The checksums.sha256 file is missing. Extract the full ZIP before running the installer.'
    }

    $checkedCount = 0
    foreach ($line in Get-Content -LiteralPath $ChecksumsPath) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line -notmatch '^(?<hash>[0-9a-fA-F]{64})\s{2}(?<relative>payload/.+)$') {
            throw "Invalid checksum entry: $line"
        }

        $relativePath = $Matches.relative.Replace('/', '\')
        $payloadFile = Join-Path $ReleaseFolder $relativePath
        if (-not (Test-Path -LiteralPath $payloadFile -PathType Leaf)) {
            throw "A checksummed payload file is missing: $relativePath"
        }

        $actualHash = (Get-FileHash -LiteralPath $payloadFile -Algorithm SHA256).Hash
        if ($actualHash -ne $Matches.hash.ToUpperInvariant()) {
            throw "Checksum verification failed for: $relativePath"
        }

        $checkedCount++
    }

    if ($checkedCount -lt 11) {
        throw 'The checksum list is incomplete.'
    }
}

$revitProcesses = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
if ($revitProcesses.Count -gt 0) {
    throw "Revit is running. Close every Revit window, wait for Revit.exe to exit, then run this installer again."
}

$releaseRoot = $PSScriptRoot
$payloadRoot = Join-Path $releaseRoot 'payload'
$templatePath = Join-Path $payloadRoot 'LASPointCloudEngine.addin.template'
$checksumsPath = Join-Path $releaseRoot 'checksums.sha256'
$applicationData = if ($PSBoundParameters.ContainsKey('ApplicationDataRoot')) {
    $ApplicationDataRoot
}
else {
    [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
}
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$revitApiPath = Join-Path $programFiles "Autodesk\Revit $RevitVersion\RevitAPI.dll"
$addinRoot = Join-Path $applicationData "Autodesk\Revit\Addins\$RevitVersion"
$versionFolderName = 'LASPointCloudEngine-v7-icons'
$targetFolder = Join-Path $addinRoot $versionFolderName
$targetDll = Join-Path $targetFolder 'LASPointCloudEngine.dll'
$activeManifest = Join-Path $addinRoot 'LASPointCloudEngine.addin'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stagingFolder = $null
$targetBackup = $null
$manifestBackup = $null

if (-not (Test-Path -LiteralPath $payloadRoot -PathType Container)) {
    throw "The payload folder is missing. Extract the full ZIP before running the installer."
}
if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) {
    throw "The add-in manifest template is missing. Extract the full ZIP before running the installer."
}
if (-not (Test-Path -LiteralPath $revitApiPath -PathType Leaf)) {
    throw "Revit $RevitVersion was not found at the expected location: $revitApiPath"
}

Test-PackageChecksums -ReleaseFolder $releaseRoot -ChecksumsPath $checksumsPath
Test-Payload -Folder $payloadRoot
New-Item -ItemType Directory -Path $addinRoot -Force | Out-Null

try {
    # Build a complete staged copy first. The currently installed add-in remains untouched until this succeeds.
    $stagingFolder = Get-AvailablePath -BasePath (Join-Path $addinRoot "$versionFolderName.pending-$timestamp")
    New-Item -ItemType Directory -Path $stagingFolder -ErrorAction Stop | Out-Null
    foreach ($fileName in @('LASPointCloudEngine.dll', 'LASPointCloudEngine.deps.json')) {
        Copy-Item -LiteralPath (Join-Path $payloadRoot $fileName) -Destination $stagingFolder -ErrorAction Stop
    }
    Copy-Item -LiteralPath (Join-Path $payloadRoot 'Resources') -Destination (Join-Path $stagingFolder 'Resources') -Recurse -ErrorAction Stop
    Test-Payload -Folder $stagingFolder

    # Preserve any same-version installation instead of deleting it.
    if (Test-Path -LiteralPath $targetFolder) {
        $targetBackup = Get-AvailablePath -BasePath "$targetFolder.backup-$timestamp"
        Move-Item -LiteralPath $targetFolder -Destination $targetBackup -ErrorAction Stop
    }
    Move-Item -LiteralPath $stagingFolder -Destination $targetFolder -ErrorAction Stop
    $stagingFolder = $null

    $template = [System.IO.File]::ReadAllText($templatePath, [System.Text.Encoding]::UTF8)
    if (-not $template.Contains('__ASSEMBLY_PATH__')) {
        throw 'The add-in manifest template does not contain the assembly placeholder.'
    }
    $escapedDllPath = [System.Security.SecurityElement]::Escape($targetDll)
    $manifestContent = $template.Replace('__ASSEMBLY_PATH__', $escapedDllPath)
    $pendingManifest = Get-AvailablePath -BasePath (Join-Path $addinRoot ".LASPointCloudEngine.pending-$timestamp.addin")
    [System.IO.File]::WriteAllText($pendingManifest, $manifestContent, [System.Text.UTF8Encoding]::new($false))

    [xml]$pendingXml = Get-Content -LiteralPath $pendingManifest
    $pendingEntries = @($pendingXml.RevitAddIns.AddIn)
    if ($pendingEntries.Count -ne 5 -or @($pendingEntries | Where-Object { $_.Assembly -ne $targetDll }).Count -ne 0) {
        throw 'The generated add-in manifest did not validate.'
    }

    if (Test-Path -LiteralPath $activeManifest) {
        $manifestBackup = Get-AvailablePath -BasePath "$activeManifest.backup-$timestamp"
        Copy-Item -LiteralPath $activeManifest -Destination $manifestBackup -ErrorAction Stop
    }
    Move-Item -LiteralPath $pendingManifest -Destination $activeManifest -Force -ErrorAction Stop

    [xml]$installedXml = Get-Content -LiteralPath $activeManifest
    $installedEntries = @($installedXml.RevitAddIns.AddIn)
    if ($installedEntries.Count -ne 5 -or @($installedEntries | Where-Object { $_.Assembly -ne $targetDll }).Count -ne 0) {
        throw 'The active add-in manifest did not validate after installation.'
    }
    Test-Payload -Folder $targetFolder

    Write-Host ''
    Write-Host 'LAS Point Cloud Engine V7 was installed for Revit 2027.' -ForegroundColor Green
    Write-Host "Add-in folder: $targetFolder"
    Write-Host "Manifest: $activeManifest"
    if ($manifestBackup) {
        Write-Host "Previous manifest backup: $manifestBackup"
    }
    if ($targetBackup) {
        Write-Host "Previous same-version folder backup: $targetBackup"
    }
    Write-Host 'Start Revit 2027 and open the LAS Point Cloud tab.'
}
catch {
    if ($stagingFolder -and (Test-Path -LiteralPath $stagingFolder)) {
        Write-Warning "Installation stopped before activation. The staged files remain at: $stagingFolder"
    }
    if ($targetBackup -and -not (Test-Path -LiteralPath $targetFolder) -and (Test-Path -LiteralPath $targetBackup)) {
        Move-Item -LiteralPath $targetBackup -Destination $targetFolder -ErrorAction SilentlyContinue
        Write-Warning 'The previous same-version add-in folder was restored.'
    }
    throw
}
