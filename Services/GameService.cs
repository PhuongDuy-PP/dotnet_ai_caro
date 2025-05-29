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
        // Check rows
        for (int i = 0; i < BoardSize; i++)
        {
            for (int j = 0; j <= BoardSize - 5; j++)
            {
                if (Board[i, j] == player &&
                    Board[i, j + 1] == player &&
                    Board[i, j + 2] == player &&
                    Board[i, j + 3] == player &&
                    Board[i, j + 4] == player)
                    return true;
            }
        }

        // Check columns
        for (int j = 0; j < BoardSize; j++)
        {
            for (int i = 0; i <= BoardSize - 5; i++)
            {
                if (Board[i, j] == player &&
                    Board[i + 1, j] == player &&
                    Board[i + 2, j] == player &&
                    Board[i + 3, j] == player &&
                    Board[i + 4, j] == player)
                    return true;
            }
        }

        // Check diagonal (top-left to bottom-right)
        for (int i = 0; i <= BoardSize - 5; i++)
        {
            for (int j = 0; j <= BoardSize - 5; j++)
            {
                if (Board[i, j] == player &&
                    Board[i + 1, j + 1] == player &&
                    Board[i + 2, j + 2] == player &&
                    Board[i + 3, j + 3] == player &&
                    Board[i + 4, j + 4] == player)
                    return true;
            }
        }

        // Check diagonal (top-right to bottom-left)
        for (int i = 0; i <= BoardSize - 5; i++)
        {
            for (int j = 4; j < BoardSize; j++)
            {
                if (Board[i, j] == player &&
                    Board[i + 1, j - 1] == player &&
                    Board[i + 2, j - 2] == player &&
                    Board[i + 3, j - 3] == player &&
                    Board[i + 4, j - 4] == player)
                    return true;
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
} 