using CaroAIServer.Data;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CaroAIServer.Services
{
    // Represents a single move with row and column
    public struct Move
    {
        public int Row { get; set; }
        public int Column { get; set; }
    }

    // New structures for hardcoded sequences
    public enum PlayerRoleInSequence { AI, Opponent, AssumedOpponent, AssumedAI }

    public struct PrioritizedMoveDefinition
    {
        public int Row { get; set; }
        public int Column { get; set; }
        public float ScoreModifier { get; set; } // e.g., 0 for main, negative for alternatives to lower their score slightly
    }

    public struct HardcodedMoveDefinition
    {
        public PlayerRoleInSequence PlayerRole { get; set; }
        public List<PrioritizedMoveDefinition> PossibleMoves { get; set; } // First move is primary for sequence continuation
        public string? StepName { get; set; } 
    }

    public struct HardcodedOpeningSequence
    {
        public string Name { get; set; }
        public List<HardcodedMoveDefinition> Moves { get; set; }
        public PlayerRoleInSequence FirstPlayerRole { get; set; }
    }

    public class OpeningGenerationService
    {
        private readonly ApplicationDbContext _context;
        private readonly AIService _aiService;
        private readonly ILogger<OpeningGenerationService> _logger;
        private const int BoardSize = GameService.BoardSize;
        private const float HARDCODED_MAIN_MOVE_SCORE = float.MaxValue - 500;
        // private const float ABSOLUTE_BEST_MOVE_SCORE = float.MaxValue - 1000; // Kept for reference, might reuse
        // private const float STRONG_RESPONSE_SCORE = float.MaxValue - 2000; // Kept for reference

        // For real-time move display (optional)
        public static List<Move> CurrentGeneratingMoves { get; private set; } = new List<Move>();
        public static string CurrentGenerationStatus { get; private set; } = "Idle";

        public OpeningGenerationService(ApplicationDbContext context, AIService aiService, ILogger<OpeningGenerationService> logger)
        {
            _context = context;
            _aiService = aiService;
            _logger = logger;
        }

        private List<HardcodedOpeningSequence> GetPredefinedOpeningSequences()
        {
            var sequences = new List<HardcodedOpeningSequence>();

            sequences.Add(new HardcodedOpeningSequence
            {
                Name = "AI_P1_Center_Variations", // Renamed for clarity
                FirstPlayerRole = PlayerRoleInSequence.AI,
                Moves = new List<HardcodedMoveDefinition>
                {
                    new HardcodedMoveDefinition { PlayerRole = PlayerRoleInSequence.AI, StepName = "AI_P1_1_CenterAndVariants", 
                        PossibleMoves = new List<PrioritizedMoveDefinition> {
                            new PrioritizedMoveDefinition { Row = 7, Column = 7, ScoreModifier = 0 },    // Main
                            new PrioritizedMoveDefinition { Row = 7, Column = 6, ScoreModifier = -10 }, // Variant
                            new PrioritizedMoveDefinition { Row = 6, Column = 7, ScoreModifier = -10 }, // Variant
                            new PrioritizedMoveDefinition { Row = 6, Column = 6, ScoreModifier = -20 }, // Variant
                            new PrioritizedMoveDefinition { Row = 8, Column = 8, ScoreModifier = -20 }, // Variant
                            new PrioritizedMoveDefinition { Row = 7, Column = 8, ScoreModifier = -30 },
                            new PrioritizedMoveDefinition { Row = 8, Column = 7, ScoreModifier = -30 }
                        }
                    },
                    // Assuming the sequence continues based on the main move (7,7) for AssumedOpponent response
                    new HardcodedMoveDefinition { PlayerRole = PlayerRoleInSequence.AssumedOpponent, StepName = "Opp_P2_1_Near_vs_77",
                        PossibleMoves = new List<PrioritizedMoveDefinition> {
                            new PrioritizedMoveDefinition { Row = 6, Column = 7, ScoreModifier = 0 }, 
                            new PrioritizedMoveDefinition { Row = 8, Column = 7, ScoreModifier = -10 },
                            new PrioritizedMoveDefinition { Row = 7, Column = 8, ScoreModifier = -10 },
                            new PrioritizedMoveDefinition { Row = 6, Column = 6, ScoreModifier = -15 } // Opponent also has variants
                        }
                    },
                    new HardcodedMoveDefinition { PlayerRole = PlayerRoleInSequence.AI, StepName = "AI_P1_2_Reinforce_vs_77_67", 
                        PossibleMoves = new List<PrioritizedMoveDefinition> {
                            // If P1 played 7,7 and P2 played 6,7, AI (P1) could play:
                            new PrioritizedMoveDefinition { Row = 7, Column = 6, ScoreModifier = 0 }, // Main reinforce for 7,7 -> 6,7
                            new PrioritizedMoveDefinition { Row = 8, Column = 6, ScoreModifier = -10 },
                            new PrioritizedMoveDefinition { Row = 5, Column = 7, ScoreModifier = -10 }
                        }
                    },
                    new HardcodedMoveDefinition { PlayerRole = PlayerRoleInSequence.AssumedOpponent, StepName = "Opp_P2_2_Block_vs_77_67_76",
                        PossibleMoves = new List<PrioritizedMoveDefinition> {
                             // If P1:7,7 -> P2:6,7 -> P1:7,6, P2 might block at 6,6 or 8,6
                            new PrioritizedMoveDefinition { Row = 6, Column = 6, ScoreModifier = 0 },
                            new PrioritizedMoveDefinition { Row = 8, Column = 6, ScoreModifier = -5 }
                        }
                    }
                }
            });

            sequences.Add(new HardcodedOpeningSequence
            {
                Name = "Opp_P1_Center_Variations_AI_P2_Response", // Renamed
                FirstPlayerRole = PlayerRoleInSequence.Opponent,
                Moves = new List<HardcodedMoveDefinition>
                {
                    new HardcodedMoveDefinition { PlayerRole = PlayerRoleInSequence.Opponent, StepName = "Opp_P1_1_CenterAndVariants",
                        PossibleMoves = new List<PrioritizedMoveDefinition> {
                            new PrioritizedMoveDefinition { Row = 7, Column = 7, ScoreModifier = 0 },
                            new PrioritizedMoveDefinition { Row = 7, Column = 6, ScoreModifier = -10 },
                            new PrioritizedMoveDefinition { Row = 6, Column = 7, ScoreModifier = -10 },
                        }
                    },
                    // AI P2 responds to P1's main move (7,7)
                    new HardcodedMoveDefinition { PlayerRole = PlayerRoleInSequence.AI, StepName = "AI_P2_1_DiagonalResponse_To_77",
                        PossibleMoves = new List<PrioritizedMoveDefinition> {
                            new PrioritizedMoveDefinition { Row = 6, Column = 6, ScoreModifier = 0 },
                            new PrioritizedMoveDefinition { Row = 7, Column = 6, ScoreModifier = -10 }, // Adjacent to P1's 7,7
                            new PrioritizedMoveDefinition { Row = 6, Column = 7, ScoreModifier = -10 }, // Adjacent to P1's 7,7
                            new PrioritizedMoveDefinition { Row = 8, Column = 8, ScoreModifier = -20 },
                            new PrioritizedMoveDefinition { Row = 5, Column = 5, ScoreModifier = -20 }
                        }
                    },
                     // P1's assumed 2nd move, e.g., after P1:7,7, P2:6,6
                    new HardcodedMoveDefinition { PlayerRole = PlayerRoleInSequence.AssumedOpponent, StepName = "Opp_P1_2_Block_vs_77_66",
                        PossibleMoves = new List<PrioritizedMoveDefinition> {
                            new PrioritizedMoveDefinition { Row = 7, Column = 6, ScoreModifier = 0 }, // P1 blocks P2's 6,6 line
                            new PrioritizedMoveDefinition { Row = 5, Column = 7, ScoreModifier = -10 }, // P1 attacks P2's 6,6 line
                            new PrioritizedMoveDefinition { Row = 8, Column = 7, ScoreModifier = -10 } // P1 develops another line
                        }
                    },
                    // AI P2's 2nd move, e.g., after P1:7,7 -> P2:6,6 -> P1:7,6
                    new HardcodedMoveDefinition { PlayerRole = PlayerRoleInSequence.AI, StepName = "AI_P2_2_CounterAttack_vs_77_66_76",
                        PossibleMoves = new List<PrioritizedMoveDefinition> {
                            new PrioritizedMoveDefinition { Row = 6, Column = 7, ScoreModifier = 0 }, // AI counters P1's 7,6
                            new PrioritizedMoveDefinition { Row = 5, Column = 6, ScoreModifier = -10 }, 
                            new PrioritizedMoveDefinition { Row = 8, Column = 5, ScoreModifier = -15 } // More distant option
                        }
                    }
                }
            });
            
            // Example for Opponent P1 playing off-center (6,7) - AI P2 responds
            sequences.Add(new HardcodedOpeningSequence
            {
                Name = "Opp_P1_OffCenter_6_7_AI_P2_Takes_Center_And_Variants",
                FirstPlayerRole = PlayerRoleInSequence.Opponent,
                Moves = new List<HardcodedMoveDefinition>
                {
                    new HardcodedMoveDefinition { PlayerRole = PlayerRoleInSequence.Opponent, StepName = "Opp_P1_1_OffCenter_67",
                        PossibleMoves = new List<PrioritizedMoveDefinition> { 
                            new PrioritizedMoveDefinition { Row = 6, Column = 7, ScoreModifier = 0 },
                            // Could add P1 variants here too if desired e.g. (6,8) or (5,7)
                            new PrioritizedMoveDefinition { Row = 6, Column = 8, ScoreModifier = -5 },
                            new PrioritizedMoveDefinition { Row = 5, Column = 7, ScoreModifier = -5 },
                        }
                    },
                    // AI P2 responds to P1's main move (6,7)
                    new HardcodedMoveDefinition { PlayerRole = PlayerRoleInSequence.AI, StepName = "AI_P2_1_Response_To_67",
                        PossibleMoves = new List<PrioritizedMoveDefinition> {
                            new PrioritizedMoveDefinition { Row = 7, Column = 7, ScoreModifier = 0 },   // AI takes center
                            new PrioritizedMoveDefinition { Row = 7, Column = 6, ScoreModifier = -5 },   // AI plays near P1's 6,7
                            new PrioritizedMoveDefinition { Row = 5, Column = 7, ScoreModifier = -10 },  // AI plays near P1's 6,7 (other side)
                            new PrioritizedMoveDefinition { Row = 6, Column = 6, ScoreModifier = -15 }  // AI plays near P1's 6,7
                        }
                    },
                    // P1's assumed 2nd move, e.g., P1:6,7 -> P2:7,7
                    new HardcodedMoveDefinition { PlayerRole = PlayerRoleInSequence.AssumedOpponent, StepName = "Opp_P1_2_Response_To_AI_Center_From_67",
                        PossibleMoves = new List<PrioritizedMoveDefinition> {
                            new PrioritizedMoveDefinition { Row = 7, Column = 6, ScoreModifier = 0 }, // P1 plays near AI's center
                            new PrioritizedMoveDefinition { Row = 5, Column = 6, ScoreModifier = -10 },
                            new PrioritizedMoveDefinition { Row = 8, Column = 7, ScoreModifier = -10 } // P1 also plays near AI's center
                        }
                    },
                    // AI P2's 2nd move, e.g., P1:6,7 -> P2:7,7 -> P1:7,6
                    new HardcodedMoveDefinition { PlayerRole = PlayerRoleInSequence.AI, StepName = "AI_P2_2_Develop_From_Center_vs_67_77_76",
                        PossibleMoves = new List<PrioritizedMoveDefinition> {
                            new PrioritizedMoveDefinition { Row = 8, Column = 8, ScoreModifier = 0 }, // AI develops from center
                            new PrioritizedMoveDefinition { Row = 6, Column = 8, ScoreModifier = -10 },
                            new PrioritizedMoveDefinition { Row = 8, Column = 6, ScoreModifier = -10 }
                        }
                    }
                }
            });
            return sequences;
        }

        public async Task GenerateOpeningsAsync(int numberOfSequencesToProcess, int maxMovesPerSequenceAfterHardcode, int aiPlayerIdForGenerationContext = 1)
        {
            CurrentGenerationStatus = "Initializing generation with multi-step hardcoded sequences (with alternatives).";
            _logger.LogInformation(CurrentGenerationStatus);

            var predefinedSequences = GetPredefinedOpeningSequences();
            int sequencesToRun = Math.Min(numberOfSequencesToProcess, predefinedSequences.Count);
            if (numberOfSequencesToProcess > predefinedSequences.Count || numberOfSequencesToProcess <= 0)
            {
                sequencesToRun = predefinedSequences.Count;
                _logger.LogInformation($"Requested {numberOfSequencesToProcess}, running all {sequencesToRun} predefined sequences.");
            }
            
            _logger.LogInformation($"Will process {sequencesToRun} hardcoded sequences. AI plays as P{aiPlayerIdForGenerationContext} in this context.");
            int sequencesProcessedSuccessfully = 0;

            foreach (var sequence in predefinedSequences.Take(sequencesToRun))
            {
                CurrentGenerationStatus = $"Starting sequence: {sequence.Name} ({sequencesProcessedSuccessfully + 1}/{sequencesToRun})";
                _logger.LogInformation(CurrentGenerationStatus);

                int[,] currentBoard = new int[BoardSize, BoardSize];
                List<Move> currentPathHistory = new List<Move>();
                int currentDepthInOriginalGame = 0;

                int p1 = (sequence.FirstPlayerRole == PlayerRoleInSequence.AI) ? aiPlayerIdForGenerationContext : ((aiPlayerIdForGenerationContext == 1) ? 2 : 1);
                int p2 = (p1 == 1) ? 2 : 1;
                int playerForNextMoveInSequence = p1;

                _logger.LogInformation($"Sequence '{sequence.Name}': P1={p1}, P2={p2}. AI is P{aiPlayerIdForGenerationContext}. First move by P{playerForNextMoveInSequence} (Role: {sequence.FirstPlayerRole})");

                bool sequenceAborted = false;
                foreach (var moveDef in sequence.Moves)
                {
                    if (moveDef.PossibleMoves == null || !moveDef.PossibleMoves.Any())
                    {
                        _logger.LogError($"Sequence '{sequence.Name}', Step '{moveDef.StepName}': No possible moves defined. Aborting sequence."); sequenceAborted = true; break;
                    }

                    int actualPlayerMakingMove;
                    if (moveDef.PlayerRole == PlayerRoleInSequence.AI) actualPlayerMakingMove = aiPlayerIdForGenerationContext;
                    else if (moveDef.PlayerRole == PlayerRoleInSequence.Opponent) actualPlayerMakingMove = (aiPlayerIdForGenerationContext == 1) ? 2 : 1;
                    else if (moveDef.PlayerRole == PlayerRoleInSequence.AssumedAI) actualPlayerMakingMove = aiPlayerIdForGenerationContext;
                    else if (moveDef.PlayerRole == PlayerRoleInSequence.AssumedOpponent) actualPlayerMakingMove = (aiPlayerIdForGenerationContext == 1) ? 2 : 1;
                    else { _logger.LogError($"Unknown PlayerRole {moveDef.PlayerRole}. Aborting sequence '{sequence.Name}'."); sequenceAborted = true; break; }

                    if (playerForNextMoveInSequence != actualPlayerMakingMove)
                    {
                         _logger.LogWarning($"Seq '{sequence.Name}', Step '{moveDef.StepName}': Turn mismatch. Expected P{playerForNextMoveInSequence}, MoveDef implies P{actualPlayerMakingMove}. Skipping step.");
                         playerForNextMoveInSequence = (playerForNextMoveInSequence == 1) ? 2 : 1; 
                         continue;
                    }

                    PrioritizedMoveDefinition primaryMove = moveDef.PossibleMoves[0]; // For sequence continuation
                     if (primaryMove.Row < 0 || primaryMove.Row >= BoardSize || primaryMove.Column < 0 || primaryMove.Column >= BoardSize)
                    {
                        _logger.LogError($"Seq '{sequence.Name}', Step '{moveDef.StepName}': Invalid primary move coords ({primaryMove.Row},{primaryMove.Column}). Aborting."); sequenceAborted = true; break;
                    }
                    if (currentBoard[primaryMove.Row, primaryMove.Column] != 0)
                    {
                        _logger.LogWarning($"Seq '{sequence.Name}', Step '{moveDef.StepName}': Primary move ({primaryMove.Row},{primaryMove.Column}) is on occupied cell. Aborting branch."); sequenceAborted = true; break;
                    }
                    
                    string stepScenarioName = $"{sequence.Name}_{moveDef.StepName ?? $"Step{currentDepthInOriginalGame}"}";
                    _logger.LogInformation($"Executing Step: {stepScenarioName} - P{actualPlayerMakingMove} will consider {moveDef.PossibleMoves.Count} options. Primary: ({primaryMove.Row},{primaryMove.Column}). Depth {currentDepthInOriginalGame}");

                    currentBoard = await ExecuteAndSaveSequenceStepAsync(
                        currentBoard,
                        actualPlayerMakingMove,
                        (actualPlayerMakingMove == 1) ? 2 : 1,
                        moveDef.PossibleMoves, // Pass all possible moves for saving
                        stepScenarioName,
                        currentDepthInOriginalGame
                    );

                    currentPathHistory.Add(new Move { Row = primaryMove.Row, Column = primaryMove.Column });
                    currentDepthInOriginalGame++;
                    playerForNextMoveInSequence = (playerForNextMoveInSequence == 1) ? 2 : 1;
                    
                    if (currentDepthInOriginalGame >= maxMovesPerSequenceAfterHardcode + sequence.Moves.Count)
                         { _logger.LogWarning($"Sequence '{sequence.Name}': Max depth reached during hardcoded phase. Breaking."); sequenceAborted = true; break;}
                }

                if (sequenceAborted) { _logger.LogInformation($"Sequence '{sequence.Name}' aborted. No further exploration."); }
                else if (currentDepthInOriginalGame < maxMovesPerSequenceAfterHardcode + sequence.Moves.Count) 
                {
                    _logger.LogInformation($"Sequence '{sequence.Name}' completed ({currentPathHistory.Count} hardcoded moves). Exploring next for P{playerForNextMoveInSequence}.");
                    await ExploreSequenceAsync(currentBoard, playerForNextMoveInSequence, currentPathHistory, maxMovesPerSequenceAfterHardcode + sequence.Moves.Count, aiPlayerIdForGenerationContext, sequence.Name + "_Explore");
                }
                else { _logger.LogInformation($"Sequence '{sequence.Name}' completed. Max depth. No AI exploration."); }
                sequencesProcessedSuccessfully++;
            }
            CurrentGenerationStatus = $"Finished all sequences. {sequencesProcessedSuccessfully} initiated.";
            _logger.LogInformation(CurrentGenerationStatus);
            CurrentGeneratingMoves.Clear();
        }

        private async Task<int[,]> ExecuteAndSaveSequenceStepAsync(
            int[,] boardBeforeMove,
            int playerMakingMove,
            int opponentOfPlayerMakingMove,
            List<PrioritizedMoveDefinition> possibleMovesOriginalCoords, // All possible moves for this step, on original board coordinates
            string stepScenarioNameForLogging,
            int depthInOriginalGame
        )
        {
            if (possibleMovesOriginalCoords == null || !possibleMovesOriginalCoords.Any())
            {
                _logger.LogError($"ExecuteAndSave: No possible moves provided for {stepScenarioNameForLogging}. Cannot proceed.");
                return (int[,])boardBeforeMove.Clone(); // Return original board to prevent crash, but this is an error state
            }

            _logger.LogDebug($"ExecuteAndSave: Saving state for {stepScenarioNameForLogging}, P{playerMakingMove} to consider {possibleMovesOriginalCoords.Count} moves at depth {depthInOriginalGame}.");
            
            await SaveHardcodedMoveAlternativesAsync(
                boardBeforeMove, 
                playerMakingMove,
                opponentOfPlayerMakingMove,
                possibleMovesOriginalCoords,
                stepScenarioNameForLogging,
                depthInOriginalGame
            );

            PrioritizedMoveDefinition primaryMoveToExecute = possibleMovesOriginalCoords[0]; // Continue sequence with the primary move
            int[,] boardAfterMove = (int[,])boardBeforeMove.Clone();
            if (primaryMoveToExecute.Row >= 0 && primaryMoveToExecute.Row < BoardSize && primaryMoveToExecute.Column >= 0 && primaryMoveToExecute.Column < BoardSize && boardAfterMove[primaryMoveToExecute.Row, primaryMoveToExecute.Column] == 0)
            {
                boardAfterMove[primaryMoveToExecute.Row, primaryMoveToExecute.Column] = playerMakingMove;
                _logger.LogDebug($"ExecuteAndSave: P{playerMakingMove} executed primary move ({primaryMoveToExecute.Row},{primaryMoveToExecute.Column}) for {stepScenarioNameForLogging}.");
            }
            else
            {
                _logger.LogError($"ExecuteAndSave: Primary move ({primaryMoveToExecute.Row},{primaryMoveToExecute.Column}) for {stepScenarioNameForLogging} is invalid or cell occupied. Sequence may be corrupted.");
                // Return board state before this problematic primary move, effectively aborting this path of the sequence
                return (int[,])boardBeforeMove.Clone(); 
            }
            return boardAfterMove;
        }

        private async Task SaveHardcodedMoveAlternativesAsync(
            int[,] boardStateToSave, 
            int playerWhoseTurnItWas, 
            int opponentOfPlayerWhoseTurnItWas,
            List<PrioritizedMoveDefinition> recommendedMovesOriginalCoords,
            string scenarioDebugName,
            int depthForOpeningPosition
        )
        {
            (int[,] canonicalBoard, TransformationInfo_AI transform) = _aiService.GetCanonicalBoardAndTransformation(boardStateToSave);
            string canonicalBoardHash = _aiService.ComputeBoardHash(canonicalBoard);

            List<MoveScoreInfo> explicitRecommendationsToSave = new List<MoveScoreInfo>();
            _logger.LogDebug($"SaveHardcodedAlts: For scenario '{scenarioDebugName}', P{playerWhoseTurnItWas}, CanonHash '{canonicalBoardHash.Substring(0,8)}', processing {recommendedMovesOriginalCoords.Count} original recommendations.");

            foreach (var moveDef in recommendedMovesOriginalCoords)
            {
                // CRITICAL TODO: Transform (moveDef.Row, moveDef.Column) to its coordinates on the canonicalBoard using 'transform'
                // (int canonicalRecMoveR, int canonicalRecMoveC) = _aiService.TransformMoveToCanonical((moveDef.Row, moveDef.Column), transform);
                (int canonicalRecMoveR, int canonicalRecMoveC) = _aiService.TransformMoveToCanonical(moveDef.Row, moveDef.Column, transform);

                if (canonicalRecMoveR < 0 || canonicalRecMoveR >= BoardSize || canonicalRecMoveC < 0 || canonicalRecMoveC >= BoardSize)
                {
                    _logger.LogWarning($"SaveHardcodedAlts: '{scenarioDebugName}' - Transformed move ({canonicalRecMoveR},{canonicalRecMoveC}) for original ({moveDef.Row},{moveDef.Column}) is out of bounds. Skipping.");
                    continue;
                }

                if (canonicalBoard[canonicalRecMoveR, canonicalRecMoveC] == 0) // Only save if the cell is empty on the canonical board
                {
                    explicitRecommendationsToSave.Add(new MoveScoreInfo 
                    { 
                        Row = canonicalRecMoveR, 
                        Column = canonicalRecMoveC, 
                        Score = HARDCODED_MAIN_MOVE_SCORE + moveDef.ScoreModifier 
                    });
                     _logger.LogTrace($"SaveHardcodedAlts: '{scenarioDebugName}' - Added rec: Can ({canonicalRecMoveR},{canonicalRecMoveC}), Orig ({moveDef.Row},{moveDef.Column}), ScoreMod: {moveDef.ScoreModifier}");
                }
                else
                {
                    _logger.LogWarning($"SaveHardcodedAlts: '{scenarioDebugName}' - Canonical cell ({canonicalRecMoveR},{canonicalRecMoveC}) for original ({moveDef.Row},{moveDef.Column}) is already occupied on canonical board. Skipping this alternative.");
                }
            }

            if (!explicitRecommendationsToSave.Any())
            {
                _logger.LogWarning($"SaveHardcodedAlts: Scenario '{scenarioDebugName}', P{playerWhoseTurnItWas}, CanonHash '{canonicalBoardHash.Substring(0,8)}'. No valid empty cells found for any of the {recommendedMovesOriginalCoords.Count} alternatives after transformation and check. No recommendations will be saved for this state.");
                // Fall through to GetOrCreate, which might then get AI moves if it's not a hardcoded override of an existing.
            }
            else
            {
                 _logger.LogInformation($"SaveHardcodedAlts: '{scenarioDebugName}', P{playerWhoseTurnItWas}, CanonHash '{canonicalBoardHash.Substring(0,8)}'. Attempting to save/update with {explicitRecommendationsToSave.Count} explicit recommendations.");
            }

            await GetOrCreateOpeningPositionAsync(
                canonicalBoard,
                canonicalBoardHash,
                playerWhoseTurnItWas,
                opponentOfPlayerWhoseTurnItWas,
                depthForOpeningPosition,
                scenarioDebugName,
                explicitRecommendationsToSave, // Can be empty if all alternatives were invalid
                true 
            );
        }
        
        private async Task ExploreSequenceAsync(
            int[,] currentOriginalBoard, int currentPlayer, List<Move> currentMoveHistory,
            int maxOverallDepth, int aiPlayerIdForGeneration, string scenarioName // maxOverallDepth includes hardcoded moves
        )
        {
            if (currentMoveHistory.Count >= maxOverallDepth)
            {
                _logger.LogDebug($"ExploreSequence: Scenario '{scenarioName}', Path Depth {currentMoveHistory.Count} >= MaxOverallDepth {maxOverallDepth}. End branch.");
                return;
            }

            (int[,] canonicalBoard, TransformationInfo_AI transformToCanonical) = _aiService.GetCanonicalBoardAndTransformation(currentOriginalBoard);
            string canonicalBoardHash = _aiService.ComputeBoardHash(canonicalBoard);
            int opponentPlayer = (currentPlayer == 1) ? 2 : 1;

            _logger.LogDebug($"ExploreSequence: Scenario='{scenarioName}', CurrentBoardDepth={currentMoveHistory.Count}, Player={currentPlayer}, CanonicalHash={canonicalBoardHash.Substring(0, 8)}");

            var positionData = await GetOrCreateOpeningPositionAsync(
                canonicalBoard, canonicalBoardHash, currentPlayer, opponentPlayer,
                currentMoveHistory.Count, // This is the depth on the original board, used for OpeningPosition.MoveNumber
                scenarioName,
                null, // No explicit recommendations for exploration phase
                false // Not a hardcoded step
            );

            if (positionData == null || !positionData.Value.Recommendations.Any())
            {
                _logger.LogWarning($"ExploreSequence: Scenario '{scenarioName}', Path Depth {currentMoveHistory.Count}: No recommendations for P{currentPlayer}, hash {canonicalBoardHash.Substring(0, 8)}. Stop exploration path.");
                return;
            }

            List<MoveScoreInfo> recommendationsToExplore = positionData.Value.Recommendations.OrderByDescending(r => r.Score).ToList();
            _logger.LogDebug($"ExploreSequence: Scenario='{scenarioName}', P{currentPlayer}, Hash={canonicalBoardHash.Substring(0, 8)}. AI Recs to explore ({recommendationsToExplore.Count}):");
            foreach (var rec in recommendationsToExplore.Take(3)) { _logger.LogDebug($"  Rec: ({rec.Row},{rec.Column}) Score: {rec.Score}"); }

            int branchesExploredThisNode = 0;
            int maxBranchesToExplorePerNode = 2; 

            foreach (var chosenCanonicalMoveInfo in recommendationsToExplore.Take(maxBranchesToExplorePerNode))
            {
                if (currentMoveHistory.Count + branchesExploredThisNode >= maxOverallDepth) break; 

                (int r_orig, int c_orig) = _aiService.InverseTransformMove((chosenCanonicalMoveInfo.Row, chosenCanonicalMoveInfo.Column), transformToCanonical);
                if (r_orig == -1 || c_orig == -1 || currentOriginalBoard[r_orig, c_orig] != 0)
                {
                    _logger.LogWarning($"ExploreSequence: Scenario '{scenarioName}', P{currentPlayer} suggested invalid/occupied cell ({r_orig},{c_orig}) from canonical ({chosenCanonicalMoveInfo.Row},{chosenCanonicalMoveInfo.Column}). Hash: {canonicalBoardHash.Substring(0,8)}. Skip branch.");
                    continue;
                }

                int[,] nextBoard = (int[,])currentOriginalBoard.Clone();
                nextBoard[r_orig, c_orig] = currentPlayer;
                var nextMoveHistory = new List<Move>(currentMoveHistory) { new Move { Row = r_orig, Column = c_orig } };
                
                CurrentGeneratingMoves.Clear(); CurrentGeneratingMoves.AddRange(nextMoveHistory); 
                _logger.LogInformation($"ExploreSequence: Scenario \'{scenarioName}\', Path Depth {nextMoveHistory.Count}: P{currentPlayer} plays ({r_orig},{c_orig}) (orig). (From canonical ({chosenCanonicalMoveInfo.Row},{chosenCanonicalMoveInfo.Column}), Score: {chosenCanonicalMoveInfo.Score})");

                if (_aiService.CheckWin(nextBoard, currentPlayer, opponentPlayer))
                {
                    _logger.LogInformation($"ExploreSequence: Scenario \'{scenarioName}\', Path Depth {nextMoveHistory.Count}: P{currentPlayer} wins. End branch.");
                }
                else if (nextMoveHistory.Count >= BoardSize * BoardSize) 
                {
                    _logger.LogInformation($"ExploreSequence: Scenario '{scenarioName}', Path Depth {nextMoveHistory.Count}: Board full. End branch.");
                }
                else
                {
                    await ExploreSequenceAsync(nextBoard, opponentPlayer, nextMoveHistory, maxOverallDepth, aiPlayerIdForGeneration, scenarioName);
                }
                branchesExploredThisNode++;
            }
        }

        private async Task<(OpeningPosition? Position, List<MoveScoreInfo> Recommendations)?> GetOrCreateOpeningPositionAsync(
            int[,] canonicalBoard, string canonicalBoardHash, int playerToMove, int opponentPlayer,
            int currentDepthOnOriginalBoard, string scenarioName, 
            List<MoveScoreInfo>? explicitRecommendations, // Can be null or EMPTY for hardcoded steps if all alternatives invalid
            bool isHardcodedScenarioStep
        )
        {
            var existingOpeningPosition = await _context.OpeningPositions
                .Include(op => op.MoveRecommendations)
                .FirstOrDefaultAsync(op => op.PositionHash == canonicalBoardHash && op.PlayerToMove == playerToMove);

            List<MoveScoreInfo> finalRecommendations = new List<MoveScoreInfo>();
            // Use explicitRecommendations if provided (even if empty for hardcoded step), otherwise it's AI service call or existing DB recs.
            bool useExplicit = isHardcodedScenarioStep && explicitRecommendations != null; 

            if (useExplicit)
            {
                _logger.LogInformation($"GetOrCreate (Hardcoded Path): Scenario='{scenarioName}', Hash={canonicalBoardHash.Substring(0, 8)}.., P{playerToMove}, DB_Depth={currentDepthOnOriginalBoard}. Explicit recs count: {explicitRecommendations!.Count}.");
                finalRecommendations = explicitRecommendations!.OrderByDescending(m => m.Score).ToList(); // explicitRecommendations is not null due to useExplicit check

                if (existingOpeningPosition != null)
                {
                    _logger.LogInformation($"GetOrCreate (Hardcoded Path): Existing Pos found. Comparing/Updating recommendations for Hash={canonicalBoardHash.Substring(0, 8)}.., P{playerToMove}.");
                    
                    bool recommendationsChanged = existingOpeningPosition.MoveRecommendations.Count != finalRecommendations.Count || 
                       !existingOpeningPosition.MoveRecommendations.OrderBy(mr => mr.MoveRow).ThenBy(mr => mr.MoveCol).Select(mr => (mr.MoveRow, mr.MoveCol, mr.MoveScore)).SequenceEqual(finalRecommendations.OrderBy(fr => fr.Row).ThenBy(fr => fr.Column).Select(fr => (fr.Row, fr.Column, fr.Score)));
                    
                    bool scoreChanged = existingOpeningPosition.EvaluationScore != (finalRecommendations.Any() ? finalRecommendations[0].Score : 0);

                    if (recommendationsChanged || scoreChanged)
                    {
                        if (recommendationsChanged) {
                            _logger.LogInformation("Recommendations differ, clearing and re-adding.");
                            existingOpeningPosition.MoveRecommendations.Clear();
                            foreach (var moveInfo in finalRecommendations)
                            {
                                existingOpeningPosition.MoveRecommendations.Add(new MoveRecommendation { MoveRow = moveInfo.Row, MoveCol = moveInfo.Column, MoveScore = moveInfo.Score });
                            }
                        }
                        if(scoreChanged) existingOpeningPosition.EvaluationScore = finalRecommendations.Any() ? finalRecommendations[0].Score : 0;
                        
                        existingOpeningPosition.LastUpdatedDate = DateTime.UtcNow;
                        _context.OpeningPositions.Update(existingOpeningPosition);
                        try
                        {
                            await _context.SaveChangesAsync();
                            _logger.LogInformation($"GetOrCreate (Hardcoded Path): UPDATED existing Pos (Hash={canonicalBoardHash.Substring(0, 8)}.., P{playerToMove}) with {finalRecommendations.Count} recs.");
                        }
                        catch (Exception dbEx)
                        {
                            _logger.LogError(dbEx, $"GetOrCreate (Hardcoded Path): Error UPDATING existing Pos (Hash={canonicalBoardHash.Substring(0, 8)}.., P{playerToMove}).");
                            return null;
                        }
                    } else {
                         _logger.LogInformation($"GetOrCreate (Hardcoded Path): Existing Pos (Hash={canonicalBoardHash.Substring(0, 8)}.., P{playerToMove}) already had identical recs. No DB update needed.");
                    }
                    return (existingOpeningPosition, finalRecommendations);
                }
                // If existingOpeningPosition is null, it will be created as NEW below using these finalRecommendations.
            }
            else if (existingOpeningPosition != null) // Not a hardcoded override with explicit recs, and position exists
            {
                _logger.LogInformation($"GetOrCreate (Exists): Scenario='{scenarioName}', Hash={canonicalBoardHash.Substring(0, 8)}.., P{playerToMove}, DB_Depth={currentDepthOnOriginalBoard}. Using its existing recs.");
                finalRecommendations = existingOpeningPosition.MoveRecommendations.OrderByDescending(m => m.MoveScore).Select(m => new MoveScoreInfo { Row = m.MoveRow, Column = m.MoveCol, Score = m.MoveScore }).ToList();
                return (existingOpeningPosition, finalRecommendations);
            }

            // Logic for NEW position creation (applies if existingOpeningPosition was null)
            if (existingOpeningPosition == null) 
            {
                _logger.LogInformation($"GetOrCreate (New): Scenario='{scenarioName}', Hash={canonicalBoardHash.Substring(0, 8)}.., P{playerToMove}, DB_Depth={currentDepthOnOriginalBoard}. IsHardcodedContext={isHardcodedScenarioStep}, HasExplicitRecs={explicitRecommendations != null }.");

                if (!useExplicit) // If not a hardcoded path with explicit recs already set, then it must be an AI exploration call
                {
                    _logger.LogInformation($"GetOrCreate (New): Calling AIService for non-hardcoded recs for P{playerToMove}, Hash={canonicalBoardHash.Substring(0, 8)}..");
                    finalRecommendations = _aiService.GetTopMoveRecommendations(canonicalBoard, playerToMove, opponentPlayer, 3);
                }
                // If 'useExplicit' was true & existingOpeningPosition was null, finalRecommendations is already populated.

                if (finalRecommendations.Any())
                {
                    string canonicalBoardJson = JsonSerializer.Serialize(_aiService.ConvertBoardToListOfLists(canonicalBoard));
                    var newOpeningPosition = new OpeningPosition
                    {
                        PositionHash = canonicalBoardHash, MoveNumber = currentDepthOnOriginalBoard, BoardState = canonicalBoardJson,
                        PlayerToMove = playerToMove, EvaluationScore = finalRecommendations.Any() ? finalRecommendations[0].Score : 0,
                        GamePhase = (currentDepthOnOriginalBoard < 6) ? GamePhase.Opening : GamePhase.EarlyMiddle,
                        CreatedDate = DateTime.UtcNow, LastUpdatedDate = DateTime.UtcNow
                    };
                    foreach (var moveInfo in finalRecommendations)
                    {
                        newOpeningPosition.MoveRecommendations.Add(new MoveRecommendation { MoveRow = moveInfo.Row, MoveCol = moveInfo.Column, MoveScore = moveInfo.Score });
                    }
                    _context.OpeningPositions.Add(newOpeningPosition);
                    try
                    {
                        await _context.SaveChangesAsync();
                        _logger.LogInformation($"GetOrCreate (New): SAVED new Pos (Hash={canonicalBoardHash.Substring(0, 8)}.., P{playerToMove}) with {finalRecommendations.Count} recs. WasHardcodedContext: {isHardcodedScenarioStep}, DB_Depth: {currentDepthOnOriginalBoard}");
                        return (newOpeningPosition, finalRecommendations);
                    }
                    catch (Exception dbEx)
                    {
                        _logger.LogError(dbEx, $"GetOrCreate (New): Error SAVING Pos (Hash={canonicalBoardHash.Substring(0, 8)}.., P{playerToMove}).");
                        return null;
                    }
                }
                else // No recommendations (neither explicit valid ones, nor from AI service)
                {
                    _logger.LogWarning($"GetOrCreate (New): NO recommendations for NEW P{playerToMove} on Hash={canonicalBoardHash.Substring(0, 8)}.. (HardcodedContext: {isHardcodedScenarioStep}, ExplicitRecsProvided&Valid: {explicitRecommendations != null && explicitRecommendations.Any()}). Position will NOT be saved.");
                    return (null, new List<MoveScoreInfo>()); 
                }
            }
            _logger.LogWarning($"GetOrCreate: Reached end of logic unexpectedly. P{playerToMove}, Hash={canonicalBoardHash.Substring(0,8)}.., Existing: {existingOpeningPosition!=null}, IsHardcodedContext: {isHardcodedScenarioStep}");
            return (null, new List<MoveScoreInfo>());
        }
    }
} 