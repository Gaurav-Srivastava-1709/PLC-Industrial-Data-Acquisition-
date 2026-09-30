using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WTT.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeAlarmIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AlarmEvents_Priority_State",
                table: "AlarmEvents");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_ActiveQuery",
                table: "AlarmEvents",
                columns: new[] { "State", "Priority", "TriggeredAtUtc" },
                descending: new[] { false, true, true },
                filter: "[State] IN (1, 2)")
                .Annotation("SqlServer:Include", new[] { "Id", "DeviceName", "TagName", "AlarmType", "TriggerValue", "ThresholdValue", "EngineeringUnit", "AcknowledgedAtUtc", "AcknowledgedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_RetentionCleanup",
                table: "AlarmEvents",
                columns: new[] { "State", "TriggeredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_SyncQuery",
                table: "AlarmEvents",
                columns: new[] { "TriggeredAtUtc", "ClearedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AlarmEvents_ActiveQuery",
                table: "AlarmEvents");

            migrationBuilder.DropIndex(
                name: "IX_AlarmEvents_RetentionCleanup",
                table: "AlarmEvents");

            migrationBuilder.DropIndex(
                name: "IX_AlarmEvents_SyncQuery",
                table: "AlarmEvents");

            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_Priority_State",
                table: "AlarmEvents",
                columns: new[] { "Priority", "State" });
        }
    }
}
