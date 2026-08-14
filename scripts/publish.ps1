param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$RuntimeIdentifier,

    [string]$OutputDirectory,

    [switch]$Desktop
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ($Desktop -and -not $RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::Ordinal)) {
    throw '当前桌面宿主只支持 Windows RID。Linux 请继续发布 CLI。'
}

$projectPath = if ($Desktop) {
    Join-Path $repositoryRoot 'src/SshWeave.Desktop.Windows/SshWeave.Desktop.Windows.csproj'
} else {
    Join-Path $repositoryRoot 'src/SshWeave/SshWeave.csproj'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $suffix = if ($Desktop) { '-desktop' } else { '' }
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/publish/$RuntimeIdentifier$suffix"
}

# Native AOT 不支持 Windows 与 Linux 之间交叉编译；调用方应在目标操作系统家族运行本脚本。
dotnet publish $projectPath `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $OutputDirectory `
    -p:PublishAot=true

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination $OutputDirectory -Force
if ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::Ordinal)) {
    & (Join-Path $PSScriptRoot 'install-windows-tun-dependencies.ps1') `
        -RuntimeIdentifier $RuntimeIdentifier `
        -OutputDirectory $OutputDirectory
}

$binaryName = if ($Desktop) {
    'sshweave-desktop.exe'
} elseif ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::Ordinal)) {
    'sshweave.exe'
} else {
    'sshweave'
}
$binaryPath = Join-Path $OutputDirectory $binaryName
if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) {
    throw "发布成功但没有找到预期二进制：$binaryPath"
}

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $binaryPath
Write-Output "已发布：$binaryPath"
Write-Output "SHA-256：$($hash.Hash)"
