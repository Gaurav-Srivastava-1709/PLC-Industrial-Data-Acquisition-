# WTT Administrator Guide

- Create the first administrator interactively; WTT has no shared default password.
- Use Admin for configuration, Operator for capture controls and Viewer for read-only monitoring.
- Configure only required PLC tags. PLC writes require explicit per-tag enablement, type/range validation, cooldown and readback.
- External queries accept exactly one parsed SELECT statement. Use a dedicated SQL account with SELECT permission only.
- Keep remote SQL `Encrypt` enabled and `Trust server certificate` disabled after installing trusted certificates.
- Schedule `deploy/Backup-Wtt.ps1`, copy backups off-machine and perform restore drills.
- SQL Server Express has a 10 GB data limit. Investigate at 8 GB; never delete untransferred readings silently.
