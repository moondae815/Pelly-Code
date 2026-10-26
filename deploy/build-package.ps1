#requires -Version 7.0
<#
.SYNOPSIS
  Builds the air-gap import zip pelly-<Version>-win-x64.zip (Task 9, PRD §11.1), or with -Package field-kit the field kit
  pelly-field-kit-<Version>-win-x64.zip (pelly-eval.exe, field guides, suite templates; M4 Task 9).

.DESCRIPTION
  1. dotnet tool restore, then CycloneDX writes sbom.cdx.json for the package's project (src/PellyCoder.Cli or
     eval/PellyCoder.Eval; transitive packages,
     win-x64 runtime). The self-contained .NET runtime packs are added as components (they are not NuGet
     package references, so CycloneDX does not list them).
  2. THIRD-PARTY-NOTICES.md is generated from the SBOM. A license that is not MIT, Apache-2.0, BSD-2-Clause or
     BSD-3-Clause fails the script (R7). A component without a license id is resolved from its nuspec in the
     local NuGet cache; anything still unresolved fails. deploy/license-exceptions.json may allow a package by
     exact id, version and license file SHA-256, only with a human approver recorded.
  3. Only the files in the allow-list (deploy/package-files.txt or deploy/field-kit-files.txt) are staged. A line
     "<dir>/** <- publish:<dir>/**" stages every file under that publish folder (missing or empty folder fails).
     deploy/version-record.json must match the Version (pelly only; the field kit records on site).
  4. SHA256SUMS.txt: "<sha256 hex>  <relative path>" for every other file.
  5. The zip.

.PARAMETER Package
  pelly (default): the air-gap zip from src/PellyCoder.Cli. field-kit: the field kit from eval/PellyCoder.Eval.
  The license gate, the exception file and the provisional-approval refusal at 1.0 apply to both alike.

.PARAMETER PublishDir
  Output folder of the win-x64 single-file publish (the publish-check command) of the package's project.

.PARAMETER OutDir
  Folder for the staging folder, the zip and intermediate files. Recreated.

.PARAMETER LicenseExceptions
  Approved license exceptions. Default deploy/license-exceptions.json. CI passes altered copies to check that a
  changed version or license hash fails the gate.
  An entry with "packages" (e.g. ["pelly"]) applies only to those -Package values.

.PARAMETER BuildProps
  Directory.Build.props to read the Version from. Default the repository's. CI passes an altered copy (Version 1.0.0)
  to check that a provisional approval cannot ship in 1.0.
#>
[CmdletBinding()]
param(
    [ValidateSet('pelly', 'field-kit')][string]$Package = 'pelly',
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$LicenseExceptions = (Join-Path $PSScriptRoot 'license-exceptions.json'),
    [string]$BuildProps = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Repo = Split-Path -Parent $PSScriptRoot
$Kind = switch ($Package) {
    'pelly' { @{ Project = 'src/PellyCoder.Cli/PellyCoder.Cli.csproj'; List = 'package-files.txt'; Prefix = 'pelly'; SbomName = 'pelly'
            Title = 'pelly {0} ({1}) 반입본'; VersionRecord = $true } }
    'field-kit' { @{ Project = 'eval/PellyCoder.Eval/PellyCoder.Eval.csproj'; List = 'field-kit-files.txt'; Prefix = 'pelly-field-kit'; SbomName = 'pelly-eval'
            Title = 'pelly 현장 키트 {0} ({1}, pelly-eval.exe)'; VersionRecord = $false } }
}
$Project = Join-Path $Repo $Kind.Project
$ListFile = $Kind.List
$Rid = 'win-x64'
$AllowedLicenses = @('MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause')
$RuntimePacks = @("Microsoft.NETCore.App.Runtime.$Rid", "Microsoft.NETCore.App.Host.$Rid")
$Utf8 = [System.Text.UTF8Encoding]::new($false)

function Invoke-Native([string]$File, [string[]]$Arguments) {
    Write-Host "> $File $($Arguments -join ' ')"
    & $File @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$File exited with $LASTEXITCODE" }
}

function Write-Utf8Lf([string]$Path, [string[]]$Lines) {
    [System.IO.File]::WriteAllText($Path, (($Lines -join "`n") + "`n"), $Utf8)
}

# --- version -----------------------------------------------------------------------------------------------
$props = [xml](Get-Content -Raw $BuildProps)
$Version = @($props.Project.PropertyGroup | ForEach-Object { $_.SelectSingleNode('Version') } | Where-Object { $_ })[0].InnerText
if ($Version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { throw "unexpected Version in Directory.Build.props: '$Version'" }
$Name = "$($Kind.Prefix)-$Version-$Rid"
Write-Host "package: $Name"

$PublishDir = (Resolve-Path $PublishDir).Path
if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
$null = New-Item -ItemType Directory -Force $OutDir
$OutDir = (Resolve-Path $OutDir).Path
$Generated = Join-Path $OutDir 'generated'
$Staging = Join-Path $OutDir $Name
$ZipPath = Join-Path $OutDir "$Name.zip"
$null = New-Item -ItemType Directory $Generated, $Staging

# --- 1. SBOM -----------------------------------------------------------------------------------------------
Push-Location $Repo
try {
    Invoke-Native 'dotnet' @('tool', 'restore')
    # Same RID as the publish so the assets file has the win-x64 target and the runtime pack versions.
    Invoke-Native 'dotnet' @('restore', $Project, '-r', $Rid)
    Invoke-Native 'dotnet' @('CycloneDX', $Project, '--runtime', $Rid, '--disable-package-restore',
        '--output-format', 'Json', '--output', $Generated, '--filename', 'sbom.cdx.json',
        '--set-name', $Kind.SbomName, '--set-version', $Version)
}
finally { Pop-Location }

$assets = Get-Content -Raw (Join-Path (Split-Path -Parent $Project) 'obj/project.assets.json') | ConvertFrom-Json -AsHashtable
$PackageRoots = @($assets['packageFolders'].Keys)

function Find-Nuspec([string]$Id, [string]$Ver) {
    foreach ($root in $PackageRoots) {
        $dir = Join-Path $root $Id.ToLowerInvariant() $Ver.ToLowerInvariant()
        $file = Join-Path $dir "$($Id.ToLowerInvariant()).nuspec"
        if (Test-Path $file) { return $file }
    }
    return $null
}

$sbomPath = Join-Path $Generated 'sbom.cdx.json'
$sbom = Get-Content -Raw $sbomPath | ConvertFrom-Json -AsHashtable
$components = [System.Collections.Generic.List[object]]::new()
foreach ($c in $sbom['components']) { $components.Add($c) }

# Runtime packs carried by the self-contained single file. They are not package references: restore lists them
# under downloadDependencies (NuGet cache), or the SDK already has them in <dotnet root>/packs (the apphost pack of
# the machine's own RID, e.g. win-x64 on the Windows CI runner). The packs folder has no nuspec, and the LICENSE.txt
# of a Windows .NET installation is the Microsoft Software License Terms of the installer, not the pack's license.
# So the same id and version is fetched into the NuGet cache (PackageDownload) and resolved from its nuspec like
# every other package.
$dotnetRoot = $env:DOTNET_ROOT
if (-not $dotnetRoot -or -not (Test-Path (Join-Path $dotnetRoot 'packs'))) {
    $cmd = (Get-Command dotnet).Source
    $target = (Get-Item $cmd).ResolvedTarget
    $dotnetRoot = Split-Path -Parent $(if ($target) { $target } else { $cmd })
}
$downloads = @($assets['project']['frameworks'].Values | ForEach-Object { $_['downloadDependencies'] } | Where-Object { $_ })
$downloadVersions = @{}
foreach ($d in $downloads) {
    if ($d['version'] -match '\A\[([0-9][^,\]]*), *\1\]\z') { $downloadVersions[$d['name']] = $Matches[1] }
}
foreach ($pack in $RuntimePacks) {
    $origin = 'self-contained .NET runtime pack (NuGet download, added by build-package.ps1)'
    if ($downloadVersions.Contains($pack)) {
        $ver = $downloadVersions[$pack]
    }
    else {
        $packDir = Join-Path $dotnetRoot 'packs' $pack
        $dirs = @(if (Test-Path $packDir) { Get-ChildItem -Directory $packDir | ForEach-Object Name })
        $known = @($dirs | Where-Object { $downloadVersions.Values -contains $_ })
        $ver = if ($known.Count -eq 1) { $known[0] } elseif ($dirs.Count -eq 1) { $dirs[0] } else { $null }
        if (-not $ver) { throw "runtime pack $pack not in downloadDependencies and no single version under $packDir (found: $($dirs -join ', '))" }
        if (-not (Find-Nuspec $pack $ver)) {
            $probeDir = Join-Path $OutDir 'runtime-pack-nuspec'
            $null = New-Item -ItemType Directory -Force $probeDir
            $probe = Join-Path $probeDir 'runtime-pack-nuspec.csproj'
            Write-Utf8Lf $probe @(
                '<Project Sdk="Microsoft.NET.Sdk">',
                '  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup>',
                "  <ItemGroup><PackageDownload Include=`"$pack`" Version=`"[$ver]`" /></ItemGroup>",
                '</Project>')
            Invoke-Native 'dotnet' @('restore', $probe)
        }
        $origin = 'self-contained .NET runtime pack (SDK packs folder; license from the same id and version in the NuGet cache; added by build-package.ps1)'
    }
    $component = [ordered]@{
        'type'       = 'framework'
        'bom-ref'    = "pkg:nuget/$pack@$ver"
        'name'       = $pack
        'version'    = $ver
        'scope'      = 'required'
        'purl'       = "pkg:nuget/$pack@$ver"
        'properties' = @(@{ 'name' = 'pelly:origin'; 'value' = $origin })
    }
    $components.Add($component)
}
$sbom['components'] = $components.ToArray()
[System.IO.File]::WriteAllText($sbomPath, ($sbom | ConvertTo-Json -Depth 64), $Utf8)

# --- 2. licenses -------------------------------------------------------------------------------------------
$exceptions = @()
if (Test-Path $LicenseExceptions) { $exceptions = @(Get-Content -Raw $LicenseExceptions | ConvertFrom-Json -AsHashtable) }
# An exception with "packages" applies only to those packages (the GUI's ANGLE natives are in pelly, not in the field kit);
# without it, to every package. Outside its packages it is neither matched nor reported as stale.
$exceptions = @($exceptions | Where-Object { -not $_.Contains('packages') -or @($_['packages']) -contains $Package })
Write-Host "license exceptions: $LicenseExceptions ($($exceptions.Count))"

function Test-Expression([string]$Expr) {
    # Plain id, "A OR B" (one allowed is enough) or "A AND B" (all must be allowed). Anything else is unknown.
    $e = $Expr.Trim()
    if ($e -match '[()]' -or $e -cmatch '\bWITH\b') { return $false }
    if ($e -cmatch '\bOR\b' -and $e -cmatch '\bAND\b') { return $false }
    if ($e -cmatch '\bOR\b') { return [bool](@($e -csplit '\s+OR\s+' | Where-Object { $AllowedLicenses -ccontains $_.Trim() }).Count) }
    $parts = @($e -csplit '\s+AND\s+')
    return @($parts | Where-Object { $AllowedLicenses -cnotcontains $_.Trim() }).Count -eq 0
}

function Resolve-License($Component) {
    $id = $Component['name']; $ver = $Component['version']
    $ids = @()
    if ($Component.Contains('licenses')) {
        foreach ($l in $Component['licenses']) {
            if ($l.Contains('expression')) { $ids += $l['expression'] }
            elseif ($l.Contains('license') -and $l['license'].Contains('id')) { $ids += $l['license']['id'] }
            elseif ($l.Contains('license') -and $l['license'].Contains('url') -and
                $l['license']['url'] -match '\Ahttps://licenses\.nuget\.org/(.+)\z') { $ids += [uri]::UnescapeDataString($Matches[1]) }
        }
    }
    if ($ids.Count -gt 0) {
        $expr = $ids -join ' AND '
        return @{ License = $expr; Source = 'SBOM'; Allowed = (Test-Expression $expr); FileSha = $null }
    }

    $nuspec = Find-Nuspec $id $ver
    if (-not $nuspec) { return @{ License = '(unknown)'; Source = 'nuspec not found'; Allowed = $false; FileSha = $null } }
    $xml = [xml](Get-Content -Raw $nuspec)
    $node = $xml.package.metadata.SelectSingleNode("*[local-name()='license']")
    if ($node -and $node.GetAttribute('type') -eq 'expression') {
        return @{ License = $node.InnerText; Source = 'nuspec expression'; Allowed = (Test-Expression $node.InnerText); FileSha = $null }
    }
    if ($node -and $node.GetAttribute('type') -eq 'file') {
        $file = Join-Path (Split-Path $nuspec) $node.InnerText
        $sha = if (Test-Path $file) { (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant() } else { $null }
        $first = if (Test-Path $file) { (Get-Content $file | Where-Object { $_.Trim() } | Select-Object -First 1).Trim() } else { '(missing)' }
        return @{ License = "file $($node.InnerText): $first"; Source = 'nuspec license file'; Allowed = $false; FileSha = $sha; File = $file }
    }
    $url = $xml.package.metadata.SelectSingleNode("*[local-name()='licenseUrl']")
    if ($url -and $url.InnerText -match '\Ahttps://licenses\.nuget\.org/(.+)\z') {
        $expr = [uri]::UnescapeDataString($Matches[1])
        return @{ License = $expr; Source = 'nuspec licenseUrl'; Allowed = (Test-Expression $expr); FileSha = $null }
    }
    return @{ License = '(unknown)'; Source = 'nuspec without license'; Allowed = $false; FileSha = $null }
}

$rows = @()
$rejected = @()
$approved = @()
foreach ($c in ($sbom['components'] | Sort-Object { $_['name'] }, { $_['version'] })) {
    $r = Resolve-License $c
    $note = $r.Source
    $origin = if ($c.Contains('properties')) { @($c['properties'] | Where-Object { $_['name'] -eq 'pelly:origin' } | ForEach-Object { $_['value'] }) } else { @() }
    if ($origin -match 'SDK packs') { $note = "$($r.Source) (SDK packs 폴더의 팩, 같은 id·버전의 nuspec)" }
    if (-not $r.Allowed) {
        $ex = $exceptions | Where-Object {
            $_['package'] -eq $c['name'] -and $_['version'] -eq $c['version'] -and
            $r.FileSha -and $_['licenseFileSha256'] -eq $r.FileSha -and -not [string]::IsNullOrWhiteSpace($_['approvedBy'])
        } | Select-Object -First 1
        if ($ex) {
            $note = "$($r.Source); 예외 승인 (아래 「예외 승인」 절)"
            $approved += [pscustomobject]@{ Component = $c; Resolved = $r; Exception = $ex }
        }
        else { $rejected += "$($c['name']) $($c['version']): $($r.License) [$($r.Source)$(if ($r.FileSha) { ", sha256 $($r.FileSha)" })]" }
    }
    $scope = if ($c.Contains('scope')) { $c['scope'] } else { 'required' }
    $rows += "| $($c['name']) | $($c['version']) | $($r.License -replace '\|', '\|') | $scope | $note |"
}
# An exception that matched nothing is stale: the package, its version or its license file changed. Fail so a
# license change cannot ride an old approval (the changed package is also reported above as not allowed).
$stale = @($exceptions | Where-Object { $ex = $_; -not ($approved | Where-Object { $_.Exception -eq $ex }) } |
    ForEach-Object { "$($_['package']) $($_['version']) (licenseFileSha256 $($_['licenseFileSha256']))" })
$stale | ForEach-Object { Write-Host "license exception matched no component: $_" }
$rejected | ForEach-Object { Write-Host "license not allowed: $_" }
if ($rejected.Count -gt 0 -or $stale.Count -gt 0) {
    throw "license gate: $($rejected.Count) package(s) outside MIT/Apache-2.0/BSD-2-Clause/BSD-3-Clause (R7), $($stale.Count) stale exception(s)"
}
foreach ($a in $approved) {
    foreach ($key in 'licenseName', 'reason', 'approvedBy', 'shipAs') {
        if ([string]::IsNullOrWhiteSpace($a.Exception[$key])) { throw "license exception $($a.Component['name']): '$key' is empty" }
    }
    # R6: 1.0 ships only after the field check (M4); a provisional (잠정) approval must be replaced by a human one first.
    if ([int]$Version.Split('.')[0] -ge 1 -and $a.Exception['approvedBy'].Contains('잠정')) {
        throw "license exception $($a.Component['name']): provisional approval cannot ship in $Version (approvedBy: $($a.Exception['approvedBy']))"
    }
    $dest = Join-Path $Generated $a.Exception['shipAs']
    $null = New-Item -ItemType Directory -Force (Split-Path $dest)
    Copy-Item $a.Resolved.File $dest
}

$notices = @(
    '# Third-Party Notices',
    '',
    "$($Kind.Title -f $Version, $Rid)에 들어 있는 서드파티 구성 요소입니다. ``sbom.cdx.json``에서 만들었습니다.",
    '',
    '* 범위 `required`는 실행 파일에 들어 있는 것, `excluded`는 빌드에만 쓰고 넣지 않은 것입니다.',
    '* 근거 `SBOM`은 CycloneDX가 적은 값, `nuspec …`은 NuGet 캐시의 패키지 정보에서 읽은 값입니다.',
    "* .NET 런타임($($RuntimePacks -join ', '))은 단일 실행 파일 안에 들어 있습니다(self-contained). NuGet 참조가 아니어서 build-package.ps1이 SBOM에 ``framework``로 더했습니다.",
    '',
    '## NuGet 패키지와 .NET 런타임',
    '',
    '| 패키지 | 버전 | 라이선스 | 범위 | 근거 |',
    '| :--- | :--- | :--- | :--- | :--- |'
) + $rows
if ($approved.Count -gt 0) {
    $notices += @('', '## 예외 승인', '',
        'MIT·Apache-2.0·BSD-2-Clause·BSD-3-Clause가 아니지만 사람이 승인해 넣은 구성 요소입니다(deploy/license-exceptions.json).',
        '버전이나 라이선스 파일이 바뀌면 승인이 무효가 되어 패키지 빌드가 실패합니다.', '')
    foreach ($a in $approved) {
        $ex = $a.Exception
        $notices += @(
            "### $($a.Component['name']) $($a.Component['version'])",
            '',
            "* 라이선스: $($ex['licenseName'])",
            "* 들어 있는 이유: $($ex['reason'])",
            "* 라이선스 전문: ``$($ex['shipAs'])`` (이 zip에 들어 있음, SHA-256 ``$($a.Resolved.FileSha)``)",
            "* 승인: $($ex['approvedBy'])",
            '')
    }
}
# Components that are not NuGet packages (embedded tokenizer files) are kept by hand in the repository notices.
$repoNotices = Get-Content (Join-Path $Repo 'THIRD-PARTY-NOTICES.md') | Select-Object -Skip 1
$notices += @('', '## NuGet 밖의 구성 요소') + ($repoNotices | ForEach-Object { $_ -replace '\A## ', '### ' })
Write-Utf8Lf (Join-Path $Generated 'THIRD-PARTY-NOTICES.md') $notices

# --- 3. staging by allow-list ------------------------------------------------------------------------------
$entries = @()
foreach ($raw in Get-Content (Join-Path $PSScriptRoot $ListFile)) {
    $line = $raw.Trim()
    if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
    if ($line -match '\A(\S+)/\*\* <- publish:(\S+)/\*\*\z') {
        # A whole publish folder (the Roslyn build hosts beside the exe): every file under it, same relative path.
        $zipDir = $Matches[1]
        $srcRel = $Matches[2]
        $srcDir = Join-Path $PublishDir $srcRel
        if (-not (Test-Path -PathType Container $srcDir)) { throw "${zipDir}/**: source folder $srcDir not found" }
        $dirFiles = @(Get-ChildItem -Recurse -File $srcDir)
        if ($dirFiles.Count -eq 0) { throw "${zipDir}/**: source folder $srcDir is empty" }
        foreach ($f in $dirFiles) {
            $rel = [System.IO.Path]::GetRelativePath($srcDir, $f.FullName).Replace('\', '/')
            $entries += [pscustomobject]@{ ZipPath = "$zipDir/$rel"; Source = "publish:$srcRel/$rel" }
        }
        continue
    }
    if ($line -notmatch '\A(\S+) <- (\S+)\z') { throw "${ListFile}: bad line '$raw'" }
    $entries += [pscustomobject]@{ ZipPath = $Matches[1]; Source = $Matches[2] }
}
$dup = $entries | Group-Object ZipPath | Where-Object Count -gt 1
if ($dup) { throw "${ListFile}: duplicate $($dup.Name -join ', ')" }

foreach ($a in $approved) {
    if (-not ($entries | Where-Object { $_.ZipPath -eq $a.Exception['shipAs'] -and $_.Source -eq 'generated' })) {
        throw "license exception $($a.Component['name']): shipAs '$($a.Exception['shipAs'])' must be a 'generated' line in $ListFile"
    }
}

if ($Kind.VersionRecord) {
    $record = Get-Content -Raw (Join-Path $PSScriptRoot 'version-record.json') -ErrorAction SilentlyContinue
    if (-not $record) { throw 'deploy/version-record.json is missing — run dotnet run --project eval/PellyCoder.Eval -- record --out deploy/version-record.json on the development PC and commit it (R8)' }
    $recordVersion = ($record | ConvertFrom-Json).pellyVersion
    if ($recordVersion -ne $Version) { throw "version-record.json pellyVersion '$recordVersion' differs from $Version — run the record command again (dotnet run --project eval/PellyCoder.Eval -- record --out deploy/version-record.json)" }
}

foreach ($e in $entries) {
    if ($e.ZipPath -eq 'SHA256SUMS.txt') { continue }
    $src = switch -Regex ($e.Source) {
        '\Agenerated\z' { Join-Path $Generated $e.ZipPath }
        '\Apublish:(.+)\z' { Join-Path $PublishDir $Matches[1] }
        default { Join-Path $Repo $e.Source }
    }
    if (-not (Test-Path -PathType Leaf $src)) { throw "$($e.ZipPath): source $src not found" }
    $dest = Join-Path $Staging $e.ZipPath
    $null = New-Item -ItemType Directory -Force (Split-Path $dest)
    Copy-Item $src $dest
}

# --- 4. SHA256SUMS -----------------------------------------------------------------------------------------
$files = Get-ChildItem -Recurse -File $Staging |
    ForEach-Object { [pscustomobject]@{ Full = $_.FullName; Rel = [System.IO.Path]::GetRelativePath($Staging, $_.FullName).Replace('\', '/') } } |
    Sort-Object Rel -Culture ([cultureinfo]::InvariantCulture) -CaseSensitive
$sums = $files | ForEach-Object { "$((Get-FileHash -Algorithm SHA256 $_.Full).Hash.ToLowerInvariant())  $($_.Rel)" }
Write-Utf8Lf (Join-Path $Staging 'SHA256SUMS.txt') $sums

$staged = @(Get-ChildItem -Recurse -File $Staging | ForEach-Object { [System.IO.Path]::GetRelativePath($Staging, $_.FullName).Replace('\', '/') } | Sort-Object)
$expected = @($entries.ZipPath | Sort-Object)
if (Compare-Object $staged $expected) { throw "staged files differ from ${ListFile}: $(Compare-Object $staged $expected | Out-String)" }

# --- 5. zip ------------------------------------------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($rel in $staged) {
        $null = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $Staging $rel), $rel,
            [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $zip.Dispose() }

$size = (Get-Item $ZipPath).Length
Write-Host ("zip: {0} ({1:N1} MB, {2} files)" -f $ZipPath, ($size / 1MB), $staged.Count)
