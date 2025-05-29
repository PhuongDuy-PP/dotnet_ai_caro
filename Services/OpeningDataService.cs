using CaroAIServer.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Generic; // For List in GetCanonicalBoard

namespace CaroAIServer.Services
{
    // Enum to represent the type of transformation
    public enum TransformationType
    {
        None, Rotate90, Rotate180, Rotate270,
        FlipNone, FlipRotate90, FlipRotate180, FlipRotate270
    }

    // Class to store transformation details
    public class TransformationInfo
    {
        public TransformationType Type { get; set; }
        public int Rotations { get; set; } // Number of 90-degree clockwise rotations
        public bool IsFlipped { get; set; } // Flipped horizontally

        public TransformationInfo(TransformationType type = TransformationType.None, int rotations = 0, bool isFlipped = false)
        {
            Type = type;
            Rotations = rotations;
            IsFlipped = isFlipped;
        }
    }

    public class OpeningDataService
    {
        private readonly ApplicationDbContext _context;
        private const int BoardSize = 15;
        private const int CenterRow = 7; // 0-indexed
        private const int CenterCol = 7; // 0-indexed
        private const int AiPlayerId = 2;
        private const int OpponentPlayerId = 1;

        public OpeningDataService(ApplicationDbContext context)
        {
            _context = context;
        }

        // --- Scoring Logic based on generate_data.txt ---

        private float CalculateCenterScore(int r, int c)
        {
            double distance = Math.Sqrt(Math.Pow(r - CenterRow, 2) + Math.Pow(c - CenterCol, 2));
            return (float)Math.Max(0, 100 - distance * 8);
        }

        private float CalculateMobilityScore(int r, int c, int[,] board) // Assuming empty board for initial moves
        {
            // For an empty board, mobility is maximal if not on edge, simplified here.
            // A more complex calculation would count actual free spaces if pieces were present.
            // The document states: "Center has 32 ô = 100 point, edge có ~20 ô = 62.5 point"
            // This simplified version gives higher scores to non-edge cells.
            int freeSpaces = 0;
            // Simplified: Max spaces = 8 directions * 4 depth = 32
            // Count actual available cells in 8 directions up to 4 cells deep
            int[] dr = { -1, -1, -1, 0, 0, 1, 1, 1 };
            int[] dc = { -1, 0, 1, -1, 1, -1, 0, 1 };

            for (int i = 0; i < 8; i++)
            {
                for (int k = 1; k <= 4; k++)
                {
                    int nr = r + dr[i] * k;
                    int nc = c + dc[i] * k;
                    if (nr >= 0 && nr < BoardSize && nc >= 0 && nc < BoardSize)
                    {
                        if (board[nr, nc] == 0) // Assuming 0 is empty
                            freeSpaces++;
                        else
                            break; // Blocked path
                    }
                    else
                    {
                        break; // Out of bounds
                    }
                }
            }
            return ( (float)freeSpaces / 32) * 100;
        }

        private float CalculateEdgePenalty(int r, int c)
        {
            int distToEdge = Math.Min(Math.Min(r, BoardSize - 1 - r), Math.Min(c, BoardSize - 1 - c));
            if (distToEdge == 0) return -30; // Corner/Edge (simplified to edge for this calc)
            if (distToEdge == 1) return -15;
            if (distToEdge == 2) return -5;
            return 0;
        }

        private float CalculateSymmetryBonus(int r, int c)
        {
            if (r == CenterRow && c == CenterCol) return 15; // Perfect center
            if (r == c || r + c == BoardSize - 1) return 10; // Main diagonals
            if (r == CenterRow || c == CenterCol) return 8; // Axes positions
            return 0;
        }

        public float CalculateTotalScore(int r, int c, int[,] board)
        {
            float centerScore = CalculateCenterScore(r, c);
            float mobilityScore = CalculateMobilityScore(r, c, board);
            float edgePenalty = CalculateEdgePenalty(r, c);
            float symmetryBonus = CalculateSymmetryBonus(r, c);

            return (centerScore * 0.4f) + (mobilityScore * 0.3f) + (edgePenalty * 0.2f) + (symmetryBonus * 0.1f);
        }

        // Helper to convert 2D array to List<List<int>> for serialization
        private List<List<int>> ConvertBoardToListOfLists(int[,] board)
        {
            var boardAsListOfLists = new List<List<int>>();
            for (int i = 0; i < BoardSize; i++)
            {
                var rowList = new List<int>();
                for (int j = 0; j < BoardSize; j++)
                {
                    rowList.Add(board[i, j]);
                }
                boardAsListOfLists.Add(rowList);
            }
            return boardAsListOfLists;
        }

        private async Task GenerateMovesForBoardStateAsync(int[,] originalBoard, int moveNumberForThisState_OriginalBoard)
        {
            // 1. Get the canonical form of the original board and the transformation applied
            (int[,] canonicalBoard, TransformationInfo transformToCanonical) = GetCanonicalBoardAndTransformation(originalBoard);
            string canonicalPositionHash = ComputeBoardHash(canonicalBoard);

            // 2. Check if this canonical position is already in the database
            if (await _context.OpeningPositions.AnyAsync(op => op.PositionHash == canonicalPositionHash))
            {
                Console.WriteLine($" Canonical opening moves for hash {canonicalPositionHash.Substring(0,8)} (from original) already generated.");
                return;
            }
            Console.WriteLine($"Generating new canonical opening data for hash {canonicalPositionHash.Substring(0,8)}. Original had {moveNumberForThisState_OriginalBoard} pieces.");

            // 3. Proceed with generation using the CANONICAL board
            string canonicalBoardJson = JsonSerializer.Serialize(ConvertBoardToListOfLists(canonicalBoard));

            var openingPosition = new OpeningPosition
            {
                PositionHash = canonicalPositionHash, // Store canonical hash
                MoveNumber = moveNumberForThisState_OriginalBoard, // Reflects piece count on ORIGINAL board when decision was made
                BoardState = canonicalBoardJson,    // Store canonical board state
                EvaluationScore = 0, 
                GamePhase = GamePhase.Opening,
                FrequencyPlayed = 0,
                WinRate = 0,
                CreatedDate = DateTime.UtcNow,
                BestMoves = "", 
            };

            List<MoveRecommendation> recommendations = new List<MoveRecommendation>();
            List<(int r, int c, float score)> allMoveScores = new List<(int, int, float)>();
            List<object> bestMovesJsonList = new List<object>();
            bool bookMoveApplied = false;

            // === LOGIC SÁCH KHAI CUỘC (APPLIED TO CANONICAL BOARD) ===
            // moveNumberForThisState_OriginalBoard is the number of pieces on the *original* board before AI makes a move.
            // This helps determine if it's AI's first actual move in a game, or a response.
            if (moveNumberForThisState_OriginalBoard == 0) // AI's first move on an empty board (original was empty, so canonical is also empty)
            {
                Console.WriteLine($" Applying book moves for AI's first move (canonical state: {canonicalPositionHash.Substring(0,8)})");
                allMoveScores.Add((CenterRow, CenterCol, 200f));
                allMoveScores.Add((CenterRow - 1, CenterCol, 195f));
                allMoveScores.Add((CenterRow + 1, CenterCol, 195f));
                allMoveScores.Add((CenterRow, CenterCol - 1, 195f));
                allMoveScores.Add((CenterRow, CenterCol + 1, 195f));
                bookMoveApplied = true;
            }
            else if (moveNumberForThisState_OriginalBoard == 1) // AI responding to opponent's first move (original had 1 piece)
            {
                // We need to find opponent's move ON THE CANONICAL BOARD
                (int oppR_canonical, int oppC_canonical) = (-1, -1);
                int pieceCountCanonical = 0;
                for (int r_find = 0; r_find < BoardSize; r_find++) {
                    for (int c_find = 0; c_find < BoardSize; c_find++) {
                        if (canonicalBoard[r_find, c_find] != 0) {
                            pieceCountCanonical++;
                            if (canonicalBoard[r_find, c_find] == OpponentPlayerId) {
                                oppR_canonical = r_find; oppC_canonical = c_find;
                            }
                        }
                    }
                }

                if (pieceCountCanonical == 1 && oppR_canonical != -1)
                {
                    Console.WriteLine($" Applying book moves for AI's response to opponent at ({oppR_canonical},{oppC_canonical}) on canonical board (hash: {canonicalPositionHash.Substring(0,8)})");
                    if (oppR_canonical == CenterRow && oppC_canonical == CenterCol) // Opponent took center on canonical board
                    {
                        allMoveScores.Add((CenterRow - 1, CenterCol, 200f)); 
                        allMoveScores.Add((CenterRow, CenterCol - 1, 198f));
                        bookMoveApplied = true;
                    }
                    // Add more specific book responses if OpponentPlayerId is at other key canonical positions
                }
            }
            // === KẾT THÚC LOGIC SÁCH KHAI CUỘC ===

            if (!bookMoveApplied) 
            {
                Console.WriteLine($" Using general scoring for canonical state: {canonicalPositionHash.Substring(0,8)}");
                for (int r_can = 0; r_can < BoardSize; r_can++)
                {
                    for (int c_can = 0; c_can < BoardSize; c_can++)
                    {
                        if (canonicalBoard[r_can, c_can] == 0) 
                        {
                            float score = CalculateTotalScore(r_can, c_can, canonicalBoard); 
                            allMoveScores.Add((r_can, c_can, score));
                        }
                    }
                }
            }
            
            allMoveScores.Sort((a, b) => b.score.CompareTo(a.score));

            int rank = 1;
            foreach (var moveScoreTuple in allMoveScores)
            {
                // (Logic for filtering, ranking, and adding to recommendations/bestMovesJsonList is mostly the same,
                // ensure it uses scores and ranks appropriately for book vs non-book moves)
                bool isBookMove = bookMoveApplied && moveScoreTuple.score >= 180f; 

                if (!isBookMove) {
                    if (moveScoreTuple.score < 70 && rank > 10 && moveNumberForThisState_OriginalBoard > 0) break; 
                    if (moveScoreTuple.score < 85 && rank > 5 && moveNumberForThisState_OriginalBoard == 0 && !bookMoveApplied) break; 
                }
                if (rank > 15 && !isBookMove) break; 
                if (rank > 8 && isBookMove && moveNumberForThisState_OriginalBoard == 0) break; 
                if (rank > 10 && isBookMove) break; 

                string reasoning = moveScoreTuple.score >= 180f ? "Book Move Candidate" : (moveScoreTuple.score >= 95 ? "Tier S" : "Tier A/B/C");

                recommendations.Add(new MoveRecommendation
                {
                    OpeningPosition = openingPosition, // EF will link it via context
                    MoveRow = moveScoreTuple.r,    // These are moves on the CANONICAL board
                    MoveCol = moveScoreTuple.c,
                    MoveScore = moveScoreTuple.score,
                    MoveRank = rank,
                    Reasoning = reasoning,
                    ThreatLevel = ThreatLevel.None 
                });
                bestMovesJsonList.Add(new { r = moveScoreTuple.r, c = moveScoreTuple.c, score = moveScoreTuple.score, rank });
                rank++;
            }
            
            openingPosition.BestMoves = JsonSerializer.Serialize(bestMovesJsonList);
            openingPosition.MoveRecommendations = recommendations;
            _context.OpeningPositions.Add(openingPosition);
            await _context.SaveChangesAsync();
            Console.WriteLine($"Successfully generated and saved CANONICAL opening data for hash {canonicalPositionHash.Substring(0,8)}.");
        }

        // Added from AIService or as a shared utility
        private string ComputeBoardHash(int[,] board)
        {
            var boardList = new List<List<int>>(board.GetLength(0));
            for (int i = 0; i < board.GetLength(0); i++)
            {
                var row = new List<int>(board.GetLength(1));
                for (int j = 0; j < board.GetLength(1); j++)
                {
                    row.Add(board[i, j]);
                }
                boardList.Add(row);
            }
            string serializedBoard = System.Text.Json.JsonSerializer.Serialize(boardList);
            using (System.Security.Cryptography.SHA256 sha256 = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(serializedBoard));
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }

        // Simulates a player making the strongest move based on current logic, without saving to DB.
        // Used by GenerateDeepOpeningSequenceAsync to simulate opponent moves or AI moves in a sequence.
        // private (int r, int c) GetStrongestMoveForPlayer(int[,] board, int playerMakingMove, int currentMoveNumberInSequence_Game)
        // {
        //     // If it's an early move and we have a book move for this exact board state (after normalization)
        //     // This part is more about responding to specific opponent openings rather than general strong play
        //     // For self-play, we might want a more general approach or a mix.

        //     // For now, let's use the CalculateTotalScore approach, but we should refine this for self-play
        //     // to be more strategic than just picking the highest immediate score.

        //     // Fallback to highest score if no book move or if we want diverse strong moves
        //     List<MoveRecommendation> potentialMoves = new List<MoveRecommendation>();
        //     for (int r = 0; r < BoardSize; r++)
        //     {
        //         for (int c = 0; c < BoardSize; c++)
        //         {
        //             if (board[r, c] == 0) // Empty cell
        //             {
        //                 float score = CalculateTotalScore(r, c, board, playerMakingMove, currentMoveNumberInSequence_Game, false, null);
        //                 potentialMoves.Add(new MoveRecommendation { Row = r, Column = c, MoveScore = score, OpeningPosition = null });
        //             }
        //         }
        //     }

        //     if (!potentialMoves.Any()) return (-1, -1); // Should not happen on a non-full board

        //     var bestMove = potentialMoves.OrderByDescending(m => m.MoveScore).First();
        //     // Add some randomness if multiple moves have the same top score to encourage variety in self-play
        //     var topMoves = potentialMoves.Where(m => m.MoveScore == bestMove.MoveScore).ToList();
        //     if (topMoves.Count > 1)
        //     {
        //         Random random = new Random();
        //         bestMove = topMoves[random.Next(topMoves.Count)];
        //     }
        //     Console.WriteLine($"GetStrongestMoveForPlayer (static) for P{playerMakingMove} chose ({bestMove.Row},{bestMove.Column}) with score {bestMove.MoveScore}");
        //     return (bestMove.Row, bestMove.Column);
        // }
        private bool IsBoardEmpty(int[,] board) {
            for(int r=0;r<BoardSize;r++) for(int c=0;c<BoardSize;c++) if(board[r,c]!=0) return false;
            return true;
        }

        private async Task GenerateDeepOpeningSequenceAsync(string sequenceName, int[,] initialBoard, int firstPlayer, int maxDepth)
        {
            Console.WriteLine($"--- Generating Deep Canonical Opening Sequence: {sequenceName} (Depth: {maxDepth}) ---");
            int[,] currentOriginalBoard = (int[,])initialBoard.Clone();
            int currentPlayer = firstPlayer;

            for (int gameMoveIndex = 0; gameMoveIndex < maxDepth; gameMoveIndex++) // gameMoveIndex is 0 for first move, 1 for second, etc.
            {
                Console.WriteLine($"Sequence {sequenceName}, Game Move {gameMoveIndex + 1}, Player {currentPlayer}");

                if (currentPlayer == AiPlayerId)
                {
                    int piecesOnBoard = 0;
                    for(int r=0; r<BoardSize; r++) for(int c=0; c<BoardSize; c++) if(currentOriginalBoard[r,c] != 0) piecesOnBoard++;
                    
                    // GenerateMovesForBoardStateAsync now handles canonical conversion internally
                    await GenerateMovesForBoardStateAsync(currentOriginalBoard, piecesOnBoard);
                }

                (int bestR, int bestC) = GetStrongestMoveForPlayer(currentOriginalBoard, currentPlayer, gameMoveIndex / 2);

                if (bestR == -1 || currentOriginalBoard[bestR, bestC] != 0) 
                { 
                    Console.WriteLine($"No valid move for player {currentPlayer} in sequence {sequenceName}. Stopping.");
                    break; 
                }
                currentOriginalBoard[bestR, bestC] = currentPlayer;
                
                if (gameMoveIndex == maxDepth -1) {
                     Console.WriteLine($"Sequence {sequenceName} reached max depth.");
                }
                currentPlayer = (currentPlayer == AiPlayerId) ? OpponentPlayerId : AiPlayerId;
            }
        }

        public async Task SeedDatabaseAsync()
        {
            Console.WriteLine("--- Starting Deep CANONICAL Opening Database Seeding ---");
            int sequenceDepth = 12; 

            var emptyBoard = new int[BoardSize, BoardSize];
            await GenerateDeepOpeningSequenceAsync("AI_Starts_EmptyBoard_Canonical", emptyBoard, AiPlayerId, sequenceDepth);

            var boardOpponent77 = new int[BoardSize, BoardSize];
            boardOpponent77[CenterRow, CenterCol] = OpponentPlayerId;
            await GenerateDeepOpeningSequenceAsync("Opponent_7_7_AI_Responds_Canonical", boardOpponent77, AiPlayerId, sequenceDepth -1);

            var boardOpponent67 = new int[BoardSize, BoardSize];
            boardOpponent67[CenterRow - 1, CenterCol] = OpponentPlayerId;
            await GenerateDeepOpeningSequenceAsync("Opponent_6_7_AI_Responds_Canonical", boardOpponent67, AiPlayerId, sequenceDepth -1);
            
            var boardOpponent00 = new int[BoardSize, BoardSize];
            boardOpponent00[0,0] = OpponentPlayerId;
            await GenerateDeepOpeningSequenceAsync("Opponent_0_0_AI_Responds_Canonical", boardOpponent00, AiPlayerId, sequenceDepth -1);

            var boardOpponent66 = new int[BoardSize, BoardSize];
            boardOpponent66[CenterRow-1, CenterCol-1] = OpponentPlayerId;
            await GenerateDeepOpeningSequenceAsync("Opponent_6_6_AI_Responds_Canonical", boardOpponent66, AiPlayerId, sequenceDepth -1);

            var boardOpponent55 = new int[BoardSize, BoardSize];
            boardOpponent55[CenterRow-2, CenterCol-2] = OpponentPlayerId;
            await GenerateDeepOpeningSequenceAsync("Opponent_5_5_AI_Responds_Canonical", boardOpponent55, AiPlayerId, sequenceDepth -1);
            
            // Add 20+ scenarios as requested
            // Example: opponent plays (7,5)
            var boardOpponent75 = new int[BoardSize, BoardSize];
            boardOpponent75[CenterRow, CenterCol - 2] = OpponentPlayerId;
            await GenerateDeepOpeningSequenceAsync("Opponent_7_5_AI_Responds_Canonical", boardOpponent75, AiPlayerId, sequenceDepth - 1);

            // Example: opponent plays (8,7) - symmetric to (6,7) but good to have explicitly if not relying on perfect canonicalization yet for specific responses
            var boardOpponent87 = new int[BoardSize, BoardSize];
            boardOpponent87[CenterRow + 1, CenterCol] = OpponentPlayerId;
            await GenerateDeepOpeningSequenceAsync("Opponent_8_7_AI_Responds_Canonical", boardOpponent87, AiPlayerId, sequenceDepth - 1);
            
            // ... Add up to 20 or more initial opponent moves ...
            // For instance, systematically cover all moves in a 5x5 or 7x7 central area, or other known strong first moves by opponent.
            List<(int r, int c, string name)> opponentFirstMoves = new List<(int r, int c, string name)> {
                (CenterRow, CenterCol + 1, "7_8"), // (7,8)
                (CenterRow -1, CenterCol +1, "6_8"), // (6,8)
                (CenterRow +1, CenterCol -1, "8_6"), // (8,6)
                (CenterRow +1, CenterCol +1, "8_8"), // (8,8)
                (CenterRow, CenterCol +2, "7_9"),    // (7,9)
                (CenterRow, CenterCol -2, "7_5"),    // (7,5) - already added, example of how to list
                (CenterRow +2, CenterCol, "9_7"),    // (9,7)
                (CenterRow -2, CenterCol, "5_7"),    // (5,7)
                (CenterRow -2, CenterCol-2, "5_5"), // (5,5) - already added
                (CenterRow +2, CenterCol+2, "9_9"), // (9,9)
                (CenterRow -2, CenterCol+2, "5_9"), // (5,9)
                (CenterRow +2, CenterCol-2, "9_5"), // (9,5)
                // Add more to reach 20+
                (CenterRow-1, CenterCol-2, "6_5"),
                (CenterRow-1, CenterCol+2, "6_9"),
                (CenterRow+1, CenterCol-2, "8_5"),
                (CenterRow+1, CenterCol+2, "8_9"),
                (CenterRow-2, CenterCol-1, "5_6"),
                (CenterRow-2, CenterCol+1, "5_8"),
                (CenterRow+2, CenterCol-1, "9_6"),
                (CenterRow+2, CenterCol+1, "9_8"),
                // Adding 5 more diverse scenarios
                (CenterRow - 3, CenterCol, "4_7"),     // (4,7)
                (CenterRow + 3, CenterCol, "10_7"),    // (10,7)
                (CenterRow, CenterCol - 3, "7_4"),     // (7,4)
                (CenterRow, CenterCol + 3, "7_10"),    // (7,10)
                (CenterRow - 3, CenterCol - 3, "4_4")  // (4,4)
            };

            foreach(var moveInfo in opponentFirstMoves)
            {
                // Avoid re-adding if covered by earlier specific examples, this is just illustrative
                if (sequenceNameExists(moveInfo.name)) continue; // pseudo-code for checking if a sequence name was already used
                
                var board = new int[BoardSize, BoardSize];
                board[moveInfo.r, moveInfo.c] = OpponentPlayerId;
                await GenerateDeepOpeningSequenceAsync($"Opponent_{moveInfo.name}_AI_Responds_Canonical", board, AiPlayerId, sequenceDepth -1);
            }

            Console.WriteLine("--- All Deep CANONICAL Opening Database Seeding Scenarios Initiated ---");
        }
        // Helper to avoid re-running for same named sequences if list has duplicates/overlaps with manual ones
        private bool sequenceNameExists(string nameSuffix) { return false; } // Implement actual check if needed or ensure unique names

        // --- Board Transformation Utilities ---
        private int[,] RotateBoard(int[,] originalBoard)
        {
            int[,] rotatedBoard = new int[BoardSize, BoardSize];
            for (int i = 0; i < BoardSize; i++)
            {
                for (int j = 0; j < BoardSize; j++)
                {
                    rotatedBoard[j, BoardSize - 1 - i] = originalBoard[i, j];
                }
            }
            return rotatedBoard;
        }

        private int[,] FlipBoard(int[,] originalBoard) // Horizontal flip
        {
            int[,] flippedBoard = new int[BoardSize, BoardSize];
            for (int i = 0; i < BoardSize; i++)
            {
                for (int j = 0; j < BoardSize; j++)
                {
                    flippedBoard[i, BoardSize - 1 - j] = originalBoard[i, j];
                }
            }
            return flippedBoard;
        }

        private List<int[,]> GetAllSymmetricBoards(int[,] board)
        {
            List<int[,]> symmetricBoards = new List<int[,]>();
            int[,] currentBoard = (int[,])board.Clone();

            for (int flip = 0; flip < 2; flip++)
            {
                if (flip == 1)
                {
                    currentBoard = FlipBoard(board); // Flip the original board once
                }
                else
                {
                    currentBoard = (int[,])board.Clone(); // Start with original or original flipped board
                }

                for (int rot = 0; rot < 4; rot++)
                {
                    if (rot > 0) // Rotate for 90, 180, 270. rot=0 is the initial state (or flipped initial)
                    {
                        currentBoard = RotateBoard(currentBoard);
                    }
                    symmetricBoards.Add((int[,])currentBoard.Clone());
                }
            }
            return symmetricBoards;
        }

        // Gets the canonical board and the transformation applied to the original to get it.
        // The canonical form is defined as the one with the lexicographically smallest hash.
        private (int[,] canonicalBoard, TransformationInfo transformationToCanonical) GetCanonicalBoardAndTransformation(int[,] originalBoard)
        {
            string smallestHash = null;
            int[,] canonicalBoard = null;
            TransformationInfo appliedTransformation = new TransformationInfo();

            int[,] currentBoard = (int[,])originalBoard.Clone();
            bool isFlipped = false;

            for (int flipCount = 0; flipCount < 2; flipCount++)
            {
                if (flipCount == 1)
                {
                    currentBoard = FlipBoard(originalBoard);
                    isFlipped = true;
                }
                else
                {
                    currentBoard = (int[,])originalBoard.Clone();
                    isFlipped = false;
                }

                for (int rotationCount = 0; rotationCount < 4; rotationCount++)
                {
                    if (rotationCount > 0)
                    {
                        currentBoard = RotateBoard(currentBoard);
                    }

                    string currentHash = ComputeBoardHash(currentBoard);
                    if (canonicalBoard == null || String.Compare(currentHash, smallestHash, StringComparison.Ordinal) < 0)
                    {
                        smallestHash = currentHash;
                        canonicalBoard = (int[,])currentBoard.Clone();
                        appliedTransformation.Rotations = rotationCount;
                        appliedTransformation.IsFlipped = isFlipped;
                        // Determine TransformationType (optional, mostly for debugging/clarity)
                        if (isFlipped)
                            appliedTransformation.Type = (TransformationType)(4 + rotationCount);
                        else
                            appliedTransformation.Type = (TransformationType)rotationCount;
                    }
                }
            }
            return (canonicalBoard, appliedTransformation);
        }

        // Transforms a single move according to the board transformation
        private (int r, int c) TransformMove((int r, int c) move, TransformationInfo transform)
        {
            int r = move.r;
            int c = move.c;

            if (transform.IsFlipped)
            {
                c = BoardSize - 1 - c;
            }

            for (int i = 0; i < transform.Rotations; i++)
            {
                int tempR = r;
                r = c; // New row is old col
                c = BoardSize - 1 - tempR; // New col is (N-1) - old row
            }
            return (r, c);
        }

        // Inverse transform for a move (to apply to a move from canonical board back to original board context)
        // This is crucial for AIService
        private (int r, int c) InverseTransformMove((int r, int c) moveOnCanonical, TransformationInfo transformAppliedToOriginal)
        {
            int r = moveOnCanonical.r;
            int c = moveOnCanonical.c;

            // Inverse rotations
            int inverseRotations = (4 - transformAppliedToOriginal.Rotations) % 4;
            for (int i = 0; i < inverseRotations; i++)
            {
                int tempR = r;
                r = BoardSize - 1 - c; // New row is (N-1) - old col
                c = tempR;             // New col is old row
            }

            // Inverse flip (flip is its own inverse)
            if (transformAppliedToOriginal.IsFlipped)
            {
                c = BoardSize - 1 - c;
            }
            return (r, c);
        }

        // --- Evaluation and Minimax ---
        private float EvaluateBoardState(int[,] board, int playerForPerspective)
        {
            // Placeholder evaluation. Needs significant improvement.
            // This function should evaluate the overall board state from playerForPerspective's view.
            // Positive if good for playerForPerspective, negative if bad.
            float score = 0;
            int opponent = (playerForPerspective == AiPlayerId) ? OpponentPlayerId : AiPlayerId;

            // Example: Count material (very basic)
            // for (int r = 0; r < BoardSize; r++)
            // {
            //     for (int c = 0; c < BoardSize; c++)
            //     {
            //         if (board[r, c] == playerForPerspective) score += 10;
            //         else if (board[r, c] == opponent) score -= 10;
            //     }
            // }

            // Iterate over all possible lines of 5 to check for threats/wins (simplified)
            // This is a very complex part if done thoroughly.
            // For now, let's use a sum of CalculateTotalScore for player's pieces vs opponent's pieces.
            // This is NOT a good board evaluation function but a starting point.
            score += GetSimplifiedPlayerScore(board, playerForPerspective);
            score -= GetSimplifiedPlayerScore(board, opponent);
            
            // Add a small random factor to break ties or encourage exploration if needed (optional)
            // Random random = new Random();
            // score += (float)(random.NextDouble() * 0.1 - 0.05); // Small noise

            return score;
        }

        private float GetSimplifiedPlayerScore(int[,] board, int player)
        {
            float totalPlayerScore = 0;
            for (int r = 0; r < BoardSize; r++)
            {
                for (int c = 0; c < BoardSize; c++)
                {
                    if (board[r, c] == player)
                    {
                        // Consider the strategic value of existing pieces based on static score components
                        // This is not CalculateTotalScore for an empty cell, but for an existing piece.
                        totalPlayerScore += CalculateCenterScore(r,c) * 0.2f; // Less weight than placing new piece
                        totalPlayerScore += CalculateSymmetryBonus(r, c) * 0.05f;
                        totalPlayerScore += 5; // Base value for a piece
                    }
                }
            }
            return totalPlayerScore;
        }

        private (float score, (int r, int c) move) MinimaxAlphaBeta(
            int[,] board, 
            int depth, 
            float alpha, 
            float beta, 
            bool maximizingPlayer, 
            int aiPlayerIdForMinimaxPerspective) // The player Minimax is trying to optimize for
        {
            if (depth == 0) // Or terminal node (win/loss/draw - simplified for now)
            {
                return (EvaluateBoardState(board, aiPlayerIdForMinimaxPerspective), (-1,-1));
            }

            List<(int r, int c)> possibleMoves = GetEmptyCells(board);
            if (!possibleMoves.Any()) // No moves left (draw or board full)
            {
                 return (EvaluateBoardState(board, aiPlayerIdForMinimaxPerspective), (-1,-1));
            }

            (int r, int c) bestMove = (-1,-1);

            if (maximizingPlayer) // AI's turn (aiPlayerIdForMinimaxPerspective)
            {
                float maxEval = float.NegativeInfinity;
                bestMove = possibleMoves[0]; // Default to first move if all have same bad score

                foreach (var move in possibleMoves)
                {
                    int[,] newBoard = (int[,])board.Clone();
                    newBoard[move.r, move.c] = aiPlayerIdForMinimaxPerspective;
                    (float eval, _) = MinimaxAlphaBeta(newBoard, depth - 1, alpha, beta, false, aiPlayerIdForMinimaxPerspective);
                    if (eval > maxEval)
                    {
                        maxEval = eval;
                        bestMove = move;
                    }
                    alpha = Math.Max(alpha, eval);
                    if (beta <= alpha) break;
                }
                return (maxEval, bestMove);
            }
            else // Opponent's turn
            {
                float minEval = float.PositiveInfinity;
                bestMove = possibleMoves[0]; // Default to first move
                int opponentOfPerspectiveAI = (aiPlayerIdForMinimaxPerspective == AiPlayerId) ? OpponentPlayerId : AiPlayerId;

                foreach (var move in possibleMoves)
                {
                    int[,] newBoard = (int[,])board.Clone();
                    newBoard[move.r, move.c] = opponentOfPerspectiveAI;
                    (float eval, _) = MinimaxAlphaBeta(newBoard, depth - 1, alpha, beta, true, aiPlayerIdForMinimaxPerspective);
                     if (eval < minEval)
                    {
                        minEval = eval;
                        bestMove = move;
                    }
                    beta = Math.Min(beta, eval);
                    if (beta <= alpha) break;
                }
                return (minEval, bestMove);
            }
        }

        private List<(int r, int c)> GetEmptyCells(int[,] board)
        {
            var emptyCells = new List<(int r, int c)>();
            for(int r=0; r<BoardSize; r++)
                for(int c=0; c<BoardSize; c++)
                    if(board[r,c] == 0) emptyCells.Add((r,c));
            return emptyCells;
        }

        // GetStrongestMoveForPlayer will now use Minimax
        private (int r, int c) GetStrongestMoveForPlayer(int[,] board, int playerMakingMove, int gameDepth)
        {
            Console.WriteLine($" GetStrongestMoveForPlayer for P{playerMakingMove} at game depth {gameDepth} using Minimax(3). Board hash: {ComputeBoardHash(board).Substring(0,6)}");
            // playerMakingMove is the one whose turn it is currently.
            // aiPlayerIdForMinimaxPerspective for Minimax should be playerMakingMove, as we want the best move for them.
            (float score, (int r, int c) move) result = MinimaxAlphaBeta(board, 3, float.NegativeInfinity, float.PositiveInfinity, true, playerMakingMove);
            
            if (result.move.r == -1 && GetEmptyCells(board).Any()) { // Minimax failed to find a move but cells are available
                Console.WriteLine($" Minimax returned no move for P{playerMakingMove}, falling back to random for simulation.");
                return GetEmptyCells(board)[new Random().Next(GetEmptyCells(board).Count)];
            }
            Console.WriteLine($" Minimax for P{playerMakingMove} suggests ({result.move.r},{result.move.c}) with score {result.score}");
            return result.move;
        }
    }
} 