param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "work\publish\win-x64"
$buildOutput = Join-Path $root "work\package-build\"
$releaseDir = Join-Path $root "outputs\Meowser-$Version"
$packageDir = Join-Path $releaseDir "packages"
$appProject = Join-Path $root "src\Meowser.App\Meowser.App.csproj"
$icon = Join-Path $root "src\Meowser.App\Assets\Meowser.ico"
$vpk = Join-Path $root "work\tools\vpk.exe"

if (-not (Test-Path -LiteralPath $vpk)) {
    dotnet tool install vpk --version 1.2.0 --tool-path (Join-Path $root "work\tools")
}
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

dotnet publish $appProject -c $Configuration -r win-x64 --self-contained true `
    -p:BaseOutputPath=$buildOutput -p:Version=$Version -p:PublishSingleFile=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Meowser publish failed with exit code $LASTEXITCODE." }

& $vpk pack --packId Meowser --packVersion $Version --packDir $publishDir `
    --mainExe Meowser.exe --packTitle Meowser --packAuthors Meowser `
    --icon $icon --framework webview2 --runtime win-x64 --outputDir $packageDir
if ($LASTEXITCODE -ne 0) { throw "Velopack packaging failed with exit code $LASTEXITCODE." }

Write-Host "Installer and update packages are ready in: $packageDir"
