using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CaroAIServer.Data
{
    [Table("generated_opening_sequences")]
    public class GeneratedOpeningSequence
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("sequence_name")]
        [StringLength(200)]
        public string SequenceName { get; set; } = string.Empty;

        [Column("move_sequence")]
        public string MoveSequence { get; set; } = string.Empty; // JSON string of List<(int Row, int Column)>

        [Column("player_to_move_after_sequence")]
        public int PlayerToMoveAfterSequence { get; set; }

        [Column("number_of_moves")]
        public int NumberOfMoves { get; set; }

        [Column("final_evaluation_score")]
        public float FinalEvaluationScore { get; set; }

        [Column("generated_at")]
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    }
} 