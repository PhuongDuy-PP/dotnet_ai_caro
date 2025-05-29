using CaroAIServer.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Generic; // Required for List
using System.Linq;
using System.Threading.Tasks;
using System;

namespace CaroAIServer.Services;

// Copied from OpeningDataService for now. Ideally, this would be in a shared utility.
public enum TransformationType_AI
{
    None, Rotate90, Rotate180, Rotate270,
    FlipNone, FlipRotate90, FlipRotate180, FlipRotate270
}

public class TransformationInfo_AI
{
    public TransformationType_AI Type { get; set; }
    public int Rotations { get; set; }
    public bool IsFlipped { get; set; }

    public TransformationInfo_AI(TransformationType_AI type = TransformationType_AI.None, int rotations = 0, bool isFlipped = false)
    {
        Type = type;
        Rotations = rotations;
        IsFlipped = isFlipped;
    }
}

public class AIService
{
    private readonly ApplicationDbContext _context;
    private readonly GameService _gameService; // Assuming GameService.BoardSize is accessible or pass BoardSize
    private const int BoardSize = 15; // Duplicating for standalone logic, or get from GameService

    // --- Transposition Table Data Structures ---
    private enum TTFlag { EXACT, LOWER_BOUND, UPPER_BOUND }
    private class TranspositionTableEntry
    {
        public float Score { get; set; }
        public int Depth { get; set; }
        public TTFlag Flag { get; set; }
        public (int r, int c) BestMove { get; set; }
    }
    private Dictionary<string, TranspositionTableEntry> transpositionTable;
    // --- End of Transposition Table Data Structures ---

    // --- Scoring Constants for Pattern Evaluation ---
    private const float SCORE_FIVE_IN_ROW = 100000000f;
    private const float SCORE_LIVE_FOUR = 1000000f;    // _XXXX_
    private const float SCORE_DEAD_FOUR = 100000f;     // OXXXX_ or _XXXXO
    private const float SCORE_LIVE_THREE = 50000f;     // _XXX_
    private const float SCORE_DEAD_THREE = 5000f;      // OXXX_ or _XXXO
    private const float SCORE_LIVE_TWO = 200f;       // _XX_
    private const float SCORE_DEAD_TWO = 20f;        // OXX_ or _XXO
    private const float SCORE_BROKEN_THREE_LIVE = 20000f; // _X_XX_ or _XX_X_
    private const float SCORE_BROKEN_THREE_DEAD = 2000f;  // O_X_XX_ or _XX_X_O etc.

    public AIService(ApplicationDbContext context, GameService gameService)
    {
        _context = context;
        _gameService = gameService;
    }

    // --- Board Transformation Utilities (Copied from OpeningDataService) ---
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

    private (int[,] canonicalBoard, TransformationInfo_AI transformationToCanonical) GetCanonicalBoardAndTransformation(int[,] originalBoard)
    {
        string smallestHash = null;
        int[,] canonicalBoard = null;
        TransformationInfo_AI appliedTransformation = new TransformationInfo_AI();
        int[,] currentBoard = (int[,])originalBoard.Clone();
        bool isFlipped = false;

        for (int flipCount = 0; flipCount < 2; flipCount++)
        {
            if (flipCount == 1) { currentBoard = FlipBoard(originalBoard); isFlipped = true; }
            else { currentBoard = (int[,])originalBoard.Clone(); isFlipped = false; }

            for (int rotationCount = 0; rotationCount < 4; rotationCount++)
            {
                if (rotationCount > 0) { currentBoard = RotateBoard(currentBoard); }
                string currentHash = ComputeBoardHash(currentBoard); // Uses AIService's own ComputeBoardHash
                if (canonicalBoard == null || String.Compare(currentHash, smallestHash, StringComparison.Ordinal) < 0)
                {
                    smallestHash = currentHash;
                    canonicalBoard = (int[,])currentBoard.Clone();
                    appliedTransformation.Rotations = rotationCount;
                    appliedTransformation.IsFlipped = isFlipped;
                    if (isFlipped) appliedTransformation.Type = (TransformationType_AI)(4 + rotationCount);
                    else appliedTransformation.Type = (TransformationType_AI)rotationCount;
                }
            }
        }
        return (canonicalBoard, appliedTransformation);
    }
    
    private (int r, int c) InverseTransformMove((int r, int c) moveOnCanonical, TransformationInfo_AI transformAppliedToOriginal)
    {
        int r = moveOnCanonical.r;
        int c = moveOnCanonical.c;
        int inverseRotations = (4 - transformAppliedToOriginal.Rotations) % 4;
        for (int i = 0; i < inverseRotations; i++)
        {
            int tempR = r;
            r = BoardSize - 1 - c; 
            c = tempR;             
        }
        if (transformAppliedToOriginal.IsFlipped)
        {
            c = BoardSize - 1 - c;
        }
        return (r, c);
    }
    // --- End of Transformation Utilities ---


    public async Task<(int row, int col)> GetBestMoveAsync(int[,] currentBoard_Original, int aiPlayer)
    {
        // 1. Normalize the current board to its canonical form
        (int[,] canonicalBoard, TransformationInfo_AI transformToGetCanonical) = GetCanonicalBoardAndTransformation(currentBoard_Original);
        string canonicalBoardHash = ComputeBoardHash(canonicalBoard);

        Console.WriteLine($"AI Service: Original Board Hash: {ComputeBoardHash(currentBoard_Original).Substring(0,8)}, Canonical Hash: {canonicalBoardHash.Substring(0,8)}, Transform: {transformToGetCanonical.Type}");

        // 2. Check Opening Book using the canonical board hash
        var openingPosition = await _context.OpeningPositions
                                            .Include(op => op.MoveRecommendations)
                                            .FirstOrDefaultAsync(op => op.PositionHash == canonicalBoardHash);

        if (openingPosition != null && openingPosition.MoveRecommendations.Any())
        {
            var bestRecommendation_Canonical = openingPosition.MoveRecommendations.OrderByDescending(r => r.MoveScore).First();
            (int moveR_Canonical, int moveC_Canonical) = (bestRecommendation_Canonical.MoveRow, bestRecommendation_Canonical.MoveCol);
            
            // 3. Inverse transform the recommended move back to the original board's orientation
            (int finalMoveR, int finalMoveC) = InverseTransformMove((moveR_Canonical, moveC_Canonical), transformToGetCanonical);
            
            Console.WriteLine($"AI Service: Found book move for canonical {canonicalBoardHash.Substring(0,8)}. Canonical move: ({moveR_Canonical},{moveC_Canonical}). Original board move: ({finalMoveR},{finalMoveC})");
            
            // Ensure the final move is valid on the original board (should be if logic is correct)
            if (finalMoveR >= 0 && finalMoveR < BoardSize && finalMoveC >= 0 && finalMoveC < BoardSize && currentBoard_Original[finalMoveR, finalMoveC] == 0)
            {
                return (finalMoveR, finalMoveC);
            }
            else
            {
                 Console.WriteLine($"AI Service WARNING: Inverse transformed move ({finalMoveR},{finalMoveC}) is invalid on original board. Falling back.");
                 // Fallback if something went wrong, though ideally this shouldn't be hit.
            }
        }

        // 4. If no opening book move (or transformed move was invalid), use Minimax (or current fallback)
        // This part remains unchanged for now (Minimax placeholder / random move)
        // IMPORTANT: The Minimax/evaluation function itself does NOT need to know about canonical forms.
        // It evaluates the *currentOriginalBoard* as is. Normalization is only for book lookup.
        Console.WriteLine($"AI Service: No book move for canonical {canonicalBoardHash.Substring(0,8)}. Using Minimax.");
        // Determine opponent player ID (assuming AI is 2, Opponent is 1, or vice versa)
        int opponentPlayerId = (aiPlayer == 1) ? 2 : 1; 
        return GetBestMoveUsingMinimax(currentBoard_Original, aiPlayer, opponentPlayerId); // Operates on original board
    }

    // ComputeBoardHash (already exists and is used by GetCanonicalBoardAndTransformation)
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
        string serializedBoard = JsonSerializer.Serialize(boardList);
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(serializedBoard));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }

    private (int row, int col) GetRandomValidMove(int[,] board)
    {
        var validMoves = new List<(int, int)>();
        for (int i = 0; i < BoardSize; i++) // Use local BoardSize or GameService.BoardSize
        {
            for (int j = 0; j < BoardSize; j++)
            {
                if (board[i, j] == 0)
                {
                    validMoves.Add((i, j));
                }
            }
        }
        if (validMoves.Any())
        {
            Random rand = new Random();
            return validMoves[rand.Next(validMoves.Count)];
        }
        return (-1, -1); 
    }
    // Placeholder for Minimax

    // --- Minimax Alpha-Beta (Copied and adapted from OpeningDataService) ---
    private float EvaluateBoardState(int[,] board, int playerForPerspective, int opponentPlayer)
    {
        // This function evaluates the overall board state from playerForPerspective\'s view.
        // Positive if good for playerForPerspective, negative if bad.
        float perspectiveScore = CalculateDetailedPlayerScore(board, playerForPerspective, opponentPlayer);
        float opponentScore = CalculateDetailedPlayerScore(board, opponentPlayer, playerForPerspective);

        return perspectiveScore - opponentScore;
    }

    private float CalculateDetailedPlayerScore(int[,] board, int player, int opponent)
    {
        float totalScore = 0;
        int[] line = new int[6];

        // Directions: Horizontal, Vertical, Diagonal Down-Right, Diagonal Down-Left
        int[][] directions = new int[][]
        {
            new int[] {0, 1}, // Horizontal
            new int[] {1, 0}, // Vertical
            new int[] {1, 1}, // Diagonal Down-Right
            new int[] {1, -1} // Diagonal Down-Left
        };

        // Iterate over all cells as potential starting points of a 6-cell line
        for (int r = 0; r < BoardSize; r++)
        {
            for (int c = 0; c < BoardSize; c++)
            {
                foreach (var dir in directions)
                {
                    int dr = dir[0];
                    int dc = dir[1];

                    // Check if the 6-cell line would go out of bounds early
                    // A full 6-cell line needs 5 steps from the start.
                    if (r + dr * 5 < 0 || r + dr * 5 >= BoardSize || c + dc * 5 < 0 || c + dc * 5 >= BoardSize)
                    {
                        // For some patterns, like XXXXX at the edge, we might still want to evaluate shorter segments
                        // or a 5-cell line. For now, this simplified check ensures we can form a 6-cell line.
                        // This check is more about the *end* of a 6-cell line.
                        // A simpler approach: just try to populate and let ScoreSingle6CellLine handle boundaries.
                    }

                    // Populate the 6-cell line segment
                    bool possibleLine = true;
                    for (int k = 0; k < 6; k++)
                    {
                        int curR = r + k * dr;
                        int curC = c + k * dc;
                        if (curR >= 0 && curR < BoardSize && curC >= 0 && curC < BoardSize)
                        {
                            line[k] = board[curR, curC];
                        }
                        else
                        {
                            // If we need a full 6-cell line for analysis and it goes out of bounds,
                            // this specific starting point (r,c) in this direction (dr,dc) might not form a full 6-cell line.
                            // The ScoreSingle6CellLine function will treat out-of-bounds as a special blocker.
                            // To ensure we only evaluate valid lines that start on board:
                            if (k==0 && (curR < 0 || curR >= BoardSize || curC < 0 || curC >= BoardSize)) {
                                possibleLine = false; // Starting cell itself is off-board (should not happen with r,c loop)
                                break;
                            }
                            line[k] = -1; // Sentinel for "off-board"
                        }
                    }
                    
                    if(possibleLine){
                         totalScore += ScoreSingle6CellLine(line, player, opponent);
                    }
                }
            }
        }
        return totalScore;
    }

    // Scores a single 6-cell line for the given player.
    // Returns the score of the most valuable pattern found for 'player'.
    private float ScoreSingle6CellLine(int[] L, int P, int O) // Line, Player, Opponent
    {
        // L is the 6-cell line. P is player's ID, O is opponent's ID, 0 is empty, -1 is off-board.

        // Check for Five-in-a-Row (P P P P P)
        // XXXXX_ or _XXXXX
        if (L[0] == P && L[1] == P && L[2] == P && L[3] == P && L[4] == P) return SCORE_FIVE_IN_ROW;
        if (L[1] == P && L[2] == P && L[3] == P && L[4] == P && L[5] == P) return SCORE_FIVE_IN_ROW;

        // Check for Live Four ( _ P P P P _ )
        if (L[0] == 0 && L[1] == P && L[2] == P && L[3] == P && L[4] == P && L[5] == 0) return SCORE_LIVE_FOUR;
        
        float currentMaxScore = 0;

        // Check for Dead Four (O P P P P _  or _ P P P P O)
        if (L[0] == O && L[1] == P && L[2] == P && L[3] == P && L[4] == P && L[5] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_FOUR);
        if (L[0] == 0 && L[1] == P && L[2] == P && L[3] == P && L[4] == P && L[5] == O) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_FOUR);
        // Check for P P P P O (if not a five already, L[0] must be P or empty or off-board)
        if (L[0] != O && L[1] == P && L[2] == P && L[3] == P && L[4] == P && L[5] == O && !(L[0]==P && L[1]==P && L[2]==P && L[3]==P && L[4]==P) ) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_FOUR);
        // Check for O P P P P P (if not a five already, L[5] must be P or empty or off-board)
        if (L[0] == O && L[1] == P && L[2] == P && L[3] == P && L[4] == P && L[5] != O && !(L[1]==P && L[2]==P && L[3]==P && L[4]==P && L[5]==P) ) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_FOUR);


        // Check for Live Three ( _ P P P _ ) using cells 0-4 or 1-5
        // _PPP_ within L[0]..L[4] or L[1]..L[5]
        if (L[0] == 0 && L[1] == P && L[2] == P && L[3] == P && L[4] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_LIVE_THREE);
        if (L[1] == 0 && L[2] == P && L[3] == P && L[4] == P && L[5] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_LIVE_THREE);

        // Check for Broken Live Three _P_PP_ or _PP_P_
        if (L[0] == 0 && L[1] == P && L[2] == 0 && L[3] == P && L[4] == P && L[5] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_BROKEN_THREE_LIVE);
        if (L[0] == 0 && L[1] == P && L[2] == P && L[3] == 0 && L[4] == P && L[5] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_BROKEN_THREE_LIVE);


        // Check for Dead Three (O P P P _ or _ P P P O)
        // OPPP_ (L[0]..L[4])
        if (L[0] == O && L[1] == P && L[2] == P && L[3] == P && L[4] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_THREE);
        // _PPPO (L[0]..L[4])
        if (L[0] == 0 && L[1] == P && L[2] == P && L[3] == P && L[4] == O) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_THREE);
        // OPPP_ (L[1]..L[5])
        if (L[1] == O && L[2] == P && L[3] == P && L[4] == P && L[5] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_THREE);
        // _PPPO (L[1]..L[5])
        if (L[1] == 0 && L[2] == P && L[3] == P && L[4] == P && L[5] == O) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_THREE);
        // PPP O (L[0]..L[3] is PPP, L[4] is O)
        if (L[0] != O && L[1] == P && L[2] == P && L[3] == P && L[4] == O) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_THREE);
        // O PPP (L[1] is O, L[2]..L[4] is PPP)
        if (L[1] == O && L[2] == P && L[3] == P && L[4] == P && L[5] != O) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_THREE);

        // Check for Broken Dead Three (e.g. O P _ P P _ )
        if (L[0] == O && L[1] == P && L[2] == 0 && L[3] == P && L[4] == P && L[5] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_BROKEN_THREE_DEAD);
        if (L[0] == 0 && L[1] == P && L[2] == 0 && L[3] == P && L[4] == P && L[5] == O) currentMaxScore = Math.Max(currentMaxScore, SCORE_BROKEN_THREE_DEAD);
        if (L[0] == O && L[1] == P && L[2] == P && L[3] == 0 && L[4] == P && L[5] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_BROKEN_THREE_DEAD);
        if (L[0] == 0 && L[1] == P && L[2] == P && L[3] == 0 && L[4] == P && L[5] == O) currentMaxScore = Math.Max(currentMaxScore, SCORE_BROKEN_THREE_DEAD);


        // Check for Live Two ( _ P P _ )
        // _PP_ in L[0]..L[3]
        if (L[0] == 0 && L[1] == P && L[2] == P && L[3] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_LIVE_TWO);
        // _PP_ in L[1]..L[4]
        if (L[1] == 0 && L[2] == P && L[3] == P && L[4] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_LIVE_TWO);
        // _PP_ in L[2]..L[5]
        if (L[2] == 0 && L[3] == P && L[4] == P && L[5] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_LIVE_TWO);

        // Check for Dead Two (O P P _ or _ P P O)
        // OPP_ in L[0]..L[3]
        if (L[0] == O && L[1] == P && L[2] == P && L[3] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_TWO);
        // _PPO in L[0]..L[3]
        if (L[0] == 0 && L[1] == P && L[2] == P && L[3] == O) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_TWO);
        // OPP_ in L[1]..L[4]
        if (L[1] == O && L[2] == P && L[3] == P && L[4] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_TWO);
        // _PPO in L[1]..L[4]
        if (L[1] == 0 && L[2] == P && L[3] == P && L[4] == O) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_TWO);
        // OPP_ in L[2]..L[5]
        if (L[2] == O && L[3] == P && L[4] == P && L[5] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_TWO);
        // _PPO in L[2]..L[5]
        if (L[2] == 0 && L[3] == P && L[4] == P && L[5] == O) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_TWO);
        
        // PP O (L[0]..L[2])
        if (L[0] != O && L[1] == P && L[2] == P && L[3] == O) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_TWO);
        // O PP (L[1]..L[3])
        if (L[1] == O && L[2] == P && L[3] == P && L[4] != O) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_TWO);


        return currentMaxScore;
    }

    private (float score, (int r, int c) move) MinimaxAlphaBeta(
        int[,] board, 
        int depth, 
        float alpha, 
        float beta, 
        bool maximizingPlayer, 
        int aiPlayerIdForMinimaxPerspective,
        int opponentPlayerId)
    {
        float originalAlpha = alpha; // Needed for TT flag determination
        string boardHash = ComputeBoardHash(board); // Potentially slow, consider Zobrist later

        // --- Transposition Table Lookup ---
        if (transpositionTable.TryGetValue(boardHash, out TranspositionTableEntry ttEntry))
        {
            if (ttEntry.Depth >= depth) // Only use if stored depth is sufficient
            {
                if (ttEntry.Flag == TTFlag.EXACT)
                {
                    return (ttEntry.Score, ttEntry.BestMove); 
                }
                if (ttEntry.Flag == TTFlag.LOWER_BOUND)
                {
                    if (ttEntry.Score >= beta) return (ttEntry.Score, ttEntry.BestMove); // Causes beta cutoff
                    alpha = Math.Max(alpha, ttEntry.Score);
                }
                else if (ttEntry.Flag == TTFlag.UPPER_BOUND)
                {
                    if (ttEntry.Score <= alpha) return (ttEntry.Score, ttEntry.BestMove); // Causes alpha cutoff
                    beta = Math.Min(beta, ttEntry.Score);
                }
            }
        }
        // --- End of Transposition Table Lookup ---

        if (depth == 0) 
        {
            float evalScore = EvaluateBoardState(board, aiPlayerIdForMinimaxPerspective, opponentPlayerId);
            // No need to store leaf nodes in TT if we don't store best move, or if depth is always 0.
            // However, if quiescence search is added, storing them might be useful.
            return (evalScore, (-1,-1));
        }

        List<(int r, int c)> possibleMovesOriginal = GetEmptyCells(board);
        if (!possibleMovesOriginal.Any()) // No moves left (draw or board full)
        {
             return (EvaluateBoardState(board, aiPlayerIdForMinimaxPerspective, opponentPlayerId), (-1,-1));
        }

        // --- Move Ordering --- 
        List<((int r, int c) move, float score)> scoredMoves = new List<((int r, int c) move, float score)>();
        int currentPlayer = maximizingPlayer ? aiPlayerIdForMinimaxPerspective : opponentPlayerId;
        int nextPlayerOpponent = maximizingPlayer ? opponentPlayerId : aiPlayerIdForMinimaxPerspective;

        foreach (var move in possibleMovesOriginal)
        {
            float heuristicScore = 0;

            // Check for immediate win for the current player making 'move'
            int[,] boardAfterThisMove = (int[,])board.Clone();
            boardAfterThisMove[move.r, move.c] = currentPlayer;
            if (CheckWin(boardAfterThisMove, currentPlayer))
            {
                heuristicScore = maximizingPlayer ? float.PositiveInfinity : float.NegativeInfinity; 
            }
            else
            {
                // Evaluate the move using the board state *before* the move is made.
                // LightWeightEvaluateMove will internally simulate the move for attack/defense calcs.
                heuristicScore = LightWeightEvaluateMove(board, move, currentPlayer, nextPlayerOpponent);
            }
            scoredMoves.Add((move, heuristicScore));
        }

        if (maximizingPlayer) // AI's turn - sort descending by score (best for AI first)
        {
            scoredMoves.Sort((a, b) => b.score.CompareTo(a.score));
        }
        else // Opponent's turn - sort ascending by score (best for opponent, i.e., worst for AI, first)
        {
            scoredMoves.Sort((a, b) => a.score.CompareTo(b.score));
        }
        // --- End of Move Ordering ---

        (int r, int c) bestMoveThisLevel = (-1,-1);
        if (scoredMoves.Count > 0) bestMoveThisLevel = scoredMoves[0].move; 
        else if (possibleMovesOriginal.Count > 0) bestMoveThisLevel = possibleMovesOriginal[0];

        if (maximizingPlayer) 
        {
            float maxEval = float.NegativeInfinity;
            foreach (var scoredMoveItem in scoredMoves)
            {
                var move = scoredMoveItem.move;
                int[,] newBoard = (int[,])board.Clone();
                newBoard[move.r, move.c] = aiPlayerIdForMinimaxPerspective;
                (float eval, _) = MinimaxAlphaBeta(newBoard, depth - 1, alpha, beta, false, aiPlayerIdForMinimaxPerspective, opponentPlayerId);
                if (eval > maxEval)
                {
                    maxEval = eval;
                    bestMoveThisLevel = move;
                }
                alpha = Math.Max(alpha, eval);
                if (beta <= alpha) break; 
            }
            // --- Transposition Table Store ---
            TTFlag flagToStore;
            if (maxEval <= originalAlpha) flagToStore = TTFlag.UPPER_BOUND;
            else if (maxEval >= beta) flagToStore = TTFlag.LOWER_BOUND;
            else flagToStore = TTFlag.EXACT;
            transpositionTable[boardHash] = new TranspositionTableEntry { Score = maxEval, Depth = depth, Flag = flagToStore, BestMove = bestMoveThisLevel };
            // --- End of Transposition Table Store ---
            return (maxEval, bestMoveThisLevel);
        }
        else 
        {
            float minEval = float.PositiveInfinity;
            foreach (var scoredMoveItem in scoredMoves)
            {
                var move = scoredMoveItem.move;
                int[,] newBoard = (int[,])board.Clone();
                newBoard[move.r, move.c] = opponentPlayerId;
                (float eval, _) = MinimaxAlphaBeta(newBoard, depth - 1, alpha, beta, true, aiPlayerIdForMinimaxPerspective, opponentPlayerId);
                if (eval < minEval)
                {
                    minEval = eval;
                    bestMoveThisLevel = move;
                }
                beta = Math.Min(beta, eval);
                if (beta <= alpha) break;
            }
            // --- Transposition Table Store ---
            TTFlag flagToStore;
            if (minEval <= originalAlpha) flagToStore = TTFlag.UPPER_BOUND;
            else if (minEval >= beta) flagToStore = TTFlag.LOWER_BOUND;
            else flagToStore = TTFlag.EXACT;
            transpositionTable[boardHash] = new TranspositionTableEntry { Score = minEval, Depth = depth, Flag = flagToStore, BestMove = bestMoveThisLevel };
            // --- End of Transposition Table Store ---
            return (minEval, bestMoveThisLevel);
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

    private (int r, int c) GetBestMoveUsingMinimax(int[,] board, int aiPlayerId, int opponentPlayerId)
    {
        this.transpositionTable = new Dictionary<string, TranspositionTableEntry>(); // Initialize/clear TT for each new top-level call
        
        int emptyCellsCount = GetEmptyCells(board).Count;
        int baseDepth = 2; 
        int endgameDepth = 3; 
        int endgameThreshold = 60; // Consider it endgame when fewer than 60 cells are empty

        int currentSearchDepth = baseDepth;
        string phase = "Midgame";
        if (emptyCellsCount < endgameThreshold && emptyCellsCount > 0) // ensure not a full board with no moves
        {
            currentSearchDepth = endgameDepth;
            phase = "Endgame";
        }

        Console.WriteLine($"AI Service: Using Minimax for P{aiPlayerId}. Board hash: {ComputeBoardHash(board).Substring(0,6)}. Phase: {phase}. EmptyCells: {emptyCellsCount}. Depth: {currentSearchDepth}");
        
        (float score, (int r, int c) move) result = MinimaxAlphaBeta(board, currentSearchDepth, float.NegativeInfinity, float.PositiveInfinity, true, aiPlayerId, opponentPlayerId);
        
        if (result.move.r == -1 && emptyCellsCount > 0) { // Check emptyCellsCount > 0 to avoid issues on a completely full board
            Console.WriteLine($" Minimax returned no move for P{aiPlayerId} (depth {currentSearchDepth}), falling back to random available move.");
            var emptyCells = GetEmptyCells(board);
            return emptyCells[new Random().Next(emptyCells.Count)];
        }
        Console.WriteLine($" Minimax for P{aiPlayerId} suggests ({result.move.r},{result.move.c}) with score {result.score}");
        return result.move;
    }
    // --- End of Minimax ---

    private bool CheckWin(int[,] board, int player)
    {
        // Check rows for win
        for (int r = 0; r < BoardSize; r++)
        {
            for (int c = 0; c <= BoardSize - 5; c++)
            {
                if (board[r,c] == player && board[r,c+1] == player && board[r,c+2] == player && board[r,c+3] == player && board[r,c+4] == player)
                    return true;
            }
        }
        // Check columns for win
        for (int c = 0; c < BoardSize; c++)
        {
            for (int r = 0; r <= BoardSize - 5; r++)
            {
                if (board[r,c] == player && board[r+1,c] == player && board[r+2,c] == player && board[r+3,c] == player && board[r+4,c] == player)
                    return true;
            }
        }
        // Check main diagonal (top-left to bottom-right)
        for (int r = 0; r <= BoardSize - 5; r++)
        {
            for (int c = 0; c <= BoardSize - 5; c++)
            {
                if (board[r,c] == player && board[r+1,c+1] == player && board[r+2,c+2] == player && board[r+3,c+3] == player && board[r+4,c+4] == player)
                    return true;
            }
        }
        // Check anti-diagonal (top-right to bottom-left)
        for (int r = 0; r <= BoardSize - 5; r++)
        {
            for (int c = 4; c < BoardSize; c++)
            {
                if (board[r,c] == player && board[r+1,c-1] == player && board[r+2,c-2] == player && board[r+3,c-3] == player && board[r+4,c-4] == player)
                    return true;
            }
        }
        return false;
    }

    // Helper to get cell value safely, returns -1 if out of bounds
    private int GetCell(int[,] board, int r, int c)
    {
        if (r >= 0 && r < BoardSize && c >= 0 && c < BoardSize) return board[r, c];
        return -1; // Sentinel for "off-board"
    }

    // Evaluates a move lightly by checking only lines passing through the move
    // board: current board state BEFORE the consideredMove is made
    // consideredMove: The move (r,c) we are evaluating the heuristic for
    // player: The player who would make the consideredMove
    // opponent: The opponent of 'player'
    private float LightWeightEvaluateMove(int[,] board, (int r, int c) consideredMove, int player, int opponent)
    {
        float finalHeuristicScore = 0;
        int pr = consideredMove.r;
        int pc = consideredMove.c;

        // Simulate placing player's piece to calculate attack score
        int[,] boardAfterPlayerMoves = (int[,])board.Clone();
        if (boardAfterPlayerMoves[pr, pc] == 0) // Should always be true as it's from GetEmptyCells
        {
            boardAfterPlayerMoves[pr, pc] = player;
        }
        else return float.MinValue; // Should not happen, move is on an occupied cell

        // Simulate placing opponent's piece (if player didn't) to calculate defensive value
        int[,] boardIfOpponentMoved = (int[,])board.Clone();
        if (boardIfOpponentMoved[pr,pc] == 0)
        {
            boardIfOpponentMoved[pr, pc] = opponent;
        }
        // If player has already moved there, this path is not for opponent potential

        float maxAttackScoreForThisMove = 0;
        float maxThreatFromOpponentIfTheyTookCell = 0;

        int[][] directions = new int[][] { new int[] {0,1}, new int[] {1,0}, new int[] {1,1}, new int[] {1,-1} };
        int[] line = new int[6];

        foreach (var dir in directions)
        {
            // Evaluate all 6 possible alignments of a 6-cell window that includes the consideredMove
            for (int i = 0; i < 6; i++) // i is the index of consideredMove within the 6-cell window
            {
                // --- Calculate Attack Score (benefit for 'player') ---
                for (int k = 0; k < 6; k++)
                {
                    line[k] = GetCell(boardAfterPlayerMoves, pr + (k - i) * dir[0], pc + (k - i) * dir[1]);
                }
                // Ensure the move itself is part of the line and is the player making the move
                if (line[i] == player)
                {
                    maxAttackScoreForThisMove = Math.Max(maxAttackScoreForThisMove, ScoreSingle6CellLine(line, player, opponent));
                }

                // --- Calculate Defense Value (threat from 'opponent' if they took this cell) ---
                for (int k = 0; k < 6; k++)
                {
                    line[k] = GetCell(boardIfOpponentMoved, pr + (k - i) * dir[0], pc + (k - i) * dir[1]);
                }
                // Ensure the move itself is part of the line and is the opponent (hypothetically)
                if (line[i] == opponent) 
                {
                    maxThreatFromOpponentIfTheyTookCell = Math.Max(maxThreatFromOpponentIfTheyTookCell, ScoreSingle6CellLine(line, opponent, player));
                }
            }
        }
        
        finalHeuristicScore = maxAttackScoreForThisMove + maxThreatFromOpponentIfTheyTookCell * 1.1f; // Slightly prioritize blocking a strong opponent move

        return finalHeuristicScore;
    }
} 