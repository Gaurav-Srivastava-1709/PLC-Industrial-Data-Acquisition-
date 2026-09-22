using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WTT.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAlarmThresholdSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AlarmDeadband",
                table: "DeviceTags",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "AlarmDescription",
                table: "DeviceTags",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AlarmMaxThreshold",
                table: "DeviceTags",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AlarmMinThreshold",
                table: "DeviceTags",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AlarmOffDelaySeconds",
                table: "DeviceTags",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AlarmOnDelaySeconds",
                table: "DeviceTags",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AlarmPriority",
                table: "DeviceTags",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsAlarmEnabled",
                table: "DeviceTags",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AlarmEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceTagId = table.Column<int>(type: "int", nullable: false),
                    DeviceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TagName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AlarmType = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    TriggerValue = table.Column<double>(type: "float", nullable: false),
                    ThresholdValue = table.Column<double>(type: "float", nullable: false),
                    EngineeringUnit = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TriggeredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClearedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlarmEvents_DeviceTags_DeviceTagId",
                        column: x => x.DeviceTagId,
                        principalTable: "DeviceTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_DeviceTagId",
                table: "AlarmEvents",
                column: "DeviceTagId");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_Priority_State",
                table: "AlarmEvents",
                columns: new[] { "Priority", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_TriggeredAtUtc",
                table: "AlarmEvents",
                column: "TriggeredAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlarmEvents");

            migrationBuilder.DropColumn(
                name: "AlarmDeadband",
                table: "DeviceTags");

            migrationBuilder.DropColumn(
                name: "AlarmDescription",
                table: "DeviceTags");

            migrationBuilder.DropColumn(
                name: "AlarmMaxThreshold",
                table: "DeviceTags");

            migrationBuilder.DropColumn(
                name: "AlarmMinThreshold",
                table: "DeviceTags");

            migrationBuilder.DropColumn(
                name: "AlarmOffDelaySeconds",
                table: "DeviceTags");

            migrationBuilder.DropColumn(
                name: "AlarmOnDelaySeconds",
                table: "DeviceTags");

            migrationBuilder.DropColumn(
                name: "AlarmPriority",
                table: "DeviceTags");

            migrationBuilder.DropColumn(
                name: "IsAlarmEnabled",
                table: "DeviceTags");
        }
    }
}
