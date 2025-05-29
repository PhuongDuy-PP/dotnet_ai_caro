using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CaroAIServer.Data
{
    public enum ThreatLevel
    {
        None,
        Low,
        Medium,
        High,
        Critical
    }

    [Table("move_recommendations")]
    public class MoveRecommendation
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("position_id")]
        public int PositionId { get; set; }

        [ForeignKey("PositionId")]
        public virtual OpeningPosition? OpeningPosition { get; set; }

        [Column("move_row")]
        public int MoveRow { get; set; }

        [Column("move_col")]
        public int MoveCol { get; set; }

        [Column("move_score")]
        public float MoveScore { get; set; }

        [Column("move_rank")]
        public int MoveRank { get; set; }

        [Column("reasoning")]
        public string? Reasoning { get; set; }

        [Column("threat_level")]
        public ThreatLevel ThreatLevel { get; set; }
    }
} 