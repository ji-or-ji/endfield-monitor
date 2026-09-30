<#
  build.ps1 —— 一次构建全部平台的产物，命名统一，可选直接推到 Gitee 发行版。

  产物统一叫 <名字>-v<版本>-<平台>-<架构>，平台与架构只有这几种写法：
      平台: win | linux
      架构: x64 | arm64
  客户端 1 份 × 3 = 3 个；采集端 2 份（普通 + 增强）× 3 = 6 个；共 9 个。

  用法：
      .\build.ps1                      # 版本取最近一个 git tag，只构建
      .\build.ps1 -Version v0.5.0      # 指定版本，只构建
      .\build.ps1 -Publish             # 构建并推到 Gitee 发行版（全量附件）
      .\build.ps1 -Version v0.5.0 -Publish

  发布需要令牌，从环境变量 GITEE_TOKEN 读（或放在 ~/.gitee-token 里）：
      $env:GITEE_TOKEN = '你的 Gitee 私人令牌'

  依赖：PATH 里有 dotnet 与 go；NuGet 与 Go 的代理/缓存按各自环境变量的设置走。
#>
param(
    [string]$Version,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot
$dist = Join-Path $repo 'dist'
$clientProj = Join-Path $repo 'src\EndfieldMonitor'
$serverDir = Join-Path $repo 'server-go'

# 找工具：PATH 优先，其次各自的家目录变量，都找不到就明说
function Find-Tool([string]$name, [string]$envVar, [string]$rel) {
    $c = Get-Command $name -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $root = [Environment]::GetEnvironmentVariable($envVar)
    if ($root) {
        $p = Join-Path $root $rel
        if (Test-Path $p) { return $p }
    }
    throw "找不到 $name，请把它放进 PATH，或设置 $envVar"
}
$dotnetExe = Find-Tool 'dotnet' 'DOTNET_ROOT' 'dotnet.exe'
$goExe = Find-Tool 'go' 'GOROOT' 'bin\go.exe'

# 三平台。Rid 给 dotnet，GoArch 给 go；对外一律叫 x64 / arm64。
$targets = @(
    @{ Os = 'win';   Arch = 'x64';   Rid = 'win-x64';     GoArch = 'amd64'; Ext = '.exe' }
    @{ Os = 'linux'; Arch = 'x64';   Rid = 'linux-x64';   GoArch = 'amd64'; Ext = '' }
    @{ Os = 'linux'; Arch = 'arm64'; Rid = 'linux-arm64'; GoArch = 'arm64'; Ext = '' }
)

# 版本：不给就取最近的 git tag
if (-not $Version) {
    $Version = (git -C $repo describe --tags --abbrev=0 2>$null)
    if (-not $Version) { throw '仓库里没有 tag，请用 -Version 指定版本号' }
}
if ($Version -notmatch '^v') { $Version = "v$Version" }

Write-Host "构建 $Version -> $dist" -ForegroundColor Cyan
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $dist | Out-Null

# ---------- 客户端 ----------
foreach ($t in $targets) {
    Write-Host "  客户端 $($t.Os)-$($t.Arch) ..." -ForegroundColor DarkGray
    $tmp = Join-Path $env:TEMP "enf-pub-$($t.Rid)"
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    & $dotnetExe publish $clientProj -c Release -r $t.Rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
        -o $tmp --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "客户端 $($t.Rid) 构建失败" }
    $src = Join-Path $tmp "EndfieldMonitor$($t.Ext)"
    Move-Item $src (Join-Path $dist "EndfieldMonitor-$Version-$($t.Os)-$($t.Arch)$($t.Ext)") -Force
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------- 采集端（普通 + 增强） ----------
Push-Location $serverDir
try {
    foreach ($t in $targets) {
        $env:GOOS = if ($t.Os -eq 'win') { 'windows' } else { 'linux' }
        $env:GOARCH = $t.GoArch
        $env:CGO_ENABLED = '0'
        foreach ($v in @(@{ Tag = ''; Name = 'enf-collector' }, @{ Tag = 'plus'; Name = 'enf-collector-plus' })) {
            Write-Host "  $($v.Name) $($t.Os)-$($t.Arch) ..." -ForegroundColor DarkGray
            $out = Join-Path $dist "$($v.Name)-$Version-$($t.Os)-$($t.Arch)$($t.Ext)"
            $goArgs = @('-ldflags', '-s -w', '-o', $out)
            if ($v.Tag) { $goArgs = @('-tags', $v.Tag) + $goArgs }
            & $goExe build @goArgs .
            if ($LASTEXITCODE -ne 0) { throw "$($v.Name) $($t.Os)-$($t.Arch) 构建失败" }
        }
    }
} finally {
    Pop-Location
    Remove-Item Env:GOOS, Env:GOARCH, Env:CGO_ENABLED -ErrorAction SilentlyContinue
}

# ---------- 清单与校验和（自动标注） ----------
$files = Get-ChildItem $dist -File | Sort-Object Name
$sums = foreach ($f in $files) {
    $h = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower()
    "$h  $($f.Name)"
}
$sums | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding utf8NoBOM

Write-Host ''
Write-Host "产物 $($files.Count) 个：" -ForegroundColor Green
foreach ($f in $files) {
    Write-Host ("  {0,-46} {1,8:N1} MB" -f $f.Name, ($f.Length / 1MB))
}
Write-Host "  （另附 SHA256SUMS.txt）" -ForegroundColor DarkGray

# ---------- 发布 ----------
if (-not $Publish) {
    Write-Host ''
    Write-Host '只构建，未发布。要发布就加 -Publish' -ForegroundColor Yellow
    return
}

$token = $env:GITEE_TOKEN
if (-not $token) {
    $p = Join-Path $HOME '.gitee-token'
    if (Test-Path $p) { $token = (Get-Content $p -Raw).Trim() }
}
if (-not $token) { throw '没找到 Gitee 令牌，请设环境变量 GITEE_TOKEN' }

$api = 'https://gitee.com/api/v5/repos/ji-or-ji/endfield-monitor'
$sanitize = { param($s) $s -replace [regex]::Escape($token), '***' }

# 说明：优先用手写的 publish/RELEASE-<版本>.md，没有就自动生成一份附件清单
$notesPath = Join-Path $repo "publish\RELEASE-$Version.md"
if (Test-Path $notesPath) {
    $body = Get-Content $notesPath -Raw
    Write-Host "用说明文件 $notesPath" -ForegroundColor DarkGray
} else {
    $body = "## 附件`n`n" + (($files | ForEach-Object {
        "- **$($_.Name)**  {0:N1} MB" -f ($_.Length / 1MB)
    }) -join "`n") + "`n`n校验和见 SHA256SUMS.txt。"
    Write-Host '没有手写说明，用自动生成的附件清单' -ForegroundColor DarkGray
}

Write-Host "创建发行版 $Version ..." -ForegroundColor Cyan
try {
    $rel = Invoke-RestMethod -Method Post -Uri "$api/releases" -ContentType 'application/json' -TimeoutSec 120 -Body (@{
        access_token = $token; tag_name = $Version; target_commitish = 'main'
        name = $Version; body = $body; prerelease = $false
    } | ConvertTo-Json -Depth 3)
} catch {
    throw ('创建发行版失败：' + (& $sanitize $_.Exception.Message))
}
Write-Host "  release id = $($rel.id)" -ForegroundColor DarkGray

# 全量上传：dist 下每个文件都传，一个都不落
foreach ($f in (Get-ChildItem $dist -File | Sort-Object Name)) {
    try {
        $null = Invoke-RestMethod -Method Post `
            -Uri "$api/releases/$($rel.id)/attach_files?access_token=$token" `
            -TimeoutSec 900 -Form @{ file = $f }
        Write-Host "  已上传 $($f.Name)" -ForegroundColor Green
    } catch {
        Write-Host "  上传失败 $($f.Name)：$(& $sanitize $_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ''
Write-Host "https://gitee.com/ji-or-ji/endfield-monitor/releases/tag/$Version" -ForegroundColor Green
