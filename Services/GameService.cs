namespace CaroAIServer.Services;

public class GameService
{
    // Game logic (e.g., checking for win, managing turns) will go here
    public const int BoardSize = 15;
    public int[,] Board { get; private set; } = new int[BoardSize, BoardSize]; // 0: empty, 1: Player 1 (Human), 2: Player 2 (AI)
    public int CurrentPlayer { get; private set; } = 1; // Player 1 starts
    public (int row, int col)? AILastMove { get; set; } // Stores the AI's last move
    public (int row, int col)? LastMove { get; set; } // Stores the overall last move

    public bool MakeMove(int row, int col)
    {
        if (row < 0 || row >= BoardSize || col < 0 || col >= BoardSize || Board[row, col] != 0)
        {
            return false; // Invalid move
        }

        Board[row, col] = CurrentPlayer;
        LastMove = (row, col); // Update the overall last move

        if (CurrentPlayer == 2) // Assuming Player 2 is AI
        {
            AILastMove = (row, col);
        }
        
        // Switch player after a move
        // CurrentPlayer = CurrentPlayer == 1 ? 2 : 1;
        return true;
    }

    public void SwitchPlayer()
    {
        CurrentPlayer = CurrentPlayer == 1 ? 2 : 1;
    }

    public bool CheckWin(int player)
    {
        // Determine opponent's ID. Assuming player 1 (Human) and 2 (AI).
        // This needs to be consistent with how player IDs are defined and used elsewhere.
        int opponent = (player == 1) ? 2 : 1;

        // Check rows for win
        for (int r = 0; r < BoardSize; r++)
        {
            for (int c = 0; c <= BoardSize - 5; c++) // c can go up to BoardSize - 5 to start a line of 5
            {
                if (IsWinningPattern(this.Board, player, opponent, r, c, 0, 1)) return true;
            }
        }
        // Check columns for win
        for (int c = 0; c < BoardSize; c++)
        {
            for (int r = 0; r <= BoardSize - 5; r++) // r can go up to BoardSize - 5
            {
                if (IsWinningPattern(this.Board, player, opponent, r, c, 1, 0)) return true;
            }
        }
        // Check main diagonal (top-left to bottom-right)
        for (int r = 0; r <= BoardSize - 5; r++)
        {
            for (int c = 0; c <= BoardSize - 5; c++)
            {
                if (IsWinningPattern(this.Board, player, opponent, r, c, 1, 1)) return true;
            }
        }
        // Check anti-diagonal (top-right to bottom-left)
        for (int r = 0; r <= BoardSize - 5; r++)
        {
            // c_start for anti-diagonal must be at least 4 (0-indexed) to allow line of 5 to c_start - 4
            // So, c can go from 4 up to BoardSize - 1
            for (int c = 4; c < BoardSize; c++) 
            {
                if (IsWinningPattern(this.Board, player, opponent, r, c, 1, -1)) return true;
            }
        }
        return false;
    }

     public bool IsBoardFull()
    {
        for (int i = 0; i < BoardSize; i++)
        {
            for (int j = 0; j < BoardSize; j++)
            {
                if (Board[i, j] == 0)
                {
                    return false; // Found an empty cell
                }
            }
        }
        return true; // All cells are filled
    }

    public void ResetGame()
    {
        Board = new int[BoardSize, BoardSize];
        CurrentPlayer = 1;
        AILastMove = null; // Reset AI's last move on game reset
        LastMove = null; // Reset overall last move on game reset
    }

    // Helper to get cell value safely, returns -1 if out of bounds
    // This GetCell is specific to GameService and uses its BoardSize
    private int GetCell(int[,] board, int r, int c)
    {
        if (r >= 0 && r < BoardSize && c >= 0 && c < BoardSize) return board[r, c];
        return -1; // Sentinel for "off-board"
    }

    // Checks if a 5-in-a-row starting at (r_start, c_start) in direction (dr, dc)
    // is a winning pattern according to standard rules (not overline, not doubly blocked by opponent).
    private bool IsWinningPattern(int[,] board, int player, int opponent, int r_start, int c_start, int dr, int dc)
    {
        // 1. Check for 5 consecutive player pieces
        for (int i = 0; i < 5; i++)
        {
            if (GetCell(board, r_start + i * dr, c_start + i * dc) != player)
                return false; // Not 5 in a row
        }

        // If we reach here, we found 5 consecutive player pieces.
        // Console.WriteLine($"DEBUG IsWinningPattern (GameService): Potential 5-in-a-row for P{player} starting at ({r_start},{c_start}) with direction ({dr},{dc}). Opponent ID is P{opponent}.");

        int r_before = r_start - dr;
        int c_before = c_start - dc;
        int val_before = GetCell(board, r_before, c_before);
        // Console.WriteLine($"  (GameService) Cell before: ({r_before},{c_before}), Value: {val_before}");

        int r_after = r_start + 5 * dr;
        int c_after = c_start + 5 * dc;
        int val_after = GetCell(board, r_after, c_after);
        // Console.WriteLine($"  (GameService) Cell after: ({r_after},{c_after}), Value: {val_after}");

        // 2. Check for overline (6th piece is also player)
        if (val_before == player || val_after == player)
        {
            // Console.WriteLine("  DEBUG IsWinningPattern (GameService): Overline detected. Not a win.");
            return false; // Overline of 6 or more
        }

        // 3. Check for doubly blocked five (O P P P P P O)
        // This means the player\'s 5-in-a-row is blocked by the opponent on both sides.
        if (val_before == opponent && val_after == opponent)
        {
            // Console.WriteLine("  DEBUG IsWinningPattern (GameService): Doubly blocked by opponent. Not a win.");
            return false; // Doubly blocked by opponent
        }
        
        // Console.WriteLine("  DEBUG IsWinningPattern (GameService): Valid win condition met.");
        return true; // It\'s a valid winning five
    }
} 