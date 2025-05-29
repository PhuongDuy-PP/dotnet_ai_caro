using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaroAIServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "opening_positions",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    position_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    move_number = table.Column<int>(type: "int", nullable: false),
                    board_state = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    best_moves = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    evaluation_score = table.Column<float>(type: "real", nullable: false),
                    game_phase = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    frequency_played = table.Column<int>(type: "int", nullable: false),
                    win_rate = table.Column<float>(type: "real", nullable: false),
                    created_date = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_opening_positions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "move_recommendations",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    position_id = table.Column<int>(type: "int", nullable: false),
                    move_row = table.Column<int>(type: "int", nullable: false),
                    move_col = table.Column<int>(type: "int", nullable: false),
                    move_score = table.Column<float>(type: "real", nullable: false),
                    move_rank = table.Column<int>(type: "int", nullable: false),
                    reasoning = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    threat_level = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_move_recommendations", x => x.id);
                    table.ForeignKey(
                        name: "FK_move_recommendations_opening_positions_position_id",
                        column: x => x.position_id,
                        principalTable: "opening_positions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_move_recommendations_position_id",
                table: "move_recommendations",
                column: "position_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "move_recommendations");

            migrationBuilder.DropTable(
                name: "opening_positions");
        }
    }
}
