using System.Text;

namespace Wingman;

public interface IScreenBuffer
{
    // strippedText has had all ANSI escape sequences removed; only \r, \n, \b, \t and printable chars remain
    void Feed(ReadOnlySpan<char> strippedText);
    void Resize(int rows, int cols);
    void Reset();
    string GetVisibleText();
}

public class ScreenBuffer : IScreenBuffer
{
    private const int MaxScrollback = 500;

    private readonly Lock _lock = new();
    private readonly List<char[]> _lines = [];
    private int _cursorRow;
    private int _cursorCol;
    private int _rows = 24;
    private int _cols = 80;

    public ScreenBuffer() => _lines.Add(new char[_cols]);

    public void Feed(ReadOnlySpan<char> strippedText)
    {
        lock (_lock)
        {
            foreach (var ch in strippedText)
            {
                switch (ch)
                {
                    case '\r': _cursorCol = 0; break;
                    case '\n': LineFeed(); break;
                    case '\b': if (_cursorCol > 0) _cursorCol--; break;
                    case '\t': _cursorCol = Math.Min((_cursorCol / 8 + 1) * 8, _cols - 1); break;
                    default:
                        if (ch >= 0x20) WriteChar(ch);
                        break;
                }
            }
        }
    }

    public void Resize(int rows, int cols)
    {
        lock (_lock)
        {
            if (rows > 0) _rows = rows;
            if (cols > 0) _cols = cols;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _lines.Clear();
            _lines.Add(new char[_cols]);
            _cursorRow = 0;
            _cursorCol = 0;
        }
    }

    public string GetVisibleText()
    {
        lock (_lock)
        {
            // anchor to last row that has actual content, ignoring trailing blank rows from
            // PSReadLine cursor-movement noise that gets added after the visible prompt
            var lastContent = 0;
            for (var i = _lines.Count - 1; i >= 0; i--)
            {
                var line = _lines[i];
                var hasContent = false;
                foreach (var ch in line)
                {
                    if (ch != '\0' && ch != ' ') { hasContent = true; break; }
                }
                if (hasContent) { lastContent = i; break; }
            }

            var start = Math.Max(0, lastContent - _rows + 1);
            var end = Math.Min(_lines.Count, start + _rows);

            var sb = new StringBuilder();
            for (var i = start; i < end; i++)
            {
                if (sb.Length > 0) sb.Append('\n');
                var line = _lines[i];
                var len = line.Length;
                while (len > 0 && (line[len - 1] == '\0' || line[len - 1] == ' ')) len--;
                sb.Append(line, 0, len);
            }
            return sb.ToString();
        }
    }

    private void WriteChar(char ch)
    {
        var line = _lines[_cursorRow];

        // expand row if needed after a resize to wider cols
        if (_cursorCol >= line.Length)
        {
            var expanded = new char[_cols];
            Array.Copy(line, expanded, line.Length);
            _lines[_cursorRow] = expanded;
            line = expanded;
        }

        if (_cursorCol < _cols)
            line[_cursorCol++] = ch;
    }

    private void LineFeed()
    {
        if (_cursorRow < _lines.Count - 1)
        {
            _cursorRow++;
        }
        else
        {
            _lines.Add(new char[_cols]);
            _cursorRow = _lines.Count - 1;

            // cap scrollback
            if (_lines.Count > MaxScrollback)
            {
                var trim = _lines.Count - MaxScrollback;
                _lines.RemoveRange(0, trim);
                _cursorRow -= trim;
            }
        }
    }
}
