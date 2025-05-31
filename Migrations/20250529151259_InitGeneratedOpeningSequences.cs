using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaroAIServer.Migrations
{
    /// <inheritdoc />
    public partial class InitGeneratedOpeningSequences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "generated_opening_sequences",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    sequence_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    move_sequence = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    player_to_move_after_sequence = table.Column<int>(type: "int", nullable: false),
                    number_of_moves = table.Column<int>(type: "int", nullable: false),
                    final_evaluation_score = table.Column<float>(type: "real", nullable: false),
                    generated_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generated_opening_sequences", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "generated_opening_sequences");
        }
    }
}
