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
    [switch]$Publish,
    [switch]$Prune,
    [switch]$GitHubAssets
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
$verNum = $Version.TrimStart('v')   # 程序里嵌的版本号不带 v

Write-Host "构建 $Version -> $dist" -ForegroundColor Cyan

# 构建前先停掉自己正在跑的产物：Windows 上覆盖运行中的 exe 会失败，
# 而忘掉这一步的代价是"构建静默不生效"。这个坑踩过三次，索性交给脚本。
Get-Process -Name 'EndfieldMonitor*', 'enf-collector*' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1

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
        -p:Version=$verNum -p:InformationalVersion=$verNum `
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

# ---------- 自包含套件 ----------
# 每个平台一个 zip：客户端 + 该平台的增强版采集端 + 一份怎么用。
# 解压后两样在同一个文件夹里，客户端地址留空即连本机，拿到就能用。
foreach ($t in $targets) {
    $stage = Join-Path $env:TEMP "enf-bundle-$($t.Os)-$($t.Arch)"
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $stage | Out-Null

    $clientName = "EndfieldMonitor-$Version-$($t.Os)-$($t.Arch)$($t.Ext)"
    $collectorName = "enf-collector-plus-$Version-$($t.Os)-$($t.Arch)$($t.Ext)"
    Copy-Item (Join-Path $dist $clientName) $stage
    Copy-Item (Join-Path $dist $collectorName) $stage

    # zip 不保留 Unix 执行权限，所以 Linux 那份要把 chmod 写进说明
    $howto = @"
EndfieldMonitor 本机套件（$($t.Os)-$($t.Arch)）

一、打开客户端
    Windows:  双击 $clientName
    Linux:    chmod +x $clientName
              ./$clientName

就这样。客户端会自己把同文件夹里的采集端拉起来，并连上本机
127.0.0.1:8898，不需要另外开一个窗口。

二（可选）：想用增强版的启停应用与取图标
    在客户端设置里填一个口令，它会带着这个口令重新拉起采集端。
    不配口令也能看数据，只是不能用动手指令。

想用增强版的启停应用与取图标：启动采集端时加上 --token 你的口令，
并在客户端设置里填同一个口令。不配口令也能看数据，只是不能用动手指令。
"@
    Set-Content (Join-Path $stage '怎么用.txt') -Value $howto -Encoding utf8NoBOM

    $zip = Join-Path $dist "EndfieldMonitor-$Version-$($t.Os)-$($t.Arch)-bundle.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  套件 $($t.Os)-$($t.Arch) 已打包" -ForegroundColor DarkGray
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

# 两个平台各要一个令牌：Gitee 是主仓库（只留最近两版），GitHub 是镜像（历史都留着）。
function Get-Token([string]$envName, [string]$fileName, [string]$who) {
    $t = [Environment]::GetEnvironmentVariable($envName)
    if ($t) { return $t.Trim() }
    $p = Join-Path $HOME $fileName
    if (-not (Test-Path $p)) { return '' }
    $raw = (Get-Content $p -Raw).Trim()
    # 有的机器上令牌文件是 key=value 写法（token=xxx），不是裸令牌
    foreach ($ln in ($raw -split "`r?`n")) {
        if ($ln -match '^\s*token\s*=\s*(.+)$') { return $Matches[1].Trim() }
    }
    return $raw
}
$giteeToken = Get-Token 'GITEE_TOKEN' '.gitee-token' 'Gitee'
if (-not $giteeToken) {
    $alt = Join-Path $HOME '.gitee-ji-or-ji.token'
    if (Test-Path $alt) { $giteeToken = (Get-Content $alt -Raw).Trim() }
}
if (-not $giteeToken) { throw '没找到 Gitee 令牌：设环境变量 GITEE_TOKEN，或放到 ~/.gitee-token' }

# GitHub 是可选镜像：没令牌就只发 Gitee，不因为缺它中断发布
$githubToken = Get-Token 'GITHUB_TOKEN' '.github-token' 'GitHub'
if (-not $githubToken) {
    Write-Host '  没有 GitHub 令牌，本次只发 Gitee（代码镜像跳过）' -ForegroundColor Yellow
}

$giteeRepo = 'ji-or-ji/endfield-monitor'
$githubRepo = 'ji-or-ji/endfield-monitor'
$giteeApi = "https://gitee.com/api/v5/repos/$giteeRepo"
$githubApi = "https://api.github.com/repos/$githubRepo"
$githubUpload = "https://uploads.github.com/repos/$githubRepo"
$githubPushUrl = "https://x-access-token:$githubToken@github.com/$githubRepo.git"
$githubHeaders = @{
    Authorization = "token $githubToken"
    Accept        = 'application/vnd.github+json'
    'User-Agent'  = 'enf-build'
}

# 两个令牌都不能漏到输出里
$sanitize = { param($s) $s -replace [regex]::Escape($giteeToken), '***' -replace [regex]::Escape($githubToken), '***' }

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

# 先把版本历史记上，再推代码——这样这一条会跟着同一轮推送上去。
# 说明取自发行说明的第一个小标题；那个标题太通用时（比如“本版做了什么”）往下取一层。
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
    $summary = if ($h2 -ne '' -and $generic -notcontains $h2) { $h2 }
    elseif ($h3 -ne '') { $h3 }
    elseif ($h2 -ne '') { $h2 }
    else { '' }
}
if ($summary -eq '') { $summary = '（这一版没有写说明）' }
$entry = "- **$Version** — $summary"
if (Test-Path $changeLog) {
    $text = Get-Content $changeLog -Raw
    if ($text -notmatch [regex]::Escape("**$Version**")) {
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

# 先把代码与标签推到两边：发行版指向的提交，远端得先有。
# 标签一次只推一个显式引用，批量推会被安全策略拦。
Write-Host '推送代码与标签 ...' -ForegroundColor Cyan
$env:GIT_TERMINAL_PROMPT = '0'
foreach ($t in @(
        @{ Label = 'Gitee'; Ref = 'origin' },
        @{ Label = 'GitHub'; Ref = $(if ($githubToken) { $githubPushUrl } else { '' }) }
    )) {
    if ($t.Ref -eq '') { continue }   # 没有令牌的远端直接跳过
    foreach ($ref in @('main', $Version)) {
        git -C $repo push $t.Ref $ref 2>&1 | Select-Object -Last 1
        if ($LASTEXITCODE -ne 0) { throw "推 $($t.Label) 的 $ref 失败，先把推送问题处理掉" }
        Write-Host "  $($t.Label) $ref 已推" -ForegroundColor DarkGray
    }
}

# -Prune：清掉 Gitee 上旧版本的发行版（附件随之释放），只留最近 2 版。
# Gitee 的仓库附件配额是 1 GB，全量发几版就会撞上；而且它的 API 没有删附件的接口，
# 只能连整个发行版一起删（tag 会留着，代码不受影响）。历史版本存在 GitHub 那边。
if ($Prune) {
    Write-Host '  Gitee 清理旧版本（只留最近 2 版）...' -ForegroundColor Yellow
    # 注意：PowerShell 7 的 Invoke-RestMethod 拿到 JSON 数组时，是把它当成“一个对象”
    # 返回的；再套一层 @() 就成了“只含这一个数组的数组”，foreach 因此只走一圈，
    # 而 $r.tag_name 是全部 tag 拼起来的字符串。之前那些排序静默失效、筛选返回四个 id，
    # 根子都在这里。所以这里不套 @()，直接拿返回值遍历。
    $all = Invoke-RestMethod -Uri "$giteeApi/releases" -TimeoutSec 60
    # 手写“取版本最高的两个”，不用 Sort-Object：
    # 在这台机器上，管道里的排序/筛选对这些对象的属性不可靠（会静默失效，
    # 结果就是取到最老的两个、反而删掉真正该留的）。纯循环 + 数值比较是确定的。
    # 把本次版本也放进候选：清理发生在建发行版之前，它此时还不在接口返回里。
    # 但**不能**在算完“最高两版”之后再另并本次版本——那等于留三版。
    $candidates = @()
    foreach ($r in $all) { $candidates += [string]$r.tag_name }
    if ($candidates -notcontains $Version) { $candidates += $Version }
    $best1 = -1; $best2 = -1; $tag1 = ''; $tag2 = ''
    foreach ($t in $candidates) {
        $m = [regex]::Match($t, '(\d+)\.(\d+)\.(\d+)')
        if (-not $m.Success) { continue }
        $s = [int]$m.Groups[1].Value * 1000000 + [int]$m.Groups[2].Value * 1000 + [int]$m.Groups[3].Value
        if ($s -gt $best1) {
            $best2 = $best1; $tag2 = $tag1; $best1 = $s; $tag1 = $t
        } elseif ($s -gt $best2 -and $s -ne $best1) {
            $best2 = $s; $tag2 = $t
        }
    }
    $keep = @($tag1, $tag2) | Where-Object { $_ -ne '' } | Select-Object -Unique
    Write-Host "    保留: $($keep -join ', ')" -ForegroundColor DarkGray
    foreach ($r in $all) {
        $tag = [string]$r.tag_name
        if ($keep -contains $tag) { continue }
        try {
            Invoke-RestMethod -Method Delete -Uri "$giteeApi/releases/$($r.id)?access_token=$giteeToken" -TimeoutSec 60 | Out-Null
            Write-Host "    已删 $tag（tag 保留）" -ForegroundColor DarkGray
        } catch {
            Write-Host "    删 $tag 失败：$(& $sanitize $_.Exception.Message)" -ForegroundColor Red
        }
    }
}

# ---------- Gitee：创建发行版并全量上传 ----------
Write-Host 'Gitee：创建发行版 ...' -ForegroundColor Cyan
$grel = $null
$giteeHave = @()
try {
    $grel = Invoke-RestMethod -Method Post -Uri "$giteeApi/releases" -ContentType 'application/json' -TimeoutSec 120 -Body (@{
        access_token = $giteeToken; tag_name = $Version; target_commitish = 'main'
        name = $Version; body = $body; prerelease = $false
    } | ConvertTo-Json -Depth 3)
} catch {
    # 已经建过了（重跑发布时常见），按 tag 直查取回来。
    # 之前用的是“列出全部再筛”的写法，结果在这台机器上时好时坏（同一句有时命中 1 条、
    # 有时 0 条，甚至拼出过带四个 id 的上传地址，13 个文件全失败）；直查接口每次都是确定的。
    $grel = $null
    try {
        $grel = Invoke-RestMethod -Uri "$giteeApi/releases/tags/$Version" -TimeoutSec 60
    } catch { }
    if (-not $grel) { throw ('创建 Gitee 发行版失败，且按 tag 也查不到：' + (& $sanitize $_.Exception.Message)) }
    Write-Host "  已有，复用 id=$($grel.id)" -ForegroundColor DarkGray
}
Write-Host "  id = $($grel.id)" -ForegroundColor DarkGray
try { $giteeHave = @((Invoke-RestMethod -Uri "$giteeApi/releases/$($grel.id)" -TimeoutSec 60).assets | ForEach-Object { $_.name }) } catch { }

# 传大文件时 Gitee 偶尔会回 400（同样大小的文件有时成有时不成，像是限流），
# 所以每个文件失败后隔几秒重试两回。
foreach ($f in (Get-ChildItem $dist -File | Sort-Object Name)) {
    if ($giteeHave -contains $f.Name) { Write-Host "  已有 $($f.Name)" -ForegroundColor DarkGray; continue }
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $null = Invoke-RestMethod -Method Post -Uri "$giteeApi/releases/$($grel.id)/attach_files?access_token=$giteeToken" -TimeoutSec 900 -Form @{ file = $f }
            Write-Host "  已上传 $($f.Name)" -ForegroundColor Green
            break
        } catch {
            # 把服务端的错误正文一并打出来。只报状态码的话，
            # 「400」这种会让人往文件本身猜，而实际原因（配额、权限、限流）就在正文里。
            $resp = $_.Exception.Response
            $why = ''
            if ($resp) {
                try { $why = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd() } catch { }
            }
            if ($attempt -lt 3) {
                Write-Host "  $($f.Name) 第 $attempt 次失败，8 秒后重试" -ForegroundColor Yellow
                Start-Sleep 8
            } else {
                Write-Host "  Gitee 上传失败 $($f.Name)：$(& $sanitize $why)" -ForegroundColor Red
            }
        }
    }
    Start-Sleep 2
}

# ---------- GitHub：默认只镜像代码，不建发行版 ----------
# 产物的历史归档暂时不做：项目还在快速变动，发行版只留最近两版，
# 需要旧版的走 issue。真要把产物也传到 GitHub 时加 -GitHubAssets——
# 但本地网络到 GitHub 很慢（实测单次 API 调用约 3 秒），大文件上传不现实。
if ($GitHubAssets -and $githubToken) {
    Write-Host 'GitHub：创建发行版 ...' -ForegroundColor Cyan
$ghrel = $null
try {
    $ghrel = Invoke-RestMethod -Method Post -Uri "$githubApi/releases" -Headers $githubHeaders -ContentType 'application/json' -TimeoutSec 120 -Body (@{
        tag_name = $Version; name = $Version; body = $body; draft = $false; prerelease = $false
    } | ConvertTo-Json -Depth 3)
} catch {
    $ghrel = $null
    try { $ghrel = Invoke-RestMethod -Uri "$githubApi/releases/tags/$Version" -Headers $githubHeaders -TimeoutSec 60 } catch { }
    if (-not $ghrel) { Write-Host "  创建失败：$(& $sanitize $_.Exception.Message)" -ForegroundColor Red }
    else { Write-Host "  已有，复用 id=$($ghrel.id)" -ForegroundColor DarkGray }
}

if ($ghrel) {
    Write-Host "  id = $($ghrel.id)" -ForegroundColor DarkGray
    $ghHave = @()
    try { $ghHave = @(Invoke-RestMethod -Uri "$githubApi/releases/$($ghrel.id)/assets" -Headers $githubHeaders -TimeoutSec 60 | ForEach-Object { $_.name }) } catch { }
    foreach ($f in (Get-ChildItem $dist -File | Sort-Object Name)) {
        if ($ghHave -contains $f.Name) { Write-Host "  已有 $($f.Name)" -ForegroundColor DarkGray; continue }
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                $null = Invoke-RestMethod -Method Post -Uri "$githubUpload/releases/$($ghrel.id)/assets?name=$($f.Name)" -Headers $githubHeaders -ContentType 'application/octet-stream' -InFile $f.FullName -TimeoutSec 1800
                Write-Host "  已上传 $($f.Name)" -ForegroundColor Green
                break
            } catch {
                if ($attempt -lt 3) {
                    Write-Host "  $($f.Name) 第 $attempt 次失败，8 秒后重试" -ForegroundColor Yellow
                    Start-Sleep 8
                } else {
                    Write-Host "  GitHub 上传失败 $($f.Name)：$(& $sanitize $_.Exception.Message)" -ForegroundColor Red
                }
            }
        }
        Start-Sleep 1
    }
}

}

Write-Host ''
Write-Host "Gitee   https://gitee.com/$giteeRepo/releases/tag/$Version" -ForegroundColor Green
Write-Host "GitHub  https://github.com/$githubRepo  （只镜像代码）" -ForegroundColor DarkGray
