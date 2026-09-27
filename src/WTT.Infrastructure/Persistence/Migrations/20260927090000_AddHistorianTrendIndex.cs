using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WTT.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// Historian trend queries: WHERE DeviceId + TagName + TimestampUtc range ORDER BY TimestampUtc.
    /// Bina is index ke 1-hour window par full-table scan hota tha.
    public partial class AddHistorianTrendIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Covering index: Value + Quality INCLUDE taaki key-lookup na ho
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
