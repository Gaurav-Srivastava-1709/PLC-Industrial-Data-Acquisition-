$serviceName = "WTTData"
Unregister-ScheduledTask -TaskName "WTT Database Backup" -Confirm:$false -ErrorAction SilentlyContinue
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force
    sc.exe delete $serviceName | Out-Null
}
Remove-Item "$env:PUBLIC\Desktop\WTT.lnk" -Force -ErrorAction SilentlyContinue
"WTT service removed. Database, backups, logs and captured data were preserved."
