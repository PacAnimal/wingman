using Wingman;

namespace Wingman.Tests;

[TestFixture]
public class ScreenBufferTests
{
    // helpers
    private static ScreenBuffer Make(int rows = 10, int cols = 20)
    {
        var sb = new ScreenBuffer();
        sb.Resize(rows, cols);
        return sb;
    }

    private static string[] Lines(string text) =>
        text.Length == 0 ? [] : text.Split('\n');

    // -------------------------------------------------------------------------

    [TestFixture]
    public class BasicText
    {
        [Test]
        public void Feed_PrintableChars_WritesToGrid()
        {
            var sb = Make();
            sb.Feed("Hello");
            Assert.That(sb.GetVisibleText(), Is.EqualTo("Hello"));
        }

        [Test]
        public void Feed_MultipleLines_WritesAcrossRows()
        {
            var sb = Make();
            sb.Feed("AB\r\nCD");
            var lines = Lines(sb.GetVisibleText());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lines[0], Is.EqualTo("AB"));
                Assert.That(lines[1], Is.EqualTo("CD"));
            }
        }

        [Test]
        public void Feed_CarriageReturn_OverwritesFromColZero()
        {
            var sb = Make();
            sb.Feed("abc\rX");
            Assert.That(sb.GetVisibleText(), Is.EqualTo("Xbc"));
        }

        [Test]
        public void Feed_Backspace_MovesCursorBack()
        {
            var sb = Make();
            sb.Feed("abc\b\bX");
            Assert.That(sb.GetVisibleText(), Is.EqualTo("aXc"));
        }

        [Test]
        public void Feed_Tab_AdvancesToNextTabStop()
        {
            var sb = Make(cols: 40);
            sb.Feed("\tX");
            // tab from col 0 → col 8; cells 0-7 are blank (rendered as spaces)
            Assert.That(sb.GetVisibleText(), Is.EqualTo(new string(' ', 8) + "X"));
        }

        [Test]
        public void Feed_BomAndZeroWidth_Ignored()
        {
            var sb = Make();
            sb.Feed("\uFEFF\u200Bhello");
            Assert.That(sb.GetVisibleText(), Is.EqualTo("hello"));
        }

        [Test]
        public void Feed_ControlCharsBelow0x20_Ignored()
        {
            var sb = Make();
            sb.Feed("\x01\x02\x03hello\x04\x05");
            Assert.That(sb.GetVisibleText(), Is.EqualTo("hello"));
        }
    }

    // -------------------------------------------------------------------------

    [TestFixture]
    public class Autowrap
    {
        [Test]
        public void Feed_Autowrap_WrapsAtColumnBoundary()
        {
            var sb = Make(rows: 5, cols: 5);
            sb.Feed("ABCDEXY"); // 5 chars fill row 0, XY wraps to row 1
            var lines = Lines(sb.GetVisibleText());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lines[0], Is.EqualTo("ABCDE"));
                Assert.That(lines[1], Is.EqualTo("XY"));
            }
        }

        [Test]
        public void Feed_AutowrapAtBottom_ScrollsGrid()
        {
            var sb = Make(rows: 3, cols: 4);
            // fill 3 rows exactly, then one more char forces scroll
            sb.Feed("AAAABBBBCCCCD");
            var lines = Lines(sb.GetVisibleText());
            using (Assert.EnterMultipleScope())
            {
                // AAAA scrolled off; visible: BBBB / CCCC / D
                Assert.That(lines[0], Is.EqualTo("BBBB"));
                Assert.That(lines[1], Is.EqualTo("CCCC"));
                Assert.That(lines[2], Is.EqualTo("D"));
            }
        }
    }

    // -------------------------------------------------------------------------

    [TestFixture]
    public class CursorPositioning
    {
        [Test]
        public void Feed_Cup_PositionsCursorAndWritesChar()
        {
            var sb = Make(rows: 10, cols: 20);
            sb.Feed("\e[3;5Hx"); // row 3, col 5 (1-indexed) → grid[2][4]
            var lines = Lines(sb.GetVisibleText());
            Assert.That(lines[2], Is.EqualTo(new string(' ', 4) + "x"));
        }

        [Test]
        public void Feed_CupWithNoParams_MovesToTopLeft()
        {
            var sb = Make();
            sb.Feed("hello");
            sb.Feed("\e[H*"); // back to 1,1 then overwrite with *
            var lines = Lines(sb.GetVisibleText());
            Assert.That(lines[0][0], Is.EqualTo('*'));
        }

        [Test]
        public void Feed_Cup_ClampsOutOfBoundsToGrid()
        {
            var sb = Make(rows: 5, cols: 5);
            sb.Feed("\e[999;999Hz"); // clamps to last row/col (4,4)
            var lines = Lines(sb.GetVisibleText());
            Assert.That(lines[^1][^1], Is.EqualTo('z'));
        }

        [Test]
        public void Feed_CursorUp_MovesCursorUp()
        {
            var sb = Make(rows: 10, cols: 20);
            sb.Feed("\e[5;1H"); // row 5, col 1 (1-indexed) → grid[4][0]
            sb.Feed("\e[2A");   // up 2 → row 2 (grid index 2)
            sb.Feed("X");
            var lines = Lines(sb.GetVisibleText());
            Assert.That(lines[2][0], Is.EqualTo('X'));
        }

        [Test]
        public void Feed_CursorDown_MovesCursorDown()
        {
            var sb = Make(rows: 10, cols: 20);
            sb.Feed("\e[2B"); // from row 0, down 2 → row 2
            sb.Feed("X");
            var lines = Lines(sb.GetVisibleText());
            Assert.That(lines[2][0], Is.EqualTo('X'));
        }

        [Test]
        public void Feed_CursorForward_MovesCursorRight()
        {
            var sb = Make(rows: 10, cols: 20);
            sb.Feed("\e[5C"); // col 0 → col 5; skipped cells render as spaces
            sb.Feed("X");
            Assert.That(sb.GetVisibleText(), Is.EqualTo(new string(' ', 5) + "X"));
        }

        [Test]
        public void Feed_CursorBack_MovesCursorLeft()
        {
            var sb = Make(rows: 10, cols: 20);
            sb.Feed("ABCDE"); // cursor now at col 5
            sb.Feed("\e[3D"); // back 3 → col 2
            sb.Feed("X");
            Assert.That(sb.GetVisibleText(), Is.EqualTo("ABXDE"));
        }

        [Test]
        public void Feed_CursorColumnAbsolute_SetsColumn()
        {
            var sb = Make(rows: 10, cols: 20);
            sb.Feed("ABCDE");
            sb.Feed("\e[3G"); // col 3 (1-indexed) → col index 2
            sb.Feed("X");
            Assert.That(sb.GetVisibleText()[2], Is.EqualTo('X'));
        }
    }

    // -------------------------------------------------------------------------

    [TestFixture]
    public class EraseCommands
    {
        [Test]
        public void Feed_EraseDisplayToEnd_ClearsCursorToEndOfScreen()
        {
            var sb = Make(rows: 5, cols: 4);
            sb.Feed("AAAABBBBCCCCDDDDEEEE"); // fill 5 rows
            sb.Feed("\e[2;2H"); // row 2, col 2 (1-indexed) → grid[1][1]
            sb.Feed("\e[0J");   // erase cursor → end of screen
            var lines = Lines(sb.GetVisibleText());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lines[0], Is.EqualTo("AAAA")); // row 0 untouched
                Assert.That(lines[1][0], Is.EqualTo('B')); // row 1 col 0 untouched
                Assert.That(lines[1].TrimEnd(), Has.Length.EqualTo(1)); // rest cleared
            }
        }

        [Test]
        public void Feed_EraseDisplayToStart_ClearsStartToCursor()
        {
            var sb = Make(rows: 5, cols: 4);
            sb.Feed("AAAABBBBCCCC");
            sb.Feed("\e[2;3H"); // row 2, col 3 (1-indexed) → grid[1][2]
            sb.Feed("\e[1J");   // erase start → cursor (inclusive)
            var lines = Lines(sb.GetVisibleText());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lines[0].Trim(), Is.Empty);          // row 0 fully cleared
                Assert.That(lines[1][..3].Trim(), Is.Empty);     // row 1 cols 0-2 cleared
                Assert.That(lines[1][3], Is.EqualTo('B'));       // col 3 remains
                Assert.That(lines[2], Is.EqualTo("CCCC"));       // row 2 untouched
            }
        }

        [Test]
        public void Feed_EraseDisplayAll_ClearsEntireGrid()
        {
            var sb = Make(rows: 5, cols: 4);
            sb.Feed("AAAABBBB");
            sb.Feed("\e[2J");
            Assert.That(sb.GetVisibleText(), Is.Empty);
        }

        [Test]
        public void Feed_EraseLineToEnd_ClearsCursorToEndOfRow()
        {
            var sb = Make(rows: 5, cols: 10);
            sb.Feed("ABCDEFGHIJ");
            sb.Feed("\e[1;4H"); // col 4 (1-indexed) → col 3 (= 'D')
            sb.Feed("\e[0K");   // erase col 3 → end of line
            Assert.That(sb.GetVisibleText(), Is.EqualTo("ABC"));
        }

        [Test]
        public void Feed_EraseLineToStart_ClearsStartToCursorInRow()
        {
            var sb = Make(rows: 5, cols: 10);
            sb.Feed("ABCDEFGHIJ");
            sb.Feed("\e[1;6H"); // col 6 (1-indexed) → col 5 (= 'F')
            sb.Feed("\e[1K");   // erase col 0 → col 5 inclusive ('A'..'F' gone)
            Assert.That(sb.GetVisibleText().TrimStart(), Is.EqualTo("GHIJ"));
        }

        [Test]
        public void Feed_EraseWholeLine_ClearsEntireRow()
        {
            var sb = Make(rows: 3, cols: 5);
            sb.Feed("AAAAABBBBBCCCCC");
            sb.Feed("\e[2;1H"); // row 2
            sb.Feed("\e[2K");   // erase whole line
            var lines = Lines(sb.GetVisibleText());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lines[0], Is.EqualTo("AAAAA"));
                Assert.That(lines[1].Trim(), Is.Empty);
                Assert.That(lines[2], Is.EqualTo("CCCCC"));
            }
        }
    }

    // -------------------------------------------------------------------------

    [TestFixture]
    public class AnsiIgnored
    {
        [Test]
        public void Feed_SgrColors_Stripped()
        {
            var sb = Make();
            sb.Feed("\e[31;1mred\e[0m");
            Assert.That(sb.GetVisibleText(), Is.EqualTo("red"));
        }

        [Test]
        public void Feed_OscTitleWithBel_Ignored()
        {
            var sb = Make();
            sb.Feed("\e]0;Window Title\avisible");
            Assert.That(sb.GetVisibleText(), Is.EqualTo("visible"));
        }

        [Test]
        public void Feed_OscTitleWithST_Ignored()
        {
            var sb = Make();
            sb.Feed("\e]0;Title\e\\visible");
            Assert.That(sb.GetVisibleText(), Is.EqualTo("visible"));
        }

        [Test]
        public void Feed_PrivateModeSequence_Ignored()
        {
            var sb = Make();
            sb.Feed("\e[?25hvisible\e[?25l");
            Assert.That(sb.GetVisibleText(), Is.EqualTo("visible"));
        }

        [Test]
        public void Feed_UnknownEscapeSequence_Ignored()
        {
            // \e= (DECKPAM) is a 2-char escape — the '=' is consumed and discarded
            var sb = Make();
            sb.Feed("\e=hello");
            Assert.That(sb.GetVisibleText(), Is.EqualTo("hello"));
        }
    }

    // -------------------------------------------------------------------------

    [TestFixture]
    public class SplitSequences
    {
        [Test]
        public void Feed_CsiSplitAcrossChunks_HandledCorrectly()
        {
            var sb = Make(rows: 10, cols: 20);
            sb.Feed("\e[5"); // partial CSI
            sb.Feed(";10Hx"); // completes \e[5;10H → grid[4][9]
            var lines = Lines(sb.GetVisibleText());
            Assert.That(lines[4][9], Is.EqualTo('x'));
        }

        [Test]
        public void Feed_EscapeByteSplitAcrossChunks_HandledCorrectly()
        {
            var sb = Make(rows: 10, cols: 20);
            sb.Feed("\e[2J");  // clear screen
            sb.Feed("\e");     // lone escape — parser enters Escape state
            sb.Feed("[3;3Hy"); // completes CUP 3,3 → grid[2][2]
            var lines = Lines(sb.GetVisibleText());
            Assert.That(lines[2][2], Is.EqualTo('y'));
        }
    }

    // -------------------------------------------------------------------------

    [TestFixture]
    public class ScrollBehavior
    {
        [Test]
        public void Feed_LineFeedAtBottom_ScrollsRowsUp()
        {
            var sb = Make(rows: 3, cols: 5);
            // fill 3 rows then one LF → AAAAA scrolls off
            sb.Feed("AAAAA\r\nBBBBB\r\nCCCCC\r\n");
            var lines = Lines(sb.GetVisibleText());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lines[0], Is.EqualTo("BBBBB"));
                Assert.That(lines[1], Is.EqualTo("CCCCC"));
                Assert.That(lines, Has.Length.EqualTo(2)); // empty bottom row trimmed
            }
        }

        [Test]
        public void Feed_LineFeedNotAtBottom_JustMovesCursor()
        {
            var sb = Make(rows: 5, cols: 10);
            sb.Feed("A\r\nB"); // CR+LF resets col; no scroll
            var lines = Lines(sb.GetVisibleText());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lines[0], Is.EqualTo("A"));
                Assert.That(lines[1], Is.EqualTo("B"));
                Assert.That(lines, Has.Length.EqualTo(2));
            }
        }
    }

    // -------------------------------------------------------------------------

    [TestFixture]
    public class ResizeTests
    {
        [Test]
        public void Resize_ChangesGridDimensions()
        {
            var sb = Make(rows: 5, cols: 10);
            sb.Resize(10, 40);
            // write 40 chars — should fit on one row without autowrap
            sb.Feed(new string('x', 40));
            var line = sb.GetVisibleText().Split('\n')[0];
            Assert.That(line, Has.Length.EqualTo(40));
        }

        [Test]
        public void Resize_WipesGrid()
        {
            var sb = Make(rows: 5, cols: 10);
            sb.Feed("hello");
            sb.Resize(5, 20); // wipes grid; ConPTY re-emission will repopulate
            Assert.That(sb.GetVisibleText(), Is.Empty);
        }

        [Test]
        public void Resize_ZeroZero_KeepsDimensions()
        {
            var sb = Make(rows: 5, cols: 10);
            sb.Resize(0, 0); // invalid: keeps 5×10, wipes
            sb.Feed(new string('x', 10)); // fills exactly one 10-col row
            Assert.That(sb.GetVisibleText().Split('\n')[0], Has.Length.EqualTo(10));
        }

        [Test]
        public void Resize_PostResizeFeedWorksAtNewDimensions()
        {
            var sb = Make(rows: 5, cols: 10);
            sb.Feed("pre-resize");
            sb.Resize(5, 20);           // wipes at new dims
            sb.Feed("\e[1;15Hx");       // CUP to col 15 (1-indexed) — beyond old 10-col grid
            Assert.That(sb.GetVisibleText().Split('\n')[0][14], Is.EqualTo('x'));
        }

        [Test]
        public void Resize_RapidResize_FinalFeedIsCorrect()
        {
            var sb = Make(rows: 5, cols: 10);
            sb.Feed("old content");
            sb.Resize(5, 15); // wipe
            sb.Feed("more old content");
            sb.Resize(5, 20); // wipe again; final dims are 5×20
            sb.Feed("\e[1;1HAA\e[1;5HBB");
            var line = sb.GetVisibleText().Split('\n')[0];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(line[0], Is.EqualTo('A'));
                Assert.That(line[1], Is.EqualTo('A'));
                Assert.That(line[4], Is.EqualTo('B'));
                Assert.That(line[5], Is.EqualTo('B'));
            }
        }
    }

    // -------------------------------------------------------------------------

    [TestFixture]
    public class ResetTests
    {
        [Test]
        public void Reset_ClearsGridAndCursor()
        {
            var sb = Make();
            sb.Feed("hello world");
            sb.Reset();
            Assert.That(sb.GetVisibleText(), Is.Empty);
        }

        [Test]
        public void Reset_ClearsParserState_PartialEscapeDoesNotGarble()
        {
            var sb = Make();
            sb.Feed("\e["); // partial CSI — parser is in Csi state
            sb.Reset();
            sb.Feed("hello");
            Assert.That(sb.GetVisibleText(), Is.EqualTo("hello"));
        }

        [Test]
        public void Reset_AllowsFreshFeed()
        {
            var sb = Make();
            sb.Feed("buffered text");
            sb.Reset(); // clears grid and parser state
            sb.Feed("fresh");
            Assert.That(sb.GetVisibleText(), Is.EqualTo("fresh"));
        }
    }

    // -------------------------------------------------------------------------

    [TestFixture]
    public class GetVisibleTextTests
    {
        [Test]
        public void GetVisibleText_EmptyGrid_ReturnsEmptyString()
        {
            var sb = Make();
            Assert.That(sb.GetVisibleText(), Is.Empty);
        }

        [Test]
        public void GetVisibleText_TrimsTrailingEmptyRows()
        {
            var sb = Make(rows: 10, cols: 20);
            sb.Feed("only one line");
            var lines = Lines(sb.GetVisibleText());
            Assert.That(lines, Has.Length.EqualTo(1));
        }

        [Test]
        public void GetVisibleText_TrimsTrailingSpacesPerRow()
        {
            var sb = Make(rows: 5, cols: 20);
            sb.Feed("hi   "); // trailing spaces via WriteChar
            Assert.That(sb.GetVisibleText(), Is.EqualTo("hi"));
        }

        [Test]
        public void GetVisibleText_PreservesInternalBlankLines()
        {
            var sb = Make(rows: 5, cols: 10);
            sb.Feed("top\r\n\r\nbottom"); // CR+LF keeps cursor at col 0
            var lines = Lines(sb.GetVisibleText());
            Assert.That(lines, Has.Length.EqualTo(3));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lines[0], Is.EqualTo("top"));
                Assert.That(lines[1], Is.Empty);
                Assert.That(lines[2], Is.EqualTo("bottom"));
            }
        }
    }

    // -------------------------------------------------------------------------

    [TestFixture]
    public class FillFromTextTests
    {
        [Test]
        public void FillFromText_BasicLines_FillsGrid()
        {
            var sb = Make(rows: 5, cols: 20);
            sb.FillFromText("hello\nworld");
            var lines = Lines(sb.GetVisibleText());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lines[0], Is.EqualTo("hello"));
                Assert.That(lines[1], Is.EqualTo("world"));
            }
        }

        [Test]
        public void FillFromText_TruncatesLongLines()
        {
            var sb = Make(rows: 5, cols: 5);
            sb.FillFromText("ABCDEFGHIJ"); // 10 chars, 5-col grid
            Assert.That(sb.GetVisibleText(), Is.EqualTo("ABCDE"));
        }

        [Test]
        public void FillFromText_IgnoresExtraRows()
        {
            var sb = Make(rows: 2, cols: 10);
            sb.FillFromText("A\nB\nC\nD"); // 4 lines in 2-row grid
            var lines = Lines(sb.GetVisibleText());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lines, Has.Length.EqualTo(2));
                Assert.That(lines[0], Is.EqualTo("A"));
                Assert.That(lines[1], Is.EqualTo("B"));
            }
        }

        [Test]
        public void FillFromText_WipesExistingContent()
        {
            var sb = Make(rows: 5, cols: 20);
            sb.Feed("old content");
            sb.FillFromText("new");
            Assert.That(sb.GetVisibleText(), Is.EqualTo("new"));
        }

        [Test]
        public void FillFromText_TrimsCarriageReturns()
        {
            var sb = Make(rows: 5, cols: 20);
            sb.FillFromText("hello\r\nworld\r\n");
            var lines = Lines(sb.GetVisibleText());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lines[0], Is.EqualTo("hello"));
                Assert.That(lines[1], Is.EqualTo("world"));
            }
        }
    }
}
