# S8 배포 형태 셋을 만든다: folder / singlefile / singlefile-buildhost
param(
    [Parameter(Mandatory = $true)][string]$Rid,
    [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'S8SemanticLoad/S8SemanticLoad.csproj'
$common = @('-c', 'Release', '-r', $Rid, '--self-contained')
$single = @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')

dotnet publish $proj @common -o (Join-Path $Out 'folder')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish $proj @common @single -o (Join-Path $Out 'singlefile')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish $proj @common @single '-p:S8BuildHostOutside=true' -o (Join-Path $Out 'singlefile-buildhost')
exit $LASTEXITCODE
