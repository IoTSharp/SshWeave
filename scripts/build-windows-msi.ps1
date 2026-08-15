[CmdletBinding()]
param(
    [string]$Version = '0.3.5',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$installerRoot = Join-Path $repositoryRoot 'artifacts\installer'
$desktopPublish = Join-Path $installerRoot 'publish-desktop'
$cliPublish = Join-Path $installerRoot 'publish-cli'
$payload = Join-Path $installerRoot 'payload'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\packages'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

New-Item -ItemType Directory -Path $desktopPublish -Force | Out-Null
New-Item -ItemType Directory -Path $cliPublish -Force | Out-Null
New-Item -ItemType Directory -Path $payload -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# 桌面端和 CLI 分别进行 Native AOT 发布，再汇总为 MSI 的显式载荷清单。
& (Join-Path $PSScriptRoot 'publish.ps1') `
    -RuntimeIdentifier win-x64 `
    -Desktop `
    -OutputDirectory $desktopPublish
& (Join-Path $PSScriptRoot 'publish.ps1') `
    -RuntimeIdentifier win-x64 `
    -OutputDirectory $cliPublish

$desktopExecutable = Join-Path $desktopPublish 'sshweave-desktop.exe'
$desktopImage = [System.Text.Encoding]::UTF8.GetString(
    [System.IO.File]::ReadAllBytes($desktopExecutable))
if (-not $desktopImage.Contains('requestedExecutionLevel level="requireAdministrator"', [System.StringComparison]::Ordinal)) {
    throw '桌面 Native AOT 产物未嵌入 requireAdministrator 清单，拒绝生成无法启动透明 TCP 的 MSI。'
}

$payloadSources = @{
    'sshweave-desktop.exe' = $desktopExecutable
    'sshweave.exe' = Join-Path $cliPublish 'sshweave.exe'
    'tun2socks.exe' = Join-Path $desktopPublish 'tun2socks.exe'
    'wintun.dll' = Join-Path $desktopPublish 'wintun.dll'
    'LICENSE' = Join-Path $desktopPublish 'LICENSE'
    'THIRD-PARTY-NOTICES.md' = Join-Path $desktopPublish 'THIRD-PARTY-NOTICES.md'
    'WINTUN-LICENSE.txt' = Join-Path $desktopPublish 'WINTUN-LICENSE.txt'
    'SshWeave.ico' = Join-Path $desktopPublish 'SshWeave.ico'
    'SshWeave.Connection.ico' = Join-Path $desktopPublish 'SshWeave.Connection.ico'
}
foreach ($entry in $payloadSources.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "MSI 缺少发布载荷：$($entry.Value)"
    }
    Copy-Item -LiteralPath $entry.Value -Destination (Join-Path $payload $entry.Key) -Force
}

$project = Join-Path $repositoryRoot 'installer\SshWeave.Installer.wixproj'
dotnet build $project `
    --configuration Release `
    --property:ProductVersion=$Version `
    --property:PublishDirectory=$payload `
    --property:OutputPath=$OutputDirectory `
    --target:Rebuild
if ($LASTEXITCODE -ne 0) {
    throw "WiX MSI 构建失败，dotnet 退出码：$LASTEXITCODE"
}

$msiPath = Join-Path $OutputDirectory "SshWeave-$Version-win-x64.msi"
if (-not (Test-Path -LiteralPath $msiPath -PathType Leaf)) {
    throw "WiX 构建成功但没有找到预期 MSI：$msiPath"
}

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $msiPath
$file = Get-Item -LiteralPath $msiPath
Write-Output "已生成真实 MSI：$msiPath"
Write-Output "大小：$($file.Length) 字节"
Write-Output "SHA-256：$($hash.Hash)"
