using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WTT.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceRuntimeStates",
                columns: table => new
                {
                    DeviceId = table.Column<int>(type: "int", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkerRunning = table.Column<bool>(type: "bit", nullable: false),
                    IsConnected = table.Column<bool>(type: "bit", nullable: false),
                    IsCapturing = table.Column<bool>(type: "bit", nullable: false),
                    IsMonitoring = table.Column<bool>(type: "bit", nullable: false),
                    LastTriggerState = table.Column<bool>(type: "bit", nullable: true),
                    LastScanUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSuccessfulReadUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StatusUpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ScanCount = table.Column<long>(type: "bigint", nullable: false),
                    ReadingsWritten = table.Column<long>(type: "bigint", nullable: false),
                    ErrorCount = table.Column<long>(type: "bigint", nullable: false),
                    ReconnectAttempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceRuntimeStates", x => x.DeviceId);
                    table.ForeignKey(
                        name: "FK_DeviceRuntimeStates_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceRuntimeState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    HeartbeatAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    StoppedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceRuntimeState", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRuntimeStates_InstanceId",
                table: "DeviceRuntimeStates",
                column: "InstanceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceRuntimeStates");

            migrationBuilder.DropTable(
                name: "ServiceRuntimeState");
        }
    }
}
