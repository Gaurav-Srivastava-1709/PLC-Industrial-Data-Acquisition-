$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
$serviceExe = "$root\src\WTT.Service\bin\Debug\net10.0-windows\win-x64\WTT.Service.exe"
$uiExe = "$root\src\WTT.UI\bin\Debug\net10.0-windows\WTT.exe"

if (-not (Test-Path -LiteralPath $serviceExe) -or -not (Test-Path -LiteralPath $uiExe)) {
    throw "Debug build not found. In Visual Studio run Build > Rebuild Solution first."
}
$existingUi = Get-Process -Name "WTT" -ErrorAction SilentlyContinue
if ($existingUi) {
    $visible = $existingUi | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
    if ($visible) { throw "WTT UI is already open. Use Alt+Tab and select '$($visible.MainWindowTitle)'." }
    $existingUi | Where-Object { $_.Path -eq $uiExe } | Stop-Process
}
$existingTestService = Get-Process -Name "WTT.Service" -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $serviceExe }
if ($existingTestService) { $existingTestService | Stop-Process }

$service = $null
try {
    $service = Start-Process -FilePath $serviceExe -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
    if ($service.HasExited) { throw "WTT test service could not start. Check C:\ProgramData\WTT\Logs." }
    $ui = Start-Process -FilePath $uiExe -PassThru -Wait
    if ($ui.ExitCode -ne 0) { throw "WTT UI exited with code $($ui.ExitCode)." }
}
finally {
    if ($service -and -not $service.HasExited) { Stop-Process -Id $service.Id }
}
