using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WTT.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// Historian trend queries: WHERE DeviceId + TagName + TimestampUtc range ORDER BY TimestampUtc.
    /// Without this index, a 1-hour window would cause a full-table scan.
    public partial class AddHistorianTrendIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Covering index: Value + Quality INCLUDE to avoid key lookups
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS [IX_TagReadings_HistorianTrend] ON [dbo].[TagReadings];
                CREATE NONCLUSTERED INDEX [IX_TagReadings_HistorianTrend]
                ON [dbo].[TagReadings] ([DeviceId], [TagName], [TimestampUtc])
                INCLUDE ([Value], [Quality]);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TagReadings_HistorianTrend",
                table: "TagReadings");
        }
    }
}
