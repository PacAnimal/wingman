using System.Text;

namespace Wingman;

public interface IScreenBuffer
{
    void Feed(ReadOnlySpan<char> text);  // raw ANSI — parses VT sequences internally
    void Resize(int rows, int cols);     // apply new dims, wipe grid
    void FillFromText(string plainText); // populate grid from plain text (e.g. UIA viewport read)
    void Reset();
    string GetVisibleText();
}

public class ScreenBuffer : IScreenBuffer
{
    private enum ParseState { Normal, Escape, Csi, Osc }

    private readonly Lock _lock = new();
    private int _rows = 24;
    private int _cols = 80;
    private char[][] _grid;

    // cursor position
    private int _cursorRow;
    private int _cursorCol;

    // ANSI parser state (persists across Feed() calls to handle split sequences)
    private ParseState _parseState = ParseState.Normal;
    private readonly StringBuilder _csiParams = new();

    public ScreenBuffer() => _grid = MakeGrid(_rows, _cols);

    public void Feed(ReadOnlySpan<char> text)
    {
        lock (_lock)
        {
            ParseAnsi(text);
        }
    }

    public void Resize(int rows, int cols)
    {
        lock (_lock)
        {
            if (rows > 0 && cols > 0)
            {
                _rows = rows;
                _cols = cols;
            }
            WipeGrid();
        }
    }

    public void Reset()
    {
        lock (_lock) { WipeGrid(); }
    }

    public void FillFromText(string plainText)
    {
        lock (_lock)
        {
            WipeGrid();
            var lines = plainText.Split('\n');
            for (var r = 0; r < Math.Min(lines.Length, _rows); r++)
            {
                var line = lines[r].TrimEnd('\r');
                for (var c = 0; c < Math.Min(line.Length, _cols); c++)
                    _grid[r][c] = line[c];
            }
        }
    }

    public string GetVisibleText()
    {
        lock (_lock)
        {
            // find last row with actual content
            var lastNonEmpty = -1;
            for (var i = 0; i < _rows; i++)
            {
                foreach (var ch in _grid[i])
                    if (ch != '\0' && ch != ' ') { lastNonEmpty = i; break; }
            }

            if (lastNonEmpty < 0) return "";

            var sb = new StringBuilder();
            for (var i = 0; i <= lastNonEmpty; i++)
            {
                if (i > 0) sb.Append('\n'); // always separate rows, even blank ones
                var row = _grid[i];
                var len = row.Length;
                while (len > 0 && (row[len - 1] == '\0' || row[len - 1] == ' ')) len--;
                for (var c = 0; c < len; c++)
                    sb.Append(row[c] == '\0' ? ' ' : row[c]); // null cells render as spaces
            }
            return sb.ToString();
        }
    }

    // --- ANSI parser ---

    private void ParseAnsi(ReadOnlySpan<char> text)
    {
        foreach (var ch in text)
        {
            switch (_parseState)
            {
                case ParseState.Normal: HandleNormal(ch); break;
                case ParseState.Escape: HandleEscape(ch); break;
                case ParseState.Csi: HandleCsi(ch); break;
                case ParseState.Osc: HandleOsc(ch); break;
            }
        }
    }

    private void HandleNormal(char ch)
    {
        switch (ch)
        {
            case '\x1B': _parseState = ParseState.Escape; break;
            case '\r': _cursorCol = 0; break;
            case '\n': LineFeed(); break;
            case '\b': if (_cursorCol > 0) _cursorCol--; break;
            case '\t': _cursorCol = Math.Min((_cursorCol / 8 + 1) * 8, _cols - 1); break;
            case '\uFEFF':
            case '\u200B': break; // BOM / zero-width space
            default:
                if (ch >= 0x20) WriteChar(ch);
                break;
        }
    }

    private void HandleEscape(char ch)
    {
        switch (ch)
        {
            case '[':
                _parseState = ParseState.Csi;
                _csiParams.Clear();
                break;
            case ']': _parseState = ParseState.Osc; break;
            default: _parseState = ParseState.Normal; break; // unknown, skip
        }
    }

    private void HandleCsi(char ch)
    {
        if (ch is >= '0' and <= '9' || ch == ';') { _csiParams.Append(ch); }
        else if (ch == '?') { /* private mode prefix — ignored */ }
        else if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
        {
            DispatchCsi(ch);
            _parseState = ParseState.Normal;
        }
        else { _parseState = ParseState.Normal; } // malformed
    }

    private void HandleOsc(char ch)
    {
        switch (ch)
        {
            case '\a': _parseState = ParseState.Normal; break; // BEL terminates OSC
            case '\x1B': _parseState = ParseState.Escape; break; // ST: \e\\ terminates OSC
                                                                 // else: consume OSC body
        }
    }

    private void DispatchCsi(char cmd)
    {
        var parts = _csiParams.ToString().Split(';');

        // param helpers: cursor movement defaults to 1, erase defaults to 0
        int Move(int idx) => idx < parts.Length && int.TryParse(parts[idx], out var v) && v > 0 ? v : 1;
        int Erase() => parts.Length > 0 && int.TryParse(parts[0], out var v) ? v : 0;

        switch (cmd)
        {
            case 'H':
            case 'f':
                {
                    // CUP: \e[row;colH (1-indexed, default 1;1)
                    var row = Move(0) - 1;
                    var col = Move(1) - 1;
                    _cursorRow = Math.Clamp(row, 0, _rows - 1);
                    _cursorCol = Math.Clamp(col, 0, _cols - 1);
                    break;
                }
            case 'A': _cursorRow = Math.Max(0, _cursorRow - Move(0)); break; // cursor up
            case 'B': _cursorRow = Math.Min(_rows - 1, _cursorRow + Move(0)); break; // cursor down
            case 'C': _cursorCol = Math.Min(_cols - 1, _cursorCol + Move(0)); break; // cursor forward
            case 'D': _cursorCol = Math.Max(0, _cursorCol - Move(0)); break; // cursor back
            case 'G': _cursorCol = Math.Clamp(Move(0) - 1, 0, _cols - 1); break;   // cursor column absolute
            case 'J':
                {
                    switch (Erase())
                    {
                        case 0: ClearRegion(_cursorRow, _cursorCol, _rows - 1, _cols - 1); break; // cursor → end
                        case 1: ClearRegion(0, 0, _cursorRow, _cursorCol); break;                  // start → cursor
                        case 2:
                        case 3: _grid = MakeGrid(_rows, _cols); break;                             // whole screen
                    }
                    break;
                }
            case 'K':
                {
                    switch (Erase())
                    {
                        case 0: ClearRow(_cursorRow, _cursorCol, _cols - 1); break; // cursor → end of line
                        case 1: ClearRow(_cursorRow, 0, _cursorCol); break;         // start → cursor
                        case 2: ClearRow(_cursorRow, 0, _cols - 1); break;          // whole line
                    }
                    break;
                }
                // SGR (m) and all other sequences: ignore
        }
    }

    // --- grid operations ---

    private void WipeGrid()
    {
        _grid = MakeGrid(_rows, _cols);
        _cursorRow = 0;
        _cursorCol = 0;
        _parseState = ParseState.Normal;
        _csiParams.Clear();
    }

    private void WriteChar(char ch)
    {
        // autowrap at right edge
        if (_cursorCol >= _cols) { LineFeed(); _cursorCol = 0; }
        _grid[_cursorRow][_cursorCol++] = ch;
    }

    private void LineFeed()
    {
        if (_cursorRow < _rows - 1)
        {
            _cursorRow++;
        }
        else
        {
            // scroll: shift rows up, clear bottom row
            var tmp = _grid[0];
            Array.Copy(_grid, 1, _grid, 0, _rows - 1);
            Array.Clear(tmp);
            _grid[_rows - 1] = tmp;
        }
    }

    private void ClearRegion(int startRow, int startCol, int endRow, int endCol)
    {
        for (var r = startRow; r <= endRow && r < _rows; r++)
            ClearRow(r, r == startRow ? startCol : 0, r == endRow ? endCol : _cols - 1);
    }

    private void ClearRow(int row, int fromCol, int toCol)
    {
        Array.Clear(_grid[row], fromCol, Math.Min(toCol + 1, _cols) - fromCol);
    }

    private static char[][] MakeGrid(int rows, int cols)
    {
        var grid = new char[rows][];
        for (var i = 0; i < rows; i++) grid[i] = new char[cols];
        return grid;
    }
}
