# Legacy 코퍼스 빌드·테스트 (Windows 전용)
# 1) 패키지 복원  2) .NET Framework 4.5 참조 어셈블리 설치(없을 때만)  3) MSBuild 빌드  4) vstest 실행
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

function Fail($step, $message) {
    Write-Host "::error::[$step] $message"
    exit 1
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { Fail 'vswhere' "vswhere.exe 없음: $vswhere" }

# 1. 패키지 복원
Write-Host '== 1. nuget restore'
$nuget = (Get-Command nuget.exe -ErrorAction SilentlyContinue).Source
if (-not $nuget) { Fail 'restore' 'nuget.exe를 찾을 수 없습니다.' }
& $nuget restore Legacy.sln -PackagesDirectory packages -NonInteractive
if ($LASTEXITCODE -ne 0) { Fail 'restore' "nuget restore 실패 ($LASTEXITCODE)" }

# 2. .NET Framework 4.5 참조 어셈블리 (Visual Studio 2022에 기본 포함되지 않을 수 있음)
Write-Host '== 2. .NET Framework 4.5 reference assemblies'
$refTarget = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5'
if (Test-Path (Join-Path $refTarget 'mscorlib.dll')) {
    Write-Host "이미 설치됨: $refTarget"
} else {
    $tmp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
    & $nuget install Microsoft.NETFramework.ReferenceAssemblies.net45 -Version 1.0.3 -OutputDirectory $tmp -NonInteractive
    if ($LASTEXITCODE -ne 0) { Fail 'refasm' "참조 어셈블리 패키지 설치 실패 ($LASTEXITCODE)" }
    $source = Join-Path $tmp 'Microsoft.NETFramework.ReferenceAssemblies.net45.1.0.3\build\.NETFramework\v4.5'
    if (-not (Test-Path $source)) { Fail 'refasm' "패키지에 v4.5 폴더가 없습니다: $source" }
    New-Item -ItemType Directory -Force -Path $refTarget | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $refTarget -Recurse -Force
    Write-Host "설치함: $refTarget"
}

# 3. MSBuild
Write-Host '== 3. MSBuild'
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { Fail 'msbuild' 'MSBuild.exe를 찾을 수 없습니다.' }
Write-Host "MSBuild: $msbuild"
& $msbuild Legacy.sln /t:Build /p:Configuration=Debug /nologo /v:minimal /nr:false
if ($LASTEXITCODE -ne 0) { Fail 'msbuild' "Legacy.sln 빌드 실패 ($LASTEXITCODE)" }

# 4. vstest
Write-Host '== 4. vstest'
$vstest = & $vswhere -latest -products * -find '**\TestPlatform\vstest.console.exe' | Select-Object -First 1
if (-not $vstest) { Fail 'vstest' 'vstest.console.exe를 찾을 수 없습니다.' }
$adapter = Join-Path $root 'packages\MSTest.TestAdapter.2.2.10\build\_common'
& $vstest (Join-Path $root 'Shop.Tests\bin\Debug\Shop.Tests.dll') "/TestAdapterPath:$adapter"
if ($LASTEXITCODE -ne 0) { Fail 'vstest' "Shop.Tests 실패 ($LASTEXITCODE)" }

Write-Host '== Legacy 코퍼스 빌드·테스트 성공'
