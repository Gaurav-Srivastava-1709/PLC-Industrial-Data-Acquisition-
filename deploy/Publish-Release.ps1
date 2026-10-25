param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Output = "$PSScriptRoot\..\artifacts\WTT-$Version",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
if (-not (Test-Path -LiteralPath (Split-Path -Parent $Output))) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Output) | Out-Null
}
if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }
New-Item -ItemType Directory -Path $Output | Out-Null

if (-not $SkipTests) {
    dotnet test "$root\WTT.slnx" -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}
dotnet publish "$root\src\WTT.UI\WTT.UI.csproj" -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=false -p:PublishTrimmed=false -o "$Output\UI"
if ($LASTEXITCODE -ne 0) { throw "UI publish failed." }
dotnet publish "$root\src\WTT.Service\WTT.Service.csproj" -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=false -p:PublishTrimmed=false -o "$Output\Service"
if ($LASTEXITCODE -ne 0) { throw "Service publish failed." }
Push-Location $root
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "Local .NET tools restore failed." }
    if (-not (Test-Path -LiteralPath "$Output\Database")) { New-Item -ItemType Directory -Path "$Output\Database" | Out-Null }
    dotnet tool run dotnet-ef migrations bundle --project "$root\src\WTT.Infrastructure\WTT.Infrastructure.csproj" --startup-project "$root\src\WTT.Infrastructure\WTT.Infrastructure.csproj" --context AppDbContext --self-contained -r win-x64 -o "$Output\Database\WTT.Database.exe" --force
    if ($LASTEXITCODE -ne 0) { throw "Database migration bundle failed." }
}
finally { Pop-Location }

Copy-Item "$root\deploy" "$Output\deploy" -Recurse -Force
Copy-Item "$root\docs" "$Output\docs" -Recurse -Force
@{ version = $Version; createdUtc = (Get-Date).ToUniversalTime().ToString("o"); runtime = "win-x64" } |
    ConvertTo-Json | Set-Content -LiteralPath "$Output\release.json" -Encoding UTF8
if (Test-Path -LiteralPath "$Output.zip") { Remove-Item -LiteralPath "$Output.zip" -Force }
# Windows Explorer compatibility ke liye standard .NET ZipFile use kar rahe hain
# tar.exe se relative path './' store hota tha jisse Windows Explorer empty show karta tha
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path $Output).Path, "$Output.zip", [System.IO.Compression.CompressionLevel]::Optimal, $false)
if (-not (Test-Path -LiteralPath "$Output.zip")) { throw "Release ZIP creation failed." }
"Release created: $Output.zip"
