using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WTT.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductionSecurityHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsWriteEnabled",
                table: "DeviceTags",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MinimumWriteIntervalMs",
                table: "DeviceTags",
                type: "int",
                nullable: false,
                defaultValue: 1000);

            migrationBuilder.AddColumn<double>(
                name: "WriteMaximum",
                table: "DeviceTags",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "WriteMinimum",
                table: "DeviceTags",
                type: "float",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsWriteEnabled",
                table: "DeviceTags");

            migrationBuilder.DropColumn(
                name: "MinimumWriteIntervalMs",
                table: "DeviceTags");

            migrationBuilder.DropColumn(
                name: "WriteMaximum",
                table: "DeviceTags");

            migrationBuilder.DropColumn(
                name: "WriteMinimum",
                table: "DeviceTags");
        }
    }
}
