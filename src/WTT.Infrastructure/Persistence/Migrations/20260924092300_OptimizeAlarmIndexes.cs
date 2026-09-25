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
            // Drop old index that was in wrong order
            migrationBuilder.DropIndex(
                name: "IX_AlarmEvents_Priority_State",
                table: "AlarmEvents");

            // Create optimized covering index for active alarm queries
            // Covers: WHERE State IN (Active, Acknowledged) ORDER BY Priority DESC, TriggeredAtUtc DESC
            migrationBuilder.Sql(@"
                CREATE NONCLUSTERED INDEX [IX_AlarmEvents_ActiveQuery]
                ON [dbo].[AlarmEvents] ([State], [Priority] DESC, [TriggeredAtUtc] DESC)
                INCLUDE ([Id], [DeviceName], [TagName], [AlarmType], [TriggerValue], [ThresholdValue], [EngineeringUnit], [AcknowledgedAtUtc], [AcknowledgedBy])
                WHERE [State] IN (0, 1); -- Active=0, Acknowledged=1
            ");

            // Add index for retention cleanup query
            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_RetentionCleanup",
                table: "AlarmEvents",
                columns: new[] { "State", "TriggeredAtUtc" });

            // Add index for cross-process sync queries
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

            // Restore old index
            migrationBuilder.CreateIndex(
                name: "IX_AlarmEvents_Priority_State",
                table: "AlarmEvents",
                columns: new[] { "Priority", "State" });
        }
    }
}
