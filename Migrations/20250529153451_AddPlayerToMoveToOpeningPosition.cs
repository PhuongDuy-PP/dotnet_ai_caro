using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaroAIServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerToMoveToOpeningPosition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "player_to_move",
                table: "opening_positions",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "player_to_move",
                table: "opening_positions");
        }
    }
}
