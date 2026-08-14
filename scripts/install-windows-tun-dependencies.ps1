param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$tun2SocksVersion = '2.6.0'
$wintunVersion = '0.14.1'
$wintunArchiveHash = '07C256185D6EE3652E09FA55C0B673E2624B565E02C4B9091C79CA7D2F24EF51'

$payload = switch ($RuntimeIdentifier) {
    'win-x64' {
        @{
            TunArchive = 'tun2socks-windows-amd64.zip'
            TunArchiveHash = '1429E2E3B1EA09052DA2C65E5005538B5730D63DA37E304F4AD6FD2698A66695'
            TunExecutable = 'tun2socks-windows-amd64.exe'
            WintunArchitecture = 'amd64'
        }
    }
    'win-arm64' {
        @{
            TunArchive = 'tun2socks-windows-arm64.zip'
            TunArchiveHash = 'E7C71F89991F9B850817E6B441E568C370292F8AEA4FA9BDF70D099DA7991ECA'
            TunExecutable = 'tun2socks-windows-arm64.exe'
            WintunArchitecture = 'arm64'
        }
    }
}

function Assert-FileHash {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedHash
    )

    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    if (-not $actualHash.Equals($ExpectedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "下载文件 SHA-256 不匹配：$Path`n预期：$ExpectedHash`n实际：$actualHash"
    }
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryDirectory = Join-Path $temporaryRoot "sshweave-tun-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null

try {
    $tunArchivePath = Join-Path $temporaryDirectory $payload.TunArchive
    $wintunArchivePath = Join-Path $temporaryDirectory "wintun-$wintunVersion.zip"
    $tunUrl = "https://github.com/xjasonlyu/tun2socks/releases/download/v$tun2SocksVersion/$($payload.TunArchive)"
    $wintunUrl = "https://www.wintun.net/builds/wintun-$wintunVersion.zip"

    Invoke-WebRequest -Uri $tunUrl -OutFile $tunArchivePath
    Invoke-WebRequest -Uri $wintunUrl -OutFile $wintunArchivePath
    Assert-FileHash -Path $tunArchivePath -ExpectedHash $payload.TunArchiveHash
    Assert-FileHash -Path $wintunArchivePath -ExpectedHash $wintunArchiveHash

    $tunExtracted = Join-Path $temporaryDirectory 'tun2socks'
    $wintunExtracted = Join-Path $temporaryDirectory 'wintun'
    Expand-Archive -LiteralPath $tunArchivePath -DestinationPath $tunExtracted
    Expand-Archive -LiteralPath $wintunArchivePath -DestinationPath $wintunExtracted

    Copy-Item -LiteralPath (Join-Path $tunExtracted $payload.TunExecutable) `
        -Destination (Join-Path $resolvedOutput 'tun2socks.exe')
    Copy-Item -LiteralPath (Join-Path $wintunExtracted "wintun/bin/$($payload.WintunArchitecture)/wintun.dll") `
        -Destination (Join-Path $resolvedOutput 'wintun.dll')
    Copy-Item -LiteralPath (Join-Path $wintunExtracted 'wintun/LICENSE.txt') `
        -Destination (Join-Path $resolvedOutput 'WINTUN-LICENSE.txt')

    # 发布输出同时报告最终载荷哈希，供安装包清单和现场复核直接使用。
    Get-FileHash -Algorithm SHA256 -LiteralPath `
        (Join-Path $resolvedOutput 'tun2socks.exe'), `
        (Join-Path $resolvedOutput 'wintun.dll') |
        ForEach-Object { Write-Output "已安装：$($_.Path)`nSHA-256：$($_.Hash)" }
}
finally {
    $resolvedTemporary = [System.IO.Path]::GetFullPath($temporaryDirectory)
    $temporaryPrefix = $temporaryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) `
        + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTemporary.StartsWith($temporaryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理临时目录范围外的路径：$resolvedTemporary"
    }
    Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force -ErrorAction SilentlyContinue
}
