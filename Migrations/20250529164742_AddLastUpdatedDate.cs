using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaroAIServer.Migrations
{
    /// <inheritdoc />
    public partial class AddLastUpdatedDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "last_updated_date",
                table: "opening_positions",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_updated_date",
                table: "opening_positions");
        }
    }
}
