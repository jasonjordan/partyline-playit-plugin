# Partyline plugin tests (`Partyline.Tests`)

Host-independent tests for the PlayIt Live plugin, covering the **webrtc-opus-audio** spec
property tests **5.8** (Property 8), **5.9** (Property 7), and the **5.10** integration test.

## Why these tests *mirror* plugin logic instead of referencing the plugin

The plugin project (`../PartylinePlugin.csproj`) targets **net48** and references the
**Windows-only PlayIt Live host assembly** (`PlayItLive.exe`) plus, optionally, the native
**MR-WebRTC** `mrwebrtc.dll`. That project **cannot build or run off-Windows**, so this test
project does **not** reference the plugin assembly.

Instead, the pure, host-independent algorithms under test are **reproduced verbatim** here so
the tests lock the plugin's exact behavior:

| Test mirror (this folder) | Mirrors in `PartylineScript.cs` |
| --- | --- |
| `AudioBridge.ResampleToMixerRate` | `ResampleToMixerRate` (nearest-sample, integer **floor** sample count) |
| `AudioBridge.ReadMainMixFrame` / `InputSamplesNeeded` | `TryReadMainMixFrame` (nearest-sample resample to 960-sample / 20 ms / 48 kHz mono) |
| `AudioBridge.SplitIntoFrames` | the 960-sample outbound framing the `AudioPumpLoop` performs |
| `MicToggle` | the latching `_micOn` / `SetMicOn` gate (and the task 5.6 UI button) |

If `ResampleToMixerRate` or `TryReadMainMixFrame` change in `PartylineScript.cs`, update
`AudioBridge.cs` to match.

## What each file covers

- **`AudioBridgeFramingTests.cs`** — *Feature: webrtc-opus-audio, Property 8* (Validates 5.2,
  5.3, 5.5). Framing preserves mono layout + 960 samples/frame; nearest-sample resample sample
  count = `floor(in * dst / src)` and stays in range; round-trip rate conversion preserves
  length within rounding tolerance. The true Opus codec round-trip is deferred to 5.10.
- **`MicToggleTests.cs`** — *Feature: webrtc-opus-audio, Property 7* (Validates 7.4, 7.5, 7.6).
  Latching parity: state after N activations from off is on iff N is odd; two consecutive
  toggles restore; hold/release events never change state.
- **`BindingOpusRoundTripTests.cs`** — task 5.10 (Validates 5.1, 5.2, 5.3, 5.4). The native
  binding + browser-peer end-to-end test is **`[Fact(Skip=…)]`** (needs Windows + PlayIt host +
  native `mrwebrtc.dll` / SIPSorcery runtime + a browser peer); a **real Concentus** mono Opus
  encode→decode round-trip runs here and covers the codec portion (5.2/5.3).

Property tests run **≥ 100 iterations** (`[Property(MaxTest = 200)]`).

## Running

```bash
dotnet test playit-plugin/Tests/Partyline.Tests.csproj
```

Targets `net8.0` with `RollForward=Major`, so it runs on a newer installed .NET runtime
(e.g. .NET 10) when no net8.0 runtime is present.

## Note on project nesting

This project lives under `playit-plugin/`. The plugin's `PartylinePlugin.csproj` uses the SDK
default recursive `**/*.cs` glob, which would otherwise pull these `Tests/*.cs` files into the
**Windows** plugin build. When building the plugin on Windows, exclude this folder, e.g. add to
`PartylinePlugin.csproj`:

```xml
<ItemGroup>
  <Compile Remove="Tests/**/*.cs" />
</ItemGroup>
```

(That edit is intentionally **not** made here to keep this change scoped to `Tests/`.)
