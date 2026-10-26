# Protocol Regression Checks

Build and run on Windows with the .NET Framework 4.7.2 runtime:

```powershell
dotnet build tests/AIChat.Tests.csproj -c Release
./tests/bin/Release/net472/AIChat.Tests.exe
```

The executable uses the production request builder, response parser, subtitle timing helper, VAD, and window-resize geometry. It covers escaping, nested API envelopes, malformed fields, legacy responses, cloud/Ollama thinking parameters, text/audio display durations, speech segmentation, detector rebuilding with a new threshold, and edge/corner resizing with minimum and screen-boundary constraints. It does not start Unity.

An optional live check sends one short synthetic prompt through SophNet. The key is read only from `SOPHNET_API_KEY`; it is not printed or written to files:

```powershell
./tests/bin/Release/net472/AIChat.Tests.exe --live-sophnet DeepSeek-V4.1-Flash-1017
```

The native UI still requires an in-game check: open F9, switch all six tabs, submit text, check IME input and scrollback, drag and resize the window, inspect masked credentials, edit and save settings, and cancel the memory/persona confirmation prompts. Test TTS failure and text-only replies separately from microphone/barge-in behavior. Hide UnityExplorer with F7 when testing keyboard input.

## Local Validation (2026-10-26)

- Release and Console configurations build without warnings or errors. The suite includes 40 offline checks, including eight resize-geometry cases.
- SophNet `DeepSeek-V4.1-Flash-1017` returned HTTP 200 with a valid structured reply using the production builder/parser.
- In-game text input, Enter submission, text-only reply, subtitle display, masked credentials, configuration categories, width editing, and save feedback were exercised. The user confirmed manual window dragging and reported no significant issue with the deployed frame-resize update; automated drag injection was inconclusive.
- Settings were inspected in a small 681x495 game window; the earlier chat UI was also inspected in a maximized window. Existing API, persona, and other configuration values were restored and compared with the backup.
- End-to-end microphone, ASR, barge-in, and real TTS playback remain unverified. The VAD checks use synthetic samples, not microphone audio. The new release-packaging steps have not been run on GitHub Actions.
