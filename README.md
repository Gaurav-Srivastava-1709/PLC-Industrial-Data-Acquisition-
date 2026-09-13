# WTT Industrial Data Acquisition

WTT 1.2 is a .NET 10 LTS Windows application for unattended PLC capture, local SQL Server Express buffering and resilient transfer to central SQL Server databases.

## Projects

- `WTT.UI`: operator and administrator WPF client.
- `WTT.Service`: unattended Windows Service owning capture and transfer.
- `WTT.Infrastructure`: SQL persistence, PLC drivers, security and pipelines.
- `WTT.Application` / `WTT.Domain`: use-case contracts and business model.
- `WTT.Tests`: SQL Server-backed automated tests.

See `docs/USER_GUIDE.md`, `docs/ADMIN_GUIDE.md`, `docs/INSTALL_AND_UPDATE.md` and `docs/SQL_SERVER_EXPRESS.md`.
