<#
.SYNOPSIS
    编译并启动编辑器（Windows 开发用）。

.DESCRIPTION
    与 tools\publish-win.ps1 / publish-win.sh 的分工：这个只管「改完代码 → 编出来 → 跑起来看」；
    要出安装包走 tools\package.sh。

    运行时目录：没设 CHENGE_RUNTIME_DIR 时用仓库内兜底副本 runtime\win-x64。
    要用自己编的运行时，先 $env:CHENGE_RUNTIME_DIR = '<目录>'（脚本不覆盖已有值）。

.PARAMETER Release
    Release 配置（默认 Debug）。

.PARAMETER NoBuild
    跳过编译，直接用上次的产物。

.PARAMETER Clean
    先 dotnet clean 再编。

.PARAMETER EditorArgs
    编辑器自己的参数（--selftest-embed / --selftest-host / --mem / --iso，或一个工程路径）。
    直接跟在后面即可；写成 `-- --selftest-host` 也行（脚本事先剔掉那个 `--`）。

.EXAMPLE
    .\tools\dev-run.ps1

.EXAMPLE
    .\tools\dev-run.ps1 -Release C:\proj\demo.hmi

.EXAMPLE
    .\tools\dev-run.ps1 -NoBuild --selftest-host
#>
[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$NoBuild,
    [switch]$Clean,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$EditorArgs
)

$ErrorActionPreference = 'Stop'

# PowerShell 没有 POSIX 的 `--` 分隔符：写成 `-- --selftest-host` 时那个 `--` 会落进
# $EditorArgs 并被原样转发，编辑器就会把 `--` 当成第一个参数（args[0] 判断全失效）。
# 这里统一剔掉，两种写法都能用。
$EditorArgs = @($EditorArgs | Where-Object { $_ -ne '--' })

$Root = Split-Path -Parent $PSScriptRoot
$Config = if ($Release) { 'Release' } else { 'Debug' }
$Proj = Join-Path $Root 'src\HmiEditor.App\HmiEditor.App.csproj'

if (-not (Test-Path $Proj)) {
    Write-Error "找不到项目文件：$Proj"
    exit 2
}

# 运行时：只补默认值，不覆盖用户显式设置
if (-not $env:CHENGE_RUNTIME_DIR) {
    $candidate = Join-Path $Root 'runtime\win-x64'
    if (Test-Path $candidate) {
        $env:CHENGE_RUNTIME_DIR = $candidate
        Write-Host "运行时：$candidate（仓库内兜底，要换就设 `$env:CHENGE_RUNTIME_DIR）"
    }
}

if ($Clean) {
    Write-Host "== dotnet clean ($Config) =="
    dotnet clean $Proj -c $Config --nologo -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not $NoBuild) {
    Write-Host "== dotnet build ($Config) =="
    dotnet build $Proj -c $Config --nologo -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "== 启动编辑器 =="
if ($EditorArgs -and $EditorArgs.Count -gt 0) {
    dotnet run --project $Proj -c $Config --no-build -- @EditorArgs
} else {
    dotnet run --project $Proj -c $Config --no-build
}
exit $LASTEXITCODE
