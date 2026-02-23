# Wingman

An AI-powered PowerShell terminal assistant for Windows. Chat with an AI that executes real commands in a live terminal session alongside you.

<!-- Add a screenshot here -->

## What it is

Wingman is a WPF desktop app with a split-pane layout: an AI chat panel on the left, a GPU-rendered PowerShell terminal on the right. The AI runs commands directly in your live terminal session — the same session you can type in yourself — so it inherits your logins, variables, and working directory. It's not a chatbot that pretends to run commands; it actually runs them.

## Features

- **Side-by-side AI + terminal** — chat and terminal in one window, draggable splitter
- **GPU-rendered terminal** — Windows Terminal's ConPTY renderer via EasyWindowsTerminalControl
- **Live session execution** — commands run in your actual PowerShell session, preserving state
- **Command safety guard** — every AI command is evaluated by a fast LLM; risky ones require your approval before running
- **Interactive choices** — AI can present numbered options for single-keypress selection instead of asking in free text
- **Terminal reading** — AI can read the current terminal viewport without executing anything
- **Streaming responses** — AI replies stream in real time; Escape cancels mid-response
- **Encrypted API key storage** — key saved to `~/.wingman` using Windows DPAPI
- **Self-contained executable** — single `.exe` (~80–120 MB), no .NET runtime required on target machines
- **Dynamic title bar** — shows a live activity spinner and a generated task description while the AI works

## Requirements

**To run:**
- Windows 10 x64 or later
- An OpenAI API key

**To build from source:**
- .NET 10 SDK (in addition to the above)

## Getting started

1. Download `Wingman.exe` from [Releases](../../releases)
2. Run it — no installer needed
3. On first launch, paste your OpenAI API key when prompted and press Enter

The key is encrypted and stored at `~/.wingman`. To change it, delete that file and restart.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+Space` | Toggle focus between chat input and terminal |
| `Ctrl+Left` / `Ctrl+Right` | Resize chat panel by 5% |
| `Enter` | Send chat message |
| `Ctrl+Enter` | Insert newline in chat input |
| `Escape` | Cancel streaming AI response |
| `Shift+Enter` | Approve a pending command (in approval card) |
| `/reset` | Clear conversation history |

## How it works

### Sentinel-based command execution

Wingman overrides the PowerShell `prompt` function to emit hidden GUID sentinels (black-on-black ANSI text) around each command boundary. When the AI calls `run_command`, Wingman writes the command to the terminal and waits for the sentinels to appear in the output stream, then slices out the command output, exit code, and working directory — reliably, without polling or fragile timing.

### Command safety guard

Before any AI-requested command runs, it's evaluated by a separate fast model (`gpt-5-mini`). Commands rated `Accepted` execute immediately with no interruption. Commands rated `NeedsReview` surface an inline approval card in the chat panel — you see the command, the AI's stated purpose, and the guard's reason. Accept with `Shift+Enter`, reject with any other key. If the guard itself errors, it fails safe to `NeedsReview`.

### AI tools

The AI has three tools available:

| Tool | Description |
|---|---|
| `run_command` | Execute a PowerShell command in the live session; returns output, exit code, working directory |
| `ask_user` | Present a numbered multiple-choice card; waits for a single keypress selection |
| `read_terminal` | Read the current terminal viewport text without executing anything |

## Building & publishing

```bash
# Build
dotnet build Wingman.sln

# Run from source
dotnet run --project Wingman/Wingman.csproj

# Publish single-file exe
dotnet publish Wingman/Wingman.csproj -c Release
```

Output: `Wingman\bin\Release\net10.0-windows10.0.19041.0\win10-x64\publish\Wingman.exe`

## Tests

```bash
dotnet test Wingman.sln
```

Tests cover the ANSI VT screen buffer (`ScreenBuffer`) used by the `read_terminal` tool.
