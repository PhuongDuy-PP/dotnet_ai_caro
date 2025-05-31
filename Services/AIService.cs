using CaroAIServer.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CaroAIServer.Services;

// Structure to hold a move and its score
public struct MoveScoreInfo
{
    public int Row { get; set; }
    public int Column { get; set; }
    public float Score { get; set; }
}

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
    private const float SCORE_DEAD_FOUR = 40000f;     // OXXXX_ or _XXXXO (Reduced from 100,000f)
    private const float SCORE_LIVE_THREE = 60000f;     // _XXX_ (Increased from 50,000f)
    private const float SCORE_DEAD_THREE = 5000f;      // OXXX_ or _XXXO
    private const float SCORE_LIVE_TWO = 200f;       // _XX_
    private const float SCORE_DEAD_TWO = 20f;        // OXX_ or _XXO
    private const float SCORE_BROKEN_THREE_LIVE = 45000f; // _X_XX_ or _XX_X_ (Increased from 20,000f)
    private const float SCORE_BROKEN_THREE_DEAD = 2000f;  // O_X_XX_ or _XX_X_O etc.
    private const float SCORE_MULTI_LIVE_TWO_BONUS = 15000f; // Bonus for creating two or more Live Twos with one move

    public AIService(ApplicationDbContext context, GameService gameService)
    {
        _context = context;
        _gameService = gameService;
        transpositionTable = new Dictionary<string, TranspositionTableEntry>(); // Initialize transpositionTable
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

    public (int[,] canonicalBoard, TransformationInfo_AI transformationToCanonical) GetCanonicalBoardAndTransformation(int[,] originalBoard)
    {
        string? smallestHash = null; // Allow null
        int[,]? canonicalBoard = null; // Allow null
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
                if (canonicalBoard == null || (smallestHash != null && String.Compare(currentHash, smallestHash, StringComparison.Ordinal) < 0)) // Add null check for smallestHash
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
        // Ensure canonicalBoard is not null before returning, or handle the case where it might be.
        // For now, assuming it will be assigned. If not, this needs a proper fix.
        if (canonicalBoard == null)
        {
            // This should ideally not happen if originalBoard is valid.
            // Consider throwing an exception or returning a default/error state.
            // For now, returning original board and default transform if canonical isn't found (should be reviewed)
            return (originalBoard, new TransformationInfo_AI());
        }
        return (canonicalBoard, appliedTransformation);
    }
    
    public (int r, int c) InverseTransformMove((int r, int c) moveOnCanonical, TransformationInfo_AI transformAppliedToOriginal)
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

    // NEW FUNCTION
    public (int r, int c) TransformMoveToCanonical(int originalR, int originalC, TransformationInfo_AI transformToGetCanonical)
    {
        int r = originalR;
        int c = originalC;

        if (transformToGetCanonical.IsFlipped)
        {
            c = BoardSize - 1 - c;
        }

        for (int i = 0; i < transformToGetCanonical.Rotations; i++)
        {
            int tempR = r;
            r = c;
            c = BoardSize - 1 - tempR; 
        }
        return (r, c);
    }
    // --- End of Transformation Utilities ---


    public (int row, int col) GetBestMove(int[,] currentBoard_Original, int aiPlayer, int opponentPlayerId)
    {
        (int[,] canonicalBoard, TransformationInfo_AI transformToGetCanonical) = GetCanonicalBoardAndTransformation(currentBoard_Original);
        string canonicalBoardHash = ComputeBoardHash(canonicalBoard);
        string originalBoardHash_debug = ComputeBoardHash(currentBoard_Original).Substring(0,8);

        Console.WriteLine($"AI Service: GetBestMove for P{aiPlayer}. OrigHash: {originalBoardHash_debug}, CanonHash: {canonicalBoardHash.Substring(0,8)}, Transform: {transformToGetCanonical.Type}");

        var openingPosition = _context.OpeningPositions
                                            .Include(op => op.MoveRecommendations)
                                            .FirstOrDefault(op => op.PositionHash == canonicalBoardHash && op.PlayerToMove == aiPlayer);

        if (openingPosition != null && openingPosition.MoveRecommendations.Any())
        {
            var bestRecommendation_Canonical = openingPosition.MoveRecommendations.OrderByDescending(r => r.MoveScore).First();
            (int moveR_Canonical, int moveC_Canonical) = (bestRecommendation_Canonical.MoveRow, bestRecommendation_Canonical.MoveCol);
            (int finalMoveR, int finalMoveC) = InverseTransformMove((moveR_Canonical, moveC_Canonical), transformToGetCanonical);
            
            Console.WriteLine($"AI Service: P{aiPlayer} found BOOK move (CanonHash: {canonicalBoardHash.Substring(0,8)}). Canonical: ({moveR_Canonical},{moveC_Canonical}) -> Original: ({finalMoveR},{finalMoveC})");
            
            if (finalMoveR >= 0 && finalMoveR < BoardSize && finalMoveC >= 0 && finalMoveC < BoardSize && currentBoard_Original[finalMoveR, finalMoveC] == 0)
            {
                return (finalMoveR, finalMoveC);
            }
            else
            {
                 Console.WriteLine($"AI Service WARNING: P{aiPlayer} - Inverse transformed book move ({finalMoveR},{finalMoveC}) is invalid on original board. OrigHash: {originalBoardHash_debug}. Falling back.");
            }
        }
        Console.WriteLine($"AI Service: P{aiPlayer} - NO book move for CanonHash: {canonicalBoardHash.Substring(0,8)}. OrigHash: {originalBoardHash_debug}. Proceeding...");

        int aiMovesCount = 0;
        for(int r=0; r<BoardSize; r++) for(int c=0; c<BoardSize; c++) if(currentBoard_Original[r,c] == aiPlayer) aiMovesCount++;
        
        int totalBoardCells = BoardSize * BoardSize;
        int emptyCellsOnBoard = GetEmptyCells(currentBoard_Original).Count;
        int movesMadeOnBoard = totalBoardCells - emptyCellsOnBoard;

        if (aiMovesCount == 0) // THIS IS AI's VERY FIRST MOVE IN THE GAME
        {
            Console.WriteLine($"AI Service: P{aiPlayer} - This is AI's FIRST move (aiMovesCount: {aiMovesCount}). Applying special first-move random logic.");
            (int r_rand, int c_rand) = (-1, -1);

            if (movesMadeOnBoard == 0) // AI is P1, board is empty
            {
                Console.WriteLine($"AI Service: P{aiPlayer} (P1) on empty board. Choosing random near center (radius 1 for 3x3).");
                (r_rand, c_rand) = GetNearCenterRandomEmptyCell(currentBoard_Original, 1); // Radius 1 for 3x3 area around center
            }
            else // AI is P2, P1 has made one move (movesMadeOnBoard should be 1)
            {
                Console.WriteLine($"AI Service: P{aiPlayer} (P2) responding to P1's first move. P2 will play random ADJACENT to P1.");
                List<(int r, int c)> opponentActualMoves = FindOpponentMoves(currentBoard_Original, opponentPlayerId);
                if (opponentActualMoves.Any())
                {
                    (int r_opp, int c_opp) = opponentActualMoves[0]; // P1 made only one move
                    Console.WriteLine($"AI Service: P{aiPlayer} (P2) - P1 ({opponentPlayerId}) played at ({r_opp},{c_opp}). P2 plays random adjacent (radius 1).");
                    (r_rand, c_rand) = GetNearbyRandomEmptyCell(currentBoard_Original, (r_opp, c_opp), 1); // Radius 1 ensures adjacent cells
                }
                else
                {
                     Console.WriteLine($"AI Service WARNING: P{aiPlayer} (P2) - Opponent P{opponentPlayerId} has no moves documented on board for P2's first turn! Fallback to random near center (radius 1).");
                     (r_rand, c_rand) = GetNearCenterRandomEmptyCell(currentBoard_Original, 1);
                }
            }

            if (r_rand != -1 && c_rand != -1 && currentBoard_Original[r_rand, c_rand] == 0)
            {
                Console.WriteLine($"AI Service: P{aiPlayer} - AI's first move (random logic) selected: ({r_rand},{c_rand})");
                return (r_rand, c_rand);
            }
            else
            {
                Console.WriteLine($"AI Service: P{aiPlayer} - AI's first move random logic FAILED to find a valid cell. OrigHash: {originalBoardHash_debug}. Falling back to Minimax.");
            }
        }
        else
        {
            Console.WriteLine($"AI Service: P{aiPlayer} - Not AI's first move (aiMovesCount: {aiMovesCount}) and no book entry. OrigHash: {originalBoardHash_debug}. Proceeding to Minimax.");
        }

        Console.WriteLine($"AI Service: P{aiPlayer} - Using Minimax. OrigHash: {originalBoardHash_debug}");
        var topMoves = GetTopMoveRecommendations(currentBoard_Original, aiPlayer, opponentPlayerId, 1);
        if (topMoves.Any())
        {
            Console.WriteLine($"AI Service: P{aiPlayer} - Minimax recommended: ({topMoves[0].Row},{topMoves[0].Column}) for OrigHash: {originalBoardHash_debug}");
            return (topMoves[0].Row, topMoves[0].Column);
        }
        
        Console.WriteLine($"AI Service ERROR: P{aiPlayer} - Minimax found NO MOVES. OrigHash: {originalBoardHash_debug}. Fallback to random empty cell.");
        var lastResortEmptyCells = GetEmptyCells(currentBoard_Original);
        if(lastResortEmptyCells.Any()) return GetRandomCellFromList(lastResortEmptyCells);
        
        Console.WriteLine($"AI Service CRITICAL ERROR: P{aiPlayer} - NO MOVES POSSIBLE AT ALL on board. OrigHash: {originalBoardHash_debug}.");
        return (-1, -1); // Absolute fallback
    }

    // ComputeBoardHash (already exists and is used by GetCanonicalBoardAndTransformation)
    public string ComputeBoardHash(int[,] board)
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

    // Helper to convert 2D array to List<List<int>> for serialization
    public List<List<int>> ConvertBoardToListOfLists(int[,] board)
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
    public float EvaluateBoardState(int[,] board, int playerForPerspective, int opponentPlayer)
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
                         float currentLineScore = ScoreSingle6CellLine(line, player, opponent);
                         if (currentLineScore == SCORE_FIVE_IN_ROW) 
                         {
                            bool isActuallyWinningFive = false;
                            // 'r', 'c' are the board coordinates for line[0]
                            // 'dir' is {dr, dc} used to populate the line

                            // Check for five starting at line[0]
                            if (line[0] == player && line[1] == player && line[2] == player && line[3] == player && line[4] == player) 
                            {
                                if (IsWinningPattern(board, player, opponent, r, c, dir[0], dir[1])) 
                                {
                                    isActuallyWinningFive = true;
                                }
                            }

                            // Check for five starting at line[1] (if not already confirmed as win)
                            if (!isActuallyWinningFive && 
                                line[1] == player && line[2] == player && line[3] == player && line[4] == player && line[5] == player) 
                            {
                                int r_five_start_on_board = r + dir[0];
                                int c_five_start_on_board = c + dir[1];
                                if (IsWinningPattern(board, player, opponent, r_five_start_on_board, c_five_start_on_board, dir[0], dir[1])) 
                                {
                                    isActuallyWinningFive = true;
                                }
                            }

                            if (!isActuallyWinningFive) 
                            {
                                currentLineScore = 0.0f; // Penalize non-winning five-in-a-rows
                            }
                        }
                        totalScore += currentLineScore;
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
        
        // Check for Jumping Live Four _PP0PP_ (e.g., O O _ O O with open ends)
        // This pattern is P P empty P P, with open spaces on both ends.
        // Represented in a 6-cell window L:
        // Case 1: L = [0, P, P, 0, P, P]  (corresponds to "empty P P empty P P")
        if (L[0] == 0 && L[1] == P && L[2] == P && L[3] == 0 && L[4] == P && L[5] == P) return SCORE_LIVE_FOUR;
        // Case 2: L = [P, P, 0, P, P, 0]  (corresponds to "P P empty P P empty")
        if (L[0] == P && L[1] == P && L[2] == 0 && L[3] == P && L[4] == P && L[5] == 0) return SCORE_LIVE_FOUR;

        float currentMaxScore = 0;

        // Check for Dead Four (O P P P P _  or _ P P P P O)
        if (L[0] == O && L[1] == P && L[2] == P && L[3] == P && L[4] == P && L[5] == 0) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_FOUR);
        if (L[0] == 0 && L[1] == P && L[2] == P && L[3] == P && L[4] == P && L[5] == O) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_FOUR);
        // Check for P P P P O (if not a five already, L[0] must be P or empty or off-board)
        if (L[0] != O && L[1] == P && L[2] == P && L[3] == P && L[4] == P && L[5] == O && !(L[0]==P && L[1]==P && L[2]==P && L[3]==P && L[4]==P) ) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_FOUR);
        // Check for O P P P P P (if not a five already, L[5] must be P or empty or off-board)
        if (L[0] == O && L[1] == P && L[2] == P && L[3] == P && L[4] == P && L[5] != O && !(L[1]==P && L[2]==P && L[3]==P && L[4]==P && L[5]==P) ) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_FOUR);

        // Check for Jumping Dead Four (e.g., O PP_PP _ or _ PP_PP O)
        // Pattern O P P _ P P (trong cửa sổ 6 ô L = [O, P, P, 0, P, P])
        // Đảm bảo rằng nó không phải là một phần của Jumping Live Four đã được return ở trên
        if (L[0] == O && L[1] == P && L[2] == P && L[3] == 0 && L[4] == P && L[5] == P) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_FOUR);
        // Pattern P P _ P P O (trong cửa sổ 6 ô L = [P, P, 0, P, P, O])
        // Đảm bảo rằng nó không phải là một phần của Jumping Live Four đã được return ở trên
        if (L[0] == P && L[1] == P && L[2] == 0 && L[3] == P && L[4] == P && L[5] == O) currentMaxScore = Math.Max(currentMaxScore, SCORE_DEAD_FOUR);


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
        if (transpositionTable.TryGetValue(boardHash, out TranspositionTableEntry? entry) && entry != null && entry.Depth >= depth) // Add null check for entry
        {
            if (entry.Flag == TTFlag.EXACT)
            {
                return (entry.Score, entry.BestMove); 
            }
            if (entry.Flag == TTFlag.LOWER_BOUND)
            {
                if (entry.Score >= beta) return (entry.Score, entry.BestMove); // Causes beta cutoff
                alpha = Math.Max(alpha, entry.Score);
            }
            else if (entry.Flag == TTFlag.UPPER_BOUND)
            {
                if (entry.Score <= alpha) return (entry.Score, entry.BestMove); // Causes alpha cutoff
                beta = Math.Min(beta, entry.Score);
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
            if (CheckWin(boardAfterThisMove, currentPlayer, nextPlayerOpponent))
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

    // Make this public to be accessible from GetBestMove more easily for counting moves
    public List<(int r, int c)> GetEmptyCells(int[,] board)
    {
        var emptyCells = new List<(int r, int c)>();
        for(int r=0; r<BoardSize; r++)
            for(int c=0; c<BoardSize; c++)
                if(board[r,c] == 0) emptyCells.Add((r,c));
        return emptyCells;
    }

    // Helper to get a list of opponent's moves
    private List<(int r, int c)> FindOpponentMoves(int[,] board, int opponentPlayerId)
    {
        var opponentMoves = new List<(int r, int c)>();
        for (int r = 0; r < BoardSize; r++)
        {
            for (int c = 0; c < BoardSize; c++)
            {
                if (board[r, c] == opponentPlayerId)
                {
                    opponentMoves.Add((r, c));
                }
            }
        }
        return opponentMoves;
    }

    // Helper to get a random cell from a list of cells
    private (int r, int c) GetRandomCellFromList(List<(int r, int c)> cells)
    {
        if (cells == null || !cells.Any()) return (-1, -1);
        Random rand = new Random();
        return cells[rand.Next(cells.Count)];
    }

    // Helper to find a random empty cell near a reference point (e.g., opponent's move)
    private (int r, int c) GetNearbyRandomEmptyCell(int[,] board, (int r, int c) referenceMove, int radius = 1)
    {
        if (referenceMove.r == -1) return (-1, -1); // No valid reference move
        var nearbyEmptyCells = new List<(int r, int c)>();
        for (int dr = -radius; dr <= radius; dr++)
        {
            for (int dc = -radius; dc <= radius; dc++)
            {
                if (dr == 0 && dc == 0) continue; // Skip the reference cell itself

                int nr = referenceMove.r + dr;
                int nc = referenceMove.c + dc;

                if (nr >= 0 && nr < BoardSize && nc >= 0 && nc < BoardSize && board[nr, nc] == 0)
                {
                    nearbyEmptyCells.Add((nr, nc));
                }
            }
        }
        return GetRandomCellFromList(nearbyEmptyCells);
    }

    // Helper to find a random empty cell near the center of the board
    private (int r, int c) GetNearCenterRandomEmptyCell(int[,] board, int centerRadius = 2) // e.g., 5x5 around center for radius 2
    {
        var centerEmptyCells = new List<(int r, int c)>();
        int mid = BoardSize / 2;
        for (int r = mid - centerRadius; r <= mid + centerRadius; r++)
        {
            for (int c = mid - centerRadius; c <= mid + centerRadius; c++)
            {
                if (r >= 0 && r < BoardSize && c >= 0 && c < BoardSize && board[r, c] == 0)
                {
                    centerEmptyCells.Add((r, c));
                }
            }
        }
        return GetRandomCellFromList(centerEmptyCells);
    }

    private (int r, int c) GetBestMoveUsingMinimax(int[,] board, int aiPlayerId, int opponentPlayerId)
    {
        var topMoves = GetTopMoveRecommendations(board, aiPlayerId, opponentPlayerId, 1);
        if (topMoves.Any())
        {
            Console.WriteLine($" GetBestMoveUsingMinimax (via TopRecommendations) for P{aiPlayerId} suggests ({topMoves[0].Row},{topMoves[0].Column}) with score {topMoves[0].Score}");
            return (topMoves[0].Row, topMoves[0].Column);
        }

        Console.WriteLine($" GetBestMoveUsingMinimax (via TopRecommendations) found no moves for P{aiPlayerId}, falling back to random.");
        var emptyCells = GetEmptyCells(board);
        if (emptyCells.Any())
            return emptyCells[new Random().Next(emptyCells.Count)];
        return (-1,-1);
    }
    // --- End of Minimax ---

    private bool IsWinningPattern(int[,] board, int player, int opponent, int r_start, int c_start, int dr, int dc)
    {
        // 1. Check for 5 consecutive player pieces
        for (int i = 0; i < 5; i++)
        {
            if (GetCell(board, r_start + i * dr, c_start + i * dc) != player)
                return false; // Not 5 in a row
        }

        // If we reach here, we found 5 consecutive player pieces.
        Console.WriteLine($"DEBUG IsWinningPattern: Potential 5-in-a-row for P{player} starting at ({r_start},{c_start}) with direction ({dr},{dc}). Opponent ID is P{opponent}.");

        int r_before = r_start - dr;
        int c_before = c_start - dc;
        int val_before = GetCell(board, r_before, c_before);
        Console.WriteLine($"  Cell before: ({r_before},{c_before}), Value: {val_before}");

        int r_after = r_start + 5 * dr;
        int c_after = c_start + 5 * dc;
        int val_after = GetCell(board, r_after, c_after);
        Console.WriteLine($"  Cell after: ({r_after},{c_after}), Value: {val_after}");

        // 2. Check for overline (6th piece is also player)
        if (val_before == player || val_after == player)
        {
            Console.WriteLine("  DEBUG IsWinningPattern: Overline detected. Not a win.");
            return false; // Overline of 6 or more
        }

        // 3. Check for doubly blocked five (O P P P P P O)
        if (val_before == opponent && val_after == opponent)
        {
            Console.WriteLine("  DEBUG IsWinningPattern: Doubly blocked by opponent. Not a win.");
            return false; // Doubly blocked by opponent
        }
        
        Console.WriteLine("  DEBUG IsWinningPattern: Valid win condition met.");
        return true; // It's a valid winning five
    }

    public bool CheckWin(int[,] board, int player, int opponentPlayerId)
    {
        // Check rows for win
        for (int r = 0; r < BoardSize; r++)
        {
            for (int c = 0; c <= BoardSize - 5; c++) // c can go up to BoardSize - 5 to start a line of 5
            {
                if (IsWinningPattern(board, player, opponentPlayerId, r, c, 0, 1)) return true;
            }
        }
        // Check columns for win
        for (int c = 0; c < BoardSize; c++)
        {
            for (int r = 0; r <= BoardSize - 5; r++) // r can go up to BoardSize - 5
            {
                if (IsWinningPattern(board, player, opponentPlayerId, r, c, 1, 0)) return true;
            }
        }
        // Check main diagonal (top-left to bottom-right)
        for (int r = 0; r <= BoardSize - 5; r++)
        {
            for (int c = 0; c <= BoardSize - 5; c++)
            {
                if (IsWinningPattern(board, player, opponentPlayerId, r, c, 1, 1)) return true;
            }
        }
        // Check anti-diagonal (top-right to bottom-left)
        for (int r = 0; r <= BoardSize - 5; r++)
        {
            // c_start for anti-diagonal must be at least 4 (0-indexed) to allow line of 5 to c_start - 4
            // So, c can go from 4 up to BoardSize - 1
            for (int c = 4; c < BoardSize; c++) 
            {
                if (IsWinningPattern(board, player, opponentPlayerId, r, c, 1, -1)) return true;
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

        if (board[pr, pc] != 0)
            return float.MinValue; // Should not happen, move is on an occupied cell

        // Simulate placing player's piece to calculate attack score
        int[,] boardAfterPlayerMoves = (int[,])board.Clone();
        boardAfterPlayerMoves[pr, pc] = player;

        // Simulate placing opponent's piece (if player didn't) to calculate defensive value
        int[,] boardIfOpponentMoved = (int[,])board.Clone();
        boardIfOpponentMoved[pr, pc] = opponent;

        float attackScoreForThisMove = 0;
        float defenseScoreForThisMove = 0; // Score for blocking opponent's potential threats at this cell

        List<float> playerPatternScores = new List<float>();
        List<float> opponentPatternScoresIfTheyTookCell = new List<float>();

        int[][] directions = new int[][] { new int[] {0,1}, new int[] {1,0}, new int[] {1,1}, new int[] {1,-1} };
        int[] line = new int[6];

        foreach (var dir in directions)
        {
            float bestPlayerPatternScoreInDir = 0;
            float bestOpponentPatternScoreInDir = 0;

            // Evaluate all 6 possible alignments of a 6-cell window that includes the consideredMove
            for (int i = 0; i < 6; i++) // i is the index of consideredMove within the 6-cell window
            {
                // --- Calculate Attack Score (benefit for 'player') ---
                for (int k = 0; k < 6; k++)
                {
                    line[k] = GetCell(boardAfterPlayerMoves, pr + (k - i) * dir[0], pc + (k - i) * dir[1]);
                }
                if (line[i] == player) // Ensure the move itself is part of the line and is the player making the move
                {
                    float currentPatternScore = ScoreSingle6CellLine(line, player, opponent);
                    if (currentPatternScore == SCORE_FIVE_IN_ROW)
                    {
                        // A five-in-a-row pattern was detected. Verify if it's a true winning move.
                        bool isActuallyWinningFive = false;
                        // Check for five starting at L[0] (0-indexed in the 6-cell line)
                        if (line[0]==player && line[1]==player && line[2]==player && line[3]==player && line[4]==player) {
                            int r5s = pr + (0-i)*dir[0]; // Start row of the 5-in-a-row
                            int c5s = pc + (0-i)*dir[1]; // Start col of the 5-in-a-row
                            if (IsWinningPattern(boardAfterPlayerMoves, player, opponent, r5s, c5s, dir[0], dir[1])) {
                                isActuallyWinningFive = true;
                            }
                        }
                        // Check for five starting at L[1] (if not already confirmed as win)
                        if (!isActuallyWinningFive && 
                            line[1]==player && line[2]==player && line[3]==player && line[4]==player && line[5]==player) {
                            int r5s = pr + (1-i)*dir[0]; // Start row of the 5-in-a-row
                            int c5s = pc + (1-i)*dir[1]; // Start col of the 5-in-a-row
                            if (IsWinningPattern(boardAfterPlayerMoves, player, opponent, r5s, c5s, dir[0], dir[1])) {
                                isActuallyWinningFive = true;
                            }
                        }

                        if (!isActuallyWinningFive) {
                            currentPatternScore = 0.0f; // Penalize non-winning five-in-a-rows
                        }
                    }
                    bestPlayerPatternScoreInDir = Math.Max(bestPlayerPatternScoreInDir, currentPatternScore);
                }

                // --- Calculate Defense Value (threat from 'opponent' if they took this cell) ---
                for (int k = 0; k < 6; k++)
                {
                    line[k] = GetCell(boardIfOpponentMoved, pr + (k - i) * dir[0], pc + (k - i) * dir[1]);
                }
                if (line[i] == opponent) // Ensure the move itself is part of the line and is the opponent (hypothetically)
                {
                    float opponentPatternScore = ScoreSingle6CellLine(line, opponent, player);
                    if (opponentPatternScore == SCORE_FIVE_IN_ROW)
                    {
                        bool isActuallyWinningFiveForOpponent = false;
                        // Check for five starting at L[0] (0-indexed in the 6-cell line)
                        // The coordinates for IsWinningPattern need to be the start of the 5-in-a-row sequence on the board.
                        if (line[0]==opponent && line[1]==opponent && line[2]==opponent && line[3]==opponent && line[4]==opponent) {
                            int r5s_opponent = pr + (0-i)*dir[0]; // Start row of the 5-in-a-row
                            int c5s_opponent = pc + (0-i)*dir[1]; // Start col of the 5-in-a-row
                            // boardIfOpponentMoved is the board where opponent has made the move at (pr, pc)
                            // 'player' (the second argument to IsWinningPattern) is the one whose win is being checked (i.e., opponent)
                            // 'opponent' (the third argument to IsWinningPattern) is the other player (i.e., 'player', which is AI)
                            if (IsWinningPattern(boardIfOpponentMoved, opponent, player, r5s_opponent, c5s_opponent, dir[0], dir[1])) {
                                isActuallyWinningFiveForOpponent = true;
                            }
                        }
                        // Check for five starting at L[1] (if not already confirmed as win)
                        if (!isActuallyWinningFiveForOpponent &&
                            line[1]==opponent && line[2]==opponent && line[3]==opponent && line[4]==opponent && line[5]==opponent) {
                            int r5s_opponent = pr + (1-i)*dir[0]; // Start row of the 5-in-a-row
                            int c5s_opponent = pc + (1-i)*dir[1]; // Start col of the 5-in-a-row
                            if (IsWinningPattern(boardIfOpponentMoved, opponent, player, r5s_opponent, c5s_opponent, dir[0], dir[1])) {
                                isActuallyWinningFiveForOpponent = true;
                            }
                        }

                        if (!isActuallyWinningFiveForOpponent) {
                            opponentPatternScore = 0.0f; // Penalize non-winning five-in-a-rows for opponent
                        }
                    }
                    bestOpponentPatternScoreInDir = Math.Max(bestOpponentPatternScoreInDir, opponentPatternScore);
                }
            }
            if (bestPlayerPatternScoreInDir > 0) playerPatternScores.Add(bestPlayerPatternScoreInDir);
            if (bestOpponentPatternScoreInDir > 0) opponentPatternScoresIfTheyTookCell.Add(bestOpponentPatternScoreInDir);
        }

        // Combine scores for player's patterns (attack)
        playerPatternScores.Sort((a, b) => b.CompareTo(a)); // Sort descending
        if (playerPatternScores.Count > 0) attackScoreForThisMove += playerPatternScores[0];
        if (playerPatternScores.Count > 1) attackScoreForThisMove += playerPatternScores[1] * 0.9f; // 3-3 or 4-3 bonus (e.g., second best pattern contributes 90%)
        if (playerPatternScores.Count > 2) attackScoreForThisMove += playerPatternScores[2] * 0.5f; // e.g., third best pattern contributes 50%

        // Combine scores for opponent's patterns if they took the cell (defense)
        opponentPatternScoresIfTheyTookCell.Sort((a, b) => b.CompareTo(a));
        if (opponentPatternScoresIfTheyTookCell.Count > 0) defenseScoreForThisMove += opponentPatternScoresIfTheyTookCell[0];
        if (opponentPatternScoresIfTheyTookCell.Count > 1) defenseScoreForThisMove += opponentPatternScoresIfTheyTookCell[1] * 0.8f; // Bonus for blocking multiple opponent threats
        
        // If player makes a five, it's an immediate win, highest priority
        if (attackScoreForThisMove >= SCORE_FIVE_IN_ROW) return SCORE_FIVE_IN_ROW * 10; // Very high value for immediate win

        // If opponent would make a five if they took this cell, blocking it is crucial
        if (defenseScoreForThisMove >= SCORE_FIVE_IN_ROW) return SCORE_FIVE_IN_ROW * 5; // High value for blocking opponent win

        // If player makes a live four, very strong move
        if (playerPatternScores.Any(s => s == SCORE_LIVE_FOUR)) 
            attackScoreForThisMove *= 1.5f; // Boost live fours significantly
        
        // If opponent would make a live four, blocking it is very important
        if (opponentPatternScoresIfTheyTookCell.Any(s => s == SCORE_LIVE_FOUR))
            defenseScoreForThisMove *= 1.6f; // Boost blocking opponent's live four


        // Favor creating Live Threes, especially multiple
        // int liveThreesCount = playerPatternScores.Count(s => s == SCORE_LIVE_THREE || s == SCORE_BROKEN_THREE_LIVE);
        // if (liveThreesCount >= 2) // Forms a 3-3 type threat
        // {
        //     attackScoreForThisMove += SCORE_LIVE_THREE * 1.5f; // Significant bonus for 3-3
        // }
        // else if (liveThreesCount == 1)
        // {
        //     attackScoreForThisMove += SCORE_LIVE_THREE * 0.5f; // Bonus for a single live three
        // }

        // Prioritize blocking opponent's Live Threes
        int opponentLiveThreesCount = opponentPatternScoresIfTheyTookCell.Count(s => s == SCORE_LIVE_THREE || s == SCORE_BROKEN_THREE_LIVE);
        if (opponentLiveThreesCount >= 2)
        {
            defenseScoreForThisMove += SCORE_LIVE_THREE * 1.4f; // Strong bonus for blocking opponent's 3-3
        }
        else if (opponentLiveThreesCount == 1)
        {
            defenseScoreForThisMove += SCORE_LIVE_THREE * 2.5f; // Temporarily increased from 1.5f to 2.5f for testing
        }

        finalHeuristicScore = attackScoreForThisMove + defenseScoreForThisMove * 1.1f; // Slightly prioritize blocking a strong opponent move

        // Add a small bonus for moves near the center in early game, if no other strong patterns are formed
        // This check should ideally be based on the number of moves made, not just scores.
        // For simplicity here, if scores are low, give a slight preference to center.
        if (attackScoreForThisMove < SCORE_LIVE_TWO && defenseScoreForThisMove < SCORE_LIVE_TWO)
        {
            int mid = BoardSize / 2;
            int distFromCenter = Math.Max(Math.Abs(pr - mid), Math.Abs(pc - mid));
            finalHeuristicScore += (BoardSize / 2 - distFromCenter) * 0.1f; // Small bonus, higher closer to center
        }

        // --- Explicit bonus for creating multiple Live Twos ---
        int liveTwosCountForBonus = playerPatternScores.Count(s => s == SCORE_LIVE_TWO);
        if (liveTwosCountForBonus >= 2)
        {
            finalHeuristicScore += SCORE_MULTI_LIVE_TWO_BONUS;
        }
        // --- End of explicit bonus for multiple Live Twos ---

        // --- Explicit bonus for 4-3 synergy ---
        bool createsStrongLiveFourAttack = playerPatternScores.Any(s => s == SCORE_LIVE_FOUR);
        bool createsAnyLiveThreeAttack = playerPatternScores.Any(s => s == SCORE_LIVE_THREE || s == SCORE_BROKEN_THREE_LIVE);

        if (createsStrongLiveFourAttack && createsAnyLiveThreeAttack)
        {
            // This move creates a 4-3. Give it a substantial extra bonus to ensure priority.
            finalHeuristicScore += 500000f; 
        }
        // --- End of explicit bonus for 4-3 synergy ---

        // --- Explicit bonus for 3-3 synergy ---
        int liveThreesCountForBonus = playerPatternScores.Count(s => s == SCORE_LIVE_THREE || s == SCORE_BROKEN_THREE_LIVE);
        if (liveThreesCountForBonus >= 2) 
        {
            // This move creates a 3-3. Give it a substantial extra bonus.
            finalHeuristicScore += 350000f; 
        }
        // --- End of explicit bonus for 3-3 synergy ---

        return finalHeuristicScore;
    }

    // New public method to get multiple move recommendations
    public List<MoveScoreInfo> GetTopMoveRecommendations(int[,] board, int aiPlayerId, int opponentPlayerId, int numberOfMoves)
    {
        this.transpositionTable = new Dictionary<string, TranspositionTableEntry>(); // Initialize/clear TT for each new top-level call
        
        int emptyCellsCount = GetEmptyCells(board).Count;
        if (emptyCellsCount == 0) return new List<MoveScoreInfo>(); // No moves possible

        int baseDepth = 2; // Reverted from 3 back to 2
        int endgameDepth = 3; // Reverted from 4 back to 3
        int endgameThreshold = 60; 

        int currentSearchDepth = baseDepth;
        string phase = "Midgame";
        if (emptyCellsCount < endgameThreshold) // Late game
        {
            currentSearchDepth = endgameDepth;
            phase = "Endgame";
        }
        // Removed: Early game phase logic (movesMade < 3)

        Console.WriteLine($"AI Service: GetTopMoveRecommendations for P{aiPlayerId}. Board hash: {ComputeBoardHash(board).Substring(0,6)}. Phase: {phase}. EmptyCells: {emptyCellsCount}. Depth: {currentSearchDepth}. NumMoves: {numberOfMoves}");

        var allPossibleMoves = GetEmptyCells(board);
        var scoredMoves = new List<MoveScoreInfo>();

        foreach (var move in allPossibleMoves)
        {
            int[,] newBoard = (int[,])board.Clone();
            newBoard[move.r, move.c] = aiPlayerId; // AI makes this move
            
            float moveScore;
            // Check for immediate win AFTER this move is made by AI
            if (this.CheckWin(newBoard, aiPlayerId, opponentPlayerId)) // Using AIService.CheckWin, which uses IsWinningPattern
            {
                // If this move results in a win for AI, assign it the highest possible score.
                moveScore = float.PositiveInfinity; 
                Console.WriteLine($"AI Service: P{aiPlayerId} can WIN with move ({move.r},{move.c}). Score: +Infinity. BoardHash after move: {ComputeBoardHash(newBoard).Substring(0,6)}");
            }
            else
            {
                // If not an immediate win, evaluate normally using Minimax
                // The score returned by MinimaxAlphaBeta is from the perspective of aiPlayerId (the one who just moved)
                (moveScore, _) = MinimaxAlphaBeta(newBoard, currentSearchDepth -1, float.NegativeInfinity, float.PositiveInfinity, false, aiPlayerId, opponentPlayerId);
            }
            scoredMoves.Add(new MoveScoreInfo { Row = move.r, Column = move.c, Score = moveScore });
        }

        // Sort moves by score in descending order (best score first)
        var sortedMoves = scoredMoves.OrderByDescending(m => m.Score).ToList();
        
        Console.WriteLine($" Minimax for P{aiPlayerId} found {sortedMoves.Count} potential moves. Top {Math.Min(numberOfMoves, sortedMoves.Count)} scores: {string.Join(", ", sortedMoves.Take(numberOfMoves).Select(m => m.Score.ToString("F2")))}");

        return sortedMoves.Take(numberOfMoves).ToList();
    }
} 