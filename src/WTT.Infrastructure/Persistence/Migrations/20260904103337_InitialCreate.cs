using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WTT.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Details = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Manufacturer = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    PollIntervalMs = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeoutMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Slot = table.Column<int>(type: "INTEGER", nullable: false),
                    Rack = table.Column<int>(type: "INTEGER", nullable: false),
                    ConnectionSize = table.Column<int>(type: "INTEGER", nullable: false),
                    CipPath = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    UnitId = table.Column<byte>(type: "INTEGER", nullable: false),
                    WordOrder = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    OneBasedAddressing = table.Column<bool>(type: "INTEGER", nullable: false),
                    CaptureMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TriggerTagName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IsCaptureEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsMonitorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExternalContextLoaded = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TagReadings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TagName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Quality = table.Column<string>(type: "TEXT", maxLength: 15, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsTransferred = table.Column<bool>(type: "INTEGER", nullable: false),
                    TransferredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagReadings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TransferBatches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TargetServer = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TargetDatabase = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RecordCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    NormalizedUsername = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    FullName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    FailedLoginCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LockoutEndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastLoginAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PasswordChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceAddresses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddressType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RegisterType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StartAddress = table.Column<int>(type: "INTEGER", nullable: false),
                    DataType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    PointCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TagPrefix = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ZeroBasedTagIndex = table.Column<bool>(type: "INTEGER", nullable: false),
                    ModuleOffset = table.Column<int>(type: "INTEGER", nullable: false),
                    DecimalPlaces = table.Column<int>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceAddresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceAddresses_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceQueries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ServerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DatabaseName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UseIntegratedSecurity = table.Column<bool>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ProtectedPassword = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Encrypt = table.Column<bool>(type: "INTEGER", nullable: false),
                    TrustServerCertificate = table.Column<bool>(type: "INTEGER", nullable: false),
                    QueryText = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    QueryType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IsVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                    VerifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceQueries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceQueries_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceServers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ServerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DatabaseName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TableName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UseIntegratedSecurity = table.Column<bool>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ProtectedPassword = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Encrypt = table.Column<bool>(type: "INTEGER", nullable: false),
                    TrustServerCertificate = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceServers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceServers_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceTriggerConditions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Conjunction = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    TagName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Operator = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceTriggerConditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceTriggerConditions_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceQueryColumns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    QueryId = table.Column<int>(type: "INTEGER", nullable: false),
                    ColumnName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceQueryColumns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceQueryColumns_DeviceQueries_QueryId",
                        column: x => x.QueryId,
                        principalTable: "DeviceQueries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddressId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Alias = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    DataType = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    PlcAddress = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    RegisterType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ElementCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ByteCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IsSelected = table.Column<bool>(type: "INTEGER", nullable: false),
                    SetValue = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ScaleFactor = table.Column<double>(type: "REAL", nullable: true),
                    ScaleOffset = table.Column<double>(type: "REAL", nullable: true),
                    DecimalPlaces = table.Column<int>(type: "INTEGER", nullable: true),
                    EngineeringUnit = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    CurrentValue = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CurrentQuality = table.Column<string>(type: "TEXT", maxLength: 15, nullable: false),
                    CurrentValueAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    QueryId = table.Column<int>(type: "INTEGER", nullable: true),
                    QueryColumnId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceTags_DeviceAddresses_AddressId",
                        column: x => x.AddressId,
                        principalTable: "DeviceAddresses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceTags_DeviceQueries_QueryId",
                        column: x => x.QueryId,
                        principalTable: "DeviceQueries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DeviceTags_DeviceQueryColumns_QueryColumnId",
                        column: x => x.QueryColumnId,
                        principalTable: "DeviceQueryColumns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DeviceTags_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TimestampUtc",
                table: "AuditLogs",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceAddresses_DeviceId",
                table: "DeviceAddresses",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceQueries_DeviceId_Name",
                table: "DeviceQueries",
                columns: new[] { "DeviceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceQueryColumns_QueryId",
                table: "DeviceQueryColumns",
                column: "QueryId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_Name",
                table: "Devices",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceServers_DeviceId",
                table: "DeviceServers",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTags_AddressId",
                table: "DeviceTags",
                column: "AddressId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTags_DeviceId_Name",
                table: "DeviceTags",
                columns: new[] { "DeviceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTags_QueryColumnId",
                table: "DeviceTags",
                column: "QueryColumnId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTags_QueryId",
                table: "DeviceTags",
                column: "QueryId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTriggerConditions_DeviceId_Order",
                table: "DeviceTriggerConditions",
                columns: new[] { "DeviceId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_TagReadings_DeviceId_IsTransferred_Id",
                table: "TagReadings",
                columns: new[] { "DeviceId", "IsTransferred", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TagReadings_IsTransferred_TimestampUtc",
                table: "TagReadings",
                columns: new[] { "IsTransferred", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferBatches_StartedAtUtc",
                table: "TransferBatches",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedUsername",
                table: "Users",
                column: "NormalizedUsername",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "DeviceServers");

            migrationBuilder.DropTable(
                name: "DeviceTags");

            migrationBuilder.DropTable(
                name: "DeviceTriggerConditions");

            migrationBuilder.DropTable(
                name: "TagReadings");

            migrationBuilder.DropTable(
                name: "TransferBatches");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "DeviceAddresses");

            migrationBuilder.DropTable(
                name: "DeviceQueryColumns");

            migrationBuilder.DropTable(
                name: "DeviceQueries");

            migrationBuilder.DropTable(
                name: "Devices");
        }
    }
}
