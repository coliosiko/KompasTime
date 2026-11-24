param(
    [switch]$Uninstall,
    [ValidatePattern('^\d+(,\d+)*$')][string]$Versions
)
$ErrorActionPreference = 'Stop'

$localRoot = Join-Path $env:LOCALAPPDATA 'KompasTime'
$installDir = Join-Path $localRoot 'DocumentPane'
$roamingRoot = Join-Path $env:APPDATA 'KompasTime'
$classes = 'HKCU:\Software\Classes'
$paneId = '{9ECC89A7-C66F-40FC-B012-B2BD3227B37C}'
$libraryId = '{73614771-A0CC-40A2-958B-1E080AB3DCCD}'
$kitRoot = Join-Path $env:APPDATA 'ASCON\KOMPAS-3D'
$minimumVersion = 23
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$settingsKey = 'HKCU:\Software\KompasTime'
$connector = Join-Path $installDir 'KompasTime.Connector.exe'
$plugin = Join-Path $installDir 'KompasTime.Pane.dll'
$regAsm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
$files = @('KompasTime.Pane.dll')
$progIds = @('KompasTime.Menu', 'KompasTime.AddIn', 'KompasTime.DocumentPane',
    'KompasTime.DockPane', 'KompasTime.Pane', 'KompasTime.SidePane', 'KompasTime.TreePane', 'KompasTime.WorkPane')

function Get-InstalledKompasVersions {
    $roots = @('HKLM:\SOFTWARE\ASCON\KOMPAS-3D', 'HKCU:\SOFTWARE\ASCON\KOMPAS-3D')
    $locations = @(
        foreach ($root in $roots) {
            if (Test-Path -LiteralPath $root) {
                foreach ($key in Get-ChildItem -LiteralPath $root) {
                    if ($key.PSChildName -match '^[0-9]+$') { $key.GetValue('InstallPath') }
                }
            }
        }
        $programs = Join-Path $env:ProgramFiles 'ASCON'
        if (Test-Path -LiteralPath $programs) {
            Get-ChildItem -LiteralPath $programs -Directory -Filter 'KOMPAS-3D v*' |
                ForEach-Object { $_.FullName }
        }
    )
    foreach ($location in $locations | Where-Object { $_ } | Sort-Object -Unique) {
        $exe = Join-Path $location 'Bin\KOMPAS.exe'
        if (Test-Path -LiteralPath $exe -PathType Leaf) {
            $major = (Get-Item -LiteralPath $exe).VersionInfo.ProductMajorPart
            if ($major -ge $minimumVersion) { $major }
        }
    }
}

function Update-KompasMenu([string]$KitPath, [bool]$Remove, [string]$DefaultKitPath) {
    $kit = New-Object System.Xml.XmlDocument
    $kit.PreserveWhitespace = $true
    if (Test-Path -LiteralPath $kitPath) {
        $kit.Load($kitPath)
    }
    elseif ($Remove) { return }
    elseif ($DefaultKitPath -and (Test-Path -LiteralPath $DefaultKitPath -PathType Leaf)) {
        $kit.Load($DefaultKitPath)
    }
    else {
        $kit.LoadXml('<?xml version="1.0" encoding="utf-16"?><Kit_Config><Version>1.0</Version><Groups><Group/></Groups></Kit_Config>')
    }
    if ($kit.DocumentElement.Name -ne 'Kit_Config') {
        if ($Remove) { return }
        throw "Unsupported KOMPAS application list format: $kitPath"
    }

    $legacy = @($kit.SelectNodes('/Kit_Config/Groups/Group/Application') | Where-Object {
        ($_.GetAttribute('id') -ne 'KompasTime.Menu' -and $progIds -contains $_.GetAttribute('id')) -or
        ($_.GetAttribute('ocxClassId') -ne 'KompasTime.Menu' -and $progIds -contains $_.GetAttribute('ocxClassId'))
    })
    $nodes = @($kit.SelectNodes('/Kit_Config/Groups/Group/Application[@id="KompasTime.Menu"]'))
    foreach ($entry in $legacy) { $entry.ParentNode.RemoveChild($entry) | Out-Null }
    if ($Remove) {
        if ($legacy.Count + $nodes.Count -eq 0) { return }
        foreach ($entry in $nodes) { $entry.ParentNode.RemoveChild($entry) | Out-Null }
    }
    else {
        $node = $nodes | Select-Object -First 1
        foreach ($duplicate in $nodes | Select-Object -Skip 1) {
            $duplicate.ParentNode.RemoveChild($duplicate) | Out-Null
        }
        if ($null -eq $node) {
            $group = $kit.SelectSingleNode('/Kit_Config/Groups/Group')
            if ($null -eq $group) { throw 'Unsupported KOMPAS application list format.' }
            $node = $kit.CreateElement('Application')
            $group.AppendChild($node) | Out-Null
        }
        $node.SetAttribute('libType', 'lbt_ocx')
        $node.SetAttribute('ocxClassId', 'KompasTime.Menu')
        $node.SetAttribute('id', 'KompasTime.Menu')
        $node.SetAttribute('libName', 'KompasTime')
        $node.SetAttribute('displayName', 'KompasTime')
        $node.SetAttribute('autostart', 'true')
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $kitPath) -Force | Out-Null
    $temp = $kitPath + '.KompasTime.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $kit.Save($temp)
        if (Test-Path -LiteralPath $kitPath) {
            if ($Remove) {
                [IO.File]::Replace($temp, $kitPath, [NullString]::Value)
            }
            else {
                [IO.File]::Replace($temp, $kitPath, ($kitPath + '.KompasTime.bak'))
            }
        }
        else { [IO.File]::Move($temp, $kitPath) }
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
    }
}

function Get-AppDataRemovalTarget([string]$Parent, [string]$Path) {
    if (!(Test-Path -LiteralPath $Path)) { return }
    $target = Get-Item -LiteralPath $Path -Force
    $expected = Join-Path (Resolve-Path -LiteralPath $Parent).ProviderPath 'KompasTime'
    if (!$target.PSIsContainer -or $target.FullName -ne $expected) {
        throw "Unexpected KompasTime data path: $Path"
    }
    $pending = New-Object 'System.Collections.Generic.Stack[System.IO.DirectoryInfo]'
    $pending.Push($target)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Linked directory prevents safe KompasTime removal: $($directory.FullName)"
        }
        foreach ($item in Get-ChildItem -LiteralPath $directory.FullName -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Linked item prevents safe KompasTime removal: $($item.FullName)"
            }
            if ($item.PSIsContainer) { $pending.Push($item) }
        }
    }
    $target.FullName
}

function Uninstall-KompasTime {
    $dataPaths = @(
        Get-AppDataRemovalTarget -Parent $env:LOCALAPPDATA -Path $localRoot
        Get-AppDataRemovalTarget -Parent $env:APPDATA -Path $roamingRoot
    ) | Sort-Object -Unique
    $comKeys = @("$classes\CLSID\$paneId", "$classes\CLSID\$libraryId")
    $comKeys += @($progIds | ForEach-Object { "$classes\$_" })
    if (Test-Path -LiteralPath "$classes\CLSID") {
        $comKeys += @(Get-ChildItem -LiteralPath "$classes\CLSID" | Where-Object {
            $_.GetValue('') -in @('KompasTime.Plugin.WorkPane', 'KompasTime.Plugin.KompasAddIn')
        } | ForEach-Object { $_.PSPath })
    }

    foreach ($path in $dataPaths) {
        Get-Process 'KompasTime', 'KompasTime.Connector' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($path + '\', [StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process
    }
    if (Test-Path -LiteralPath $kitRoot) {
        foreach ($profile in Get-ChildItem -LiteralPath $kitRoot -Directory) {
            if ($profile.Name -notmatch '^[0-9]+$') { continue }
            $kitPath = Join-Path $profile.FullName 'KOMPAS.kit.config'
            Update-KompasMenu -KitPath $kitPath -Remove $true
            $backup = $kitPath + '.KompasTime.bak'
            if (Test-Path -LiteralPath $backup -PathType Leaf) {
                Remove-Item -LiteralPath $backup -Force
            }
            Get-ChildItem -LiteralPath $profile.FullName -Force -File -Filter 'KOMPAS.kit.config.KompasTime.*.tmp' |
                Where-Object { $_.Name -match '^KOMPAS\.kit\.config\.KompasTime\.[0-9a-f]{32}\.tmp$' } |
                ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
        }
    }
    $run = Get-Item -LiteralPath $runKey -ErrorAction SilentlyContinue
    if ($run -and $run.GetValueNames() -contains 'KompasTime') {
        Remove-ItemProperty -LiteralPath $runKey -Name KompasTime
    }
    foreach ($key in ($comKeys + $settingsKey | Sort-Object -Unique)) {
        if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force }
    }
    foreach ($path in $dataPaths) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

try {
    if (![Environment]::Is64BitProcess) {
        throw 'Run the installer in 64-bit Windows PowerShell.'
    }
    if (Get-Process KOMPAS -ErrorAction SilentlyContinue) {
        throw 'Close KOMPAS-3D before installing or uninstalling KompasTime.'
    }

    if ($Uninstall) {
        if ($Versions) { throw 'Uninstall removes KompasTime from all versions. Omit -Versions.' }
        Uninstall-KompasTime
        Write-Host 'KompasTime and its local settings were removed.'
        exit 0
    }

    $targets = if ($Versions) { $Versions.Split(',') | ForEach-Object { [int]$_ } }
        else { Get-InstalledKompasVersions }
    $targets = @($targets | Sort-Object -Unique)
    if ($targets.Count -eq 0) {
        throw 'No KOMPAS-3D v23 or later detected. For a custom installation, pass -Versions with its major version.'
    }
    if ($targets[0] -lt $minimumVersion) { throw 'KOMPAS-3D v23 or later is required.' }

    if (!(Test-Path -LiteralPath $regAsm)) { throw 'Windows x64 and .NET Framework 4.8 are required.' }
    foreach ($file in $files) {
        if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot $file))) {
            throw 'Extract the complete installation archive first.'
        }
    }
    $needsCopy = @($files | Where-Object {
        $installed = Join-Path $installDir $_
        !(Test-Path -LiteralPath $installed) -or
            (Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_)).Hash
    })
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    foreach ($file in $needsCopy) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $installDir -Force
    }

    $regFile = Join-Path $installDir ('registration-' + [guid]::NewGuid().ToString('N') + '.reg')
    try {
        & $regAsm $plugin /nologo /codebase ("/regfile:" + $regFile)
        if ($LASTEXITCODE -ne 0) { throw 'RegAsm could not prepare registration.' }
        $data = [IO.File]::ReadAllText($regFile).Replace('HKEY_CLASSES_ROOT\', 'HKEY_CURRENT_USER\Software\Classes\')
        [IO.File]::WriteAllText($regFile, $data, [Text.Encoding]::Unicode)
        & reg.exe import $regFile /reg:64
        if ($LASTEXITCODE -ne 0) { throw 'Could not register the KompasTime COM library.' }
    }
    finally {
        if (Test-Path -LiteralPath $regFile) { Remove-Item -LiteralPath $regFile }
    }

    New-Item -Path "$classes\CLSID\$libraryId\Kompas_Library" -Force | Out-Null
    Set-Item -LiteralPath "$classes\CLSID\$libraryId\InprocServer32" -Value (Join-Path ([Environment]::SystemDirectory) 'mscoree.dll')
    foreach ($legacyClass in @("$classes\CLSID\$paneId", "$classes\KompasTime.DocumentPane")) {
        if (Test-Path -LiteralPath $legacyClass) {
            Remove-Item -LiteralPath $legacyClass -Recurse
        }
    }
    Get-Process 'KompasTime.Connector' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $connector } | Stop-Process
    foreach ($target in $targets) {
        $defaultKit = Join-Path $env:ProgramData "ASCON\KOMPAS-3D\$target\base.kit.config"
        Update-KompasMenu -KitPath (Join-Path $kitRoot "$target\KOMPAS.kit.config") -Remove $false -DefaultKitPath $defaultKit
        Write-Host "KompasTime configured for KOMPAS-3D v$target."
    }
    $settings = New-Item -Path $settingsKey -Force
    if ($null -eq $settings.GetValue('AutoConnect')) {
        $settings.SetValue('AutoConnect', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
    }
    # Earlier versions used a separate Windows logon watcher.
    $legacyRun = Get-Item -LiteralPath $runKey -ErrorAction SilentlyContinue
    if ($legacyRun -and $legacyRun.GetValue('KompasTime') -eq ('"' + $connector + '" --watch')) {
        Remove-ItemProperty -LiteralPath $runKey -Name KompasTime
    }
    Write-Host 'KompasTime is installed. Start KOMPAS-3D to use the overlay.'
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
