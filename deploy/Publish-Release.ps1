param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Output = "$PSScriptRoot\..\artifacts\WTT-$Version"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
if (-not (Test-Path -LiteralPath (Split-Path -Parent $Output))) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Output) | Out-Null
}

dotnet test "$root\WTT.slnx" -c Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
dotnet publish "$root\src\WTT.UI\WTT.UI.csproj" -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=false -p:PublishTrimmed=false -o "$Output\UI"
if ($LASTEXITCODE -ne 0) { throw "UI publish failed." }
dotnet publish "$root\src\WTT.Service\WTT.Service.csproj" -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=false -p:PublishTrimmed=false -o "$Output\Service"
if ($LASTEXITCODE -ne 0) { throw "Service publish failed." }
dotnet ef migrations bundle --project "$root\src\WTT.Infrastructure\WTT.Infrastructure.csproj" --startup-project "$root\src\WTT.Infrastructure\WTT.Infrastructure.csproj" --context AppDbContext --self-contained -r win-x64 -o "$Output\Database\WTT.Database.exe" --force
if ($LASTEXITCODE -ne 0) { throw "Database migration bundle failed." }

Copy-Item "$root\deploy" "$Output\deploy" -Recurse -Force
Copy-Item "$root\docs" "$Output\docs" -Recurse -Force
@{ version = $Version; createdUtc = (Get-Date).ToUniversalTime().ToString("o"); runtime = "win-x64" } |
    ConvertTo-Json | Set-Content -LiteralPath "$Output\release.json" -Encoding UTF8
if (Test-Path -LiteralPath "$Output.zip") { Remove-Item -LiteralPath "$Output.zip" -Force }
& tar.exe -a -c -f "$Output.zip" -C "$Output" .
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath "$Output.zip")) { throw "Release ZIP creation failed." }
"Release created: $Output.zip"
