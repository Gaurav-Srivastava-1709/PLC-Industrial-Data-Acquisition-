using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WTT.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHistorianTrendIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TagReadings_HistorianTrend",
                table: "TagReadings",
                columns: new[] { "DeviceId", "TagName", "TimestampUtc" })
                .Annotation("SqlServer:Include", new[] { "Value", "Quality" });
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
