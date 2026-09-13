# WTT User Guide

1. Open WTT from the desktop shortcut and sign in.
2. Dashboard shows device, historian-buffer and transfer health. The blue line is readings; amber is buffered data.
3. Devices contains PLC configuration. Only administrators can change it.
4. Live Data refreshes from the Windows Service through SQL Server. Use filters or **Export CSV**.
5. PLC writes are disabled per tag by default. An administrator must define write permission and optional limits.
6. Data Transfer shows pending rows, destination status and recent batches.
7. `Ctrl+D` opens Dashboard. `Ctrl+L` signs out.

The UI can be closed without stopping capture. Do not stop SQL Server or the **WTT Industrial Data Service**. If data stops, note the time and send service logs from `%ProgramData%\WTT\Logs` and UI logs from `%LocalAppData%\WTT\Logs` to support.
