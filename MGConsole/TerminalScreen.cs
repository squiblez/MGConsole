using System;
using System.Text;
using Microsoft.Xna.Framework;

namespace MGConsole
{
    // 2D character grid + VT/ANSI escape sequence parser.
    // Implements enough of xterm/VT100 to support cmd.exe and TUI apps
    // like 'edit', 'more', etc. running through ConPTY.
    internal sealed class TerminalScreen
    {
        public struct Cell
        {
            public char Ch;
            public Color Fg;
            public Color Bg;
        }

        public int Cols { get; private set; }
        public int Rows { get; private set; }
        public Cell[,] Buffer;
        public int CursorRow;
        public int CursorCol;
        public bool CursorVisible = true;

        // 16-color VGA-ish palette (index 0-15). Mutable so Settings can override at startup.
        public static Color[] Palette = new Color[]
        {
            new Color( 12,  12,  12), // 0 black
            new Color(197,  15,  31), // 1 red
            new Color( 19, 161,  14), // 2 green
            new Color(193, 156,   0), // 3 yellow
            new Color(  0,  55, 218), // 4 blue
            new Color(136,  23, 152), // 5 magenta
            new Color( 58, 150, 221), // 6 cyan
            new Color(204, 204, 204), // 7 white (light gray)
            new Color(118, 118, 118), // 8 bright black (dark gray)
            new Color(231,  72,  86), // 9 bright red
            new Color( 22, 198,  12), // 10 bright green
            new Color(249, 241, 165), // 11 bright yellow
            new Color( 59, 120, 255), // 12 bright blue
            new Color(180,   0, 158), // 13 bright magenta
            new Color( 97, 214, 214), // 14 bright cyan
            new Color(242, 242, 242), // 15 bright white
        };

        Color _fg = Palette[7];
        Color _bg = Palette[0];
        bool _bold;
        bool _reverse;

        int _saveRow, _saveCol;
        int _scrollTop;     // 0-based, inclusive
        int _scrollBottom;  // 0-based, inclusive

        enum State { Normal, Esc, EscIntermediate, Csi, Osc, OscEsc }
        State _state = State.Normal;
        readonly StringBuilder _seq = new();

        public TerminalScreen(int cols, int rows)
        {
            Resize(cols, rows);
        }

        public void Resize(int cols, int rows)
        {
            if (cols < 1) cols = 1;
            if (rows < 1) rows = 1;

            var oldBuf = Buffer;
            int oldCols = Cols;
            int oldRows = Rows;

            Cols = cols;
            Rows = rows;
            Buffer = new Cell[rows, cols];
            _scrollTop = 0;
            _scrollBottom = rows - 1;

            // Initialize all cells to blank using current attributes.
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    Buffer[r, c] = new Cell { Ch = ' ', Fg = _fg, Bg = _bg };

            // Preserve as much of the previous content as fits (top-left aligned).
            if (oldBuf != null)
            {
                int copyRows = Math.Min(oldRows, rows);
                int copyCols = Math.Min(oldCols, cols);
                for (int r = 0; r < copyRows; r++)
                    for (int c = 0; c < copyCols; c++)
                        Buffer[r, c] = oldBuf[r, c];
            }

            CursorRow = Math.Min(CursorRow, rows - 1);
            CursorCol = Math.Min(CursorCol, cols - 1);
        }

        public void ClearAll()
        {
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    Buffer[r, c] = new Cell { Ch = ' ', Fg = _fg, Bg = _bg };
        }

        public void Feed(ReadOnlySpan<char> data)
        {
            for (int i = 0; i < data.Length; i++)
                FeedChar(data[i]);
        }

        void FeedChar(char ch)
        {
            switch (_state)
            {
                case State.Normal:
                    if (ch == 0x1B) { _state = State.Esc; _seq.Clear(); }
                    else if (ch == '\r') CursorCol = 0;
                    else if (ch == '\n') Linefeed();
                    else if (ch == '\b') { if (CursorCol > 0) CursorCol--; }
                    else if (ch == '\t') CursorCol = Math.Min(Cols - 1, ((CursorCol / 8) + 1) * 8);
                    else if (ch == 0x07) { /* bell */ }
                    else if (ch >= 0x20) PutChar(ch);
                    break;

                case State.Esc:
                    if (ch == '[') { _state = State.Csi; _seq.Clear(); }
                    else if (ch == ']') { _state = State.Osc; _seq.Clear(); }
                    else if (ch == '7') { _saveRow = CursorRow; _saveCol = CursorCol; _state = State.Normal; }
                    else if (ch == '8') { CursorRow = _saveRow; CursorCol = _saveCol; _state = State.Normal; }
                    else if (ch == 'M') { ReverseLinefeed(); _state = State.Normal; }
                    else if (ch == 'D') { Linefeed(); _state = State.Normal; }
                    else if (ch == 'E') { CursorCol = 0; Linefeed(); _state = State.Normal; }
                    else if (ch == 'c') { ResetTerminal(); _state = State.Normal; }
                    else if (ch == '(' || ch == ')' || ch == '#' || ch == '%') _state = State.EscIntermediate;
                    else _state = State.Normal;
                    break;

                case State.EscIntermediate:
                    _state = State.Normal; // swallow the next byte (charset designator etc.)
                    break;

                case State.Csi:
                    if ((ch >= '0' && ch <= '9') || ch == ';' || ch == '?' || ch == '>' || ch == '=' || ch == ' ')
                        _seq.Append(ch);
                    else
                    {
                        HandleCsi(ch, _seq.ToString());
                        _seq.Clear();
                        _state = State.Normal;
                    }
                    break;

                case State.Osc:
                    if (ch == 0x07) _state = State.Normal;             // BEL terminator
                    else if (ch == 0x1B) _state = State.OscEsc;        // ESC \ terminator
                    else _seq.Append(ch);
                    break;

                case State.OscEsc:
                    _state = State.Normal;
                    break;
            }
        }

        void PutChar(char ch)
        {
            if (CursorCol >= Cols)
            {
                CursorCol = 0;
                Linefeed();
            }
            var cell = new Cell { Ch = ch, Fg = _reverse ? _bg : _fg, Bg = _reverse ? _fg : _bg };
            Buffer[CursorRow, CursorCol] = cell;
            CursorCol++;
        }

        void Linefeed()
        {
            if (CursorRow == _scrollBottom)
                ScrollUp(1);
            else if (CursorRow < Rows - 1)
                CursorRow++;
        }

        void ReverseLinefeed()
        {
            if (CursorRow == _scrollTop)
                ScrollDown(1);
            else if (CursorRow > 0)
                CursorRow--;
        }

        void ScrollUp(int n)
        {
            for (int i = 0; i < n; i++)
            {
                for (int r = _scrollTop; r < _scrollBottom; r++)
                    for (int c = 0; c < Cols; c++)
                        Buffer[r, c] = Buffer[r + 1, c];
                for (int c = 0; c < Cols; c++)
                    Buffer[_scrollBottom, c] = new Cell { Ch = ' ', Fg = _fg, Bg = _bg };
            }
        }

        void ScrollDown(int n)
        {
            for (int i = 0; i < n; i++)
            {
                for (int r = _scrollBottom; r > _scrollTop; r--)
                    for (int c = 0; c < Cols; c++)
                        Buffer[r, c] = Buffer[r - 1, c];
                for (int c = 0; c < Cols; c++)
                    Buffer[_scrollTop, c] = new Cell { Ch = ' ', Fg = _fg, Bg = _bg };
            }
        }

        void ResetTerminal()
        {
            _fg = Palette[7];
            _bg = Palette[0];
            _bold = false;
            _reverse = false;
            CursorRow = CursorCol = 0;
            _scrollTop = 0;
            _scrollBottom = Rows - 1;
            ClearAll();
        }

        // ---------------- CSI handling ----------------

        void HandleCsi(char final, string param)
        {
            bool isPrivate = param.StartsWith("?");
            if (isPrivate) param = param.Substring(1);
            int[] args = ParseArgs(param);

            int A(int i, int def = 0) => (i < args.Length && args[i] != int.MinValue) ? args[i] : def;

            switch (final)
            {
                case 'A': CursorRow = Math.Max(0, CursorRow - Math.Max(1, A(0, 1))); break;
                case 'B': CursorRow = Math.Min(Rows - 1, CursorRow + Math.Max(1, A(0, 1))); break;
                case 'C': CursorCol = Math.Min(Cols - 1, CursorCol + Math.Max(1, A(0, 1))); break;
                case 'D': CursorCol = Math.Max(0, CursorCol - Math.Max(1, A(0, 1))); break;
                case 'E': CursorRow = Math.Min(Rows - 1, CursorRow + Math.Max(1, A(0, 1))); CursorCol = 0; break;
                case 'F': CursorRow = Math.Max(0, CursorRow - Math.Max(1, A(0, 1))); CursorCol = 0; break;
                case 'G': CursorCol = Clamp(A(0, 1) - 1, 0, Cols - 1); break;
                case 'H':
                case 'f':
                    CursorRow = Clamp(A(0, 1) - 1, 0, Rows - 1);
                    CursorCol = Clamp(A(1, 1) - 1, 0, Cols - 1);
                    break;
                case 'd': CursorRow = Clamp(A(0, 1) - 1, 0, Rows - 1); break;
                case 'J': EraseDisplay(A(0, 0)); break;
                case 'K': EraseLine(A(0, 0)); break;
                case 'L': InsertLines(Math.Max(1, A(0, 1))); break;
                case 'M': DeleteLines(Math.Max(1, A(0, 1))); break;
                case 'P': DeleteChars(Math.Max(1, A(0, 1))); break;
                case '@': InsertChars(Math.Max(1, A(0, 1))); break;
                case 'X': EraseChars(Math.Max(1, A(0, 1))); break;
                case 'S': ScrollUp(Math.Max(1, A(0, 1))); break;
                case 'T': ScrollDown(Math.Max(1, A(0, 1))); break;
                case 'm': HandleSgr(args); break;
                case 'r':
                    _scrollTop = Clamp(A(0, 1) - 1, 0, Rows - 1);
                    _scrollBottom = Clamp(A(1, Rows) - 1, 0, Rows - 1);
                    if (_scrollTop > _scrollBottom) { _scrollTop = 0; _scrollBottom = Rows - 1; }
                    CursorRow = 0; CursorCol = 0;
                    break;
                case 'h':
                    if (isPrivate)
                    {
                        if (A(0) == 25) CursorVisible = true;
                        // 1049/47/1047 = alternate screen buffer (we don't separately track; fine for most apps)
                        if (A(0) == 1049) { ClearAll(); CursorRow = CursorCol = 0; }
                    }
                    break;
                case 'l':
                    if (isPrivate)
                    {
                        if (A(0) == 25) CursorVisible = false;
                        if (A(0) == 1049) { ClearAll(); CursorRow = CursorCol = 0; }
                    }
                    break;
                case 's': _saveRow = CursorRow; _saveCol = CursorCol; break;
                case 'u': CursorRow = _saveRow; CursorCol = _saveCol; break;
            }
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        static int[] ParseArgs(string s)
        {
            if (string.IsNullOrEmpty(s)) return new[] { int.MinValue };
            string[] parts = s.Split(';');
            int[] a = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                a[i] = int.TryParse(parts[i], out int v) ? v : int.MinValue;
            return a;
        }

        void EraseDisplay(int mode)
        {
            switch (mode)
            {
                case 0: // cursor to end
                    EraseLine(0);
                    for (int r = CursorRow + 1; r < Rows; r++) ClearRow(r);
                    break;
                case 1: // start to cursor
                    for (int r = 0; r < CursorRow; r++) ClearRow(r);
                    EraseLine(1);
                    break;
                case 2:
                case 3:
                    ClearAll();
                    break;
            }
        }

        void EraseLine(int mode)
        {
            switch (mode)
            {
                case 0: for (int c = CursorCol; c < Cols; c++) Buffer[CursorRow, c] = Blank(); break;
                case 1: for (int c = 0; c <= CursorCol; c++) Buffer[CursorRow, c] = Blank(); break;
                case 2: ClearRow(CursorRow); break;
            }
        }

        void ClearRow(int r) { for (int c = 0; c < Cols; c++) Buffer[r, c] = Blank(); }
        Cell Blank() => new Cell { Ch = ' ', Fg = _fg, Bg = _bg };

        void InsertLines(int n)
        {
            if (CursorRow < _scrollTop || CursorRow > _scrollBottom) return;
            for (int i = 0; i < n; i++)
            {
                for (int r = _scrollBottom; r > CursorRow; r--)
                    for (int c = 0; c < Cols; c++)
                        Buffer[r, c] = Buffer[r - 1, c];
                ClearRow(CursorRow);
            }
        }

        void DeleteLines(int n)
        {
            if (CursorRow < _scrollTop || CursorRow > _scrollBottom) return;
            for (int i = 0; i < n; i++)
            {
                for (int r = CursorRow; r < _scrollBottom; r++)
                    for (int c = 0; c < Cols; c++)
                        Buffer[r, c] = Buffer[r + 1, c];
                ClearRow(_scrollBottom);
            }
        }

        void InsertChars(int n)
        {
            for (int i = 0; i < n; i++)
            {
                for (int c = Cols - 1; c > CursorCol; c--)
                    Buffer[CursorRow, c] = Buffer[CursorRow, c - 1];
                Buffer[CursorRow, CursorCol] = Blank();
            }
        }

        void DeleteChars(int n)
        {
            for (int i = 0; i < n; i++)
            {
                for (int c = CursorCol; c < Cols - 1; c++)
                    Buffer[CursorRow, c] = Buffer[CursorRow, c + 1];
                Buffer[CursorRow, Cols - 1] = Blank();
            }
        }

        void EraseChars(int n)
        {
            for (int c = CursorCol; c < Math.Min(Cols, CursorCol + n); c++)
                Buffer[CursorRow, c] = Blank();
        }

        void HandleSgr(int[] args)
        {
            if (args.Length == 0 || (args.Length == 1 && args[0] == int.MinValue))
            {
                ResetSgr();
                return;
            }
            for (int i = 0; i < args.Length; i++)
            {
                int n = args[i] == int.MinValue ? 0 : args[i];
                switch (n)
                {
                    case 0: ResetSgr(); break;
                    case 1: _bold = true; break;
                    case 7: _reverse = true; break;
                    case 22: _bold = false; break;
                    case 27: _reverse = false; break;
                    case 39: _fg = Palette[7]; break;
                    case 49: _bg = Palette[0]; break;
                    case >= 30 and <= 37: _fg = Palette[n - 30 + (_bold ? 8 : 0)]; break;
                    case >= 40 and <= 47: _bg = Palette[n - 40]; break;
                    case >= 90 and <= 97: _fg = Palette[n - 90 + 8]; break;
                    case >= 100 and <= 107: _bg = Palette[n - 100 + 8]; break;
                    case 38:
                        // 38;5;n  or  38;2;r;g;b
                        if (i + 2 < args.Length && args[i + 1] == 5)
                        {
                            _fg = Color256(args[i + 2]);
                            i += 2;
                        }
                        else if (i + 4 < args.Length && args[i + 1] == 2)
                        {
                            _fg = new Color(args[i + 2], args[i + 3], args[i + 4]);
                            i += 4;
                        }
                        break;
                    case 48:
                        if (i + 2 < args.Length && args[i + 1] == 5)
                        {
                            _bg = Color256(args[i + 2]);
                            i += 2;
                        }
                        else if (i + 4 < args.Length && args[i + 1] == 2)
                        {
                            _bg = new Color(args[i + 2], args[i + 3], args[i + 4]);
                            i += 4;
                        }
                        break;
                }
            }
        }

        void ResetSgr()
        {
            _fg = Palette[7];
            _bg = Palette[0];
            _bold = false;
            _reverse = false;
        }

        static Color Color256(int n)
        {
            if (n < 16) return Palette[n];
            if (n >= 232)
            {
                int g = (n - 232) * 10 + 8;
                return new Color(g, g, g);
            }
            int idx = n - 16;
            int r = (idx / 36) % 6;
            int gg = (idx / 6) % 6;
            int b = idx % 6;
            int[] steps = { 0, 95, 135, 175, 215, 255 };
            return new Color(steps[r], steps[gg], steps[b]);
        }
    }
}
