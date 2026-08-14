param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$RuntimeIdentifier,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src/SshWeave/SshWeave.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/publish/$RuntimeIdentifier"
}

# Native AOT 不支持 Windows 与 Linux 之间交叉编译；调用方应在目标操作系统家族运行本脚本。
dotnet publish $projectPath `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $OutputDirectory `
    -p:PublishAot=true

$binaryName = if ($RuntimeIdentifier.StartsWith('win-', [System.StringComparison]::Ordinal)) {
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
