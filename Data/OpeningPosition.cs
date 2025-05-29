using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CaroAIServer.Data
{
    public enum GamePhase
    {
        Opening,
        EarlyMiddle
    }

    [Table("opening_positions")]
    public class OpeningPosition
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("position_hash")]
        [StringLength(64)]
        public string? PositionHash { get; set; }

        [Column("move_number")]
        public int MoveNumber { get; set; }

        [Column("board_state")]
        public string? BoardState { get; set; } // JSON string

        [Column("best_moves")]
        public string? BestMoves { get; set; } // JSON array

        [Column("evaluation_score")]
        public float EvaluationScore { get; set; }

        [Column("game_phase")]
        public GamePhase GamePhase { get; set; }

        [Column("frequency_played")]
        public int FrequencyPlayed { get; set; }

        [Column("win_rate")]
        public float WinRate { get; set; }

        [Column("created_date")]
        public DateTime CreatedDate { get; set; }

        public virtual ICollection<MoveRecommendation> MoveRecommendations { get; set; } = new List<MoveRecommendation>();
    }
} 