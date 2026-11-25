<img src="assets/icon.svg" width="64" height="64" alt="">

# LedVisual

Native desktop tray/menu bar app for **macOS** and **Windows** that captures an on-screen multiple-choice question, asks a Google AI Studio model (**Gemini Decision 1** by default via the zero-output-token Decisions API, with Gemini 3.5 Flash Lite, Gemini 3.5 Flash, and Gemma 4 selectable in Settings) for the answer, and discreetly blinks your keyboard's **Caps Lock LED** to signal the result.

## Behavior

- **Tap hotkey** (default `⌥Space` on macOS, editable to any key, modifier combo, or standalone modifier): captures a screenshot of the display under your cursor and sends it to the selected AI model.
- **Press & hold hotkey** (`≥ 350ms`): silently records deduplicated keyframes while you scroll through a long question, then sends them on release.
- **Discreet LED output** (nothing shown on screen):
  - **Single answer** (e.g. `3`): blinks the Caps Lock LED **3 short times**, then stops.
  - **Multi-select** (e.g. `1, 3`): blinks **1 time**, pauses clearly, then blinks **3 times**.
  - **Failed request / no MCQ detected**: **1 long blink** (`~1.4s`).
  - Restores your original Caps Lock state cleanly after blinking.

## Build & Run

### macOS

```bash
# Run unit tests
swift test

# Build release .app bundle (dist/LedVisual.app)
./scripts/build-app.sh
open dist/LedVisual.app

# Build compressed macOS installer DMG (dist/LedVisual-macOS-arm64.dmg)
./scripts/build-dmg.sh
```

### Windows

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows (`windows/LedVisual.sln`).

```powershell
# Run unit tests
dotnet test windows/LedVisual.sln -c Release

# Publish standalone single-file executable (LedVisual-Windows-x64.exe)
dotnet publish windows/LedVisual.App/LedVisual.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none -p:DebugSymbols=false -o publish-win
Copy-Item publish-win/LedVisual.exe LedVisual-Windows-x64.exe
```

## GitHub Releases

The [`.github/workflows/release.yml`](.github/workflows/release.yml) workflow builds and tests both platforms (`LedVisual-macOS-arm64.dmg` on `macos-15` and `LedVisual-Windows-x64.exe` on `windows-latest`) and attaches both assets to a GitHub Release.

- **Tag and push (recommended)**:
  ```bash
  git tag v1.0.0
  git push origin v1.0.0
  ```
- **Manual workflow dispatch**: In GitHub, open **Actions → Release → Run workflow**, enter the desired `tag` (defaults to `v1.0.0`), and run it.
- **Manual upload with GitHub CLI**:
  ```bash
  ./scripts/build-dmg.sh
  gh release create v1.0.0 \
    dist/LedVisual-macOS-arm64.dmg \
    LedVisual-Windows-x64.exe \
    --generate-notes
  ```

## Permissions & Security

- **API Key**: Stored securely in the macOS Keychain (`com.led-visual.app` / `google-ai-studio-api-key`) on macOS (or Windows Credential/DPAPI storage on Windows) and sent only via the `x-goog-api-key` header.
- **Accessibility (macOS)**: Required for global hotkey detection (tap vs press-and-hold).
- **Screen Recording (macOS)**: Required by macOS `ScreenCaptureKit` to capture screenshots and hold-to-record frames.
