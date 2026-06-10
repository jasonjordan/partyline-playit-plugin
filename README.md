# Partyline PlayIt Live Plugin — Co-Host Bridge

A PlayIt Live plugin that accepts WebRTC connections from remote co-hosts and mixes their audio directly into PlayIt Live's main output. No virtual cables, no external relay, no cloud services needed for audio.

## Architecture

```
Remote Co-Host (browser)
    │
    ├── WebRTC audio (Opus) ──→ Plugin decodes → PlayIt Live Main Mix
    │                                              (via IAudioPipeline.RegisterSpecialAudioStream)
    │
    └── WebRTC audio (receive) ←── Plugin captures loopback of PlayIt Live output
                                     (co-host hears DJ + music)
```

## How It Works

1. Plugin starts an HTTP listener on port 25433 (shared with PlayIt Live via HTTP.sys) at path `/partyline/`
2. Co-host opens `http://your-ip:25433/partyline/join` in their browser
3. Browser captures mic, establishes WebRTC connection directly to the plugin
4. Plugin decodes Opus audio and injects PCM samples into PlayIt Live's main mix using `RegisterSpecialAudioStream`
5. Plugin captures PlayIt Live's audio output (loopback) and sends it back to the co-host via WebRTC
6. Both parties hear each other + the music. Audience hears everything.

## Port Sharing

The plugin shares port 25433 with PlayIt Live using Windows HTTP.sys URL ACL reservations. On first run, it prompts for admin permission to register:

```
http://+:25433/partyline/
```

If port sharing fails (e.g., PlayIt Live binds exclusively), the plugin falls back to port 8080.

## Co-Host Page

The co-host webpage at `/partyline/join` provides:
- One-click connect button (requests mic permission)
- Push-to-Talk (hold to unmute, release to mute)
- Connection status indicator
- VU meter

## Building

### Prerequisites
- Visual Studio 2022 with .NET Framework 4.8 targeting pack
- PlayIt Live installed (need reference to PlayItLive.exe)

### Steps
1. Open `PartylinePlugin.csproj` in Visual Studio
2. Update the `PlayItLive` reference HintPath if needed
3. Build in Release mode
4. Copy output DLLs to PlayIt Live's `Plugins` folder:
   - `PartylinePlugin.dll`
   - `SIPSorcery.dll`
   - `SIPSorceryMedia.Windows.dll`
   - `Newtonsoft.Json.dll` (if not already present in PlayIt Live folder)

### Install
1. Copy DLLs to `C:\Program Files (x86)\PlayIt Live\Plugins\`
2. Restart PlayIt Live
3. A "Partyline" menu item appears
4. On first connect attempt, accept the UAC prompt for port sharing

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

- **SIPSorcery** — Pure C# WebRTC implementation
- **opus.dll** — Already bundled with PlayIt Live (used for Opus decoding)
- **Newtonsoft.Json** — Already bundled with PlayIt Live

## Limitations

- WebRTC requires STUN for NAT traversal (configured with Google's free STUN server)
- If both parties are behind symmetric NATs, a TURN server would be needed
- Audio quality depends on the co-host's internet connection
- Currently supports multiple co-hosts but they all mix into a single stream
