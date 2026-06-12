# Partyline PlayIt Live Plugin — Co-Host Bridge

A PlayIt Live plugin that connects remote co-hosts over a WebRTC/Opus mesh and mixes their audio directly into PlayIt Live's main output. No virtual cables and no audio relay — Cloudflare is used for signaling only, and the media flows peer-to-peer between co-hosts.

## Architecture

```
Remote Co-Host (peer)                         PlayIt Live host (this plugin: NewPlugin)
    │                                              │
    ├── Cloudflare Worker (signaling only) ────────┤   SDP offer/answer + ICE candidates
    │   (no audio passes through Cloudflare)        │
    │                                              │
    └── WebRTC audio (Opus) ◀───── peer-to-peer mesh ─────▶ decode → PlayIt Live Main Mix
                                                       (via IAudioPipeline.RegisterSpecialAudioStream)
```

The plugin entry point is the `NewPlugin` class in `PartylineScript.cs`. It builds a WebRTC/Opus
co-host mesh (`Partyline.WebRtc` namespace: `IWebRtcPeer`, `WebRtcMeshClient`, the SIPSorcery +
Concentus peer adapter, and an optional MR-WebRTC peer behind `#if PARTYLINE_MRWEBRTC`). Cloudflare
provides signaling (SDP/ICE exchange and ICE server config) only — audio never traverses Cloudflare.

## How It Works

1. The plugin (`NewPlugin`) registers a special audio stream into PlayIt Live's main mix via `IAudioPipeline.RegisterSpecialAudioStream`.
2. Co-hosts exchange WebRTC SDP offers/answers and ICE candidates through the Cloudflare signaling Worker.
3. Once signaled, Opus audio flows peer-to-peer over the WebRTC mesh — it does not pass through Cloudflare.
4. The plugin decodes incoming Opus to PCM and mixes co-host audio into PlayIt Live's main output.
5. The audience hears the DJ plus all connected co-hosts.

## Building

### Prerequisites
- Visual Studio 2022 (or the .NET SDK) with the .NET Framework 4.8 targeting pack
- PlayIt Live installed (needed for the reference to `PlayItLive.exe`)

### How the plugin is built

The plugin ships as the **compiled DLL** `PartylinePlugin.dll`, built from `PartylinePlugin.csproj`
with `NewPlugin` (in `PartylineScript.cs`) as the single plugin entry point. The third-party
packages — SIPSorcery, Concentus, and Newtonsoft.Json — resolve via NuGet during the DLL build.

> **The in-app `.pips` script editor cannot be used for this plugin.** PlayIt Live's in-app script
> editor has no third-party references, so SIPSorcery/Concentus types do not resolve there. Build and
> ship the compiled DLL instead.

The superseded older plugin (`PartylinePlugin.cs` and its helpers `CoHostManager.cs`, `CoHostPage.cs`,
`PartylineMixerForm.cs`, `PartylineStatusStrip.cs`) is kept on disk for reference but excluded from the
build via `<Compile Remove="..." />` in `PartylinePlugin.csproj`, so there is exactly one entry point.

### Steps
1. Open `PartylinePlugin.csproj` in Visual Studio (or build from the CLI with `dotnet build`).
2. Update the `PlayItLive` reference HintPath if your install path differs (defaults to `C:\Program Files (x86)\PlayIt Live\PlayItLive.exe`).
3. Build in Release mode.
4. Copy the runtime DLLs into PlayIt Live's `Plugins` folder (see Install below).

### Install
1. Copy the following DLLs to `C:\Program Files (x86)\PlayIt Live\Plugins\`:
   - `PartylinePlugin.dll` (the plugin)
   - `SIPSorcery.dll`
   - `SIPSorceryMedia.Abstractions.dll`
   - `Concentus.dll`
   - `Newtonsoft.Json.dll`
   - SIPSorcery's dependency closure (already vendored in `deps/`):
     - `BouncyCastle.Crypto.dll`
     - `DnsClient.dll`
     - `Microsoft.Extensions.Logging.Abstractions.dll`
2. Restart PlayIt Live.
3. A "Partyline" menu item appears.

## Key Interfaces Used (Undocumented)

Found via decompilation of PlayItLive.exe:

```csharp
// IAudioPipeline — accessed via App.AudioPipeline
IAudioPipelineMixer GetMainMix();
void RegisterSpecialAudioStream(string id, ISpecialAudioStream stream);

// ISpecialAudioStream — our implementation
IStreamContainer CreateStream(string sParams);

// IStreamContainer — provides the callback
StreamFunc GetStreamFunc(); // delegate int StreamFunc(int length, IntPtr buffer)
```

## Dependencies

- **SIPSorcery** (6.2.4) — Pure C# WebRTC implementation (signaling + media transport)
- **Concentus** (1.1.7) — Pure-managed C# Opus codec used by the SIPSorcery + Concentus peer adapter
- **Newtonsoft.Json** (13.0.3) — JSON serialization for signaling messages
- SIPSorcery dependency closure vendored in `deps/`: `BouncyCastle.Crypto.dll`, `DnsClient.dll`, `Microsoft.Extensions.Logging.Abstractions.dll`

## Limitations

- WebRTC requires STUN for NAT traversal (configured with Google's free STUN server)
- If both parties are behind symmetric NATs, a TURN server would be needed
- Audio quality depends on the co-host's internet connection
- Currently supports multiple co-hosts but they all mix into a single stream
