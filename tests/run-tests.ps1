<#
.SYNOPSIS
    按分类运行 VSManager 测试。/ Runs VSManager tests by category.
.DESCRIPTION
    NoPopup（默认）：只运行完全不弹窗的测试（不显示窗口、不抢焦点、不用剪贴板、不启动子进程），日常修改后运行这一类即可。
    UI：显示窗口 / 抢焦点 / 使用剪贴板的界面测试，运行期间请勿在其他窗口输入文字。
    Console：启动 git、cmd、PowerShell 等命令行进程的测试（需要 git 在 PATH 中）。
    All：全部测试。
    NoPopup (default): only tests that never pop up (no window, no focus change, no clipboard, no child process); enough after routine changes.
    UI: tests that show windows / take focus / use the clipboard; avoid typing in other windows while they run.
    Console: tests that start command-line processes such as git, cmd or PowerShell (git must be on PATH).
    All: every test.
.EXAMPLE
    .\tests\run-tests.ps1
    .\tests\run-tests.ps1 -Scope All
    .\tests\run-tests.ps1 -Scope NoPopup -Filter "FullyQualifiedName~TaskDispatcher"
#>
param(
    [ValidateSet('NoPopup', 'UI', 'Console', 'All')]
    [string]$Scope = 'NoPopup',
    # 额外的 dotnet test 筛选条件，与分类条件同时生效。/ Extra dotnet test filter, combined with the category filter.
    [string]$Filter = '',
    [string]$Configuration = 'Debug',
    # 输出目录；默认放在测试项目 bin 下，避免与正在运行的 VSManager 争用 bin\Debug。/ Output folder; defaults under the test project's bin so a running VSManager does not lock bin\Debug.
    [string]$OutDir = (Join-Path $PSScriptRoot 'VSManager.Tests\bin\run-tests\'),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'VSManager.Tests\VSManager.Tests.csproj'
$scopeFilter = @{
    NoPopup = 'TestCategory!=UI&TestCategory!=Console'
    UI      = 'TestCategory=UI'
    Console = 'TestCategory=Console'
    All     = ''
}[$Scope]
$parts = @($scopeFilter, $Filter) | Where-Object { $_ }
$combined = ($parts | ForEach-Object { if ($parts.Count -gt 1) { "($_)" } else { $_ } }) -join '&'

if (($Scope -eq 'Console' -or $Scope -eq 'All') -and -not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Warning '未在 PATH 中找到 git，命令行类测试会失败 / git was not found on PATH; Console tests will fail'
}

$testArgs = @('test', $project, '-c', $Configuration, '-nologo', "-p:TestScope=$Scope", "-p:OutDir=$($OutDir.TrimEnd('\') + '\')")
if ($NoBuild) { $testArgs += '--no-build' }
if ($combined) { $testArgs += @('--filter', $combined) }
Write-Host "dotnet $($testArgs -join ' ')"
& dotnet @testArgs
exit $LASTEXITCODE