$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\SwingStudio\SwingStudio.csproj'
$publishDir = Join-Path $root 'publish\win-x64'
$script = Join-Path $PSScriptRoot 'SwingStudio.iss'

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
    Write-Host 'Inno Setup 6 was not found. Install it with:'
    Write-Host '  winget install --id JRSoftware.InnoSetup -e'
    exit 1
}

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish $project -p:PublishProfile=win-x64 --nologo
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $iscc /Q $script
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$setup = Get-ChildItem (Join-Path $PSScriptRoot 'Output') -Filter 'SwingStudio-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
Write-Host "Installer: $($setup.FullName)"
