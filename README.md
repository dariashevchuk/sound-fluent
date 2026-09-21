# SoundFluent

SoundFluent is a small Windows tray app that turns clipboard text into natural Polish. Copy text in any application, press `Ctrl+Alt+P`, and SoundFluent opens with that text ready to send. Polish input is corrected; English or Ukrainian input is translated. The result keeps the original tone and register and, by default, is copied back to the clipboard automatically.

After the first result, you can keep the window open and request revisions such as `krócej` or `bardziej formalnie`. Follow-ups include the current conversation as context.

## Requirements

- Windows 10 or 11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) for development, or the .NET 9 Desktop Runtime for a framework-dependent published build
- An OpenAI API key with access to the configured model

The app is Windows-only because it uses WPF, a WinForms tray icon, Windows global-hotkey APIs, and Windows DPAPI for API-key encryption.

## Run from source

```powershell
git clone https://github.com/dariashevchuk/sound-fluent.git
cd SoundFluent
dotnet restore
dotnet run
```

The main window is hidden at startup; look for SoundFluent in the system tray. If `OPENAI_API_KEY` is not set and no key has been saved, the app prompts for one on first launch.

## Start automatically when you sign in

Run this once from the repository folder in PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-Startup.ps1
```

This publishes a release executable to `artifacts\startup` and adds a SoundFluent shortcut to your Windows Startup folder. On your next sign-in, SoundFluent starts silently in the tray; press `Ctrl+Alt+P` to open it. Your current API key and settings are still read from `%APPDATA%\SoundFluent\settings.json`. Run the script again after changing the source code to update the executable used at sign-in.

To stop automatic startup, remove **SoundFluent** from Windows **Settings → Apps → Startup**, or delete `SoundFluent.lnk` from the folder opened by `Win+R` → `shell:startup`.

You can also right-click the tray icon at any time to open the app, replace the saved API key, or quit.

## How to use it

1. Copy Polish, English, or Ukrainian text from any application.
2. Press `Ctrl+Alt+P`.
3. With **Send on open** enabled (the default), the clipboard text is submitted immediately. Otherwise, edit it and press `Enter`.
4. The Polish result appears in the transcript and, with **Copy the fix automatically** enabled (the default), replaces the clipboard contents.
5. Paste the result where you need it, or type a follow-up instruction in the same window.

Each response also has its own **Copy** button.

### Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+P` | Open SoundFluent with the current clipboard text |
| `Enter` | Send the current input |
| `Shift+Enter` | Insert a newline |
| `Ctrl+Enter` | Copy the latest reply and hide the window |
| `Esc` | Hide the window |
| `Up` / `Down` | Move within multiline input, or scroll the transcript at its edge |

Closing the window hides it instead of exiting. Use **Quit Sound Fluent** from the tray menu to stop the app.

## Conversation behavior

SoundFluent keeps one in-memory conversation:

- A non-empty clipboard value different from the text that started the current thread creates a new thread and clears the previous history.
- Opening the app again with the same source clipboard text restores the current thread.
- An empty clipboard opens the existing thread without replacing it.
- Hiding the window cancels any request still in progress.
- Conversation history is not written to disk and is lost when the app exits.

Because automatic copying replaces the clipboard with the result, reopening with `Ctrl+Alt+P` after a response will normally treat that result as a new source. Enter follow-up requests before hiding the current window if you want to preserve its context.

## Settings and API key

The gear button in the main window exposes these settings:

| Setting | Default | Purpose |
| --- | --- | --- |
| `Model` | `gpt-5.6-terra` | Model ID sent to the OpenAI Responses API; the field is editable |
| `AutoCopy` | `true` | Copy each completed response to the clipboard |
| `AutoSend` | `true` | Send new clipboard text as soon as the window opens |
| `AlwaysOnTop` | `true` | Keep SoundFluent above other windows |

Settings are saved as UTF-8 JSON at:

```text
%APPDATA%\SoundFluent\settings.json
```

The API key is resolved in this order:

1. `OPENAI_API_KEY` from the process, user, or machine environment.
2. `ProtectedApiKey` from `settings.json`.

A key entered through the tray menu is encrypted with Windows DPAPI for the current user before it is saved. It cannot be decrypted by another Windows account or after copying the settings file to another machine. The environment variable always takes precedence over the saved key.

> [!IMPORTANT]
> Clipboard text and follow-up messages are sent to the OpenAI Responses API. Do not submit sensitive text unless that is appropriate for your environment and account.

## What happens during a request

The first turn and follow-up turns intentionally use different API paths:

1. On the first turn, `OpenAiClient.PolishAsync` sends the source text with the automatic-Polish system prompt.
2. The request uses a strict JSON schema containing one `corrected` string, which keeps the rendered response free of headings, notes, and explanations.
3. Polish is corrected; non-Polish input is translated into Polish while preserving voice, intent, and formality.
4. On later turns, `OpenAiClient.FollowUpAsync` sends the full in-memory history with a revision prompt and expects plain text.
5. Responses are parsed by block type (`message` → `output_text`) instead of by array position, so Responses API reasoning blocks do not interfere with output extraction.

Requests go directly to `https://api.openai.com/v1/responses` through `HttpClient`; the project does not use the OpenAI .NET SDK.

## Build and publish

Build a release configuration:

```powershell
dotnet build -c Release
```

Create a single-file, framework-dependent Windows x64 executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

The output is written to:

```text
bin\Release\net9.0-windows\win-x64\publish\
```

The target machine must have the .NET 9 Desktop Runtime installed. For a self-contained build, change `--self-contained` to `true`; this produces a much larger output because it includes the runtime.

## Project structure

```text
App.xaml                 Shared colors, styles, and control templates
App.xaml.cs              Startup, settings load, hidden window, tray, and hotkey wiring
MainWindow.xaml          Frameless chat/settings window and resize handles
MainWindow.xaml.cs       Clipboard threads, shortcuts, API calls, and transcript rendering
SoundFluent.csproj       .NET 9 WPF/WinForms project configuration
Services/
  HotkeyManager.cs       Ctrl+Alt+P registration through the Windows API
  OpenAiClient.cs        Responses API request construction and response parsing
  Prompts.cs             First-turn prompt, follow-up prompt, and JSON schema
  Settings.cs            JSON persistence, environment lookup, and DPAPI encryption
  TrayIcon.cs            Runtime-drawn notification icon and tray menu
  WordDiff.cs            Legacy word-level LCS diff utility; currently unused
Ui/
  ApiKeyDialog.cs        Modal API-key editor
  SmoothScroll.cs        Animated mouse-wheel and keyboard scrolling
```

## Troubleshooting

- **Nothing appears after launch:** the app starts in the notification area. Double-click its tray icon or press `Ctrl+Alt+P`.
- **The hotkey does not work:** another application may already own `Ctrl+Alt+P`. SoundFluent shows a warning and remains available from the tray.
- **No request is sent:** set an API key from the tray menu or through `OPENAI_API_KEY`.
- **The API rejects the model:** enter a model ID available to your API account in the in-app settings or edit `Model` in `settings.json`.
- **A copied settings file loses its key:** DPAPI encryption is intentionally tied to the original Windows user and machine. Save the key again on the new system.
