<#
.SYNOPSIS
    产出一份「装完即用」的 Windows 负载：编辑器 + 随包运行时（画布与实时预览都不再要求用户填路径）。

.DESCRIPTION
    负载布局（编辑器按这个顺序找运行时，见 ChengeExportService.LocateRuntime）：

        <安装目录>\HmiEditor.App.exe
        <安装目录>\chenge-runtime\chenge-runtime.exe        ← 截图预览（导出并预览）
        <安装目录>\chenge-runtime\chenge-preview-host.exe   ← 设计画布 / 实时预览的宿主
        <安装目录>\chenge-runtime\SDL2.dll                  ← 上面两个都要它

    设置项「运行时目录」与环境变量 CHENGE_RUNTIME_DIR 仍然优先，所以装了随包运行时之后，
    开发时想临时指到另一个 build 目录照样可以。

.PARAMETER RuntimeDir
    **Windows 版**运行时目录（含 chenge-runtime.exe / chenge-preview-host.exe / SDL2.dll）。
    这个目录不由本脚本产生 —— 运行时是 C + SDL2，必须在 Windows 工具链（MSYS2/mingw-w64）
    里编（见 docs/chenge-rt 的宿主构建说明）。

.PARAMETER SkipInstaller
    只出 zip，不调 NSIS。

.EXAMPLE
    pwsh tools/publish-win.ps1 -RuntimeDir C:\build\chenge-runtime-win -Version 2.22.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RuntimeDir,
    [string]$Version = "0.0.0",
    [string]$Configuration = "Release",
    [string]$OutRoot = "",
    [switch]$SkipInstaller,
    [switch]$SkipZip
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutRoot)) { $OutRoot = Join-Path $repoRoot "dist" }

$payload  = Join-Path $OutRoot "win-x64"
$runtimeTarget = Join-Path $payload "chenge-runtime"

# ── 1. 必需文件清单：缺一个就当场停，不产出"装完发现预览不能用"的包 ──
# Windows 包只需要**一个**可执行文件：预览宿主（不链 SDL，静态链接，4MB）。
# 进程版 chenge-runtime.exe 要 SDL2.dll，属于可选（有就一并带上，截图预览会优先用它）。
$runtimeRequired = @("chenge-preview-host.exe")
$runtimeOptional = @("chenge-runtime.exe", "SDL2.dll")

Write-Host "[1/5] 校验运行时目录：$RuntimeDir"
$missing = @()
foreach ($name in $runtimeRequired) {
    if (-not (Test-Path (Join-Path $RuntimeDir $name))) { $missing += $name }
}
if ($missing.Count -gt 0) {
    Write-Error @"
运行时目录里缺少：$($missing -join ', ')
需要的是 Windows 版运行时（不是 Linux 那份 chenge-runtime / chenge-preview-host）。
在 MSYS2 / mingw-w64 里编完 chenge-runtime 后，把产物目录用 -RuntimeDir 指过来。
"@
}

# ── 2. 发布编辑器（win-x64 自包含：目标机不需要装 .NET）──
Write-Host "[2/5] dotnet publish → $payload"
if (Test-Path $payload) { Remove-Item -Recurse -Force $payload }
dotnet publish (Join-Path $repoRoot "src/HmiEditor.App/HmiEditor.App.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:InformationalVersion=$Version `
    -o $payload --nologo
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish 失败（$LASTEXITCODE）" }

# ── 3. 随包运行时 ──
Write-Host "[3/5] 拷运行时 → $runtimeTarget"
New-Item -ItemType Directory -Force -Path $runtimeTarget | Out-Null
Copy-Item -Path (Join-Path $RuntimeDir "*") -Destination $runtimeTarget -Recurse -Force

# 运行时目录里若带了无关的构建中间物（.o/.d、源码），别打进安装包
Get-ChildItem -Path $runtimeTarget -Recurse -Include *.o, *.d, *.c, *.h, *.mk |
    Remove-Item -Force -ErrorAction SilentlyContinue

# ── 4. 自查：布局与"编辑器能不能自己找到运行时" ──
Write-Host "[4/5] 自查负载"
$exe = Join-Path $payload "HmiEditor.App.exe"
if (-not (Test-Path $exe)) { Write-Error "负载里没有 HmiEditor.App.exe" }
foreach ($name in $runtimeRequired) {
    if (-not (Test-Path (Join-Path $runtimeTarget $name))) {
        Write-Error "随包运行时少了 $name（拷贝阶段被过滤掉了？）"
    }
}
$bytes = (Get-ChildItem $payload -Recurse -File | Measure-Object -Sum Length).Sum
Write-Host ("      编辑器 {0:N1} MB + 运行时 {1:N1} MB" -f
    ((Get-ChildItem $payload -File | Measure-Object -Sum Length).Sum / 1MB),
    ((Get-ChildItem $runtimeTarget -Recurse -File | Measure-Object -Sum Length).Sum / 1MB))

# ── 5. 打包：zip（绿色版）+ 安装包（NSIS，可选）──
if (-not $SkipZip) {
    $zip = Join-Path $OutRoot "HMI-Studio-$Version-win-x64.zip"
    Write-Host "[5/5] 压缩 → $zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path (Join-Path $payload "*") -DestinationPath $zip -CompressionLevel Optimal
}

if (-not $SkipInstaller) {
    $makensis = Get-Command makensis -ErrorAction SilentlyContinue
    if (-not $makensis) {
        Write-Warning "找不到 makensis（NSIS）：跳过安装包。装 NSIS 后重跑，或手工执行：makensis -DVERSION=$Version tools/installer/hmi-editor.nsi"
    }
    else {
        Write-Host "      生成安装包（NSIS）"
        Push-Location (Join-Path $repoRoot "tools/installer")
        try {
            & $makensis.Source "-DVERSION=$Version" "-DPAYLOAD=$payload" hmi-editor.nsi
            if ($LASTEXITCODE -ne 0) { Write-Error "makensis 失败（$LASTEXITCODE）" }
        }
        finally { Pop-Location }
    }
}

Write-Host "完成：$OutRoot"
