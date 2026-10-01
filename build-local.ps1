<#
  build-local.ps1 —— 本机（Hanako 工作区 / 192.168.101.2）用的构建与发布脚本。

  与仓库里的 build.ps1 是同一套约定的两份实现，不是替换关系：
  这份按本机环境写，那份按另一台机器写。设计思想见 docs/release-pipeline.md，
  产物命名、版本注入、配额策略、幂等要求两边完全一致。

  本机与另一台机器的差异（也就是这份脚本存在的原因）：
    1. Gitee 令牌放在 C:\Users\oobba\.hanako\secrets\gitee-ji-or-ji.token，
       而且是 key=value 格式（token=xxx），不是裸令牌。
       查找顺序：环境变量 GITEE_TOKEN -> ~/.gitee-token -> 上面那个文件。
    2. 只有 Gitee 一个远端，没有 GitHub 远端也没有 GitHub 令牌。
       所以 GitHub 那一段是「有就做，没有就跳过」，绝不因为缺令牌而中断发布。
    3. 不依赖任何针对其它机器的怪癖结论；排序与筛选一律用显式循环 + 数值比较。

  用法：
      .\build-local.ps1                        # 版本取最近一个 git tag，只构建
      .\build-local.ps1 -Version v0.6.1        # 指定版本，只构建
      .\build-local.ps1 -Version v0.6.1 -Publish
      .\build-local.ps1 -Version v0.6.1 -Publish -Prune   # 顺带清掉旧的发行版

  说明：发行说明优先读 publish\RELEASE-<版本>.md（该目录不入库），
  没有就自动生成一份附件清单。要发版就先把说明写进去。

  依赖：PowerShell 7（用了 utf8NoBOM 与 -Form），PATH 里有 dotnet 与 go。
#>
param(
    [string]$Version,
    [switch]$Publish,
    [switch]$Prune,
    [switch]$SkipLinuxArm64
)

$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot
$dist = Join-Path $repo 'dist'
$clientProj = Join-Path $repo 'src\EndfieldMonitor'
$serverDir = Join-Path $repo 'server-go'
$giteeRepo = 'ji-or-ji/endfield-monitor'
$giteeApi = "https://gitee.com/api/v5/repos/$giteeRepo"

# ---------------------------------------------------------------- 工具定位

# PATH 优先；找不到再看各自的老家环境变量和几个常见安装位置。
# 本机 dotnet 与 go 都在 PATH 里，这里写全是为了换台机器也不至于当场死掉。
function Find-Tool([string]$name, [string]$envVar, [string]$rel, [string[]]$extra) {
    $c = Get-Command $name -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $candidates = @()
    $root = [Environment]::GetEnvironmentVariable($envVar)
    if ($root) { $candidates += (Join-Path $root $rel) }
    if ($extra) { $candidates += $extra }
    foreach ($p in $candidates) { if ($p -and (Test-Path $p)) { return $p } }
    throw "找不到 $name：请把它放进 PATH，或设置 $envVar"
}

$dotnetExe = Find-Tool 'dotnet' 'DOTNET_ROOT' 'dotnet.exe' @('C:\Program Files\dotnet\dotnet.exe')
$goExe = Find-Tool 'go' 'GOROOT' 'bin\go.exe' @('C:\Program Files\Go\bin\go.exe')

# ---------------------------------------------------------------- 平台矩阵

# 三个平台。Rid 给 dotnet，GoArch 给 go，对外一律叫 x64 / arm64。
$targets = @(
    @{ Os = 'win';   Arch = 'x64';   Rid = 'win-x64';     GoArch = 'amd64'; Ext = '.exe' }
    @{ Os = 'linux'; Arch = 'x64';   Rid = 'linux-x64';   GoArch = 'amd64'; Ext = '' }
    @{ Os = 'linux'; Arch = 'arm64'; Rid = 'linux-arm64'; GoArch = 'arm64'; Ext = '' }
)
if ($SkipLinuxArm64) { $targets = $targets | Where-Object { $_.Arch -ne 'arm64' } }

# ---------------------------------------------------------------- 版本号

# 产物名里的版本带 v（-v0.6.1-win-x64），程序里嵌的不带 v（0.6.1）。
if (-not $Version) {
    $Version = (git -C $repo describe --tags --abbrev=0 2>$null)
    if (-not $Version) { throw '仓库里没有 tag，请用 -Version 指定版本号' }
}
if ($Version -notmatch '^v') { $Version = "v$Version" }
$verNum = $Version.TrimStart('v')

Write-Host "构建 $Version（程序内嵌 $verNum） -> $dist" -ForegroundColor Cyan

# 构建前先停掉自己正在跑的产物：Windows 上覆盖运行中的 exe 会静默失败，
# 表现是"构建好像成功但东西没变"。
Get-Process -Name 'EndfieldMonitor*', 'enf-collector*' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $dist -Force | Out-Null

# ---------------------------------------------------------------- 客户端

foreach ($t in $targets) {
    Write-Host "  客户端 $($t.Os)-$($t.Arch) ..." -ForegroundColor DarkGray
    $tmp = Join-Path $env:TEMP "enf-pub-$($t.Rid)"
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    & $dotnetExe publish $clientProj -c Release -r $t.Rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
        -p:Version=$verNum -p:InformationalVersion=$verNum `
        -o $tmp --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "客户端 $($t.Rid) 构建失败" }
    $produced = Join-Path $tmp "EndfieldMonitor$($t.Ext)"
    if (-not (Test-Path $produced)) { throw "客户端 $($t.Rid) 没有产出 $produced（单文件参数是否被改动？）" }
    Move-Item $produced (Join-Path $dist "EndfieldMonitor-$Version-$($t.Os)-$($t.Arch)$($t.Ext)") -Force
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- 采集端

# 普通版与增强版（-tags plus）各一份，共 2 × 3 = 6 个。
Push-Location $serverDir
try {
    foreach ($t in $targets) {
        $env:GOOS = if ($t.Os -eq 'win') { 'windows' } else { 'linux' }
        $env:GOARCH = $t.GoArch
        $env:CGO_ENABLED = '0'
        foreach ($v in @(@{ Tag = ''; Name = 'enf-collector' }, @{ Tag = 'plus'; Name = 'enf-collector-plus' })) {
            Write-Host "  $($v.Name) $($t.Os)-$($t.Arch) ..." -ForegroundColor DarkGray
            $out = Join-Path $dist "$($v.Name)-$Version-$($t.Os)-$($t.Arch)$($t.Ext)"
            $goArgs = @('-ldflags', "-s -w -X main.version=$verNum", '-o', $out)
            if ($v.Tag) { $goArgs = @('-tags', $v.Tag) + $goArgs }
            & $goExe build @goArgs .
            if ($LASTEXITCODE -ne 0) { throw "$($v.Name) $($t.Os)-$($t.Arch) 构建失败" }
        }
    }
} finally {
    Pop-Location
    Remove-Item Env:GOOS, Env:GOARCH, Env:CGO_ENABLED -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- 自包含套件

# 每个平台一个 zip：客户端 + 该平台的增强版采集端 + 一份怎么用。
# 意义是让「同机自看」变成一步：客户端先探本机端口，没人监听就把同目录的采集端拉起来。
foreach ($t in $targets) {
    $stage = Join-Path $env:TEMP "enf-bundle-$($t.Os)-$($t.Arch)"
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    $clientName = "EndfieldMonitor-$Version-$($t.Os)-$($t.Arch)$($t.Ext)"
    $collectorName = "enf-collector-plus-$Version-$($t.Os)-$($t.Arch)$($t.Ext)"
    Copy-Item (Join-Path $dist $clientName) $stage
    Copy-Item (Join-Path $dist $collectorName) $stage

    # zip 不保留 Unix 执行位，所以 Linux 那份必须把 chmod 写进说明里。
    $howto = @"
EndfieldMonitor 本机套件（$($t.Os)-$($t.Arch)）

一、打开客户端
    Windows:  双击 $clientName
    Linux:    chmod +x $clientName
              ./$clientName

就这样。客户端会自己把同文件夹里的采集端拉起来，连上本机
127.0.0.1:8898，不用另外开一个窗口。

二、可选：要用增强版的启停应用与取图标
    在客户端设置里填一个口令。填了口令，它就会带着口令重新拉起采集端；
    不填也能看数据，只是不能用动手指令。
"@
    Set-Content (Join-Path $stage '怎么用.txt') -Value $howto -Encoding utf8NoBOM

    $zip = Join-Path $dist "EndfieldMonitor-$Version-$($t.Os)-$($t.Arch)-bundle.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  套件 $($t.Os)-$($t.Arch) 已打包" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- 校验和与清单

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
Write-Host '  （另附 SHA256SUMS.txt）' -ForegroundColor DarkGray

if (-not $Publish) {
    Write-Host ''
    Write-Host '只构建，未发布。要发布就加 -Publish' -ForegroundColor Yellow
    return
}

# ---------------------------------------------------------------- 令牌

# 只从环境变量或家目录下的文件读，绝不写进仓库；输出里也要遮掉。
function Get-GiteeToken {
    $t = [Environment]::GetEnvironmentVariable('GITEE_TOKEN')
    if ($t) { return $t.Trim() }
    $paths = @(
        (Join-Path $HOME '.gitee-token'),
        'C:\Users\oobba\.hanako\secrets\gitee-ji-or-ji.token'
    )
    foreach ($p in $paths) {
        if (-not (Test-Path $p)) { continue }
        $raw = Get-Content $p -Raw
        # 本机那份是 key=value 格式（token=xxx），不是裸令牌。
        if ($raw -match '(?m)^\s*token\s*=\s*(\S+)\s*$') { return $Matches[1] }
        $trim = $raw.Trim()
        if ($trim -and $trim -notmatch '=') { return $trim }
    }
    throw ("没找到 Gitee 令牌。设环境变量 GITEE_TOKEN，或放到 `n  " +
        ($paths -join "`n  "))
}

$giteeToken = Get-GiteeToken
$sanitize = { param($s) ([string]$s) -replace [regex]::Escape($giteeToken), '***' }

# GitHub 是代码镜像，不是产物仓库。本机没有远端也没有令牌，所以这一段是「有就做」。
$githubToken = [Environment]::GetEnvironmentVariable('GITHUB_TOKEN')
if (-not $githubToken) {
    $ghTokenFile = Join-Path $HOME '.github-token'
    if (Test-Path $ghTokenFile) { $githubToken = (Get-Content $ghTokenFile -Raw).Trim() }
}
$githubRemote = $null
foreach ($r in (git -C $repo remote)) {
    if ($r -ne 'origin') {
        $url = (git -C $repo remote get-url $r 2>$null)
        if ($url -match 'github\.com') { $githubRemote = $r; break }
    }
}
if (-not ($githubToken -and $githubRemote)) { $githubToken = $null }

# ---------------------------------------------------------------- 发行说明

$notesPath = Join-Path $repo "publish\RELEASE-$Version.md"
if (Test-Path $notesPath) {
    $body = Get-Content $notesPath -Raw
    Write-Host "用说明文件 $notesPath" -ForegroundColor DarkGray
} else {
    $body = "## 附件`n`n" + (($files | ForEach-Object {
                "- **$($_.Name)**  {0:N1} MB" -f ($_.Length / 1MB)
            }) -join "`n") + "`n`n校验和见 SHA256SUMS.txt。"
    Write-Host '没有手写说明（publish\RELEASE-*.md），用自动生成的附件清单' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 版本历史

# 先记 CHANGELOG，再推代码，这样这一条跟着同一轮推送上去。
$changeLog = Join-Path $repo 'CHANGELOG.md'
$summary = ''
if (Test-Path $notesPath) {
    $h2 = ''; $h3 = ''
    foreach ($ln in (Get-Content $notesPath)) {
        $t = $ln.Trim()
        if ($h2 -eq '' -and $t.StartsWith('## ')) { $h2 = $t.Substring(3).Trim() }
        elseif ($h3 -eq '' -and $t.StartsWith('### ')) { $h3 = $t.Substring(4).Trim() }
    }
    $generic = @('本版做了什么', '这一版', '本版', '说明')
    if ($h2 -ne '' -and $generic -notcontains $h2) { $summary = $h2 }
    elseif ($h3 -ne '') { $summary = $h3 }
    elseif ($h2 -ne '') { $summary = $h2 }
}
if ($summary -eq '') { $summary = '（这一版没有写说明）' }

if (Test-Path $changeLog) {
    $text = Get-Content $changeLog -Raw
    if ($text -notmatch [regex]::Escape("**$Version**")) {
        $entry = "- **$Version** — $summary"
        $text = $text.Replace('<!-- 新的在上面 -->', "<!-- 新的在上面 -->`r`n`r`n$entry")
        Set-Content $changeLog -Value $text -Encoding utf8NoBOM
        git -C $repo add -- CHANGELOG.md
        git -C $repo commit -q -m "版本历史：$Version"
        Write-Host "  已记入 CHANGELOG.md：$entry" -ForegroundColor DarkGray
    } else {
        Write-Host "  CHANGELOG.md 里已有 $Version，不重复" -ForegroundColor DarkGray
    }
} else {
    Write-Host '  没有 CHANGELOG.md，跳过历史记录' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 推代码与标签

# 发行版指向的提交，远端得先有。标签一次只推一个显式引用，不批量推。
$env:GIT_TERMINAL_PROMPT = '0'
Write-Host '推送代码与标签 ...' -ForegroundColor Cyan

$tagExists = (git -C $repo tag -l $Version)
if (-not $tagExists) {
    git -C $repo tag -a $Version -m "$Version`: $summary"
    Write-Host "  本地没有 $Version，已在 HEAD 上创建" -ForegroundColor DarkGray
}
foreach ($ref in @('main', $Version)) {
    git -C $repo push origin $ref 2>&1 | Select-Object -Last 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "推 origin 的 $ref 失败，先把推送问题处理掉" }
    Write-Host "  origin $ref 已推" -ForegroundColor DarkGray
}

if ($githubToken -and $githubRemote) {
    $ghUrl = (git -C $repo remote get-url $githubRemote)
    $pushUrl = $ghUrl -replace 'https://', "https://x-access-token:$githubToken@"
    try {
        foreach ($ref in @('main', $Version)) {
            git -C $repo push $pushUrl $ref 2>&1 | Select-Object -Last 1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "推 $githubRemote 的 $ref 失败" }
            Write-Host "  $githubRemote $ref 已推" -ForegroundColor DarkGray
        }
    } catch {
        Write-Host ("  GitHub 镜像推送跳过（不影响本次发布）：" + (& $sanitize $_.Exception.Message)) -ForegroundColor Yellow
    }
} else {
    Write-Host '  没有 GitHub 远端或令牌，跳过镜像（本机只发 Gitee）' -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- 清旧发行版

# Gitee 的发行版附件配额是 1 GB，全量发几版就撞墙；而它的 API 没有删附件的接口，
# 只能连整个发行版一起删（tag 会留着，代码不受影响）。
function Get-VersionScore([string]$tag) {
    $m = [regex]::Match([string]$tag, '(\d+)\.(\d+)\.(\d+)')
    if (-not $m.Success) { return -1 }
    return [int]$m.Groups[1].Value * 1000000 + [int]$m.Groups[2].Value * 1000 + [int]$m.Groups[3].Value
}

# 注意：PowerShell 的 Invoke-RestMethod 拿到 JSON 数组时是当成"一个对象"返回的，
# 外面再套一层 @() 就成了"只含这一个数组的数组"：foreach 只走一圈、
# 属性变成全部元素拼起来的字符串，于是排序与筛选会静默失效（曾把该留的删了）。
# 所以这里不套 @()，直接拿返回值遍历；数量用 .Count 显式数。
$allReleases = Invoke-RestMethod -Uri "$giteeApi/releases?access_token=$giteeToken" -TimeoutSec 60

if ($Prune) {
    Write-Host '清理 Gitee 旧发行版（保留最近 2 版 + 本次）...' -ForegroundColor Yellow
    # 取版本最高的两版（**把本次版本也算进去**）：显式循环 + 数值比较，
    # 不走管道排序，行为是确定的。
    # 别再把「本次版本」另并进保留集：那等于留三版（第一次跑就多留了一个 v0.4.1）。
    $candidates = @()
    foreach ($r in $allReleases) { $candidates += [string]$r.tag_name }
    if ($candidates -notcontains $Version) { $candidates += $Version }
    $best1 = -1; $best2 = -1; $tag1 = ''; $tag2 = ''
    foreach ($t in $candidates) {
        $s = Get-VersionScore $t
        if ($s -lt 0) { continue }
        if ($s -gt $best1) {
            $best2 = $best1; $tag2 = $tag1; $best1 = $s; $tag1 = $t
        } elseif ($s -gt $best2 -and $s -ne $best1) {
            $best2 = $s; $tag2 = $t
        }
    }
    $keep = @($tag1, $tag2) | Where-Object { $_ -ne '' } | Select-Object -Unique
    Write-Host "  保留：$($keep -join '、')" -ForegroundColor DarkGray
    foreach ($r in $allReleases) {
        $tag = [string]$r.tag_name
        if ($keep -contains $tag) { continue }
        try {
            Invoke-RestMethod -Method Delete -Uri "$giteeApi/releases/$($r.id)?access_token=$giteeToken" -TimeoutSec 60 | Out-Null
            Write-Host "  已删 $tag（tag 保留）" -ForegroundColor DarkGray
        } catch {
            Write-Host ("  删 $tag 失败：" + (& $sanitize $_.Exception.Message)) -ForegroundColor Red
        }
    }
}

# ---------------------------------------------------------------- 发行版

Write-Host 'Gitee：创建发行版 ...' -ForegroundColor Cyan
$release = $null
try {
    $release = Invoke-RestMethod -Method Post -Uri "$giteeApi/releases" -ContentType 'application/json' -TimeoutSec 120 -Body (@{
            access_token     = $giteeToken
            tag_name         = $Version
            target_commitish = 'main'
            name             = $Version
            body             = $body
            prerelease       = $false
        } | ConvertTo-Json -Depth 3)
} catch {
    # 重跑发布时常见：已经建过了。按 tag 直查取回来（不要"列出全部再筛"，后者筛出来的
    # 可能不是一个对象，表现为上传全失败）。
    $firstErr = $_.Exception.Message
    try { $release = Invoke-RestMethod -Uri "$giteeApi/releases/tags/$Version?access_token=$giteeToken" -TimeoutSec 60 } catch { }
    if (-not $release) { throw ('创建 Gitee 发行版失败，且按 tag 也查不到：' + (& $sanitize $firstErr)) }
    Write-Host "  已有，复用 id=$($release.id)" -ForegroundColor DarkGray
}

# 拿不到确定的 id 就停下：带着空 id 继续跑会失败一长串，看起来像文件有问题。
$releaseId = $release.id
if (-not $releaseId) { throw "发行版 id 为空，停下。返回内容：$(& $sanitize ($release | ConvertTo-Json -Depth 2))" }
Write-Host "  id = $releaseId" -ForegroundColor DarkGray

# 已有附件就跳过，保证重跑幂等。
$have = @()
try {
    $detail = Invoke-RestMethod -Uri "$giteeApi/releases/$releaseId`?access_token=$giteeToken" -TimeoutSec 60
    if ($detail.assets) { $have = @($detail.assets | ForEach-Object { $_.name }) }
} catch { }

# 传大文件时 Gitee 偶尔回 400（同大小的文件有时成有时不成，像限流），
# 所以每个文件失败后隔几秒重试两回；失败时把服务端返回的正文一并打出来，
# 只报状态码会让人往文件本身猜，而真正的原因（配额、权限、限流）就在正文里。
$failed = @()
foreach ($f in (Get-ChildItem $dist -File | Sort-Object Name)) {
    if ($have -contains $f.Name) { Write-Host "  已有 $($f.Name)" -ForegroundColor DarkGray; continue }
    $ok = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $null = Invoke-RestMethod -Method Post -Uri "$giteeApi/releases/$releaseId/attach_files?access_token=$giteeToken" -TimeoutSec 1800 -Form @{ file = $f }
            Write-Host "  已上传 $($f.Name)" -ForegroundColor Green
            $ok = $true
            break
        } catch {
            $why = ''
            $resp = $_.Exception.Response
            if ($resp) {
                try { $why = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd() } catch { }
            }
            if (-not $why) { $why = $_.Exception.Message }
            if ($attempt -lt 3) {
                Write-Host ("  $($f.Name) 第 $attempt 次失败，8 秒后重试：{0}" -f (& $sanitize $why)) -ForegroundColor Yellow
                Start-Sleep -Seconds 8
            } else {
                Write-Host ("  Gitee 上传失败 $($f.Name)：" + (& $sanitize $why)) -ForegroundColor Red
            }
        }
    }
    if (-not $ok) { $failed += $f.Name }
    Start-Sleep -Seconds 2
}

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host "有 $($failed.Count) 个附件没传上去：$($failed -join '、')" -ForegroundColor Red
    Write-Host '重跑同一条命令即可续传（已有的会自动跳过）。' -ForegroundColor Yellow
} else {
    Write-Host '全部附件就位。' -ForegroundColor Green
}
Write-Host "Gitee   https://gitee.com/$giteeRepo/releases/tag/$Version" -ForegroundColor Green
