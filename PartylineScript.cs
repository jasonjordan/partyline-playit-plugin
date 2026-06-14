using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using PlayIt.PluginEngine;

// NOTE: This type lives in PartylinePlugin.dll. PlayIt Live only loads `.pips`
// script plugins, not arbitrary DLLs, so a thin `.pips` bootstrapper loads this
// assembly by reflection and calls Run(App)/Cleanup()/Configure(). It is therefore
// NOT a Plugin<IPlayItLiveApp> subclass; the host application is injected via Run().
public class NewPlugin
{
    // Injected PlayIt Live application (passed by the .pips loader). Replaces the
    // inherited Plugin<>.App property.
    private IPlayItLiveApp _app;
    private CancellationTokenSource _cts;

    // Hardcoded signaling server (Cloudflare Worker custom domain). The signaling
    // endpoint is not user-configurable — the plugin always talks to this origin.
    private const string SignalingBaseUrl = "https://partyline.compressed.stream";

    private static string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Partyline", "partylinelog.txt");
    private SettingsManager _settingsManager = new SettingsManager();
    private string _stationName = "Partyline Co-Host";
    // Retained settings: the relay URL + station key are now REUSED to derive the
    // WebRTC mesh signaling base URL and room slug (see DeriveMeshBaseUrl /
    // DeriveRoomSlug). No new required config is introduced by the migration.
    private string _relayUrl = "";
    private string _stationKey = "";

    // Co-host sessions
    private ConcurrentDictionary<string, CoHostState> _cohosts = new ConcurrentDictionary<string, CoHostState>();

    // Authentication and audio subsystems (initialized in Run)
    private AuthenticationManager _authManager;
    private AudioMixer _audioMixer;

    // --- WebRTC mesh outbound pump (task 5.4) ---
    // Bridges the BASS-captured main mix into the WebRTC outbound track. The active
    // peer + signaling client are wired at startup in Run() (task 5.7).
    private IWebRtcPeer _webRtcPeer;
    private WebRtcMeshClient _webRtcMesh;

    // Latching mic toggle for the mesh outbound path (the UI button is task 5.6).
    // Defaults OFF; the pump only transmits while this is true (replaces PTT gating).
    private volatile bool _micOn;

    // Lifecycle flag + thread for the main-mix -> Opus outbound pump.
    private volatile bool _meshActive;
    private Thread _audioPumpThread;

    // Static accessor for mesh connection status (used by the UI panel). Repoints
    // the former relay-status accessors at the WebRTC mesh transport.
    private static WebRtcMeshClient _staticWebRtcMesh;

    /// <summary>The running mesh client (null before Run/after stop). Used by the
    /// Configure dialog to publish a Room ID change immediately.</summary>
    public static WebRtcMeshClient ActiveMesh { get { return _staticWebRtcMesh; } }

    // The active WebRTC peer transport, exposed statically so the control panel's
    // Kick button can forcibly drop a co-host's connection.
    private static IWebRtcPeer _staticPeer;

    /// <summary>
    /// Forcibly drop a co-host: close its WebRTC peer connection and clear the
    /// connected/live tracking so the host UI stops showing it and the co-host can
    /// reconnect cleanly. <paramref name="peerId"/> must be the audio/connection key
    /// (the co-host's display name), i.e. the row CohostId.
    /// </summary>
    public static void KickPeer(string peerId)
    {
        if (string.IsNullOrEmpty(peerId)) return;
        try { if (_staticPeer != null) _staticPeer.ClosePeerConnection(peerId); }
        catch (Exception ex) { LogStatic("KickPeer close error: " + ex.Message); }
        bool ignored;
        _connectedPeers.TryRemove(peerId, out ignored);
        CohostNetStat removedStat;
        _cohostNetStats.TryRemove(peerId, out removedStat);
        LogStatic("Kicked co-host '" + peerId + "': connection closed and state cleared.");
    }

    /// <summary>True while the WebRTC mesh signaling client reports a live connection.</summary>
    public static bool IsMeshConnected
    {
        get { return _staticWebRtcMesh != null && _staticWebRtcMesh.IsConnected; }
    }

    /// <summary>True once a WebRTC mesh signaling client has been wired at startup.</summary>
    public static bool IsMeshEnabled
    {
        get { return _staticWebRtcMesh != null; }
    }

    // --- Mic toggle wiring for the UI panel (task 5.6) ---
    // PartylineControlPanel does not hold a reference to the running NewPlugin
    // instance, so the instance publishes a latching-toggle hook the panel invokes
    // on click, plus a status accessor the panel polls to reflect on-air/muted
    // (mirrors the IsMeshConnected static-accessor pattern above).
    private static Action<bool> _staticMicToggle;
    private static volatile bool _staticMicOn;

    /// <summary>True while the plugin's outbound mic is ON (on air); used by the UI.</summary>
    public static bool IsMicOn
    {
        get { return _staticMicOn; }
    }

    /// <summary>
    /// Invoked by the UI's latching Mic button to flip the plugin's outbound mic
    /// state. Routes to the running instance's <see cref="SetMicOn"/>; a safe no-op
    /// if the plugin has not wired the hook yet.
    /// </summary>
    public static void RequestMicToggle(bool on)
    {
        Action<bool> hook = _staticMicToggle;
        if (hook != null) hook(on);
    }

    // --- Per-co-host network quality (traffic light) -------------------------
    // The mesh client polls the signaling server's telemetry every few seconds
    // and publishes each co-host's quality here; the UI strip reads it per row.
    public sealed class CohostNetStat
    {
        public string Quality = "none"; // good | fair | poor | none
        public int Rtt;
        public int Loss;
        public int Jitter;
    }
    private static readonly ConcurrentDictionary<string, CohostNetStat> _cohostNetStats =
        new ConcurrentDictionary<string, CohostNetStat>();

    /// <summary>Publishes a co-host's latest network-quality sample (called by the mesh client).</summary>
    public static void PublishCohostNetStat(string peerId, string quality, int rtt, int loss, int jitter)
    {
        if (string.IsNullOrEmpty(peerId)) return;
        _cohostNetStats[peerId] = new CohostNetStat { Quality = quality ?? "none", Rtt = rtt, Loss = loss, Jitter = jitter };
    }

    /// <summary>Reads a co-host's network-quality sample for the UI; null if none.</summary>
    public static CohostNetStat GetCohostNetStat(string peerId)
    {
        if (string.IsNullOrEmpty(peerId)) return null;
        CohostNetStat s;
        return _cohostNetStats.TryGetValue(peerId, out s) ? s : null;
    }

    /// <summary>
    /// Publishes a co-host's mute state to the signaling server so that co-host's
    /// web page can drive its ON-AIR sign (unmuted == live to air). Routed through
    /// the running mesh client; a safe no-op before the mesh is up.
    /// </summary>
    public static void PublishCohostMuteState(string peerId, bool muted)
    {
        try
        {
            WebRtcMeshClient mesh = _staticWebRtcMesh;
            if (mesh != null) mesh.PublishMuteState(peerId, muted);
        }
        catch { }
    }

    // --- Remote (co-host) self-mute state ------------------------------------
    // Tracks whether each co-host has their OWN microphone open. Driven by the
    // 'mic-state' signals the co-host browser broadcasts. Lets the plugin UI show
    // when a remote presenter has muted themselves (distinct from the DJ muting them).
    private static readonly ConcurrentDictionary<string, bool> _cohostMicOn =
        new ConcurrentDictionary<string, bool>();

    /// <summary>Records a co-host's own mic on/off state (from a 'mic-state' signal).</summary>
    public static void SetCohostRemoteMic(string peerId, bool micOn)
    {
        if (string.IsNullOrEmpty(peerId)) return;
        _cohostMicOn[peerId] = micOn;
    }

    /// <summary>Clears a co-host's tracked mic state (on disconnect).</summary>
    public static void ClearCohostRemoteMic(string peerId)
    {
        if (string.IsNullOrEmpty(peerId)) return;
        bool ignored;
        _cohostMicOn.TryRemove(peerId, out ignored);
    }

    /// <summary>True when a co-host is known to have muted their own microphone.</summary>
    public static bool IsCohostSelfMuted(string peerId)
    {
        if (string.IsNullOrEmpty(peerId)) return false;
        bool micOn;
        return _cohostMicOn.TryGetValue(peerId, out micOn) && !micOn;
    }

    // Return audio (BASS mixer capture)
    private int _mixerHandle;
    private int _mixerChannels = 2; // default stereo, updated from BASS_ChannelGetInfo
    private int _mixerFreq = 44100; // default 44.1kHz, updated from BASS_ChannelGetInfo
    private bool _mixerIsFloat;     // true if the DSP buffer is 32-bit float, else 16-bit PCM
    private byte[] _returnBuffer = new byte[44100 * 2 * 4]; // 4 seconds of 16-bit mono at 44.1kHz
    private int _returnWritePos;
    private int _returnReadPos;
    private int _returnAvailable;
    private readonly object _returnLock = new object();
    private Thread _captureThread;
    private volatile bool _captureRunning;

    // DIAGNOSTIC: capture-rate measurement (samples/sec produced by the mixer DSP).
    private long _dspMonoSamplesSinceLog;
    private System.Diagnostics.Stopwatch _dspRateClock;
    // Measured actual capture rate (mono samples/sec of wall clock). The mixer DSP
    // tap can deliver at a rate that does NOT match BASS_ChannelGetInfo's reported
    // freq, so we drive the outbound resample from THIS measured value.
    private volatile int _captureRateHz;
    // Peak of the captured (sanitized) signal over the window, for the DIAG line.
    private float _dspCleanPeak;
    // DIAGNOSTIC: outbound pump push-rate measurement.
    private long _pumpFramesSinceLog;
    private System.Diagnostics.Stopwatch _pumpRateClock;

    // Reusable DSP scratch buffers (touched only on the BASS playback thread). The
    // DSP callback used to allocate fresh float/short/byte arrays on every buffer,
    // which created steady garbage on the audio thread; the resulting GC pauses
    // showed up as periodic warble (slight speed-up/slow-down) in the main playout.
    // Reusing these (growing only when needed) keeps the playback thread allocation-free.
    private float[] _dspFloatScratch;
    private short[] _dspShortScratch;
    private byte[] _dspPcm16Scratch;

    /// <summary>The source sample rate to resample outbound audio FROM: the measured
    /// capture rate once known, else the reported mixer freq, else 44100.</summary>
    private int CaptureSourceRate()
    {
        int r = _captureRateHz;
        if (r >= 8000 && r <= 96000) return r;
        return _mixerFreq > 0 ? _mixerFreq : 44100;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BASS_CHANNELINFO
    {
        public int freq;
        public int chans;
        public int flags;
        public int ctype;
        public int origres;
        public int plugin;
        public int sample;
        public IntPtr filename;
    }

    [DllImport("bass", CallingConvention = CallingConvention.StdCall)]
    private static extern bool BASS_ChannelGetInfo(int handle, ref BASS_CHANNELINFO info);

    [DllImport("bass", CallingConvention = CallingConvention.StdCall)]
    private static extern int BASS_ChannelGetData(int handle, [Out] byte[] buffer, int length);

    [DllImport("bass", CallingConvention = CallingConvention.StdCall)]
    private static extern int BASS_ChannelGetData(int handle, [Out] float[] buffer, int length);

    // DSP callback for non-consuming mixer tap
    private delegate void DSPPROC(int handle, int channel, IntPtr buffer, int length, IntPtr user);

    [DllImport("bass", CallingConvention = CallingConvention.StdCall)]
    private static extern int BASS_ChannelSetDSP(int handle, DSPPROC proc, IntPtr user, int priority);

    [DllImport("bass", CallingConvention = CallingConvention.StdCall)]
    private static extern bool BASS_ChannelRemoveDSP(int handle, int dsp);

    private DSPPROC _dspDelegate;
    private System.Runtime.InteropServices.GCHandle _dspGcHandle;
    private int _dspHandle;

    // --- Co-host mix -> main mix injection -----------------------------------
    // PlayIt's RegisterSpecialAudioStream is NOT auto-pulled into the broadcast
    // mix (confirmed: FillAudioBuffer is never called), so co-host audio never
    // reached air. Instead we create our own BASS decode stream (the co-host mix)
    // and add it directly to PlayIt's main-mix BASSmix channel — the same channel
    // the outbound DSP tap reads. BASSmix lives in bassmix.dll, loaded in-process.
    private delegate int STREAMPROC(int handle, IntPtr buffer, int length, IntPtr user);

    [DllImport("bass", CallingConvention = CallingConvention.StdCall)]
    private static extern int BASS_StreamCreate(int freq, int chans, int flags, STREAMPROC proc, IntPtr user);

    [DllImport("bass", CallingConvention = CallingConvention.StdCall)]
    private static extern bool BASS_StreamFree(int handle);

    [DllImport("bass", CallingConvention = CallingConvention.StdCall)]
    private static extern int BASS_ErrorGetCode();

    [DllImport("bassmix", CallingConvention = CallingConvention.StdCall)]
    private static extern bool BASS_Mixer_StreamAddChannel(int handle, int channel, int flags);

    [DllImport("bassmix", CallingConvention = CallingConvention.StdCall)]
    private static extern bool BASS_Mixer_ChannelRemove(int handle);

    private const int BASS_STREAM_DECODE = 0x200000;

    private STREAMPROC _mixStreamProc;
    private System.Runtime.InteropServices.GCHandle _mixStreamGcHandle;
    private int _mixStreamHandle;

    private static void Log(string message)
    {
        try
        {
            string dir = Path.GetDirectoryName(_logPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    public static void LogStatic(string message)
    {
        Log(message);
    }

    // Guards one-time SIPSorcery preload.
    private static bool _sipPreloadDone;

    /// <summary>
    /// Diagnostic only: logs any SIPSorcery DLLs found in the host app directory.
    /// The actual 6.2.4 engine now runs in an isolated child AppDomain
    /// (IsolatedSipSorceryPeer), so we intentionally do NOT load SIPSorcery into
    /// the default domain here — doing so just duplicated the assembly and added
    /// memory pressure in the 32-bit host (it contributed to a stack/address-space
    /// exhaustion crash). Kept as a one-time diagnostic.
    /// </summary>
    internal static void PreloadBundledSipSorcery()
    {
        if (_sipPreloadDone) return;
        _sipPreloadDone = true;

        // Diagnostic: report any SIPSorcery copies sitting in the host directory.
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
            if (baseDir.Length > 0 && Directory.Exists(baseDir))
            {
                foreach (var f in Directory.GetFiles(baseDir, "SIPSorcery*.dll", SearchOption.TopDirectoryOnly))
                {
                    string ver = "?";
                    try { ver = System.Diagnostics.FileVersionInfo.GetVersionInfo(f).FileVersion; } catch { }
                    LogStatic("[SIPSorcery] host dir copy: " + f + " (v" + ver + ")");
                }
            }
        }
        catch { }
    }

    // (former eager-preload body removed: the engine runs in the isolated domain)

    /// <summary>
    /// The short (<=6 char) invite code for a co-host account, derived from its
    /// stable hash. This is the ONLY token in the co-host URL
    /// (https://partyline.compressed.stream/&lt;code&gt;) and doubles as the invite id.
    /// </summary>
    public static string CohostCode(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return "";
        string h = hash.Trim().ToLowerInvariant();
        return h.Length > 6 ? h.Substring(0, 6) : h;
    }

    public void Run(IPlayItLiveApp app)
    {
        try
        {
            _cts = new CancellationTokenSource();
            _app = app;
            Log("Plugin starting...");

            // --- SIPSorcery version pinning (Task 9 root-cause fix) -------------
            // PlayIt Live ships its OWN SIPSorcery (observed: 5.2.3.0) in its app
            // directory. Default CLR probing finds that copy by simple name BEFORE
            // Costura's AssemblyResolve fallback can hand over our bundled 6.2.4,
            // so the wrong (TURN-weak) version binds and ICE fails on CGNAT/Starlink.
            // We pre-load the embedded 6.2.4 bytes into the process here, before any
            // SIPSorcery type is touched. Because the assembly is unsigned, later
            // references bind by simple name to this already-loaded 6.2.4.
            PreloadBundledSipSorcery();

            // Load settings and initialize subsystems
            List<CoHostAccount> accounts = _settingsManager.Load();
            _stationName = _settingsManager.LoadStationName();
            _relayUrl = _settingsManager.LoadRelayUrl();
            _stationKey = _settingsManager.LoadStationKey();

            Log("Relay URL (reused for mesh base): " + _relayUrl);

            _authManager = new AuthenticationManager();
            _authManager.SetAccounts(accounts);

            _audioMixer = new AudioMixer();

            Log("Initialized AuthenticationManager and AudioMixer.");

            // --- WebRTC mesh transport (Requirement 6.4) ---
            // Establish co-host audio transport through the WebRTC binding instead of
            // the deprecated WebSocket raw-PCM relay.
            //
            // Adapter selection: the MR-WebRTC primary adapter (Google libwebrtc) lives
            // behind the PARTYLINE_MRWEBRTC build symbol (OFF by default). When that
            // symbol is defined AND the native engine loads, construct it; otherwise
            // fall back to the pure-managed SIPSorcery + Concentus adapter (the default
            // path on this build).
            IWebRtcPeer peer = null;
#if PARTYLINE_MRWEBRTC
            if (MixedRealityWebRtcLoader.TryLoad())
            {
                try
                {
                    peer = new Partyline.WebRtc.MixedRealityWebRtcPeer();
                    Log("WebRTC binding: Microsoft.MixedReality.WebRTC (libwebrtc primary).");
                }
                catch (Exception ex)
                {
                    Log("MR-WebRTC construction failed; falling back to SIPSorcery: " + ex.Message);
                    peer = null;
                }
            }
            else
            {
                Log("MR-WebRTC native engine unavailable; falling back to SIPSorcery.");
            }
#endif
            if (peer == null)
            {
                // Default path: run SIPSorcery 6.2.4 in a SEPARATE 64-bit process
                // (clean address space, normal thread stacks) — sidesteps the host's
                // 5.2.3 conflict AND the 32-bit crash. Falls back to the isolated
                // AppDomain, then in-process, only if the helper can't be launched.
                try
                {
                    peer = new Partyline.WebRtc.OutOfProcessWebRtcPeer();
                    Log("WebRTC binding: SIPSorcery 6.2.4 (out-of-process host).");
                }
                catch (Exception ex)
                {
                    Log("Out-of-process host failed (" + ex.Message + "); trying isolated AppDomain.");
                    try
                    {
                        peer = new Partyline.WebRtc.IsolatedSipSorceryPeer();
                        Log("WebRTC binding: SIPSorcery 6.2.4 (isolated AppDomain).");
                    }
                    catch (Exception ex2)
                    {
                        Log("Isolated SIPSorcery domain failed (" + ex2.Message + "); using in-process SIPSorcery.");
                        peer = new Partyline.WebRtc.SipSorceryWebRtcPeer();
                        Log("WebRTC binding: SIPSorcery + Concentus (in-process fallback).");
                    }
                }
            }

            _webRtcPeer = peer;

            // Hook decoded remote audio -> AudioMixer (task 5.5) and connection-state logging.
            WireWebRtcPeer(_webRtcPeer);
            _webRtcPeer.OnConnectionStateChanged = OnWebRtcConnectionStateChanged;

            // Derive the mesh signaling base URL + room slug from the EXISTING settings
            // so no new required config is introduced:
            //   * baseUrl: the configured relay URL converted to an https origin
            //     (wss:// -> https://, ws:// -> http://, bare host -> https://) with any
            //     path/query/fragment stripped. WebRtcMeshClient appends the /api/...
            //     paths itself.
            //   * slug:    the station key when set, else the station name, normalised to
            //     a URL-safe room slug.
            // If no relay/base URL is configured, log and skip starting the mesh (the peer
            // is still ready; we just don't open signaling) rather than crashing.
            // Signaling transport is fixed (hardcoded origin) and the room identity
            // is auto-generated on first run — there is no user-configurable URL,
            // room name, or room password. The plugin claims its own room with a
            // private DJ key and authorizes co-hosts via per-user invite passwords.
            string[] ident = _settingsManager.EnsureRoomIdentity();
            string[] metaNames = _settingsManager.LoadMeta(); // [roomName, stationName, djName]
            // Append the DJ's optional custom Room ID as a 4th element so the mesh
            // client can publish it to the server (provision + invites).
            string[] meta = new string[] { metaNames[0], metaNames[1], metaNames[2], _settingsManager.LoadRoomCode() };
            string meshBaseUrl = SignalingBaseUrl;
            string meshSlug = ident[0];   // public room id (appears in invite URLs)
            string djKey = ident[1];      // private DJ credential (never shared)

            if (meshSlug != null && meshSlug.Length > 0 && djKey != null && djKey.Length > 0)
            {
                // The client auto-provisions/claims the room with djKey, authenticates
                // as the DJ, publishes the co-host accounts as invites, and publishes
                // the display metadata (room/station/DJ names) for the co-host page.
                _webRtcMesh = new WebRtcMeshClient(meshBaseUrl, meshSlug, "plugin", _webRtcPeer, djKey, accounts, meta);
                _staticWebRtcMesh = _webRtcMesh;
                _staticPeer = _webRtcPeer;
                _webRtcMesh.Start(_cts.Token);
                Log("WebRTC mesh signaling started: base=" + meshBaseUrl + " room=" + meshSlug);
            }
            else
            {
                Log("Room identity unavailable; WebRTC mesh signaling skipped (peer ready).");
            }

            // Register audio stream into PlayIt Live's main mix
            Log("Registering special audio stream 'partyline'...");
            _app.AudioPipeline.RegisterSpecialAudioStream("partyline", new PartylineStream(this));
            Log("Special audio stream registered. PlayIt Live will call CreateStream/GetStreamFunc internally.");

            // Capture mixer handle for return audio (delayed — mixer may not be ready at startup)
            var captureStartThread = new Thread(() => StartCaptureWhenReady()) { IsBackground = true, Name = "PartylineCaptureInit" };
            captureStartThread.Start();

            // Start the WebRTC outbound pump (task 5.4). The peer is wired above; the
            // pump only transmits while the mic toggle is ON.
            _meshActive = true;
            _audioPumpThread = new Thread(AudioPumpLoop);
            _audioPumpThread.IsBackground = true;
            _audioPumpThread.Name = "PartylineAudioPump";
            _audioPumpThread.Start();
            Log("Audio pump thread started (transmits while mic on).");

            // Register embedded UI control
            Log("Registering UI control...");
            // Publish the latching mic-toggle hook + initial state so the panel's
            // Mic button (task 5.6) can drive _micOn and reflect on-air/muted.
            _staticMicOn = _micOn;
            _staticMicToggle = SetMicOn;
            _app.RegisterUserControl(() => new PartylineControlPanel(_audioMixer, _authManager, accounts, _settingsManager), UserControlLocation.AboveTrackGroupSelector, 100);
            Log("UI control registered.");

            Log("Plugin started successfully.");

            // Keep plugin running - this blocks until plugin is stopped
            _app.WaitForPluginStop();
        }
        catch (Exception ex)
        {
            Log("FATAL in Run(): " + ex.ToString());
        }
    }

    // Called by PlayIt Live's audio pipeline when it needs samples
    // Now delegates to AudioMixer
    private bool _fillBufferFirstCallLogged = false;
    private bool _fillBufferNonSilenceLogged = false;

    internal int FillAudioBuffer(int length, IntPtr buffer)
    {
        if (!_fillBufferFirstCallLogged)
        {
            _fillBufferFirstCallLogged = true;
            Log("FillAudioBuffer called for the first time");
        }

        if (_audioMixer != null)
        {
            int result = _audioMixer.FillOutputBuffer(length, buffer);

            if (!_fillBufferNonSilenceLogged && result > 0)
            {
                // Check if any non-zero samples in the buffer
                int sampleCount = length / 2;
                short[] check = new short[sampleCount];
                Marshal.Copy(buffer, check, 0, sampleCount);
                for (int i = 0; i < sampleCount; i++)
                {
                    if (check[i] != 0)
                    {
                        _fillBufferNonSilenceLogged = true;
                        Log("FillAudioBuffer producing non-silence audio");
                        break;
                    }
                }
            }

            return result;
        }

        // Fallback: output silence if mixer not initialized
        int fallbackSampleCount = length / 2;
        var silence = new short[fallbackSampleCount];
        Marshal.Copy(silence, 0, buffer, fallbackSampleCount);
        return length;
    }

    public int GetCoHostCount() { return _cohosts.Count; }

    /// <summary>
    /// BASS STREAMPROC: PlayIt's main-mix mixer pulls the co-host mix from here
    /// (mono 16-bit PCM at 44.1kHz). Delegates to the AudioMixer, which sums every
    /// live+unmuted co-host (and writes silence when none are active). This is what
    /// actually puts co-host audio on air, since the special-stream path is never
    /// pulled by PlayIt.
    /// </summary>
    private int MixStreamProc(int handle, IntPtr buffer, int length, IntPtr user)
    {
        try
        {
            if (_audioMixer != null) return _audioMixer.FillOutputBuffer(length, buffer);
        }
        catch { }
        return length;
    }

    private void StartCaptureWhenReady()
    {
        Log("Waiting for mixer to become available...");
        for (int attempt = 0; attempt < 120; attempt++) // Try for up to 2 minutes
        {
            if (_cts.IsCancellationRequested) return;
            Thread.Sleep(2000);

            try
            {
                var mainMix = _app.AudioPipeline.GetMainMix();
                if (mainMix == null) continue;

                int handle = mainMix.GetMixerChannelHandle();
                if (handle != 0)
                {
                    _mixerHandle = handle;
                    Log("Mixer channel handle acquired: " + _mixerHandle);

                    // Query mixer format for proper audio conversion
                    BASS_CHANNELINFO info = new BASS_CHANNELINFO();
                    if (BASS_ChannelGetInfo(_mixerHandle, ref info))
                    {
                        _mixerFreq = info.freq > 0 ? info.freq : 44100;
                        _mixerChannels = info.chans > 0 ? info.chans : 2;
                        // BASS_SAMPLE_FLOAT = 0x100. If absent, the channel is 16-bit PCM.
                        _mixerIsFloat = (info.flags & 0x100) != 0;
                        Log("Mixer format: freq=" + info.freq + " chans=" + info.chans
                            + " flags=" + info.flags + " => " + (_mixerIsFloat ? "32-bit float" : "16-bit PCM"));
                    }
                    else
                    {
                        Log("BASS_ChannelGetInfo failed, assuming stereo 44.1kHz float");
                    }

                    _captureRunning = true;
                    
                    // Register DSP callback to tap mixer audio without consuming it
                    _dspDelegate = new DSPPROC(DspCallback);
                    _dspGcHandle = System.Runtime.InteropServices.GCHandle.Alloc(_dspDelegate);
                    _dspHandle = BASS_ChannelSetDSP(_mixerHandle, _dspDelegate, IntPtr.Zero, 0);
                    if (_dspHandle != 0)
                    {
                        Log("DSP callback registered on mixer (handle=" + _dspHandle + "). Return audio active.");
                    }
                    else
                    {
                        Log("WARNING: BASS_ChannelSetDSP failed. Return audio disabled.");
                    }

                    // Inject the co-host mix directly INTO the main mix so it reaches
                    // air. A mono 16-bit 44.1kHz decode stream pulled by the mixer via
                    // FillOutputBuffer; the mixer up/resamples to its own format.
                    try
                    {
                        _mixStreamProc = new STREAMPROC(MixStreamProc);
                        _mixStreamGcHandle = System.Runtime.InteropServices.GCHandle.Alloc(_mixStreamProc);
                        _mixStreamHandle = BASS_StreamCreate(44100, 1, BASS_STREAM_DECODE, _mixStreamProc, IntPtr.Zero);
                        if (_mixStreamHandle == 0)
                        {
                            Log("WARNING: BASS_StreamCreate for co-host mix failed (err=" + BASS_ErrorGetCode() + "). Co-host audio will not reach air.");
                        }
                        else if (!BASS_Mixer_StreamAddChannel(_mixerHandle, _mixStreamHandle, 0))
                        {
                            Log("WARNING: BASS_Mixer_StreamAddChannel failed (err=" + BASS_ErrorGetCode() + "). Co-host audio will not reach air.");
                        }
                        else
                        {
                            Log("Co-host mix added to main mix (stream=" + _mixStreamHandle + "). Co-host audio now routes to air.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("ERROR adding co-host mix to main mix: " + ex.Message);
                    }

                    Log("Return audio capture ready.");
                    return;
                }
            }
            catch (Exception ex)
            {
                if (attempt == 0) Log("Mixer not ready yet: " + ex.Message);
            }
        }
        Log("WARNING: Mixer handle never became valid, return audio disabled.");
    }

    /// <summary>
    /// BASS DSP callback — called by BASS for every audio buffer that passes through the mixer.
    /// This does NOT consume data; it just observes/copies it.
    /// Buffer contains float samples in the mixer's format (stereo 44100Hz float).
    /// </summary>
    private bool _dspFirstCallLogged;

    private void DspCallback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (!_meshActive) return;

        try
        {
            int channels = _mixerChannels > 0 ? _mixerChannels : 2;
            if (channels < 1) channels = 1;

            // The PlayIt main-mix tap here is 16-bit PCM (BASS_SAMPLE_FLOAT absent),
            // NOT 32-bit float. Read per the detected format. PCM is interleaved Int16
            // at the full mixer rate (e.g. 44100 stereo). All scratch buffers are
            // reused (no per-callback allocation) to keep the audio thread GC-free.
            int monoSamples;
            byte[] pcm16;

            if (_mixerIsFloat)
            {
                int floatSamples = length / 4;
                monoSamples = floatSamples / channels;
                if (_dspFloatScratch == null || _dspFloatScratch.Length < floatSamples)
                    _dspFloatScratch = new float[floatSamples];
                float[] fd = _dspFloatScratch;
                Marshal.Copy(buffer, fd, 0, floatSamples);
                int need = monoSamples * 2;
                if (_dspPcm16Scratch == null || _dspPcm16Scratch.Length < need)
                    _dspPcm16Scratch = new byte[need];
                pcm16 = _dspPcm16Scratch;
                for (int i = 0; i < monoSamples; i++)
                {
                    float sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int idx = i * channels + ch;
                        if (idx < floatSamples)
                        {
                            float v = fd[idx];
                            if (float.IsNaN(v) || float.IsInfinity(v) || v > 4f || v < -4f) v = 0f;
                            sum += v;
                        }
                    }
                    float mono = sum / channels;
                    if (mono > 1f) mono = 1f;
                    if (mono < -1f) mono = -1f;
                    short s16 = (short)(mono * 32767f);
                    pcm16[i * 2] = (byte)(s16 & 0xFF);
                    pcm16[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
                }
            }
            else
            {
                int totalSamples = length / 2;          // 16-bit samples in the buffer
                monoSamples = totalSamples / channels;  // interleaved frames
                if (_dspShortScratch == null || _dspShortScratch.Length < totalSamples)
                    _dspShortScratch = new short[totalSamples];
                short[] pin = _dspShortScratch;
                Marshal.Copy(buffer, pin, 0, totalSamples);
                int need = monoSamples * 2;
                if (_dspPcm16Scratch == null || _dspPcm16Scratch.Length < need)
                    _dspPcm16Scratch = new byte[need];
                pcm16 = _dspPcm16Scratch;
                for (int i = 0; i < monoSamples; i++)
                {
                    int sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int idx = i * channels + ch;
                        if (idx < totalSamples) sum += pin[idx];
                    }
                    int mono = sum / channels;          // average stays within Int16 range
                    if (mono > 32767) mono = 32767;
                    if (mono < -32768) mono = -32768;
                    short s16 = (short)mono;
                    pcm16[i * 2] = (byte)(s16 & 0xFF);
                    pcm16[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
                }
            }

            int pcmBytes = monoSamples * 2;
            lock (_returnLock)
            {
                for (int i = 0; i < pcmBytes; i++)
                {
                    _returnBuffer[_returnWritePos] = pcm16[i];
                    _returnWritePos = (_returnWritePos + 1) % _returnBuffer.Length;
                    if (_returnAvailable < _returnBuffer.Length)
                    {
                        _returnAvailable++;
                    }
                    else
                    {
                        // Buffer full: drop oldest byte by advancing the read pointer so
                        // the available count never reports stale/overwritten data.
                        _returnReadPos = (_returnReadPos + 1) % _returnBuffer.Length;
                    }
                }
            }

            // Measure the actual capture (frame) rate to drive the outbound resample.
            _dspMonoSamplesSinceLog += monoSamples;
            if (_dspRateClock == null) _dspRateClock = System.Diagnostics.Stopwatch.StartNew();
            long dspMs = _dspRateClock.ElapsedMilliseconds;
            if (dspMs >= 1000)
            {
                int rate = (int)(_dspMonoSamplesSinceLog * 1000L / dspMs);
                if (rate >= 8000 && rate <= 96000)
                    _captureRateHz = _captureRateHz > 0 ? (_captureRateHz * 3 + rate) / 4 : rate;
                _dspMonoSamplesSinceLog = 0;
                _dspRateClock.Restart();
            }
        }
        catch { }
    }

    internal byte[] ReadReturnAudio(int maxBytes)
    {
        lock (_returnLock)
        {
            if (_returnAvailable <= 0) return new byte[0];

            int toRead = Math.Min(_returnAvailable, maxBytes);
            byte[] result = new byte[toRead];

            for (int i = 0; i < toRead; i++)
            {
                result[i] = _returnBuffer[_returnReadPos];
                _returnReadPos = (_returnReadPos + 1) % _returnBuffer.Length;
            }

            _returnAvailable -= toRead;
            return result;
        }
    }

    /// <summary>Bytes of captured main-mix audio currently buffered (for the pump's
    /// full-frame drain guard).</summary>
    internal int ReturnAudioAvailableBytes()
    {
        lock (_returnLock) { return _returnAvailable; }
    }

    /// <summary>Drop the oldest captured audio beyond <paramref name="maxBytes"/> so
    /// buffered outbound latency stays bounded if PlayIt ever renders the main mix
    /// ahead of real time in a burst. Normal (real-time-paced) operation rarely
    /// triggers this; it is a safety cap, not the steady-state path.</summary>
    internal void TrimReturnAudioBacklog(int maxBytes)
    {
        if (maxBytes < 0) maxBytes = 0;
        lock (_returnLock)
        {
            if (_returnAvailable <= maxBytes) return;
            int drop = _returnAvailable - maxBytes;
            _returnReadPos = (_returnReadPos + drop) % _returnBuffer.Length;
            _returnAvailable -= drop;
        }
    }

    public void MuteAll() { foreach (var c in _cohosts.Values) c.IsMuted = true; }
    public void UnmuteAll() { foreach (var c in _cohosts.Values) c.IsMuted = false; }
    public void KickAll() { _cohosts.Clear(); }

    public void Cleanup()
    {
        if (_cts != null) _cts.Cancel();

        _captureRunning = false;
        if (_captureThread != null)
        {
            try { _captureThread.Join(2000); } catch { }
        }

        // Remove DSP callback
        if (_dspHandle != 0 && _mixerHandle != 0)
        {
            try { BASS_ChannelRemoveDSP(_mixerHandle, _dspHandle); } catch { }
            _dspHandle = 0;
        }
        if (_dspGcHandle.IsAllocated)
        {
            _dspGcHandle.Free();
        }

        // Remove + free the co-host mix injection stream from the main mix.
        if (_mixStreamHandle != 0)
        {
            try { BASS_Mixer_ChannelRemove(_mixStreamHandle); } catch { }
            try { BASS_StreamFree(_mixStreamHandle); } catch { }
            _mixStreamHandle = 0;
        }
        if (_mixStreamGcHandle.IsAllocated)
        {
            _mixStreamGcHandle.Free();
        }

        _meshActive = false;
        if (_audioPumpThread != null)
        {
            try { _audioPumpThread.Join(2000); } catch { }
        }

        // Tear down the WebRTC mesh signaling/peer. Stop() sends a best-effort 'leave'
        // and closes the signaling connection; then dispose/close the peer connections.
        if (_webRtcMesh != null)
        {
            try { _webRtcMesh.Stop(); }
            catch (Exception ex) { Log("Error stopping WebRtcMeshClient: " + ex.Message); }
        }
        if (_webRtcPeer != null)
        {
            try { _webRtcPeer.CloseAll(); }
            catch (Exception ex) { Log("Error closing WebRTC peer: " + ex.Message); }
            // Unload the isolated SIPSorcery AppDomain if one is in use.
            var disposablePeer = _webRtcPeer as IDisposable;
            if (disposablePeer != null) { try { disposablePeer.Dispose(); } catch { } }
        }

        if (_authManager != null)
        {
            _authManager.InvalidateAllSessions();
        }

        KickAll();
        Log("Plugin cleanup completed.");
    }

    public void Configure()
    {
        var form = new PartylineConfigForm(_settingsManager, _webRtcMesh);
        if (form.ShowDialog() == DialogResult.OK)
        {
            // Push saved co-host accounts + display names to the running mesh so
            // invite links and room/station/DJ names take effect without a restart.
            try
            {
                List<CoHostAccount> accounts = _settingsManager.Load();
                string[] m = _settingsManager.LoadMeta();
                string[] meta = new string[] { m[0], m[1], m[2], _settingsManager.LoadRoomCode() };
                if (_webRtcMesh != null) _webRtcMesh.RepublishConfig(accounts, meta);
            }
            catch (Exception ex)
            {
                Log("Config republish error: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Inbound remote-audio handler (task 5.5) — the remote Opus → <see cref="AudioMixer"/>
    /// ingest bridge that replaced the deprecated relay's PCM-receive loop. Called by the
    /// active <see cref="IWebRtcPeer"/> (its <c>OnRemoteAudioFrame</c> callback) with decoded
    /// remote PCM16 for a single peer. The adapter contract delivers mono samples
    /// (<paramref name="sampleCount"/> mono PCM16 samples at <paramref name="sampleRate"/>,
    /// e.g. 48 kHz from Opus).
    ///
    /// We resample to the PlayIt mixer rate (<c>_mixerFreq</c>, e.g. 44.1 kHz) using the same
    /// nearest-sample approach the rest of the bridge uses, then ingest into the per-co-host
    /// mixer channel via the existing <see cref="AudioMixer.EnsureCoHost"/> /
    /// <see cref="AudioMixer.IngestAudio"/> path. <see cref="AudioMixer"/> sums per-co-host
    /// channels into the buffer <c>PartylineStream</c> feeds back into the PlayIt main mix, so
    /// inbound remote voice reaches the broadcast exactly as before — only the decode source
    /// changed from raw-PCM-over-WebSocket to Opus-over-WebRTC.
    /// (Requirement 5.3)
    /// </summary>
    private bool _firstRemoteFrameLogged;
    private void OnRemoteAudioFrame(string peerId, short[] pcm, int sampleCount, int sampleRate)
    {
        if (_audioMixer == null || peerId == null || pcm == null || sampleCount <= 0) return;

        int srcRate = sampleRate > 0 ? sampleRate : 48000;
        int dstRate = _mixerFreq > 0 ? _mixerFreq : 44100;

        byte[] mixerPcm = ResampleToMixerRate(pcm, sampleCount, srcRate, dstRate);
        if (mixerPcm.Length == 0) return;

        if (!_firstRemoteFrameLogged)
        {
            _firstRemoteFrameLogged = true;
            Log("First decoded remote audio frame ingested into mixer from " + peerId
                + " (" + sampleCount + " samples @ " + srcRate + "Hz -> " + dstRate + "Hz)");
        }

        _audioMixer.EnsureCoHost(peerId);
        _audioMixer.IngestAudio(peerId, mixerPcm, mixerPcm.Length);
    }

    /// <summary>
    /// Wires a peer's decoded-remote-frame callback to <see cref="OnRemoteAudioFrame"/> so
    /// inbound Opus reaches the mixer. Intended to be called by the future startup wiring task
    /// (along with the other <see cref="IWebRtcPeer"/> callbacks); defined here so 5.5 has a
    /// single place that owns the inbound hookup. Safe to call before <c>_webRtcPeer</c> is
    /// otherwise used.
    /// </summary>
    private void WireWebRtcPeer(IWebRtcPeer peer)
    {
        if (peer == null) return;
        _webRtcPeer = peer;
        peer.OnRemoteAudioFrame = OnRemoteAudioFrame;
    }

    /// <summary>
    /// Logs WebRTC per-peer connection-state transitions (e.g. "failed" for Req 1.5).
    /// Wired to <c>IWebRtcPeer.OnConnectionStateChanged</c> at startup.
    /// </summary>
    private void OnWebRtcConnectionStateChanged(string peerId, string state)
    {
        Log("WebRTC connection state [" + (peerId ?? "?") + "]: " + (state ?? "?"));
        if (string.IsNullOrEmpty(peerId)) return;
        string s = (state ?? "").ToLowerInvariant();
        if (s == "connected" || s == "completed")
        {
            _connectedPeers[peerId] = true;
            // Put the connected presenter on air automatically, keyed by the SAME
            // peerId that receives audio, so it is actually mixed into the main output
            // (FillOutputBuffer only sums IsLive co-hosts). The DJ can still mute.
            if (_audioMixer != null)
            {
                _audioMixer.EnsureCoHost(peerId);
                // Live (in the mix path) but muted by default — the DJ unmutes when
                // they want the presenter on air.
                _audioMixer.SetLive(peerId, true);
                _audioMixer.SetMuted(peerId, true);
            }
            // Tell this co-host's web page it is connected but muted (sign off).
            if (_webRtcMesh != null) _webRtcMesh.PublishMuteState(peerId, true);
            // Co-hosts join with their own mic muted; assume self-muted until a
            // mic-state signal says otherwise so the UI starts in the right state.
            NewPlugin.SetCohostRemoteMic(peerId, false);
            Log("Co-host " + peerId + " connected -> live but muted.");
        }
        else if (s == "failed" || s == "closed" || s == "disconnected")
        {
            bool ignored;
            _connectedPeers.TryRemove(peerId, out ignored);
            // Drop any stale quality reading so the strip doesn't show a dead peer.
            CohostNetStat removed;
            _cohostNetStats.TryRemove(peerId, out removed);
            // Stop mixing and tracking a co-host that is no longer connected, so the
            // host UI/roster reflect reality instead of showing ghosts.
            if (_audioMixer != null) _audioMixer.RemoveCoHost(peerId);
            ClearCohostRemoteMic(peerId);
            Log("Co-host " + peerId + " " + s + " -> removed from mix.");
        }
    }

    // Peers whose WebRTC connection is currently established (drives the strip's
    // connected/offline status in the new mesh model — replaces the relay-era
    // AuthenticationManager session check).
    private static readonly ConcurrentDictionary<string, bool> _connectedPeers =
        new ConcurrentDictionary<string, bool>();

    /// <summary>True while a live WebRTC connection to <paramref name="peerId"/> exists.</summary>
    public static bool IsPeerConnected(string peerId)
    {
        if (string.IsNullOrEmpty(peerId)) return false;
        bool v;
        return _connectedPeers.TryGetValue(peerId, out v) && v;
    }

    /// <summary>Snapshot of currently-connected peer ids (for the UI to enumerate).</summary>
    public static List<string> GetConnectedPeers()
    {
        return new List<string>(_connectedPeers.Keys);
    }

    /// <summary>
    /// Derives the WebRTC mesh signaling base origin from the configured relay URL,
    /// reusing existing settings rather than introducing new config (Req 6.4). The
    /// relay used ws/wss; the mesh signaling is plain HTTPS, so the scheme is
    /// normalised (wss:// -> https://, ws:// -> http://, a bare host -> https://) and
    /// any path/query/fragment is stripped, leaving only "scheme://host[:port]".
    /// <see cref="WebRtcMeshClient"/> appends the /api/... paths itself. Returns null
    /// when no relay URL is configured (caller then skips starting the mesh).
    /// </summary>
    internal static string DeriveMeshBaseUrl(string relayUrl)
    {
        if (relayUrl == null) return null;
        string u = relayUrl.Trim();
        if (u.Length == 0) return null;

        if (u.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            u = "https://" + u.Substring(6);
        else if (u.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            u = "http://" + u.Substring(5);
        else if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            u = "https://" + u; // bare host -> assume https

        int schemeEnd = u.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return null;
        schemeEnd += 3;
        int pathStart = u.IndexOfAny(new char[] { '/', '?', '#' }, schemeEnd);
        if (pathStart >= 0) u = u.Substring(0, pathStart);
        return u;
    }

    /// <summary>
    /// Derives a URL-safe room slug from existing settings: the station key when set,
    /// otherwise the station name (Req 6.4, no new config). Lowercases, keeps
    /// alphanumerics, collapses any run of other characters to a single hyphen, and
    /// trims leading/trailing hyphens. Returns null when neither setting is usable.
    /// </summary>
    internal static string DeriveRoomSlug(string stationKey, string stationName)
    {
        string raw = (stationKey != null && stationKey.Trim().Length > 0) ? stationKey : stationName;
        if (raw == null) return null;
        raw = raw.Trim();
        if (raw.Length == 0) return null;

        StringBuilder sb = new StringBuilder(raw.Length);
        bool lastDash = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                sb.Append(c);
                lastDash = false;
            }
            else if (c >= 'A' && c <= 'Z')
            {
                sb.Append((char)(c + 32)); // ASCII lowercase
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }
        string slug = sb.ToString();
        if (slug.Length > 0 && slug[slug.Length - 1] == '-')
            slug = slug.Substring(0, slug.Length - 1);
        return slug.Length > 0 ? slug : null;
    }

    /// <summary>
    /// Drives the plugin's outbound mic state from the latching UI toggle (task 5.6).
    /// Sets the <c>_micOn</c> gate the <see cref="AudioPumpLoop"/> checks (ON begins
    /// transmitting captured main-mix frames to connected peers, OFF stops), mirrors
    /// the value into the static accessor the UI polls, and broadcasts a
    /// <c>mic-state</c> signal via the mesh client when present so consoles render the
    /// plugin's on-air/muted indicator. This is a latching flip (each call sets an
    /// explicit state) — never momentary, never always-open.
    /// (Requirements 7.3, 7.4, 7.5, 7.6, 7.7, 7.8)
    /// </summary>
    public void SetMicOn(bool on)
    {
        _micOn = on;
        _staticMicOn = on;

        if (_webRtcMesh != null)
        {
            try { _webRtcMesh.SetMicState(on); }
            catch (Exception ex) { Log("Error broadcasting mic-state: " + ex.Message); }
        }

        Log("Mic toggled " + (on ? "ON (on air)" : "OFF (muted)"));
    }

    /// <summary>
    /// Resamples mono PCM16 from <paramref name="srcRate"/> to <paramref name="dstRate"/> using
    /// a nearest-sample mapping (consistent with <see cref="TryReadMainMixFrame"/>),
    /// returning little-endian PCM16 bytes ready for
    /// <see cref="AudioMixer.IngestAudio"/>. When the rates already match, this is a straight
    /// short[] → byte[] conversion.
    /// </summary>
    private static byte[] ResampleToMixerRate(short[] pcm, int sampleCount, int srcRate, int dstRate)
    {
        if (pcm == null || sampleCount <= 0 || srcRate <= 0 || dstRate <= 0)
            return new byte[0];

        // Clamp to the actual buffer length in case the caller over-reported sampleCount.
        int inputSamples = sampleCount <= pcm.Length ? sampleCount : pcm.Length;
        if (inputSamples <= 0) return new byte[0];

        int outputSamples = (int)((long)inputSamples * dstRate / srcRate);
        if (outputSamples < 1) outputSamples = 1;

        byte[] outBytes = new byte[outputSamples * 2];
        for (int i = 0; i < outputSamples; i++)
        {
            // Nearest-sample resample dstRate -> srcRate index lookup.
            int srcIdx = (int)((long)i * srcRate / dstRate);
            if (srcIdx >= inputSamples) srcIdx = inputSamples - 1;
            short s = pcm[srcIdx];
            outBytes[i * 2] = (byte)(s & 0xFF);
            outBytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return outBytes;
    }

    /// <summary>
    /// Outbound frame pump (task 5.4) — the BASS main-mix → Opus outbound bridge that
    /// replaces the deprecated relay's PCM-send loop. Reads 20 ms / 960-sample mono
    /// PCM16 @ 48 kHz frames from the existing DSP ring buffer (<see cref="ReadReturnAudio"/>)
    /// and pushes them to the active <see cref="IWebRtcPeer"/>, which performs the mono
    /// Opus encode + SRTP transmit to every connected peer.
    ///
    /// Transmission is gated by the latching mic toggle (<c>_micOn</c>), replacing PTT.
    /// The loop always drains a frame per tick so latency stays bounded, but only pushes
    /// while the mic is ON and a peer is wired. It no-ops safely when <c>_webRtcPeer</c>
    /// is null, so nothing breaks before the startup wiring (later task) lands.
    /// (Requirements 5.2, 5.5)
    /// </summary>
    private void AudioPumpLoop()
    {
        Log("AudioPumpLoop started.");
        const int frameSamples = 960; // 20 ms @ 48 kHz mono
        const int frameMs = 20;
        const int maxCatchupFrames = 4;   // bound any burst if we briefly fall behind
        const int backlogMs = 1000;       // generous safety cap during diagnosis (was 200)
        short[] frame = new short[frameSamples];
        bool firstPushLogged = false;

        // Pace outbound to REAL TIME: one 20 ms frame per 20 ms of wall clock. The
        // capture DSP fills the return ring in real-time *bursts*; sending at the fill
        // rate overfills the browser's NetEq jitter buffer, which then time-compresses
        // playback (chipmunk/too fast) and later starves (gaps). Under-sending (an
        // imprecise Sleep) instead drops audio through ring overflow. A wall-clock pace
        // keeps the average send rate exact and the remote jitter buffer stable.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long framesSent = 0;

        while (_meshActive)
        {
            try
            {
                IWebRtcPeer peer = _webRtcPeer;

                int srcFreq = CaptureSourceRate();
                int inputBytesPerFrame = ((int)((long)frameSamples * srcFreq / 48000)) * 2;
                if (inputBytesPerFrame < 2) inputBytesPerFrame = 2;

                // Keep buffered latency bounded (safety only; real-time pacing keeps the
                // ring near-empty in steady state).
                int backlogBytes = (int)((long)inputBytesPerFrame * backlogMs / frameMs);
                TrimReturnAudioBacklog(backlogBytes);

                long framesDue = clock.ElapsedMilliseconds / frameMs;

                // While no peer is connected, keep the pacing clock in step so we don't
                // build a huge "owed frames" deficit that would burst-drain (and
                // overfill NetEq) the instant a co-host joins.
                if (peer == null) { framesSent = framesDue; Thread.Sleep(5); continue; }

                int slots = (int)(framesDue - framesSent);
                if (slots > maxCatchupFrames) slots = maxCatchupFrames;

                for (int k = 0; k < slots; k++)
                {
                    // PlayIt's mixer always produces real-time samples (silence
                    // included), so a shortfall here is transient sub-frame jitter:
                    // wait for the next pass rather than dropping the slot, so no
                    // program audio is lost and we stay locked to the wall clock.
                    if (ReturnAudioAvailableBytes() < inputBytesPerFrame) break;
                    if (!TryReadMainMixFrame(frame)) break;
                    peer.PushOutboundAudio(frame, frameSamples, 48000, 1);
                    framesSent++;
                    _pumpFramesSinceLog++;
                    if (!firstPushLogged)
                    {
                        firstPushLogged = true;
                        Log("AudioPumpLoop: first outbound frame pushed (" + frameSamples + " samples @ 48kHz mono).");
                    }
                }

            }
            catch (Exception ex)
            {
                Log("AudioPumpLoop error: " + ex.Message);
            }
            Thread.Sleep(5);
        }
        Log("AudioPumpLoop exited.");
    }

    /// <summary>
    /// Fills a 960-sample mono PCM16 frame at 48 kHz (20 ms) from the DSP ring buffer,
    /// resampling from the mixer rate (<c>_mixerFreq</c>, e.g. 44.1 kHz) to 48 kHz with
    /// a nearest-sample approach. Returns false
    /// when no captured audio is currently available.
    /// </summary>
    private bool TryReadMainMixFrame(short[] frame)
    {
        const int outputSamples = 960; // 20 ms @ 48 kHz mono
        int srcFreq = CaptureSourceRate();

        // Source (mono PCM16) sample count needed to produce 960 samples at 48 kHz.
        int inputSamplesNeeded = (int)((long)outputSamples * srcFreq / 48000);
        if (inputSamplesNeeded < 1) inputSamplesNeeded = 1;

        byte[] raw = ReadReturnAudio(inputSamplesNeeded * 2);
        if (raw == null || raw.Length < 4)
        {
            // No real audio available this tick.
            return false;
        }

        int inputSamples = raw.Length / 2;
        for (int i = 0; i < outputSamples; i++)
        {
            // Linear-interpolated resample srcFreq -> 48 kHz. Nearest-sample (zero-order
            // hold) on an upsample (e.g. 22050 -> 48000) injects audible imaging
            // distortion ("overmodulation"); linear interpolation is clean for voice/
            // program material.
            long pos = (long)i * srcFreq;          // position in source, scaled by 48000
            int srcIdx = (int)(pos / 48000);
            int frac = (int)(pos % 48000);         // 0..47999 fractional part
            if (srcIdx >= inputSamples - 1)
            {
                int last = inputSamples - 1;
                frame[i] = (short)(raw[last * 2] | (raw[last * 2 + 1] << 8));
            }
            else
            {
                short s0 = (short)(raw[srcIdx * 2] | (raw[srcIdx * 2 + 1] << 8));
                short s1 = (short)(raw[(srcIdx + 1) * 2] | (raw[(srcIdx + 1) * 2 + 1] << 8));
                frame[i] = (short)(s0 + (int)(((long)(s1 - s0) * frac) / 48000));
            }
        }
        return true;
    }
}

// --- AuthenticationManager ---

public class AuthenticationManager
{
    private List<CoHostAccount> _accounts;
    private ConcurrentDictionary<string, ActiveSession> _sessions;
    private readonly object _accountsLock;

    public AuthenticationManager()
    {
        _accounts = new List<CoHostAccount>();
        _sessions = new ConcurrentDictionary<string, ActiveSession>();
        _accountsLock = new object();
    }

    public AuthResult Authenticate(string username, string password)
    {
        if (username == null || password == null)
        {
            AuthResult failResult = new AuthResult();
            failResult.Success = false;
            failResult.Error = "Invalid credentials";
            return failResult;
        }

        CoHostAccount matchedAccount = null;

        lock (_accountsLock)
        {
            for (int i = 0; i < _accounts.Count; i++)
            {
                CoHostAccount acct = _accounts[i];
                if (acct.Username != null && acct.Password != null &&
                    acct.Username.Equals(username, StringComparison.OrdinalIgnoreCase) &&
                    acct.Password.Equals(password, StringComparison.Ordinal))
                {
                    matchedAccount = acct;
                    break;
                }
            }
        }

        if (matchedAccount == null)
        {
            AuthResult failResult = new AuthResult();
            failResult.Success = false;
            failResult.Error = "Invalid credentials";
            return failResult;
        }

        // Single session per user: invalidate any existing session before issuing a new one
        InvalidateSession(matchedAccount.Username);

        string token = Guid.NewGuid().ToString("N");
        ActiveSession session = new ActiveSession();
        session.Token = token;
        session.CohostId = matchedAccount.Username;
        session.DisplayName = matchedAccount.DisplayName;
        session.CreatedAt = DateTime.UtcNow;

        _sessions[token] = session;

        AuthResult successResult = new AuthResult();
        successResult.Success = true;
        successResult.Token = token;
        successResult.DisplayName = matchedAccount.DisplayName;
        return successResult;
    }

    public AuthResult AuthenticateByHash(string hash)
    {
        if (hash == null)
        {
            AuthResult failResult = new AuthResult();
            failResult.Success = false;
            failResult.Error = "Invalid credentials";
            return failResult;
        }

        CoHostAccount matchedAccount = FindByHash(hash);

        if (matchedAccount == null)
        {
            AuthResult failResult = new AuthResult();
            failResult.Success = false;
            failResult.Error = "Invalid credentials";
            return failResult;
        }

        // Single session per user: invalidate any existing session before issuing a new one
        InvalidateSession(matchedAccount.Username);

        string token = Guid.NewGuid().ToString("N");
        ActiveSession session = new ActiveSession();
        session.Token = token;
        session.CohostId = matchedAccount.Username;
        session.DisplayName = matchedAccount.DisplayName;
        session.CreatedAt = DateTime.UtcNow;

        _sessions[token] = session;

        AuthResult successResult = new AuthResult();
        successResult.Success = true;
        successResult.Token = token;
        successResult.DisplayName = matchedAccount.DisplayName;
        return successResult;
    }

    public CoHostAccount FindByHash(string hash)
    {
        if (hash == null) return null;

        lock (_accountsLock)
        {
            for (int i = 0; i < _accounts.Count; i++)
            {
                CoHostAccount acct = _accounts[i];
                if (acct.Hash != null && acct.Hash.Equals(hash, StringComparison.Ordinal))
                {
                    return acct;
                }
            }
        }

        return null;
    }

    public bool ValidateToken(string token, out string cohostId)
    {
        cohostId = null;
        if (token == null)
        {
            return false;
        }

        ActiveSession session;
        if (_sessions.TryGetValue(token, out session))
        {
            cohostId = session.CohostId;
            return true;
        }

        return false;
    }

    public void InvalidateSession(string cohostId)
    {
        if (cohostId == null)
        {
            return;
        }

        List<string> tokensToRemove = new List<string>();
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.CohostId != null &&
                kvp.Value.CohostId.Equals(cohostId, StringComparison.OrdinalIgnoreCase))
            {
                tokensToRemove.Add(kvp.Key);
            }
        }

        for (int i = 0; i < tokensToRemove.Count; i++)
        {
            ActiveSession removed;
            _sessions.TryRemove(tokensToRemove[i], out removed);
        }
    }

    public void InvalidateAllSessions()
    {
        _sessions.Clear();
    }

    public bool HasActiveSession(string cohostId)
    {
        if (cohostId == null) return false;
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.CohostId != null &&
                kvp.Value.CohostId.Equals(cohostId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public bool HasAnyActiveSession()
    {
        return _sessions.Count > 0;
    }

    /// <summary>
    /// Registers a session for a co-host that joined via the relay.
    /// This allows the UI to show them as "Connected".
    /// </summary>
    public void RegisterRelaySession(string cohostId, string displayName)
    {
        if (cohostId == null) return;

        // Remove any existing session for this cohostId first
        InvalidateSession(cohostId);

        string token = "relay_" + Guid.NewGuid().ToString("N");
        ActiveSession session = new ActiveSession();
        session.Token = token;
        session.CohostId = cohostId;
        session.DisplayName = displayName ?? cohostId;
        session.CreatedAt = DateTime.UtcNow;
        _sessions[token] = session;
    }

    public void SetAccounts(List<CoHostAccount> accounts)
    {
        if (accounts == null)
        {
            accounts = new List<CoHostAccount>();
        }

        lock (_accountsLock)
        {
            _accounts = new List<CoHostAccount>(accounts);
        }
    }
}

// --- Supporting classes ---

public class CoHostAccount
{
    private string _username;
    private string _password;
    private string _displayName;
    private string _hash;

    public string Username
    {
        get { return _username; }
        set { _username = value; }
    }

    public string Password
    {
        get { return _password; }
        set { _password = value; }
    }

    public string DisplayName
    {
        get { return _displayName; }
        set { _displayName = value; }
    }

    public string Hash
    {
        get { return _hash; }
        set { _hash = value; }
    }
}

public class ActiveSession
{
    private string _token;
    private string _cohostId;
    private string _displayName;
    private DateTime _createdAt;

    public string Token
    {
        get { return _token; }
        set { _token = value; }
    }

    public string CohostId
    {
        get { return _cohostId; }
        set { _cohostId = value; }
    }

    public string DisplayName
    {
        get { return _displayName; }
        set { _displayName = value; }
    }

    public DateTime CreatedAt
    {
        get { return _createdAt; }
        set { _createdAt = value; }
    }
}

public class AuthResult
{
    private bool _success;
    private string _token;
    private string _displayName;
    private string _error;

    public bool Success
    {
        get { return _success; }
        set { _success = value; }
    }

    public string Token
    {
        get { return _token; }
        set { _token = value; }
    }

    public string DisplayName
    {
        get { return _displayName; }
        set { _displayName = value; }
    }

    public string Error
    {
        get { return _error; }
        set { _error = value; }
    }
}

public class CoHostState
{
    private bool _isLive;
    private bool _isMuted;
    private bool _isConnected;
    private CoHostAudioBuffer _buffer;
    private DateTime _lastAudioReceived;

    public bool IsLive
    {
        get { return _isLive; }
        set { _isLive = value; }
    }

    public bool IsMuted
    {
        get { return _isMuted; }
        set { _isMuted = value; }
    }

    public bool IsConnected
    {
        get { return _isConnected; }
        set { _isConnected = value; }
    }

    public CoHostAudioBuffer Buffer
    {
        get { return _buffer; }
        set { _buffer = value; }
    }

    public DateTime LastAudioReceived
    {
        get { return _lastAudioReceived; }
        set { _lastAudioReceived = value; }
    }
}

public class CoHostAudioBuffer
{
    private readonly short[] _buffer;
    private int _writePos;
    private int _readPos;
    private int _availableSamples;
    private float _peakLevel;
    private readonly object _lock;

    public CoHostAudioBuffer()
    {
        _buffer = new short[44100 * 2];
        _writePos = 0;
        _readPos = 0;
        _availableSamples = 0;
        _peakLevel = 0f;
        _lock = new object();
    }

    public void Write(byte[] pcmBytes, int count)
    {
        int sampleCount = count / 2;
        lock (_lock)
        {
            float maxAbs = 0f;
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(pcmBytes, i * 2);
                _buffer[_writePos] = sample;
                _writePos = (_writePos + 1) % _buffer.Length;

                float absVal = Math.Abs((float)sample) / 32768f;
                if (absVal > maxAbs)
                {
                    maxAbs = absVal;
                }
            }

            _availableSamples += sampleCount;
            if (_availableSamples > _buffer.Length)
            {
                // Overflow: advance read pointer to discard oldest
                int overflow = _availableSamples - _buffer.Length;
                _readPos = (_readPos + overflow) % _buffer.Length;
                _availableSamples = _buffer.Length;
            }

            if (maxAbs > _peakLevel)
            {
                _peakLevel = maxAbs;
            }
        }
    }

    public int Read(short[] output, int sampleCount)
    {
        lock (_lock)
        {
            int toRead = Math.Min(sampleCount, _availableSamples);

            for (int i = 0; i < toRead; i++)
            {
                output[i] = _buffer[_readPos];
                _readPos = (_readPos + 1) % _buffer.Length;
            }

            // Pad with silence if insufficient samples
            for (int i = toRead; i < sampleCount; i++)
            {
                output[i] = 0;
            }

            _availableSamples -= toRead;
            return toRead;
        }
    }

    public float GetPeakLevel()
    {
        lock (_lock)
        {
            float level = _peakLevel;
            _peakLevel = _peakLevel * 0.85f;
            return level;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _writePos = 0;
            _readPos = 0;
            _availableSamples = 0;
            _peakLevel = 0f;
        }
    }
}

public class AudioMixer
{
    private ConcurrentDictionary<string, CoHostState> _coHosts;
    private ConcurrentDictionary<string, float> _lastLatency;
    private ConcurrentDictionary<string, bool> _firstAudioLogged;
    private ConcurrentDictionary<string, string> _cohostIps;
    private readonly System.Diagnostics.Stopwatch _mixDiagClock = System.Diagnostics.Stopwatch.StartNew();

    public AudioMixer()
    {
        _coHosts = new ConcurrentDictionary<string, CoHostState>();
        _lastLatency = new ConcurrentDictionary<string, float>();
        _firstAudioLogged = new ConcurrentDictionary<string, bool>();
        _cohostIps = new ConcurrentDictionary<string, string>();
    }

    public void SetIp(string cohostId, string ip)
    {
        if (cohostId == null) return;
        if (ip == null) ip = "";
        _cohostIps[cohostId] = ip;
    }

    public string GetIp(string cohostId)
    {
        if (cohostId == null) return "";
        string ip;
        if (_cohostIps.TryGetValue(cohostId, out ip))
        {
            return ip;
        }
        return "";
    }

    public void EnsureCoHost(string cohostId)
    {
        if (cohostId == null) return;
        if (!_coHosts.ContainsKey(cohostId))
        {
            CoHostState state = new CoHostState();
            state.IsLive = false;
            state.IsMuted = false;
            state.IsConnected = true;
            state.Buffer = new CoHostAudioBuffer();
            state.LastAudioReceived = DateTime.UtcNow;
            _coHosts.TryAdd(cohostId, state);
        }
    }

    public void IngestAudio(string cohostId, byte[] pcmData, int length)
    {
        IngestAudio(cohostId, pcmData, length, -1);
    }

    public void IngestAudio(string cohostId, byte[] pcmData, int length, long clientTimestampMs)
    {
        if (cohostId == null || pcmData == null || length <= 0) return;

        // Log first time audio arrives from this co-host
        bool alreadyLogged;
        if (!_firstAudioLogged.TryGetValue(cohostId, out alreadyLogged) || !alreadyLogged)
        {
            _firstAudioLogged[cohostId] = true;
            NewPlugin.LogStatic("Audio received from co-host: " + cohostId + ", " + length + " bytes");
        }

        EnsureCoHost(cohostId);

        CoHostState state;
        if (_coHosts.TryGetValue(cohostId, out state))
        {
            state.Buffer.Write(pcmData, length);
            state.LastAudioReceived = DateTime.UtcNow;
        }

        // Calculate latency if timestamp provided
        if (clientTimestampMs > 0)
        {
            long nowMs = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            float latency = (float)(nowMs - clientTimestampMs);
            if (latency < 0) latency = 0;
            _lastLatency[cohostId] = latency;
        }
    }

    public bool IsConnected(string cohostId)
    {
        if (cohostId == null) return false;
        CoHostState state;
        if (_coHosts.TryGetValue(cohostId, out state))
        {
            TimeSpan elapsed = DateTime.UtcNow - state.LastAudioReceived;
            return elapsed.TotalSeconds <= 5;
        }
        return false;
    }

    public float GetLatency(string cohostId)
    {
        if (cohostId == null) return 0f;
        float latency;
        if (_lastLatency.TryGetValue(cohostId, out latency))
        {
            return latency;
        }
        return 0f;
    }

    public int FillOutputBuffer(int length, IntPtr buffer)
    {
        int sampleCount = length / 2;
        short[] output = new short[sampleCount];

        // Collect all live + unmuted co-host keys
        List<CoHostState> activeStates = new List<CoHostState>();
        foreach (var kvp in _coHosts)
        {
            CoHostState s = kvp.Value;
            if (s.IsLive && !s.IsMuted)
            {
                activeStates.Add(s);
            }
        }

        if (activeStates.Count == 0)
        {
            // Output silence
            Marshal.Copy(output, 0, buffer, sampleCount);
            return length;
        }

        // Read from each active co-host and sum with clamping
        short[][] buffers = new short[activeStates.Count][];
        for (int i = 0; i < activeStates.Count; i++)
        {
            buffers[i] = new short[sampleCount];
            activeStates[i].Buffer.Read(buffers[i], sampleCount);
        }

        for (int s = 0; s < sampleCount; s++)
        {
            int sum = 0;
            for (int i = 0; i < buffers.Length; i++)
            {
                sum += buffers[i][s];
            }
            // Clamp to 16-bit signed range
            if (sum > 32767) sum = 32767;
            if (sum < -32768) sum = -32768;
            output[s] = (short)sum;
        }

        Marshal.Copy(output, 0, buffer, sampleCount);
        return length;
    }

    public void SetLive(string cohostId, bool live)
    {
        if (cohostId == null) return;
        EnsureCoHost(cohostId);
        CoHostState state;
        if (_coHosts.TryGetValue(cohostId, out state))
        {
            state.IsLive = live;
        }
    }

    public void SetMuted(string cohostId, bool muted)
    {
        if (cohostId == null) return;
        EnsureCoHost(cohostId);
        CoHostState state;
        if (_coHosts.TryGetValue(cohostId, out state))
        {
            state.IsMuted = muted;
        }
    }

    public bool GetLive(string cohostId)
    {
        if (cohostId == null) return false;
        CoHostState state;
        if (_coHosts.TryGetValue(cohostId, out state))
        {
            return state.IsLive;
        }
        return false;
    }

    public bool GetMuted(string cohostId)
    {
        if (cohostId == null) return false;
        CoHostState state;
        if (_coHosts.TryGetValue(cohostId, out state))
        {
            return state.IsMuted;
        }
        return false;
    }

    public float GetLevel(string cohostId)
    {
        if (cohostId == null) return 0f;
        CoHostState state;
        if (_coHosts.TryGetValue(cohostId, out state))
        {
            return state.Buffer.GetPeakLevel();
        }
        return 0f;
    }

    public void RemoveCoHost(string cohostId)
    {
        if (cohostId == null) return;
        CoHostState removed;
        if (_coHosts.TryRemove(cohostId, out removed))
        {
            removed.Buffer.Clear();
        }
    }
}

public class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Partyline",
        "cohosts.json");

    // Auto-generated room identity, persisted on first run. roomId is the PUBLIC
    // room slug embedded in co-host invite URLs; djKey is the PRIVATE credential
    // the plugin uses to claim/authenticate the room with the signaling server and
    // is NEVER shared. Neither is user-configurable — they are generated once and
    // reused, so co-host invite links always resolve to the same room.
    private static readonly string IdentityPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Partyline",
        "identity.json");

    /// <summary>
    /// Returns the persisted room identity as { roomId, djKey }, generating and
    /// saving a fresh pair (32 hex chars each) on first run. Stored separately from
    /// the co-host accounts so the account-editing path can never clobber it.
    /// </summary>
    public string[] EnsureRoomIdentity()
    {
        try
        {
            if (SettingsStore.Has("identity"))
            {
                string json = SettingsStore.Read("identity");
                string rid = ExtractTopLevelString(json, "roomId");
                string dk = ExtractTopLevelString(json, "djKey");
                if (!string.IsNullOrEmpty(rid) && !string.IsNullOrEmpty(dk))
                {
                    return new string[] { rid, dk };
                }
            }
        }
        catch (Exception ex)
        {
            NewPlugin.LogStatic("ERROR reading identity.json: " + ex.Message);
        }

        // Generate a fresh identity (two independent 32-hex-char tokens).
        string roomId = Guid.NewGuid().ToString("N"); // 32 hex chars
        string djKey = Guid.NewGuid().ToString("N"); // 32 hex chars
        try
        {
            string body = "{\"roomId\":\"" + roomId + "\",\"djKey\":\"" + djKey + "\"}";
            SettingsStore.Write("identity", body);
            NewPlugin.LogStatic("Generated new room identity (roomId=" + roomId + ").");
        }
        catch (Exception ex)
        {
            NewPlugin.LogStatic("ERROR writing identity.json: " + ex.Message);
        }
        return new string[] { roomId, djKey };
    }

    /// <summary>Convenience: just the public room slug.</summary>
    public string LoadRoomId()
    {
        return EnsureRoomIdentity()[0];
    }

    // Display metadata shown to co-hosts (room name / station name / DJ name).
    // Stored separately from accounts + identity. Returns { roomName, stationName, djName }.
    private static readonly string MetaPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Partyline",
        "meta.json");

    public string[] LoadMeta()
    {
        try
        {
            if (SettingsStore.Has("meta"))
            {
                string json = SettingsStore.Read("meta");
                return new string[]
                {
                    ExtractTopLevelString(json, "roomName") ?? "",
                    ExtractTopLevelString(json, "stationName") ?? "",
                    ExtractTopLevelString(json, "djName") ?? ""
                };
            }
        }
        catch (Exception ex)
        {
            NewPlugin.LogStatic("ERROR reading meta.json: " + ex.Message);
        }
        return new string[] { "", "", "" };
    }

    /// <summary>
    /// The DJ-chosen custom room id (the public code co-hosts type into the join
    /// link). Stored in meta.json alongside the display names. Empty when unset, in
    /// which case the server falls back to the slug-derived code. Always returned
    /// lowercased to match the case-insensitive join resolution.
    /// </summary>
    public string LoadRoomCode()
    {
        try
        {
            if (SettingsStore.Has("meta"))
            {
                string json = SettingsStore.Read("meta");
                string code = ExtractTopLevelString(json, "roomCode");
                return SanitizeRoomCode(code);
            }
        }
        catch (Exception ex)
        {
            NewPlugin.LogStatic("ERROR reading roomCode: " + ex.Message);
        }
        return "";
    }

    /// <summary>
    /// Normalises a room id to the server's accepted shape: trimmed, lowercased,
    /// 1-24 chars of [a-z0-9_-] only. Returns "" if it cannot be made valid.
    /// </summary>
    public static string SanitizeRoomCode(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string code = raw.Trim().ToLowerInvariant();
        if (code.Length == 0 || code.Length > 24) return "";
        for (int i = 0; i < code.Length; i++)
        {
            char ch = code[i];
            bool ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_';
            if (!ok) return "";
        }
        return code;
    }

    public void SaveMeta(string roomName, string stationName, string djName)
    {
        SaveMeta(roomName, stationName, djName, LoadRoomCode());
    }

    public void SaveMeta(string roomName, string stationName, string djName, string roomCode)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("{\"roomName\":").Append(EscapeJsonString(roomName ?? ""));
            sb.Append(",\"stationName\":").Append(EscapeJsonString(stationName ?? ""));
            sb.Append(",\"djName\":").Append(EscapeJsonString(djName ?? ""));
            sb.Append(",\"roomCode\":").Append(EscapeJsonString(SanitizeRoomCode(roomCode)));
            sb.Append("}");
            SettingsStore.Write("meta", sb.ToString());
        }
        catch (Exception ex)
        {
            NewPlugin.LogStatic("ERROR writing meta: " + ex.Message);
        }
    }

    private static string ExtractTopLevelString(string json, string key)
    {
        if (json == null) return null;
        string searchKey = "\"" + key + "\"";
        int keyIdx = json.IndexOf(searchKey);
        if (keyIdx < 0) return null;
        int colonIdx = json.IndexOf(':', keyIdx + searchKey.Length);
        if (colonIdx < 0) return null;
        int valueStart = json.IndexOf('"', colonIdx + 1);
        if (valueStart < 0) return null;
        int valueEnd = valueStart + 1;
        while (valueEnd < json.Length && json[valueEnd] != '"') valueEnd++;
        if (valueEnd >= json.Length) return null;
        return json.Substring(valueStart + 1, valueEnd - valueStart - 1);
    }

    public List<CoHostAccount> Load()
    {
        try
        {
            if (!SettingsStore.Has("cohosts"))
            {
                NewPlugin.LogStatic("No saved co-host accounts, starting with empty list");
                return new List<CoHostAccount>();
            }

            string json = SettingsStore.Read("cohosts");
            List<CoHostAccount> accounts = ParseAccountsJson(json);

            // Generate hash for any accounts that are missing one (legacy accounts)
            bool needsResave = false;
            for (int i = 0; i < accounts.Count; i++)
            {
                if (string.IsNullOrEmpty(accounts[i].Hash))
                {
                    accounts[i].Hash = Guid.NewGuid().ToString("N").Substring(0, 12);
                    needsResave = true;
                    NewPlugin.LogStatic("Generated hash for account: " + accounts[i].Username);
                }
            }

            if (needsResave)
            {
                try
                {
                    string stationName = ExtractJsonStringValue(json, "stationName");
                    Save(accounts, stationName);
                    NewPlugin.LogStatic("Re-saved settings with generated hashes");
                }
                catch (Exception saveEx)
                {
                    NewPlugin.LogStatic("WARNING: Could not re-save settings with generated hashes: " + saveEx.Message);
                }
            }

            return accounts;
        }
        catch (Exception ex)
        {
            NewPlugin.LogStatic("ERROR loading settings: " + ex.Message);
            return new List<CoHostAccount>();
        }
    }

    public string LoadStationName()
    {
        try
        {
            if (!SettingsStore.Has("cohosts"))
            {
                return "Partyline Co-Host";
            }

            string json = SettingsStore.Read("cohosts");
            string name = ExtractJsonStringValue(json, "stationName");
            if (string.IsNullOrEmpty(name))
            {
                return "Partyline Co-Host";
            }
            return name;
        }
        catch (Exception ex)
        {
            NewPlugin.LogStatic("ERROR loading station name: " + ex.Message);
            return "Partyline Co-Host";
        }
    }

    public string LoadRelayUrl()
    {
        try
        {
            if (!SettingsStore.Has("cohosts"))
            {
                return "";
            }

            string json = SettingsStore.Read("cohosts");
            string url = ExtractJsonStringValue(json, "relayUrl");
            if (string.IsNullOrEmpty(url))
            {
                return "";
            }
            return url;
        }
        catch (Exception ex)
        {
            NewPlugin.LogStatic("ERROR loading relay URL: " + ex.Message);
            return "";
        }
    }

    public string LoadStationKey()
    {
        try
        {
            if (!SettingsStore.Has("cohosts"))
            {
                return "";
            }

            string json = SettingsStore.Read("cohosts");
            string key = ExtractJsonStringValue(json, "stationKey");
            if (string.IsNullOrEmpty(key))
            {
                return "";
            }
            return key;
        }
        catch (Exception ex)
        {
            NewPlugin.LogStatic("ERROR loading station key: " + ex.Message);
            return "";
        }
    }

    private List<CoHostAccount> ParseAccountsJson(string json)
    {
        var result = new List<CoHostAccount>();

        int accountsIdx = json.IndexOf("\"accounts\"");
        if (accountsIdx < 0)
        {
            NewPlugin.LogStatic("Settings file has no accounts key, starting with empty list");
            return result;
        }

        // Find the array start
        int arrayStart = json.IndexOf('[', accountsIdx);
        if (arrayStart < 0) return result;

        int arrayEnd = json.LastIndexOf(']');
        if (arrayEnd < 0) return result;

        string arrayContent = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);

        // Split into objects by finding matched braces
        int depth = 0;
        int objStart = -1;
        for (int i = 0; i < arrayContent.Length; i++)
        {
            char c = arrayContent[i];
            if (c == '{')
            {
                if (depth == 0) objStart = i;
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0 && objStart >= 0)
                {
                    string objStr = arrayContent.Substring(objStart, i - objStart + 1);
                    CoHostAccount account = ParseAccountObject(objStr);
                    if (account != null) result.Add(account);
                    objStart = -1;
                }
            }
        }

        NewPlugin.LogStatic("Loaded " + result.Count + " co-host accounts from settings");
        return result;
    }

    private CoHostAccount ParseAccountObject(string objJson)
    {
        var account = new CoHostAccount();
        account.Username = ExtractJsonStringValue(objJson, "username");
        account.Password = ExtractJsonStringValue(objJson, "password");
        account.DisplayName = ExtractJsonStringValue(objJson, "displayName");
        account.Hash = ExtractJsonStringValue(objJson, "hash");
        return account;
    }

    private string ExtractJsonStringValue(string json, string key)
    {
        string searchKey = "\"" + key + "\"";
        int keyIdx = json.IndexOf(searchKey);
        if (keyIdx < 0) return null;

        int colonIdx = json.IndexOf(':', keyIdx + searchKey.Length);
        if (colonIdx < 0) return null;

        // Find the opening quote of the value
        int valueStart = json.IndexOf('"', colonIdx + 1);
        if (valueStart < 0) return null;

        // Find the closing quote (handle escaped quotes)
        int valueEnd = valueStart + 1;
        while (valueEnd < json.Length)
        {
            if (json[valueEnd] == '"' && json[valueEnd - 1] != '\\')
                break;
            valueEnd++;
        }

        if (valueEnd >= json.Length) return null;
        return json.Substring(valueStart + 1, valueEnd - valueStart - 1)
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\")
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t");
    }

    public void Save(List<CoHostAccount> accounts)
    {
        Save(accounts, null, null, null);
    }

    public void Save(List<CoHostAccount> accounts, string stationName)
    {
        Save(accounts, stationName, null, null);
    }

    public void Save(List<CoHostAccount> accounts, string stationName, string relayUrl, string stationKey)
    {
        try
        {
            // If stationName not provided, preserve the existing one
            if (stationName == null)
            {
                stationName = LoadStationName();
            }

            // If relayUrl not provided, preserve the existing one
            if (relayUrl == null)
            {
                relayUrl = LoadRelayUrl();
            }

            // If stationKey not provided, preserve the existing one
            if (stationKey == null)
            {
                stationKey = LoadStationKey();
            }

            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"stationName\":");
            sb.Append(EscapeJsonString(stationName));
            sb.Append(",\"relayUrl\":");
            sb.Append(EscapeJsonString(relayUrl));
            sb.Append(",\"stationKey\":");
            sb.Append(EscapeJsonString(stationKey));
            sb.Append(",\"accounts\":[");

            for (int i = 0; i < accounts.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(",");
                }

                sb.Append("{");
                sb.Append("\"username\":");
                sb.Append(EscapeJsonString(accounts[i].Username));
                sb.Append(",\"password\":");
                sb.Append(EscapeJsonString(accounts[i].Password));
                sb.Append(",\"displayName\":");
                sb.Append(EscapeJsonString(accounts[i].DisplayName));
                sb.Append(",\"hash\":");
                sb.Append(EscapeJsonString(accounts[i].Hash));
                sb.Append("}");
            }

            sb.Append("]}");

            SettingsStore.Write("cohosts", sb.ToString());
            NewPlugin.LogStatic("Saved " + accounts.Count + " co-host accounts to settings");
        }
        catch (Exception ex)
        {
            NewPlugin.LogStatic("ERROR saving settings: " + ex.Message);
            throw;
        }
    }

    // Single obfuscated settings container. Replaces the separate cohosts.json /
    // identity.json / meta.json plain-text files with ONE DPAPI-encrypted file
    // (partyline.dat). Each logical section keeps its original JSON text, so every
    // existing parser is unchanged — only the storage layer differs. Encryption is
    // per-Windows-user (DPAPI, CurrentUser scope), so the file is unreadable if
    // copied to another machine/account. Legacy plain-text files are migrated on
    // first load and then deleted.
    private static class SettingsStore
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Partyline");
        private static readonly string StorePath = Path.Combine(Dir, "partyline.dat");
        private static readonly string[][] Legacy = new string[][] {
            new[] { "cohosts",  Path.Combine(Dir, "cohosts.json") },
            new[] { "identity", Path.Combine(Dir, "identity.json") },
            new[] { "meta",     Path.Combine(Dir, "meta.json") },
        };

        private static readonly object _lock = new object();
        private static Dictionary<string, string> _cache;

        private static void EnsureLoaded()
        {
            if (_cache != null) return;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(StorePath))
                {
                    byte[] prot = File.ReadAllBytes(StorePath);
                    byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(
                        prot, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    Deserialize(plain, map);
                    _cache = map;
                    return;
                }

                // No encrypted store yet: migrate any legacy plain-text files.
                bool migrated = false;
                foreach (var l in Legacy)
                {
                    try
                    {
                        if (File.Exists(l[1])) { map[l[0]] = File.ReadAllText(l[1], Encoding.UTF8); migrated = true; }
                    }
                    catch { }
                }
                _cache = map;
                if (migrated)
                {
                    Persist();
                    foreach (var l in Legacy) { try { if (File.Exists(l[1])) File.Delete(l[1]); } catch { } }
                    NewPlugin.LogStatic("Migrated legacy settings into encrypted store.");
                }
                return;
            }
            catch (Exception ex)
            {
                NewPlugin.LogStatic("ERROR reading settings store: " + ex.Message);
            }
            _cache = map;
        }

        private static void Persist()
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                byte[] plain = Serialize(_cache);
                byte[] prot = System.Security.Cryptography.ProtectedData.Protect(
                    plain, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                File.WriteAllBytes(StorePath, prot);
            }
            catch (Exception ex)
            {
                NewPlugin.LogStatic("ERROR writing settings store: " + ex.Message);
            }
        }

        private static byte[] Serialize(Dictionary<string, string> map)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                bw.Write(map.Count);
                foreach (var kv in map) { bw.Write(kv.Key); bw.Write(kv.Value ?? ""); }
                bw.Flush();
                return ms.ToArray();
            }
        }

        private static void Deserialize(byte[] data, Dictionary<string, string> map)
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms, Encoding.UTF8))
            {
                int n = br.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    string k = br.ReadString();
                    string v = br.ReadString();
                    map[k] = v;
                }
            }
        }

        public static bool Has(string name)
        {
            lock (_lock) { EnsureLoaded(); string v; return _cache.TryGetValue(name, out v) && !string.IsNullOrEmpty(v); }
        }

        public static string Read(string name)
        {
            lock (_lock) { EnsureLoaded(); string v; return _cache.TryGetValue(name, out v) ? v : null; }
        }

        public static void Write(string name, string content)
        {
            lock (_lock) { EnsureLoaded(); _cache[name] = content ?? ""; Persist(); }
        }
    }

    private static string EscapeJsonString(string value)
    {
        if (value == null)
        {
            return "null";
        }

        var sb = new StringBuilder();
        sb.Append("\"");

        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }

        sb.Append("\"");
        return sb.ToString();
    }
}

public class PartylineConfigForm : Form
{
    private SettingsManager _settingsManager;
    private WebRtcMeshClient _mesh;   // running mesh (may be null if not started)
    private List<CoHostAccount> _accounts;
    private string _stationName;
    private string _relayUrl;
    private string _stationKey;
    private DataGridView _grid;
    private Panel _editPanel;
    private TextBox _txtUsername;
    private TextBox _txtPassword;
    private TextBox _txtDisplayName;
    private TextBox _txtStationName;
    private TextBox _txtRoomName;
    private TextBox _txtDjName;
    private TextBox _txtRelayUrl;
    private TextBox _txtStationKey;
    private TextBox _txtRoomCode;
    private string _roomName;
    private string _djName;
    private string _roomCode;
    private Button _btnSave;
    private Button _btnCancel;
    private Button _btnAdd;
    private int _editingIndex;

    public PartylineConfigForm(SettingsManager settingsManager)
        : this(settingsManager, null)
    {
    }

    public PartylineConfigForm(SettingsManager settingsManager, WebRtcMeshClient mesh)
    {
        _settingsManager = settingsManager;
        _mesh = mesh;
        _accounts = _settingsManager.Load();
        string[] meta = _settingsManager.LoadMeta();
        _roomName = meta[0];
        _stationName = meta[1];
        _djName = meta[2];
        _relayUrl = _settingsManager.LoadRelayUrl();
        _stationKey = _settingsManager.LoadStationKey();
        _roomCode = _settingsManager.LoadRoomCode();
        _editingIndex = -1;
        InitializeFormComponents();
        LoadGrid();
    }

    private void InitializeFormComponents()
    {
        Text = "Partyline Co-Host Configuration";
        // Use ClientSize (not Width/Height) so the bottom buttons, which are
        // positioned in client coordinates, are never clipped by the title bar.
        ClientSize = new System.Drawing.Size(548, 358);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        // Display metadata shown to co-hosts — three fields side by side.
        int fy = 10, by = 30, fw = 168;
        Label lblStationName = new Label();
        lblStationName.Text = "Station Name:";
        lblStationName.Location = new System.Drawing.Point(12, fy);
        lblStationName.AutoSize = true;
        lblStationName.Font = new System.Drawing.Font("Segoe UI", 8.5f, System.Drawing.FontStyle.Bold);
        Controls.Add(lblStationName);
        _txtStationName = new TextBox();
        _txtStationName.Location = new System.Drawing.Point(12, by);
        _txtStationName.Size = new System.Drawing.Size(fw, 22);
        _txtStationName.Text = _stationName;
        Controls.Add(_txtStationName);

        Label lblRoomName = new Label();
        lblRoomName.Text = "Room Name:";
        lblRoomName.Location = new System.Drawing.Point(190, fy);
        lblRoomName.AutoSize = true;
        lblRoomName.Font = new System.Drawing.Font("Segoe UI", 8.5f, System.Drawing.FontStyle.Bold);
        Controls.Add(lblRoomName);
        _txtRoomName = new TextBox();
        _txtRoomName.Location = new System.Drawing.Point(190, by);
        _txtRoomName.Size = new System.Drawing.Size(fw, 22);
        _txtRoomName.Text = _roomName;
        Controls.Add(_txtRoomName);

        Label lblDjName = new Label();
        lblDjName.Text = "DJ Name:";
        lblDjName.Location = new System.Drawing.Point(368, fy);
        lblDjName.AutoSize = true;
        lblDjName.Font = new System.Drawing.Font("Segoe UI", 8.5f, System.Drawing.FontStyle.Bold);
        Controls.Add(lblDjName);
        _txtDjName = new TextBox();
        _txtDjName.Location = new System.Drawing.Point(368, by);
        _txtDjName.Size = new System.Drawing.Size(fw, 22);
        _txtDjName.Text = _djName;
        Controls.Add(_txtDjName);

        // Room ID: the DJ-chosen public code that forms the co-host join link
        // (partyline.compressed.stream/<room-id>). Up to 24 chars, no spaces.
        Label lblRoomCode = new Label();
        lblRoomCode.Text = "Room ID (co-host link):";
        lblRoomCode.Location = new System.Drawing.Point(12, 58);
        lblRoomCode.AutoSize = true;
        lblRoomCode.Font = new System.Drawing.Font("Segoe UI", 8.5f, System.Drawing.FontStyle.Bold);
        Controls.Add(lblRoomCode);
        _txtRoomCode = new TextBox();
        _txtRoomCode.Location = new System.Drawing.Point(12, 78);
        _txtRoomCode.Size = new System.Drawing.Size(200, 22);
        _txtRoomCode.MaxLength = 24;
        _txtRoomCode.Text = _roomCode;
        Controls.Add(_txtRoomCode);

        // Dedicated Save button so the DJ can save + verify the Room ID (and push
        // it live to the server) without closing the whole dialog.
        Button btnSaveRoomCode = new Button();
        btnSaveRoomCode.Text = "Save";
        btnSaveRoomCode.Location = new System.Drawing.Point(218, 77);
        btnSaveRoomCode.Size = new System.Drawing.Size(70, 24);
        btnSaveRoomCode.Click += OnSaveRoomCodeClick;
        Controls.Add(btnSaveRoomCode);

        Label lblRoomCodeHint = new Label();
        lblRoomCodeHint.Text = "1-24 chars: a-z 0-9 - _  (no spaces).";
        lblRoomCodeHint.Location = new System.Drawing.Point(296, 81);
        lblRoomCodeHint.AutoSize = true;
        lblRoomCodeHint.ForeColor = System.Drawing.SystemColors.GrayText;
        Controls.Add(lblRoomCodeHint);

        Label lblTitle = new Label();
        lblTitle.Text = "Co-Host Accounts:";
        lblTitle.Location = new System.Drawing.Point(12, 110);
        lblTitle.AutoSize = true;
        lblTitle.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
        Controls.Add(lblTitle);

        // Add button sits on the title row (was previously clipped under the grid).
        _btnAdd = new Button();
        _btnAdd.Text = "+ Add Co-Host";
        _btnAdd.Location = new System.Drawing.Point(412, 106);
        _btnAdd.Size = new System.Drawing.Size(124, 26);
        _btnAdd.Click += OnAddClick;
        Controls.Add(_btnAdd);

        // DataGridView for account list
        _grid = new DataGridView();
        _grid.Location = new System.Drawing.Point(12, 138);
        _grid.Size = new System.Drawing.Size(524, 170);
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = System.Drawing.SystemColors.Window;

        DataGridViewTextBoxColumn colUsername = new DataGridViewTextBoxColumn();
        colUsername.Name = "Username";
        colUsername.HeaderText = "Username";
        colUsername.FillWeight = 25;
        _grid.Columns.Add(colUsername);

        DataGridViewTextBoxColumn colDisplay = new DataGridViewTextBoxColumn();
        colDisplay.Name = "DisplayName";
        colDisplay.HeaderText = "Display Name";
        colDisplay.FillWeight = 30;
        _grid.Columns.Add(colDisplay);

        // Join URL is intentionally NOT shown — the Invite button copies it.
        DataGridViewButtonColumn colCopy = new DataGridViewButtonColumn();
        colCopy.Name = "Copy";
        colCopy.HeaderText = "";
        colCopy.Text = "Invite";
        colCopy.UseColumnTextForButtonValue = true;
        colCopy.FillWeight = 14;
        _grid.Columns.Add(colCopy);

        DataGridViewButtonColumn colEdit = new DataGridViewButtonColumn();
        colEdit.Name = "Edit";
        colEdit.HeaderText = "";
        colEdit.Text = "Edit";
        colEdit.UseColumnTextForButtonValue = true;
        colEdit.FillWeight = 12;
        _grid.Columns.Add(colEdit);

        DataGridViewButtonColumn colDelete = new DataGridViewButtonColumn();
        colDelete.Name = "Delete";
        colDelete.HeaderText = "";
        colDelete.Text = "Delete";
        colDelete.UseColumnTextForButtonValue = true;
        colDelete.FillWeight = 13;
        _grid.Columns.Add(colDelete);

        _grid.CellContentClick += OnGridCellContentClick;
        Controls.Add(_grid);

        // Edit panel (toggled by Add/Edit) overlays the grid area so it doesn't
        // force extra form height (which left a large blank gap).
        _editPanel = new Panel();
        _editPanel.Location = new System.Drawing.Point(12, 138);
        _editPanel.Size = new System.Drawing.Size(524, 170);
        _editPanel.BackColor = System.Drawing.SystemColors.Control;
        _editPanel.Visible = false;

        Label lblSep = new Label();
        lblSep.Text = "Add/Edit Co-Host:";
        lblSep.Location = new System.Drawing.Point(0, 0);
        lblSep.AutoSize = true;
        lblSep.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
        _editPanel.Controls.Add(lblSep);

        Label lblUser = new Label();
        lblUser.Text = "Username:";
        lblUser.Location = new System.Drawing.Point(0, 28);
        lblUser.AutoSize = true;
        _editPanel.Controls.Add(lblUser);

        _txtUsername = new TextBox();
        _txtUsername.Location = new System.Drawing.Point(100, 25);
        _txtUsername.Size = new System.Drawing.Size(200, 22);
        _editPanel.Controls.Add(_txtUsername);

        Label lblPass = new Label();
        lblPass.Text = "Password:";
        lblPass.Location = new System.Drawing.Point(0, 56);
        lblPass.AutoSize = true;
        _editPanel.Controls.Add(lblPass);

        _txtPassword = new TextBox();
        _txtPassword.Location = new System.Drawing.Point(100, 53);
        _txtPassword.Size = new System.Drawing.Size(200, 22);
        _txtPassword.UseSystemPasswordChar = true;
        _editPanel.Controls.Add(_txtPassword);

        Label lblDisplayName = new Label();
        lblDisplayName.Text = "Display Name:";
        lblDisplayName.Location = new System.Drawing.Point(0, 84);
        lblDisplayName.AutoSize = true;
        _editPanel.Controls.Add(lblDisplayName);

        _txtDisplayName = new TextBox();
        _txtDisplayName.Location = new System.Drawing.Point(100, 81);
        _txtDisplayName.Size = new System.Drawing.Size(200, 22);
        _editPanel.Controls.Add(_txtDisplayName);

        _btnSave = new Button();
        _btnSave.Text = "Save";
        _btnSave.Location = new System.Drawing.Point(100, 115);
        _btnSave.Size = new System.Drawing.Size(80, 28);
        _btnSave.Click += OnSaveClick;
        _editPanel.Controls.Add(_btnSave);

        _btnCancel = new Button();
        _btnCancel.Text = "Cancel";
        _btnCancel.Location = new System.Drawing.Point(190, 115);
        _btnCancel.Size = new System.Drawing.Size(80, 28);
        _btnCancel.Click += OnCancelClick;
        _editPanel.Controls.Add(_btnCancel);

        Controls.Add(_editPanel);

        // Save & Close button at bottom-right (client coordinates).
        Button btnSaveClose = new Button();
        btnSaveClose.Text = "Save && Close";
        btnSaveClose.Location = new System.Drawing.Point(ClientSize.Width - 210, ClientSize.Height - 40);
        btnSaveClose.Size = new System.Drawing.Size(100, 30);
        btnSaveClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        btnSaveClose.Click += OnSaveCloseClick;
        Controls.Add(btnSaveClose);

        // Close button (no save)
        Button btnClose = new Button();
        btnClose.Text = "Close";
        btnClose.Location = new System.Drawing.Point(ClientSize.Width - 100, ClientSize.Height - 40);
        btnClose.Size = new System.Drawing.Size(80, 30);
        btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        btnClose.Click += OnCloseClick;
        Controls.Add(btnClose);
    }

    private void OnSaveCloseClick(object sender, EventArgs e)
    {
        // Validate the optional custom Room ID before persisting: up to 24 chars,
        // no spaces, URL-safe. Keep the dialog open on invalid input.
        string rawCode = _txtRoomCode.Text.Trim();
        if (rawCode.Length > 0)
        {
            string sanitized = SettingsManager.SanitizeRoomCode(rawCode);
            if (sanitized.Length == 0)
            {
                MessageBox.Show(
                    "Room ID must be 1-24 characters and may contain only letters, numbers, hyphens and underscores (no spaces).",
                    "Invalid Room ID", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtRoomCode.Focus();
                _txtRoomCode.SelectAll();
                return;
            }
            _roomCode = sanitized;
        }
        else
        {
            _roomCode = "";
        }

        // Persist display metadata (shown to co-hosts) and the co-host accounts.
        _stationName = _txtStationName.Text.Trim();
        _roomName = _txtRoomName.Text.Trim();
        _djName = _txtDjName.Text.Trim();
        _settingsManager.SaveMeta(_roomName, _stationName, _djName, _roomCode);
        _settingsManager.Save(_accounts);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnCloseClick(object sender, EventArgs e)
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    // Save + verify just the Room ID, and push it live to the server immediately.
    // Confirms (a) it passes validation, (b) it round-trips to disk, and (c) the
    // server accepted it (or reports a conflict / that it'll publish on connect).
    private void OnSaveRoomCodeClick(object sender, EventArgs e)
    {
        string rawCode = _txtRoomCode.Text.Trim();
        string sanitized = "";
        if (rawCode.Length > 0)
        {
            sanitized = SettingsManager.SanitizeRoomCode(rawCode);
            if (sanitized.Length == 0)
            {
                MessageBox.Show(
                    "Room ID must be 1-24 characters and may contain only letters, numbers, hyphens and underscores (no spaces).",
                    "Invalid Room ID", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtRoomCode.Focus();
                _txtRoomCode.SelectAll();
                return;
            }
        }
        _roomCode = sanitized;
        _txtRoomCode.Text = sanitized; // reflect the normalised (lowercased) value

        // Persist alongside the current display-name fields so nothing is lost.
        string sn = _txtStationName.Text.Trim();
        string rn = _txtRoomName.Text.Trim();
        string dj = _txtDjName.Text.Trim();
        _settingsManager.SaveMeta(rn, sn, dj, _roomCode);

        // Verify the value actually round-tripped to disk.
        string saved = _settingsManager.LoadRoomCode();
        if (saved != _roomCode)
        {
            MessageBox.Show(
                "Save verification FAILED: the Room ID on disk ('" + saved + "') does not match what was entered ('" + _roomCode + "'). Check folder permissions for meta.json.",
                "Partyline", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string url = string.IsNullOrEmpty(_roomCode)
            ? "(auto-generated link)"
            : "https://partyline.compressed.stream/" + _roomCode;

        // Push it to the server now if the mesh is running, so the link goes live
        // without waiting for the next reconnect.
        string status;
        if (_mesh != null)
        {
            bool conflict;
            string err;
            bool ok = _mesh.TryPublishRoomCode(_roomCode, out conflict, out err);
            if (!ok)
                status = "\n\nSaved on disk, but the server could not be reached yet (" + err + ").\nIt will publish automatically when the plugin next connects.";
            else if (conflict)
                status = "\n\nWARNING: this Room ID is already in use by another room.\nCo-hosts must use the auto-generated link until you choose a different Room ID.";
            else
                status = "\n\nPublished to the server - the link is live now.";
        }
        else
        {
            status = "\n\nSaved. It will publish to the server when the plugin is running and connected.";
        }

        MessageBox.Show(
            "Room ID saved: " + (string.IsNullOrEmpty(_roomCode) ? "(blank - auto code)" : _roomCode)
                + "\nCo-host link: " + url + status,
            "Partyline", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // Builds the co-host join URL for the new Cloudflare room console:
    //   <worker-origin>/room/<slug>?invite=<hash>
    // The origin and slug are derived with the SAME logic the running plugin uses
    // (NewPlugin.DeriveMeshBaseUrl / DeriveRoomSlug) so the link always points at
    // the room the plugin actually joins. Returns "" if no signaling URL or slug
    // can be derived (e.g. blank config) so the grid shows an empty cell rather
    // than a broken link.
    private string BuildJoinUrl(CoHostAccount acct)
    {
        // Stable room link: the co-host URL is derived from the ROOM slug (not the
        // co-host account), so it never changes when accounts are added/edited.
        // The co-host logs in with their name + password; ?n= just prefills the name.
        try
        {
            string[] ident = _settingsManager.EnsureRoomIdentity();
            string slug = (ident != null && ident.Length > 0) ? ident[0] : "";
            if (string.IsNullOrEmpty(slug)) return "";
            // Prefer the DJ's custom Room ID; fall back to the slug-derived code.
            string code = _settingsManager.LoadRoomCode();
            if (string.IsNullOrEmpty(code))
                code = slug.Length > 6 ? slug.Substring(0, 6).ToLowerInvariant() : slug.ToLowerInvariant();
            string url = "https://partyline.compressed.stream/" + code;
            string name = (acct != null)
                ? (!string.IsNullOrEmpty(acct.DisplayName) ? acct.DisplayName : acct.Username)
                : null;
            if (!string.IsNullOrEmpty(name)) url += "?n=" + Uri.EscapeDataString(name);
            return url;
        }
        catch { return ""; }
    }

    private void LoadGrid()
    {
        _grid.Rows.Clear();
        for (int i = 0; i < _accounts.Count; i++)
        {
            CoHostAccount acct = _accounts[i];
            // Join URL is not displayed; the Invite button copies it.
            _grid.Rows.Add(acct.Username, acct.DisplayName);
        }
    }

    private void OnGridCellContentClick(object sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;

        if (_grid.Columns[e.ColumnIndex].Name == "Edit")
        {
            BeginEdit(e.RowIndex);
        }
        else if (_grid.Columns[e.ColumnIndex].Name == "Copy")
        {
            if (e.RowIndex >= 0 && e.RowIndex < _accounts.Count)
            {
                CoHostAccount acct = _accounts[e.RowIndex];
                string fullUrl = BuildJoinUrl(acct);
                if (!string.IsNullOrEmpty(fullUrl))
                {
                    Clipboard.SetText(fullUrl);
                    MessageBox.Show("Join URL copied to clipboard.", "Partyline", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("Set the Signaling Server URL and Station Key/Name first to generate a join link.", "Partyline", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
        else if (_grid.Columns[e.ColumnIndex].Name == "Delete")
        {
            _accounts.RemoveAt(e.RowIndex);
            LoadGrid();
            HideEditPanel();
        }
    }

    private void OnAddClick(object sender, EventArgs e)
    {
        _editingIndex = -1;
        _txtUsername.Text = "";
        _txtPassword.Text = "";
        _txtDisplayName.Text = "";
        _txtUsername.Enabled = true;
        _editPanel.Visible = true;
        _editPanel.BringToFront();
    }

    private void BeginEdit(int index)
    {
        if (index < 0 || index >= _accounts.Count) return;
        _editingIndex = index;
        CoHostAccount acct = _accounts[index];
        _txtUsername.Text = acct.Username;
        _txtPassword.Text = acct.Password;
        _txtDisplayName.Text = acct.DisplayName;
        _txtUsername.Enabled = false;
        _editPanel.Visible = true;
        _editPanel.BringToFront();
    }

    private void OnSaveClick(object sender, EventArgs e)
    {
        string username = _txtUsername.Text.Trim();
        string password = _txtPassword.Text;
        string displayName = _txtDisplayName.Text.Trim();

        if (string.IsNullOrEmpty(username))
        {
            MessageBox.Show("Username is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            MessageBox.Show("Password is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_editingIndex < 0)
        {
            // Adding new account — generate a unique hash for auto-login URL
            CoHostAccount newAcct = new CoHostAccount();
            newAcct.Username = username;
            newAcct.Password = password;
            newAcct.DisplayName = string.IsNullOrEmpty(displayName) ? username : displayName;
            newAcct.Hash = Guid.NewGuid().ToString("N").Substring(0, 12);
            _accounts.Add(newAcct);
        }
        else
        {
            // Editing existing account
            CoHostAccount existing = _accounts[_editingIndex];
            existing.Username = username;
            existing.Password = password;
            existing.DisplayName = string.IsNullOrEmpty(displayName) ? username : displayName;
        }

        LoadGrid();
        HideEditPanel();
    }

    private void OnCancelClick(object sender, EventArgs e)
    {
        HideEditPanel();
    }

    private void HideEditPanel()
    {
        _editPanel.Visible = false;
        _editingIndex = -1;
        _txtUsername.Text = "";
        _txtPassword.Text = "";
        _txtDisplayName.Text = "";
    }
}

public class PartylineStream : ISpecialAudioStream
{
    private NewPlugin _plugin;
    public PartylineStream(NewPlugin plugin) { _plugin = plugin; }
    public IStreamContainer CreateStream(string sParams) { return new PartylineStreamContainer(_plugin); }
}

public class PartylineStreamContainer : IStreamContainer
{
    private NewPlugin _plugin;
    public PartylineStreamContainer(NewPlugin plugin) { _plugin = plugin; }
    public int NumberOfChannels { get { return 1; } }
    public int SampleRate { get { return 44100; } }
    public StreamFunc GetStreamFunc() { return _plugin.FillAudioBuffer; }
    public void Cleanup() { }
}

public class PartylineControlPanel : UserControl
{
    private AudioMixer _audioMixer;
    private AuthenticationManager _authManager;
    private List<CoHostAccount> _accounts;
    private System.Windows.Forms.Timer _vuTimer;
    private List<CoHostRow> _rows;
    private ToolTip _toolTip;
    private SettingsManager _settingsManager;
    private int _pulseCounter;
    private Label _meshStatusLabel;
    public PartylineControlPanel(AudioMixer audioMixer, AuthenticationManager authManager, List<CoHostAccount> accounts, SettingsManager settingsManager)
    {
        _audioMixer = audioMixer;
        _settingsManager = settingsManager;
        _authManager = authManager;
        _accounts = accounts != null ? accounts : new List<CoHostAccount>();
        _rows = new List<CoHostRow>();

        _toolTip = new ToolTip();
        _toolTip.AutoPopDelay = 5000;
        _toolTip.InitialDelay = 400;
        _toolTip.ReshowDelay = 200;
        _toolTip.ShowAlways = true;

        AutoSize = true;
        Dock = DockStyle.Top;
        BackColor = System.Drawing.Color.FromArgb(30, 30, 50);
        Padding = new Padding(4);

        BuildRows();

        _vuTimer = new System.Windows.Forms.Timer();
        _vuTimer.Interval = 50;
        _vuTimer.Tick += OnVuTimerTick;
        _vuTimer.Start();
    }

    private void BuildRows()
    {
        Controls.Clear();
        _rows.Clear();

        // Title panel with darker background (section header style)
        Panel titlePanel = new Panel();
        titlePanel.Dock = DockStyle.Top;
        titlePanel.Height = 24;
        titlePanel.BackColor = System.Drawing.Color.FromArgb(50, 50, 55);

        Label titleLabel = new Label();
        titleLabel.Text = "Partyline";
        titleLabel.ForeColor = System.Drawing.Color.White;
        titleLabel.Font = new System.Drawing.Font("Segoe UI", 11f, System.Drawing.FontStyle.Regular);
        titleLabel.Dock = DockStyle.Fill;
        titleLabel.Padding = new Padding(2, 4, 0, 0);
        titlePanel.Controls.Add(titleLabel);

        Button configBtn = new Button();
        configBtn.Text = "\u2699 Configure";
        configBtn.FlatStyle = FlatStyle.Flat;
        configBtn.Size = new System.Drawing.Size(90, 22);
        configBtn.Dock = DockStyle.Right;
        configBtn.Font = new System.Drawing.Font("Segoe UI", 8f);
        configBtn.ForeColor = System.Drawing.Color.FromArgb(180, 180, 190);
        configBtn.BackColor = System.Drawing.Color.FromArgb(50, 50, 55);
        configBtn.FlatAppearance.BorderSize = 0;
        configBtn.Cursor = Cursors.Hand;
        configBtn.Click += OnConfigureClick;
        titlePanel.Controls.Add(configBtn);
        _toolTip.SetToolTip(configBtn, "Configure co-host accounts");

        // Mesh status indicator (pill label, docked right of config button).
        // Repointed from the former relay-status pill to the WebRTC mesh transport.
        _meshStatusLabel = new Label();
        _meshStatusLabel.Text = "Mesh";
        _meshStatusLabel.Font = new System.Drawing.Font("Segoe UI", 7f, System.Drawing.FontStyle.Bold);
        _meshStatusLabel.Size = new System.Drawing.Size(70, 18);
        _meshStatusLabel.Dock = DockStyle.Right;
        _meshStatusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
        _meshStatusLabel.Padding = new Padding(2, 3, 2, 0);
        _meshStatusLabel.ForeColor = System.Drawing.Color.Gray;
        _meshStatusLabel.BackColor = System.Drawing.Color.FromArgb(50, 50, 55);
        titlePanel.Controls.Add(_meshStatusLabel);
        _toolTip.SetToolTip(_meshStatusLabel, "WebRTC mesh connection status");

        Controls.Add(titlePanel);

        // 1px bottom border separator
        Panel separator = new Panel();
        separator.Dock = DockStyle.Top;
        separator.Height = 1;
        separator.BackColor = System.Drawing.Color.FromArgb(80, 80, 100);
        Controls.Add(separator);

        // Since Dock=Top adds in reverse visual order, we set BringToFront
        separator.BringToFront();
        titlePanel.BringToFront();

        int yOffset = 28;
        for (int i = 0; i < _accounts.Count; i++)
        {
            CoHostAccount acct = _accounts[i];
            CoHostRow row = CreateRow(acct, yOffset);
            _rows.Add(row);
            yOffset += 28;
        }

        Height = yOffset + 4;
    }

    private CoHostRow CreateRow(CoHostAccount account, int yOffset)
    {
        CoHostRow row = new CoHostRow();
        // The mesh/worker identity for a co-host is the published invite name
        // (DisplayName ?? Username), which becomes their peerId — and therefore the
        // key used by the AudioMixer, connection tracking, telemetry, and quality.
        // Row id MUST match that (mirrors PublishInvitesOnce) or per-row VU/latency/
        // connection/quality lookups silently miss.
        row.CohostId = !string.IsNullOrEmpty(account.DisplayName) ? account.DisplayName : account.Username;

        // Row panel
        Panel rowPanel = new Panel();
        rowPanel.Location = new System.Drawing.Point(4, yOffset);
        rowPanel.Size = new System.Drawing.Size(Width - 8, 26);
        rowPanel.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        rowPanel.BackColor = System.Drawing.Color.FromArgb(60, 60, 70);
        row.RowPanel = rowPanel;

        // Connected indicator (pill)
        Label connIndicator = new Label();
        connIndicator.Text = "Offline";
        connIndicator.ForeColor = System.Drawing.Color.Gray;
        connIndicator.BackColor = System.Drawing.Color.FromArgb(80, 80, 90);
        connIndicator.Font = new System.Drawing.Font("Segoe UI", 7f, System.Drawing.FontStyle.Bold);
        connIndicator.Location = new System.Drawing.Point(2, 5);
        connIndicator.Size = new System.Drawing.Size(60, 16);
        connIndicator.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
        rowPanel.Controls.Add(connIndicator);
        row.ConnectedIndicator = connIndicator;

        // Display name label
        Label nameLabel = new Label();
        nameLabel.Text = account.DisplayName != null ? account.DisplayName : account.Username;
        nameLabel.ForeColor = System.Drawing.Color.White;
        nameLabel.Font = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold);
        nameLabel.Location = new System.Drawing.Point(66, 4);
        nameLabel.Size = new System.Drawing.Size(80, 20);
        nameLabel.AutoEllipsis = true;
        rowPanel.Controls.Add(nameLabel);
        _toolTip.SetToolTip(nameLabel, "Co-host display name");

        // VU meter container (outer panel)
        Panel vuOuter = new Panel();
        vuOuter.Location = new System.Drawing.Point(148, 6);
        vuOuter.Size = new System.Drawing.Size(60, 14);
        vuOuter.BackColor = System.Drawing.Color.FromArgb(40, 40, 50);
        rowPanel.Controls.Add(vuOuter);
        _toolTip.SetToolTip(vuOuter, "Audio level from co-host");

        // VU meter fill (inner panel)
        Panel vuFill = new Panel();
        vuFill.Location = new System.Drawing.Point(0, 0);
        vuFill.Size = new System.Drawing.Size(0, 14);
        vuFill.BackColor = System.Drawing.Color.FromArgb(34, 197, 94);
        vuOuter.Controls.Add(vuFill);
        row.VuFill = vuFill;
        row.VuOuter = vuOuter;

        // Combined latency + IP label
        Label latencyLabel = new Label();
        latencyLabel.Text = "";
        latencyLabel.ForeColor = System.Drawing.Color.White;
        latencyLabel.Font = new System.Drawing.Font("Segoe UI", 8f);
        latencyLabel.Location = new System.Drawing.Point(212, 5);
        latencyLabel.Size = new System.Drawing.Size(130, 16);
        latencyLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        rowPanel.Controls.Add(latencyLabel);
        row.LatencyLabel = latencyLabel;

        // Mute button (anchored to right)
        Button muteBtn = new Button();
        muteBtn.Text = "Mute";
        muteBtn.FlatStyle = FlatStyle.Flat;
        muteBtn.Size = new System.Drawing.Size(50, 22);
        muteBtn.Font = new System.Drawing.Font("Segoe UI", 7.5f);
        muteBtn.ForeColor = System.Drawing.Color.White;
        muteBtn.BackColor = System.Drawing.Color.FromArgb(70, 70, 85);
        muteBtn.FlatAppearance.BorderSize = 0;
        muteBtn.Cursor = Cursors.Hand;
        muteBtn.Tag = row.CohostId;
        muteBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        muteBtn.Click += OnMuteClick;
        rowPanel.Controls.Add(muteBtn);
        row.MuteButton = muteBtn;
        _toolTip.SetToolTip(muteBtn, "Mute/unmute co-host audio");

        // Kick button (anchored to right)
        Button kickBtn = new Button();
        kickBtn.Text = "Kick";
        kickBtn.FlatStyle = FlatStyle.Flat;
        kickBtn.Size = new System.Drawing.Size(40, 22);
        kickBtn.Font = new System.Drawing.Font("Segoe UI", 7.5f);
        kickBtn.ForeColor = System.Drawing.Color.White;
        kickBtn.BackColor = System.Drawing.Color.FromArgb(180, 60, 60);
        kickBtn.FlatAppearance.BorderSize = 0;
        kickBtn.Cursor = Cursors.Hand;
        kickBtn.Tag = row.CohostId;
        kickBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        kickBtn.Click += OnKickClick;
        rowPanel.Controls.Add(kickBtn);
        row.KickButton = kickBtn;
        _toolTip.SetToolTip(kickBtn, "Disconnect co-host");

        // Position buttons from right edge
        int rightEdge = rowPanel.Width;
        kickBtn.Location = new System.Drawing.Point(rightEdge - 64, 2);
        muteBtn.Location = new System.Drawing.Point(rightEdge - 118, 2);

        Controls.Add(rowPanel);
        return row;
    }

    private void OnMuteClick(object sender, EventArgs e)
    {
        Button btn = sender as Button;
        if (btn == null) return;
        string cohostId = btn.Tag as string;
        if (cohostId == null) return;

        bool currentMuted = _audioMixer.GetMuted(cohostId);
        _audioMixer.SetMuted(cohostId, !currentMuted);
        // Reflect the new state on the co-host's web page (drives their ON-AIR sign).
        NewPlugin.PublishCohostMuteState(cohostId, !currentMuted);

        UpdateRowState(cohostId);
    }

    private void OnKickClick(object sender, EventArgs e)
    {
        Button btn = sender as Button;
        if (btn == null) return;
        string cohostId = btn.Tag as string;
        if (cohostId == null) return;

        _authManager.InvalidateSession(cohostId);
        _audioMixer.RemoveCoHost(cohostId);
        // Forcibly drop the live WebRTC connection so the co-host is actually
        // disconnected (not just removed from the mix) and can reconnect cleanly.
        NewPlugin.KickPeer(cohostId);

        UpdateRowState(cohostId);
    }

    private void OnConfigureClick(object sender, EventArgs e)
    {
        var form = new PartylineConfigForm(_settingsManager, NewPlugin.ActiveMesh);
        form.ShowDialog();
    }

    private void UpdateRowState(string cohostId)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            CoHostRow row = _rows[i];
            if (row.CohostId == cohostId)
            {
                bool isMuted = _audioMixer.GetMuted(cohostId);

                // Update mute button
                if (isMuted)
                {
                    row.MuteButton.Text = "Unmute";
                    row.MuteButton.BackColor = System.Drawing.Color.FromArgb(180, 120, 30);
                }
                else
                {
                    row.MuteButton.Text = "Mute";
                    row.MuteButton.BackColor = System.Drawing.Color.FromArgb(70, 70, 85);
                }

                // Row background reflects on-air state. Co-hosts are always live once
                // connected, so "on air" == not muted (green); muted == off air (gray).
                if (!isMuted)
                {
                    row.RowPanel.BackColor = System.Drawing.Color.FromArgb(30, 70, 40);
                }
                else
                {
                    row.RowPanel.BackColor = System.Drawing.Color.FromArgb(60, 60, 70);
                }

                break;
            }
        }
    }

    private void OnVuTimerTick(object sender, EventArgs e)
    {
        _pulseCounter++;

        // Update mesh status indicator
        if (_meshStatusLabel != null)
        {
            if (!NewPlugin.IsMeshEnabled)
            {
                _meshStatusLabel.Text = "No Mesh";
                _meshStatusLabel.ForeColor = System.Drawing.Color.FromArgb(140, 140, 150);
                _meshStatusLabel.BackColor = System.Drawing.Color.FromArgb(50, 50, 55);
            }
            else if (NewPlugin.IsMeshConnected)
            {
                _meshStatusLabel.Text = "\u25CF Mesh";
                _meshStatusLabel.ForeColor = System.Drawing.Color.FromArgb(34, 197, 94);
                _meshStatusLabel.BackColor = System.Drawing.Color.FromArgb(20, 50, 30);
            }
            else
            {
                _meshStatusLabel.Text = "\u2716 Mesh";
                _meshStatusLabel.ForeColor = System.Drawing.Color.FromArgb(239, 68, 68);
                _meshStatusLabel.BackColor = System.Drawing.Color.FromArgb(60, 30, 30);
            }
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            CoHostRow row = _rows[i];
            float level = _audioMixer.GetLevel(row.CohostId);
            int fillWidth = (int)(level * row.VuOuter.Width);
            if (fillWidth < 0) fillWidth = 0;
            if (fillWidth > row.VuOuter.Width) fillWidth = row.VuOuter.Width;
            row.VuFill.Width = fillWidth;

            // Color based on level
            if (level > 0.8f)
            {
                row.VuFill.BackColor = System.Drawing.Color.FromArgb(239, 68, 68);
            }
            else if (level > 0.5f)
            {
                row.VuFill.BackColor = System.Drawing.Color.FromArgb(234, 179, 8);
            }
            else
            {
                row.VuFill.BackColor = System.Drawing.Color.FromArgb(34, 197, 94);
            }

            // Update connected indicator pill — driven by the live WebRTC mesh
            // connection state (the relay-era AuthenticationManager no longer tracks
            // mesh co-hosts, which is why this previously showed "Offline").
            bool connected = NewPlugin.IsPeerConnected(row.CohostId);
            NewPlugin.CohostNetStat net = connected ? NewPlugin.GetCohostNetStat(row.CohostId) : null;
            if (connected)
            {
                // Traffic light: color the pill by network quality when we have a
                // telemetry sample; fall back to the throbbing green "Connected".
                if (net != null && net.Quality == "poor")
                {
                    row.ConnectedIndicator.Text = "\u25CF Poor";
                    row.ConnectedIndicator.ForeColor = System.Drawing.Color.White;
                    row.ConnectedIndicator.BackColor = System.Drawing.Color.FromArgb(239, 68, 68);
                }
                else if (net != null && net.Quality == "fair")
                {
                    row.ConnectedIndicator.Text = "\u25CF Fair";
                    row.ConnectedIndicator.ForeColor = System.Drawing.Color.FromArgb(40, 30, 0);
                    row.ConnectedIndicator.BackColor = System.Drawing.Color.FromArgb(245, 158, 11);
                }
                else
                {
                    row.ConnectedIndicator.Text = (net != null) ? "\u25CF Good" : "Connected";
                    row.ConnectedIndicator.ForeColor = System.Drawing.Color.White;
                    // Throb between bright green and dimmer green every 500ms (10 ticks at 50ms)
                    bool bright = ((_pulseCounter / 10) % 2) == 0;
                    row.ConnectedIndicator.BackColor = bright
                        ? System.Drawing.Color.FromArgb(34, 197, 94)
                        : System.Drawing.Color.FromArgb(24, 157, 74);
                }
            }
            else
            {
                row.ConnectedIndicator.Text = "Offline";
                row.ConnectedIndicator.ForeColor = System.Drawing.Color.Gray;
                row.ConnectedIndicator.BackColor = System.Drawing.Color.FromArgb(80, 80, 90);
            }

            // Update combined latency / loss / jitter + IP display
            if (connected)
            {
                string combined = "";
                if (net != null)
                {
                    combined = net.Rtt + "ms \u00b7 " + net.Loss + "% \u00b7 " + net.Jitter + "ms jit";
                }
                else
                {
                    float latency = _audioMixer.GetLatency(row.CohostId);
                    if (latency > 0) combined = ((int)latency).ToString() + "ms";
                }
                string ip = _audioMixer.GetIp(row.CohostId);
                if (ip != null && ip.Length > 0)
                {
                    combined = combined.Length > 0 ? (combined + " | " + ip) : ip;
                }
                // Show when the remote presenter has muted their OWN mic (distinct
                // from the DJ muting them via the row's Mute button).
                if (NewPlugin.IsCohostSelfMuted(row.CohostId))
                {
                    combined = "\uD83D\uDD07 SELF-MUTED" + (combined.Length > 0 ? "  \u00b7  " + combined : "");
                }
                row.LatencyLabel.Text = combined;
            }
            else
            {
                row.LatencyLabel.Text = "";
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_vuTimer != null)
        {
            _vuTimer.Stop();
            _vuTimer.Dispose();
        }
        if (_toolTip != null)
        {
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal class CoHostRow
{
    private string _cohostId;
    private Panel _rowPanel;
    private Panel _vuFill;
    private Panel _vuOuter;
    private Button _muteButton;
    private Button _kickButton;
    private Label _connectedIndicator;
    private Label _latencyLabel;

    public string CohostId
    {
        get { return _cohostId; }
        set { _cohostId = value; }
    }

    public Panel RowPanel
    {
        get { return _rowPanel; }
        set { _rowPanel = value; }
    }

    public Panel VuFill
    {
        get { return _vuFill; }
        set { _vuFill = value; }
    }

    public Panel VuOuter
    {
        get { return _vuOuter; }
        set { _vuOuter = value; }
    }

    public Button MuteButton
    {
        get { return _muteButton; }
        set { _muteButton = value; }
    }

    public Button KickButton
    {
        get { return _kickButton; }
        set { _kickButton = value; }
    }

    public Label ConnectedIndicator
    {
        get { return _connectedIndicator; }
        set { _connectedIndicator = value; }
    }

    public Label LatencyLabel
    {
        get { return _latencyLabel; }
        set { _latencyLabel = value; }
    }
}

// =============================================================================
// WebRTC mesh layer (Requirements 5.1, 6.4)
//
// This is the new co-host audio transport that replaces the WebSocket raw-PCM
// relay. The actual libwebrtc binding is hidden behind IWebRtcPeer so the
// MR-WebRTC primary adapter (task 5.2) and the SIPSorcery fallback adapter
// (task 5.3) can be swapped without touching the signaling client.
//
// NOTE: The deprecated WebSocket raw-PCM relay (RelaySignalingClient /
// AudioStreamLoop / OnRelayBinaryReceived) was removed in task 5.7; this mesh
// transport replaces it end to end.
// =============================================================================

/// <summary>
/// A single ICE server entry returned by GET /api/rtc-config/:slug.
/// </summary>
[Serializable]
public class WebRtcIceServer
{
    public string[] Urls;
    public string Username;
    public string Credential;
}

/// <summary>
/// Abstraction over the underlying libwebrtc-based binding. One implementation
/// wraps Microsoft.MixedReality.WebRTC (primary, task 5.2); another wraps
/// SIPSorcery + Concentus (fallback, task 5.3). The signaling client
/// (WebRtcMeshClient) drives an instance of this interface and never depends on
/// the concrete binding types.
///
/// All audio is mono Opus on the wire; PCM is exchanged across this boundary as
/// PCM16 (short[]) and the binding performs the Opus encode/decode.
/// </summary>
public interface IWebRtcPeer
{
    // --- ICE configuration ---

    /// <summary>Apply the ICE servers fetched from /api/rtc-config/:slug.</summary>
    void SetIceServers(WebRtcIceServer[] iceServers);

    // --- Connection lifecycle (one RTCPeerConnection per remote peerId) ---

    /// <summary>Create (or reuse) the peer connection to the given remote peer.</summary>
    void CreatePeerConnection(string peerId);

    /// <summary>Close and dispose the connection to a single remote peer.</summary>
    void ClosePeerConnection(string peerId);

    /// <summary>Close every peer connection (used on shutdown).</summary>
    void CloseAll();

    // --- SDP negotiation ---

    /// <summary>Create an SDP offer for a remote peer. Returns the SDP text.</summary>
    string CreateOffer(string peerId);

    /// <summary>Create an SDP answer for a remote peer after a remote offer was applied.</summary>
    string CreateAnswer(string peerId);

    /// <summary>Apply a remote SDP description. <paramref name="type"/> is "offer" or "answer".</summary>
    void ApplyRemoteDescription(string peerId, string type, string sdp);

    // --- Trickle ICE ---

    /// <summary>Apply a remote ICE candidate (raw RTCIceCandidateInit JSON).</summary>
    void AddIceCandidate(string peerId, string candidateJson);

    // --- Outbound audio ---

    /// <summary>
    /// Feed one frame of the external outbound audio source (the BASS main-mix tap).
    /// PCM is mono/stereo PCM16 at <paramref name="sampleRate"/>; the binding encodes
    /// it to mono Opus and transmits to every connected peer. The same source is
    /// shared across all peer connections so encode happens once.
    /// </summary>
    void PushOutboundAudio(short[] pcm, int sampleCount, int sampleRate, int channels);

    // --- Callbacks (Action delegates for C# 5 compatibility) ---

    /// <summary>(peerId, candidateJson) raised when the binding discovers a local ICE candidate.</summary>
    Action<string, string> OnLocalIceCandidate { get; set; }

    /// <summary>(peerId, pcm, sampleCount, sampleRate) raised with decoded remote PCM16, per remote peer.</summary>
    Action<string, short[], int, int> OnRemoteAudioFrame { get; set; }

    /// <summary>(peerId, state) raised on connection-state transitions, e.g. "failed" for Req 1.5.</summary>
    Action<string, string> OnConnectionStateChanged { get; set; }
}

/// <summary>
/// Placeholder no-op IWebRtcPeer so the signaling client compiles and can be
/// wired before the real adapters land. Replaced by the MR-WebRTC adapter
/// (task 5.2) and the SIPSorcery fallback (task 5.3). DO NOT ship as the
/// runtime peer -- it carries no media.
/// </summary>
public class NoOpWebRtcPeer : IWebRtcPeer
{
    public Action<string, string> OnLocalIceCandidate { get; set; }
    public Action<string, short[], int, int> OnRemoteAudioFrame { get; set; }
    public Action<string, string> OnConnectionStateChanged { get; set; }

    public void SetIceServers(WebRtcIceServer[] iceServers) { }
    public void CreatePeerConnection(string peerId) { }
    public void ClosePeerConnection(string peerId) { }
    public void CloseAll() { }
    public string CreateOffer(string peerId) { return null; }
    public string CreateAnswer(string peerId) { return null; }
    public void ApplyRemoteDescription(string peerId, string type, string sdp) { }
    public void AddIceCandidate(string peerId, string candidateJson) { }
    public void PushOutboundAudio(short[] pcm, int sampleCount, int sampleRate, int channels) { }
}

/// <summary>
/// Signaling client for the WebRTC mesh. Speaks ONLY plain HTTPS (no WebSocket,
/// per Requirement 6):
///   * GET  /api/rtc-config/:slug         -> ICE servers (fetched at join)
///   * GET  /api/telemetry/stream/:slug   -> SSE; parse "signal" events for peerId "plugin"
///   * POST /api/signal/:slug             -> publish offer/answer/ice + join/leave/mic-state
///
/// It drives an IWebRtcPeer instance: applies remote SDP/ICE, requests offers
/// and answers, and forwards locally-discovered ICE candidates. Mirrors the
/// existing RelaySignalingClient patterns (TLS 1.2 enablement, background
/// reconnect loop with exponential backoff, CancellationToken usage, and the
/// same lightweight JSON helpers / Log() style).
/// </summary>
public class WebRtcMeshClient
{
    private readonly string _baseUrl;     // e.g. https://partyline.example.com
    private readonly string _slug;        // room slug
    private readonly string _peerId;      // "plugin"
    private readonly string _role;        // "plugin"
    private readonly IWebRtcPeer _peer;

    // Room password used to authenticate as the DJ to POST /api/plugin/auth and
    // obtain a signaling session token. The co-host accounts are published as
    // room invites once, right after auth, so browser co-hosts are authorized by
    // the plugin's local account list.
    private readonly string _password;
    private List<CoHostAccount> _accounts;
    private string _metaRoomName;
    private string _metaStationName;
    private string _metaDjName;
    private string _metaRoomCode;        // DJ-chosen custom room id (optional)
    private string _token;                // bearer session token (role 'dj')
    private volatile bool _invitesPublished;
    private volatile bool _metaPublished;

    private HttpClient _httpClient;       // short requests (config + POST signal)
    private HttpClient _streamClient;     // long-lived SSE read (infinite timeout)

    private Thread _reconnectThread;
    private Thread _heartbeatThread;
    private volatile bool _heartbeatRunning;
    private volatile bool _running;
    private volatile bool _connected;
    private volatile bool _micOn;
    private int _reconnectDelay = 1000;
    private CancellationTokenSource _cts;

    // Remote peers we have observed in the room (keyed by peerId).
    private readonly ConcurrentDictionary<string, byte> _knownPeers = new ConcurrentDictionary<string, byte>();

    public bool IsConnected
    {
        get { return _connected; }
    }

    public WebRtcMeshClient(string baseUrl, string slug, string peerId, IWebRtcPeer peer)
        : this(baseUrl, slug, peerId, peer, null, null, null)
    {
    }

    public WebRtcMeshClient(string baseUrl, string slug, string peerId, IWebRtcPeer peer, string password, List<CoHostAccount> accounts)
        : this(baseUrl, slug, peerId, peer, password, accounts, null)
    {
    }

    public WebRtcMeshClient(string baseUrl, string slug, string peerId, IWebRtcPeer peer, string password, List<CoHostAccount> accounts, string[] meta)
    {
        _baseUrl = baseUrl != null ? baseUrl : "";
        _slug = slug != null ? slug : "";
        _peerId = (peerId != null && peerId.Length > 0) ? peerId : "plugin";
        _role = "plugin";
        _peer = peer;
        _password = password != null ? password : "";
        _accounts = accounts;
        _metaRoomName = (meta != null && meta.Length > 0) ? (meta[0] ?? "") : "";
        _metaStationName = (meta != null && meta.Length > 1) ? (meta[1] ?? "") : "";
        _metaDjName = (meta != null && meta.Length > 2) ? (meta[2] ?? "") : "";
        _metaRoomCode = (meta != null && meta.Length > 3) ? (meta[3] ?? "") : "";
    }

    /// <summary>
    /// Applies updated co-host accounts + display metadata (e.g. after the Configure
    /// dialog is saved) and re-publishes them to the signaling server immediately,
    /// so a plugin restart is not needed for invite links / names to take effect.
    /// Safe to call before the client has connected (republish will then happen on
    /// connect). 
    /// </summary>
    public void RepublishConfig(List<CoHostAccount> accounts, string[] meta)
    {
        _accounts = accounts;
        _metaRoomName = (meta != null && meta.Length > 0) ? (meta[0] ?? "") : "";
        _metaStationName = (meta != null && meta.Length > 1) ? (meta[1] ?? "") : "";
        _metaDjName = (meta != null && meta.Length > 2) ? (meta[2] ?? "") : "";
        _metaRoomCode = (meta != null && meta.Length > 3) ? (meta[3] ?? "") : "";
        _invitesPublished = false;
        _metaPublished = false;
        // Only push now if we already hold a session token; otherwise the connect
        // path will publish with the refreshed values.
        if (!string.IsNullOrEmpty(_token))
        {
            try { PublishInvitesOnce(); } catch (Exception ex) { Log("RepublishConfig invites error: " + ex.Message); }
            try { PublishMetaOnce(); } catch (Exception ex) { Log("RepublishConfig meta error: " + ex.Message); }
        }
    }

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = true;

        if (_peer != null)
        {
            // Forward locally-discovered ICE candidates to the addressed peer.
            _peer.OnLocalIceCandidate = OnPeerIceCandidate;
        }

        _reconnectThread = new Thread(ReconnectLoop);
        _reconnectThread.IsBackground = true;
        _reconnectThread.Name = "WebRtcMeshSignaling";
        _reconnectThread.Start();
    }

    public void Stop()
    {
        _running = false;
        try
        {
            // Best-effort leave so the roster prunes us promptly.
            PostSignal("leave", null, "\"role\":\"" + EscapeJson(_role) + "\"");
        }
        catch { }

        if (_cts != null)
        {
            _cts.Cancel();
        }

        if (_peer != null)
        {
            try { _peer.CloseAll(); }
            catch (Exception ex) { Log("CloseAll error: " + ex.Message); }
        }

        DisposeClients();

        if (_reconnectThread != null && _reconnectThread.IsAlive)
        {
            _reconnectThread.Join(5000);
        }
    }

    /// <summary>Latching mic toggle. Broadcasts mic-state to the room (Req 7.3-7.8).</summary>
    public void SetMicState(bool on)
    {
        _micOn = on;
        PostSignal("mic-state", null, "\"micOn\":" + (on ? "true" : "false"));
    }

    public bool MicOn
    {
        get { return _micOn; }
    }

    // --- Connection loop -----------------------------------------------------

    private void ReconnectLoop()
    {
        _reconnectDelay = 1000;
        EnsureClients();

        // Full provision/auth/publish only on first connect or after a failure. On a
        // routine SSE drop we just refresh the token and re-open the stream, so the
        // plugin starts draining its signaling mailbox again within ~1s instead of
        // spending ~9s re-running the whole sequence (which strands co-host offers).
        bool needFullInit = true;

        while (_running && !_cts.IsCancellationRequested)
        {
            try
            {
                if (needFullInit)
                {
                    Provision();
                    Authenticate();
                    // Republish invites + metadata so the current co-host codes/names
                    // are always live server-side.
                    _invitesPublished = false;
                    _metaPublished = false;
                    PublishInvitesOnce();
                    PublishMetaOnce();
                    FetchRtcConfig();
                    needFullInit = false;
                }
                else
                {
                    // Lightweight reconnect: refresh the auth token only (one request),
                    // then re-open the stream immediately. Provision/invites/meta/ICE
                    // are unchanged since the first connect.
                    Authenticate();
                }
                Join();
                _connected = true;
                _reconnectDelay = 1000; // reset on a successful (re)subscribe
                StartHeartbeat();       // keep our roster presence fresh (< TTL)
                try
                {
                    OpenSignalStream(); // blocks until the SSE stream drops
                }
                finally
                {
                    StopHeartbeat();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("Signaling connection lost: " + ex.Message);
                // A failed attempt may mean the token expired or the room state was
                // lost; force a full provision/auth/publish on the next attempt.
                needFullInit = true;
            }

            _connected = false;
            if (!_running || _cts.IsCancellationRequested) break;

            Log("Reconnecting signaling in " + _reconnectDelay + "ms...");
            try { Thread.Sleep(_reconnectDelay); }
            catch (ThreadInterruptedException) { break; }

            // Exponential backoff: 1s -> 2s -> 4s -> 8s -> 16s -> 30s cap
            _reconnectDelay = Math.Min(_reconnectDelay * 2, 30000);
        }
    }

    private void EnsureClients()
    {
        // Ensure TLS 1.2 is enabled (.NET 4.8 may default to older protocols).
        System.Net.ServicePointManager.SecurityProtocol =
            System.Net.ServicePointManager.SecurityProtocol | System.Net.SecurityProtocolType.Tls12;

        if (_httpClient == null)
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
        }
        if (_streamClient == null)
        {
            _streamClient = new HttpClient();
            _streamClient.Timeout = Timeout.InfiniteTimeSpan; // SSE is long-lived
        }
    }

    private void DisposeClients()
    {
        try { if (_httpClient != null) _httpClient.Dispose(); } catch { }
        try { if (_streamClient != null) _streamClient.Dispose(); } catch { }
        _httpClient = null;
        _streamClient = null;
    }

    // --- DJ authentication + invite publishing ------------------------------

    /// <summary>
    /// Immediately (re)publishes the DJ's custom Room ID to the server using the
    /// stored slug + djKey, without waiting for the next reconnect. Used by the
    /// Configure dialog's Save button so the co-host link goes live at once.
    /// Returns false (with <paramref name="error"/>) if the server can't be
    /// reached; sets <paramref name="conflict"/> when the code is owned by another
    /// room. Safe to call on a running client (idempotent provision).
    /// </summary>
    public bool TryPublishRoomCode(string roomCode, out bool conflict, out string error)
    {
        conflict = false;
        error = null;
        try
        {
            _metaRoomCode = roomCode != null ? roomCode : "";
            if (string.IsNullOrEmpty(_baseUrl) || string.IsNullOrEmpty(_slug))
            {
                error = "no signaling URL/room configured";
                return false;
            }
            EnsureClients();
            string url = _baseUrl.TrimEnd('/') + "/api/plugin/provision/" + Uri.EscapeDataString(_slug);
            string body = "{\"djKey\":\"" + EscapeJson(_password) + "\""
                + (string.IsNullOrEmpty(_metaRoomCode) ? "" : ",\"roomCode\":\"" + EscapeJson(_metaRoomCode) + "\"")
                + "}";
            string resp = HttpPost(url, body);
            if (resp == null) { error = "no response"; return false; }
            if (resp.IndexOf("\"roomCodeConflict\":true", StringComparison.OrdinalIgnoreCase) >= 0)
                conflict = true;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Claims/refreshes this plugin's room on the signaling server using the
    /// private DJ key. Idempotent: creates the room on first run, and on later
    /// startups simply re-validates and refreshes the room's lifetime. Throws on
    /// failure so ReconnectLoop backs off and retries. A 403 here means the room
    /// id is already claimed by a different DJ key (should never happen for a
    /// stable install).
    /// </summary>
    private void Provision()
    {
        string url = _baseUrl.TrimEnd('/') + "/api/plugin/provision/" + Uri.EscapeDataString(_slug);
        string body = "{\"djKey\":\"" + EscapeJson(_password) + "\""
            + (string.IsNullOrEmpty(_metaRoomCode) ? "" : ",\"roomCode\":\"" + EscapeJson(_metaRoomCode) + "\"")
            + "}";
        string resp = HttpPost(url, body);
        if (resp == null)
        {
            throw new Exception("Room provision failed (no response).");
        }
        if (!string.IsNullOrEmpty(_metaRoomCode) && resp.IndexOf("\"roomCodeConflict\":true", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Log("WARNING: Room ID '" + _metaRoomCode + "' is already in use by another room; "
                + "co-hosts must use the auto-generated link until you choose a different Room ID.");
        }
        Log("Room provisioned/claimed: " + _slug);
    }

    /// <summary>
    /// Authenticates the plugin as the room DJ using the configured room password
    /// and stores the returned signaling session token. The token is attached as
    /// a Bearer header on the short-request HttpClient (covers FetchRtcConfig and
    /// every PostSignal) and as a ?token= query param on the SSE stream URL.
    /// Throws on failure so ReconnectLoop backs off and retries.
    /// </summary>
    private void Authenticate()
    {
        string url = _baseUrl.TrimEnd('/') + "/api/plugin/auth/" + Uri.EscapeDataString(_slug);
        string body = "{\"password\":\"" + EscapeJson(_password) + "\"}";
        string resp = HttpPost(url, body);
        if (resp == null)
        {
            throw new Exception("Plugin auth failed (no/again-error response). Check the room password and that the room exists.");
        }
        string token = ExtractJsonValue(resp, "token");
        if (token == null || token.Length == 0)
        {
            throw new Exception("Plugin auth returned no token.");
        }
        _token = token;
        // Attach the bearer token to all subsequent short requests.
        try
        {
            _httpClient.DefaultRequestHeaders.Remove("Authorization");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }
        catch { }
        Log("Plugin authenticated as DJ; signaling token acquired.");
    }

    /// <summary>
    /// Publishes the plugin's local co-host accounts as room invites (once per
    /// process) so browser co-hosts authenticate against the plugin's account
    /// list. Each invite's id is the account's stable Hash, matching the join URL
    /// the config dialog generates (.../room/&lt;slug&gt;?invite=&lt;hash&gt;).
    /// Best-effort: logs and continues on failure (mesh still works for the DJ).
    /// </summary>
    private void PublishInvitesOnce()
    {
        if (_invitesPublished) return;
        if (_accounts == null || _accounts.Count == 0) { _invitesPublished = true; return; }

        var sb = new StringBuilder();
        sb.Append("{\"invites\":[");
        int n = 0;
        int skipped = 0;
        for (int i = 0; i < _accounts.Count; i++)
        {
            CoHostAccount a = _accounts[i];
            if (a == null || string.IsNullOrEmpty(a.Hash) || string.IsNullOrEmpty(a.Password))
            {
                // A co-host with no stored plaintext password can't be (re)published
                // (the server needs it to hash the invite). This silently broke
                // co-host links after editing an account without re-entering a password.
                skipped++;
                Log("Invite NOT published for '" + (a != null ? (a.Username ?? a.DisplayName ?? "?") : "null")
                    + "': missing " + (a == null ? "account" : (string.IsNullOrEmpty(a.Hash) ? "hash" : "password"))
                    + " (re-enter the co-host password in Configure to publish its link).");
                continue;
            }
            string name = !string.IsNullOrEmpty(a.DisplayName) ? a.DisplayName : a.Username;
            if (string.IsNullOrEmpty(name)) name = a.Hash;
            // The invite id IS the short 6-char code used in the co-host URL.
            string code = NewPlugin.CohostCode(a.Hash);
            if (n > 0) sb.Append(",");
            sb.Append("{\"inviteId\":\"").Append(EscapeJson(code)).Append("\",");
            sb.Append("\"name\":\"").Append(EscapeJson(name)).Append("\",");
            sb.Append("\"password\":\"").Append(EscapeJson(a.Password)).Append("\"}");
            Log("Publishing co-host invite code '" + code + "' for '" + name + "'.");
            n++;
        }
        sb.Append("]");
        // Refresh the DJ's custom Room ID alongside the invites (provision also
        // publishes it; this keeps it live on account-edit republishes).
        if (!string.IsNullOrEmpty(_metaRoomCode))
            sb.Append(",\"roomCode\":\"").Append(EscapeJson(_metaRoomCode)).Append("\"");
        sb.Append("}");

        if (n == 0)
        {
            _invitesPublished = true;
            Log("No co-host invites published (" + skipped + " skipped). Co-host links will 404 until an account with a password is saved.");
            return;
        }

        string url = _baseUrl.TrimEnd('/') + "/api/plugin/invites/" + Uri.EscapeDataString(_slug);
        string resp = HttpPost(url, sb.ToString());
        if (resp != null)
        {
            _invitesPublished = true;
            Log("Published " + n + " co-host invite(s) to the room.");
        }
        else
        {
            Log("WARNING: failed to publish co-host invites (will retry on reconnect).");
        }
    }

    /// <summary>
    /// Publishes the room display metadata (room/station/DJ names) so the co-host
    /// page can render them. Once per process; retried on reconnect if it fails.
    /// </summary>
    private void PublishMetaOnce()
    {
        if (_metaPublished) return;
        string body = "{\"roomName\":\"" + EscapeJson(_metaRoomName) + "\","
            + "\"stationName\":\"" + EscapeJson(_metaStationName) + "\","
            + "\"djName\":\"" + EscapeJson(_metaDjName) + "\"}";
        string url = _baseUrl.TrimEnd('/') + "/api/plugin/meta/" + Uri.EscapeDataString(_slug);
        string resp = HttpPost(url, body);
        if (resp != null)
        {
            _metaPublished = true;
            Log("Published room metadata (station/room/DJ names).");
        }
        else
        {
            Log("WARNING: failed to publish room metadata (will retry on reconnect).");
        }
    }

    // --- ICE configuration ---------------------------------------------------

    private void FetchRtcConfig()
    {
        string url = _baseUrl.TrimEnd('/') + "/api/rtc-config/" + Uri.EscapeDataString(_slug);
        string body = HttpGet(url);
        if (body == null)
        {
            Log("rtc-config fetch returned no body");
            return;
        }
        WebRtcIceServer[] servers = ParseIceServers(body);
        if (_peer != null)
        {
            _peer.SetIceServers(servers);
        }
        Log("Applied " + servers.Length + " ICE server entries");
    }

    // --- Join / roster -------------------------------------------------------

    /// <summary>
    /// Periodically re-posts `join` to refresh this peer's roster presence so it
    /// stays within the server's presence TTL (otherwise late-joining peers won't
    /// see us and never send an offer). Runs while the SSE stream is open.
    /// </summary>
    private void StartHeartbeat()
    {
        StopHeartbeat();
        _heartbeatRunning = true;
        _heartbeatThread = new Thread(() =>
        {
            while (_heartbeatRunning && _running && !_cts.IsCancellationRequested)
            {
                try { Thread.Sleep(5000); }
                catch (ThreadInterruptedException) { break; }
                if (!_heartbeatRunning || _cts.IsCancellationRequested) break;
                try { Join(); }
                catch (Exception ex) { Log("Heartbeat join error: " + ex.Message); }
                try { FetchCohostStats(); }
                catch (Exception ex) { Log("Heartbeat stats error: " + ex.Message); }
            }
        });
        _heartbeatThread.IsBackground = true;
        _heartbeatThread.Name = "WebRtcMeshHeartbeat";
        _heartbeatThread.Start();
    }

    private void StopHeartbeat()
    {
        _heartbeatRunning = false;
        Thread t = _heartbeatThread;
        _heartbeatThread = null;
        if (t != null && t.IsAlive)
        {
            try { t.Join(1000); } catch { }
        }
    }

    /// <summary>
    /// Fetches per-co-host network telemetry (rtt/loss/jitter/quality) from the
    /// signaling server and publishes it for the UI strip's traffic-light display.
    /// Uses the authenticated short-request client (Bearer token already attached).
    /// </summary>
    private void FetchCohostStats()
    {
        string url = _baseUrl.TrimEnd('/') + "/api/telemetry/stats/" + Uri.EscapeDataString(_slug);
        string resp = HttpGet(url);
        if (resp == null) return;

        List<string> rows = ExtractJsonArrayObjects(resp, "stats");
        for (int i = 0; i < rows.Count; i++)
        {
            string obj = rows[i];
            string userId = ExtractJsonValue(obj, "user_id");
            if (string.IsNullOrEmpty(userId) || userId == _peerId) continue;
            string quality = ExtractJsonValue(obj, "quality");
            int rtt = ParseIntSafe(ExtractJsonValue(obj, "rtt"));
            int loss = ParseIntSafe(ExtractJsonValue(obj, "packet_loss"));
            int jitter = ParseIntSafe(ExtractJsonValue(obj, "jitter"));
            NewPlugin.PublishCohostNetStat(userId, quality, rtt, loss, jitter);
        }
    }

    private static int ParseIntSafe(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        double d;
        if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d))
        {
            return (int)Math.Round(d);
        }
        return 0;
    }

    /// <summary>
    /// Publishes a single co-host's mute state to the server's latest-wins mute map
    /// (POST /api/telemetry/mute-state/:slug). The co-host page reads its own entry
    /// to flash the ON-AIR sign when the DJ unmutes it. Fire-and-forget.
    /// </summary>
    public void PublishMuteState(string peerId, bool muted)
    {
        if (string.IsNullOrEmpty(peerId)) return;
        try
        {
            EnsureClients();
            string url = _baseUrl.TrimEnd('/') + "/api/telemetry/mute-state/" + Uri.EscapeDataString(_slug);
            string body = "{\"peerId\":\"" + EscapeJson(peerId) + "\",\"muted\":" + (muted ? "true" : "false") + "}";
            HttpPost(url, body);
        }
        catch (Exception ex)
        {
            Log("PublishMuteState error: " + ex.Message);
        }
    }

    private void Join()
    {
        string resp = PostSignal("join", null, "\"role\":\"" + EscapeJson(_role) + "\"");
        if (resp == null) return;

        // The join response returns the current roster; offer to existing peers.
        List<string> peerObjs = ExtractJsonArrayObjects(resp, "peers");
        if (peerObjs.Count == 0) peerObjs = ExtractJsonArrayObjects(resp, "roster");

        for (int i = 0; i < peerObjs.Count; i++)
        {
            string pid = ExtractJsonValue(peerObjs[i], "peerId");
            if (pid == null || pid == _peerId) continue;
            _knownPeers[pid] = 1;
            // Re-offer to any present co-host we are not currently connected to (e.g.
            // after this DJ left and rejoined). MaybeOffer skips already-connected
            // peers and throttles, so the heartbeat does not churn live links.
            MaybeOffer(pid);
        }
    }

    // Perfect-negotiation initiator rule: the lexicographically smaller peerId
    // initiates the offer, so each pair negotiates exactly one connection.
    private bool IsInitiator(string remotePeerId)
    {
        return string.CompareOrdinal(_peerId, remotePeerId) < 0;
    }

    // Re-offer throttle so the 5 s join heartbeat doesn't spam offers at a peer
    // that is mid-negotiation.
    private const int OfferCooldownMs = 8000;
    private readonly ConcurrentDictionary<string, int> _lastOfferAt = new ConcurrentDictionary<string, int>();

    /// <summary>
    /// Offer to a roster peer when we are its initiator and we are NOT already
    /// connected to it. This both makes the first connection AND re-establishes one
    /// after the DJ (plugin) left and returned: on rejoin the roster still lists any
    /// co-hosts present in the room, and any whose WebRTC link is down get a fresh
    /// offer. Connected peers are skipped (no churn) and a short cooldown avoids
    /// re-offering while a negotiation is still in flight.
    /// </summary>
    private void MaybeOffer(string pid)
    {
        if (pid == null || pid == _peerId) return;
        if (!IsInitiator(pid)) return;
        if (NewPlugin.IsPeerConnected(pid)) return; // already connected — leave it alone
        int now = Environment.TickCount;
        int last;
        if (_lastOfferAt.TryGetValue(pid, out last))
        {
            int elapsed = now - last;
            if (elapsed >= 0 && elapsed < OfferCooldownMs) return;
        }
        _lastOfferAt[pid] = now;
        Log("Offering to present, unconnected peer: " + pid);
        InitiateOffer(pid);
    }

    private void InitiateOffer(string remotePeerId)
    {
        if (_peer == null) return;
        try
        {
            _peer.CreatePeerConnection(remotePeerId);
            string sdp = _peer.CreateOffer(remotePeerId);
            if (sdp != null)
            {
                PostSignal("offer", remotePeerId, "\"sdp\":\"" + EscapeJson(sdp) + "\"");
            }
        }
        catch (Exception ex)
        {
            Log("InitiateOffer error for " + remotePeerId + ": " + ex.Message);
        }
    }

    private void OnPeerIceCandidate(string remotePeerId, string candidateJson)
    {
        if (candidateJson == null) return;
        // candidateJson is already a JSON object (RTCIceCandidateInit); embed raw.
        PostSignal("ice-candidate", remotePeerId, "\"candidate\":" + candidateJson);
    }

    // --- SSE signal stream ---------------------------------------------------

    private void OpenSignalStream()
    {
        string url = _baseUrl.TrimEnd('/') + "/api/telemetry/stream/" + Uri.EscapeDataString(_slug)
            + "?peerId=" + Uri.EscapeDataString(_peerId)
            + (string.IsNullOrEmpty(_token) ? "" : "&token=" + Uri.EscapeDataString(_token));

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

        HttpResponseMessage response = null;
        try
        {
            var sendTask = _streamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            sendTask.Wait(_cts.Token);
            response = UnwrapResult(sendTask);
            response.EnsureSuccessStatusCode();

            var streamTask = response.Content.ReadAsStreamAsync();
            streamTask.Wait(_cts.Token);
            var stream = UnwrapResult(streamTask);

            Log("Signal stream open: " + url);

            try
            {
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string eventName = null;
                    var dataBuffer = new StringBuilder();

                    while (_running && !_cts.IsCancellationRequested)
                    {
                        string line = reader.ReadLine();
                        if (line == null) break; // stream closed

                        if (line.Length == 0)
                        {
                            // Blank line terminates an SSE event.
                            if (dataBuffer.Length > 0)
                            {
                                DispatchSseEvent(eventName, dataBuffer.ToString());
                            }
                            eventName = null;
                            dataBuffer.Length = 0;
                            continue;
                        }

                        if (line[0] == ':') continue; // SSE comment / keep-alive

                        if (line.StartsWith("event:"))
                        {
                            eventName = line.Substring(6).Trim();
                        }
                        else if (line.StartsWith("data:"))
                        {
                            if (dataBuffer.Length > 0) dataBuffer.Append("\n");
                            dataBuffer.Append(line.Substring(5).Trim());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // The stream was already open, so this is a benign long-lived SSE
                // drop (idle timeout / network blip), not a setup failure. Returning
                // normally lets ReconnectLoop reconnect lightweight (token refresh +
                // re-open) instead of re-running the full provision sequence.
                Log("Signal stream closed: " + ex.Message);
            }
        }
        finally
        {
            if (response != null)
            {
                try { response.Dispose(); } catch { }
            }
        }
    }

    private void DispatchSseEvent(string eventName, string data)
    {
        // Only signaling events drive the mesh; telemetry/chat/intercom are
        // handled elsewhere and ignored here.
        if (eventName != "signal") return;
        try
        {
            HandleSignal(data);
        }
        catch (Exception ex)
        {
            Log("HandleSignal error: " + ex.Message);
        }
    }

    private void HandleSignal(string json)
    {
        if (_peer == null || json == null) return;

        string type = ExtractJsonValue(json, "type");
        string from = ExtractJsonValue(json, "from");
        string to = ExtractJsonValue(json, "to"); // null => broadcast
        if (type == null) return;

        // Ignore messages addressed to a different peer.
        if (to != null && to != _peerId && to != "*") return;
        // Never act on our own broadcasts.
        if (from != null && from == _peerId) return;

        string payload = ExtractRawJsonObject(json, "payload");

        if (type == "offer")
        {
            string sdp = ExtractJsonValue(payload, "sdp");
            if (from == null || sdp == null) return;
            _knownPeers[from] = 1;
            // A fresh offer means the remote wants a new session. If we are holding a
            // stale (failed/old) peer connection for them — e.g. this DJ just rejoined
            // and the co-host is re-offering — tear it down first so we answer on a
            // clean connection instead of a dead one (CreatePeerConnection no-ops if a
            // connection already exists).
            if (!NewPlugin.IsPeerConnected(from)) _peer.ClosePeerConnection(from);
            _peer.CreatePeerConnection(from);
            _peer.ApplyRemoteDescription(from, "offer", sdp);
            string answer = _peer.CreateAnswer(from);
            if (answer != null)
            {
                PostSignal("answer", from, "\"sdp\":\"" + EscapeJson(answer) + "\"");
            }
        }
        else if (type == "answer")
        {
            string sdp = ExtractJsonValue(payload, "sdp");
            if (from == null || sdp == null) return;
            _peer.ApplyRemoteDescription(from, "answer", sdp);
        }
        else if (type == "ice-candidate")
        {
            string candidate = ExtractRawJsonObject(payload, "candidate");
            if (from == null || candidate == null) return;
            Log("Remote ICE cand from " + from + ": " + candidate);
            _peer.AddIceCandidate(from, candidate);
        }
        else if (type == "join")
        {
            // A peer announced itself (new, or re-announcing after we rejoined);
            // offer if we are the initiator and not already connected to it.
            if (from == null) return;
            _knownPeers[from] = 1;
            MaybeOffer(from);
        }
        else if (type == "leave")
        {
            if (from == null) return;
            byte ignored;
            _knownPeers.TryRemove(from, out ignored);
            int lastIgnored;
            _lastOfferAt.TryRemove(from, out lastIgnored);
            _peer.ClosePeerConnection(from);
            Log("Peer left: " + from);
        }
        else if (type == "mic-state")
        {
            // Track the co-host's own mic state so the plugin UI can show when a
            // remote presenter has muted themselves (distinct from the DJ muting them).
            string micOn = ExtractJsonValue(payload, "micOn");
            NewPlugin.SetCohostRemoteMic(from, micOn == "true");
        }
    }

    // --- HTTP helpers --------------------------------------------------------

    private string PostSignal(string type, string to, string payloadInner)
    {
        if (_httpClient == null) EnsureClients();

        var sb = new StringBuilder();
        sb.Append("{\"type\":\"");
        sb.Append(EscapeJson(type));
        sb.Append("\",\"slug\":\"");
        sb.Append(EscapeJson(_slug));
        sb.Append("\",\"from\":\"");
        sb.Append(EscapeJson(_peerId));
        sb.Append("\",\"to\":");
        if (to == null) sb.Append("null");
        else { sb.Append("\""); sb.Append(EscapeJson(to)); sb.Append("\""); }
        sb.Append(",\"payload\":{");
        if (payloadInner != null) sb.Append(payloadInner);
        sb.Append("}}");

        string url = _baseUrl.TrimEnd('/') + "/api/signal/" + Uri.EscapeDataString(_slug);
        return HttpPost(url, sb.ToString());
    }

    private string HttpGet(string url)
    {
        try
        {
            var task = _httpClient.GetAsync(url, _cts.Token);
            task.Wait(_cts.Token);
            var resp = UnwrapResult(task);
            resp.EnsureSuccessStatusCode();
            var bodyTask = resp.Content.ReadAsStringAsync();
            bodyTask.Wait(_cts.Token);
            return UnwrapResult(bodyTask);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log("GET " + url + " failed: " + ex.Message);
            return null;
        }
    }

    private string HttpPost(string url, string jsonBody)
    {
        try
        {
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var task = _httpClient.PostAsync(url, content, _cts.Token);
            task.Wait(_cts.Token);
            var resp = UnwrapResult(task);
            resp.EnsureSuccessStatusCode();
            var bodyTask = resp.Content.ReadAsStringAsync();
            bodyTask.Wait(_cts.Token);
            return UnwrapResult(bodyTask);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log("POST " + url + " failed: " + ex.Message);
            return null;
        }
    }

    // Unwrap a faulted Task<T> to its inner exception (mirrors RelaySignalingClient).
    private static T UnwrapResult<T>(System.Threading.Tasks.Task<T> task)
    {
        try
        {
            return task.Result;
        }
        catch (AggregateException ae)
        {
            throw ae.InnerException ?? ae;
        }
    }

    // --- JSON helpers (same lightweight style as RelaySignalingClient) -------

    private string EscapeJson(string value)
    {
        if (value == null) return "";
        var sb = new StringBuilder();
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private string ExtractJsonValue(string json, string key)
    {
        if (json == null) return null;
        string searchKey = "\"" + key + "\"";
        int keyIdx = json.IndexOf(searchKey);
        if (keyIdx < 0) return null;

        int colonIdx = json.IndexOf(':', keyIdx + searchKey.Length);
        if (colonIdx < 0) return null;

        int i = colonIdx + 1;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n'))
        {
            i++;
        }
        if (i >= json.Length) return null;

        // String value
        if (json[i] == '"')
        {
            int valueStart = i;
            int valueEnd = valueStart + 1;
            while (valueEnd < json.Length)
            {
                if (json[valueEnd] == '"' && json[valueEnd - 1] != '\\')
                    break;
                valueEnd++;
            }
            if (valueEnd >= json.Length) return null;
            return json.Substring(valueStart + 1, valueEnd - valueStart - 1)
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }

        // Not a scalar (array/object); callers use ExtractRawJsonObject /
        // ExtractJsonStringArray for those.
        if (json[i] == '[' || json[i] == '{') return null;

        // Bare value (number / bool / null) up to the next delimiter.
        int tokenEnd = i;
        while (tokenEnd < json.Length
               && json[tokenEnd] != ','
               && json[tokenEnd] != '}'
               && json[tokenEnd] != ']'
               && json[tokenEnd] != ' '
               && json[tokenEnd] != '\r'
               && json[tokenEnd] != '\n'
               && json[tokenEnd] != '\t')
        {
            tokenEnd++;
        }
        string token = json.Substring(i, tokenEnd - i);
        if (token == "null" || token.Length == 0) return null;
        return token;
    }

    /// <summary>
    /// Returns the raw JSON object (including braces) assigned to <paramref name="key"/>,
    /// or null if the key is missing or its value is not an object.
    /// </summary>
    private string ExtractRawJsonObject(string json, string key)
    {
        if (json == null) return null;
        string searchKey = "\"" + key + "\"";
        int keyIdx = json.IndexOf(searchKey);
        if (keyIdx < 0) return null;

        int colonIdx = json.IndexOf(':', keyIdx + searchKey.Length);
        if (colonIdx < 0) return null;

        int braceIdx = json.IndexOf('{', colonIdx);
        if (braceIdx < 0) return null;

        int depth = 1;
        int end = braceIdx + 1;
        while (end < json.Length && depth > 0)
        {
            if (json[end] == '{') depth++;
            else if (json[end] == '}') depth--;
            end++;
        }
        if (depth != 0) return null;
        return json.Substring(braceIdx, end - braceIdx);
    }

    /// <summary>
    /// Returns each top-level JSON object (including braces) found inside the array
    /// assigned to <paramref name="key"/>. Empty list if missing.
    /// </summary>
    private List<string> ExtractJsonArrayObjects(string json, string key)
    {
        var result = new List<string>();
        if (json == null) return result;

        string searchKey = "\"" + key + "\"";
        int keyIdx = json.IndexOf(searchKey);
        if (keyIdx < 0) return result;

        int colonIdx = json.IndexOf(':', keyIdx + searchKey.Length);
        if (colonIdx < 0) return result;

        int bracketIdx = json.IndexOf('[', colonIdx);
        if (bracketIdx < 0) return result;

        int i = bracketIdx + 1;
        while (i < json.Length && json[i] != ']')
        {
            if (json[i] == '{')
            {
                int depth = 1;
                int start = i;
                int end = i + 1;
                while (end < json.Length && depth > 0)
                {
                    if (json[end] == '{') depth++;
                    else if (json[end] == '}') depth--;
                    end++;
                }
                if (depth != 0) break;
                result.Add(json.Substring(start, end - start));
                i = end;
            }
            else
            {
                i++;
            }
        }
        return result;
    }

    /// <summary>
    /// Returns the quoted strings inside the array assigned to <paramref name="key"/>.
    /// Empty array if missing.
    /// </summary>
    private string[] ExtractJsonStringArray(string json, string key)
    {
        var result = new List<string>();
        if (json == null) return result.ToArray();

        string searchKey = "\"" + key + "\"";
        int keyIdx = json.IndexOf(searchKey);
        if (keyIdx < 0) return result.ToArray();

        int colonIdx = json.IndexOf(':', keyIdx + searchKey.Length);
        if (colonIdx < 0) return result.ToArray();

        int bracketIdx = json.IndexOf('[', colonIdx);
        if (bracketIdx < 0) return result.ToArray();

        int i = bracketIdx + 1;
        while (i < json.Length && json[i] != ']')
        {
            if (json[i] == '"')
            {
                int valueStart = i;
                int valueEnd = valueStart + 1;
                while (valueEnd < json.Length)
                {
                    if (json[valueEnd] == '"' && json[valueEnd - 1] != '\\')
                        break;
                    valueEnd++;
                }
                if (valueEnd >= json.Length) break;
                result.Add(json.Substring(valueStart + 1, valueEnd - valueStart - 1));
                i = valueEnd + 1;
            }
            else
            {
                i++;
            }
        }
        return result.ToArray();
    }

    private WebRtcIceServer[] ParseIceServers(string json)
    {
        var servers = new List<WebRtcIceServer>();
        List<string> objs = ExtractJsonArrayObjects(json, "iceServers");
        for (int i = 0; i < objs.Count; i++)
        {
            string obj = objs[i];
            var server = new WebRtcIceServer();

            // "urls" may be a single string or an array of strings.
            string single = ExtractJsonValue(obj, "urls");
            if (single != null)
            {
                server.Urls = new string[] { single };
            }
            else
            {
                server.Urls = ExtractJsonStringArray(obj, "urls");
            }

            server.Username = ExtractJsonValue(obj, "username");
            server.Credential = ExtractJsonValue(obj, "credential");
            servers.Add(server);
        }
        return servers.ToArray();
    }

    private static void Log(string message)
    {
        NewPlugin.LogStatic("[WebRtcMesh] " + message);
    }
}

// =============================================================================
// ====================  MR-WEBRTC PRIMARY ADAPTER (task 5.2)  =================
// =============================================================================
// Everything between this banner and the matching "END MR-WEBRTC" banner was
// added by task 5.2 (the Microsoft.MixedReality.WebRTC primary IWebRtcPeer
// adapter over Google libwebrtc). It is self-contained so the SIPSorcery +
// Concentus fallback adapter (task 5.3) can be added alongside it without
// merge conflicts. The deprecated WebSocket raw-PCM relay was removed in
// task 5.7.
//
// Build / packaging notes (Requirements 5.1, 5.2, 5.3, 5.4):
//   * NuGet: Microsoft.MixedReality.WebRTC, pinned to 2.0.2 (last published
//     release; the project is archived/read-only since 2022, so the version is
//     pinned and the native binaries are vendored rather than tracked).
//   * The managed assembly loads as a normal .NET 4.6.2+ assembly inside the
//     PlayIt Live WinForms host. The native engine ships as mrwebrtc.dll, one
//     per architecture (win-x86 / win-x64). The vendored mrwebrtc.dll MUST match
//     the PlayIt Live host process bitness, exactly as bass.dll already does.
//   * Opus is configured *inside* libwebrtc: the engine offers/answers Opus in
//     the SDP and performs the encode/decode. We only ever hand it mono PCM16
//     (channelCount = 1) on the outbound external source and downmix decoded
//     remote frames to mono, so the wire format stays mono Opus speech audio.
//
// Conditional compilation:
//   The adapter that references Microsoft.MixedReality.WebRTC types is wrapped
//   in "#if PARTYLINE_MRWEBRTC". That symbol is OFF by default (see
//   PartylinePlugin.csproj -> PartylineMrWebRtc). With it off, the language
//   server / Roslyn never sees the MR-WebRTC types, so diagnostics on the rest
//   of this file stay clean on machines that cannot restore the native package
//   (e.g. the macOS dev box). Build the primary adapter on Windows with:
//       dotnet build -p:PartylineMrWebRtc=true
//   which both defines PARTYLINE_MRWEBRTC and pulls in the NuGet package.
//
//   NOTE: enabling PARTYLINE_MRWEBRTC requires an MR-WebRTC build whose managed
//   assembly + native mrwebrtc.dll expose the external *audio* track source
//   (ExternalAudioTrackSource). This is the push-PCM source the BASS bridge
//   feeds; if a given vendored build lacks it, use the SIPSorcery fallback
//   (task 5.3) instead, selected at startup via MixedRealityWebRtcLoader below.
// =============================================================================

/// <summary>
/// Native load + bitness assertion for mrwebrtc.dll. Intentionally carries NO
/// Microsoft.MixedReality.WebRTC type dependencies, so it compiles and runs
/// regardless of the PARTYLINE_MRWEBRTC build symbol. Startup / fallback wiring
/// (task 5.4) calls <see cref="TryLoad()"/> (or reads <see cref="IsAvailable"/>)
/// to decide whether to construct the MR-WebRTC primary adapter or fall back to
/// the SIPSorcery adapter (Requirement 5.1 fallback path).
/// </summary>
public static class MixedRealityWebRtcLoader
{
    private const string NativeBaseName = "mrwebrtc";

    private static readonly object _lock = new object();
    private static bool _probed;
    private static bool _available;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string fileName);

    /// <summary>True once the native engine has been located, verified for
    /// bitness, and loaded. Probes lazily on first access.</summary>
    public static bool IsAvailable
    {
        get
        {
            lock (_lock)
            {
                if (!_probed) TryLoadInternal(null);
                return _available;
            }
        }
    }

    /// <summary>Probe + load mrwebrtc.dll from the default search locations.</summary>
    public static bool TryLoad()
    {
        return TryLoad(null);
    }

    /// <summary>
    /// Probe + load mrwebrtc.dll, optionally from an explicit directory. Verifies
    /// the native DLL architecture matches Environment.Is64BitProcess and logs
    /// the outcome via NewPlugin.LogStatic. Returns false (rather than throwing)
    /// so the caller can fall back to the managed SIPSorcery adapter.
    /// </summary>
    public static bool TryLoad(string nativeDir)
    {
        lock (_lock)
        {
            return TryLoadInternal(nativeDir);
        }
    }

    private static bool TryLoadInternal(string nativeDir)
    {
        _probed = true;
        _available = false;
        try
        {
            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            string dllName = NativeBaseName + ".dll";
            string path = ResolveDllPath(nativeDir, dllName, arch);
            bool fromEmbedded = false;
            if (path == null)
            {
                // Not found loose on disk -> use the copy embedded inside
                // PartylinePlugin.dll (single-file deployment). Extract to a
                // temp folder and load from there.
                path = ExtractEmbeddedNative(arch, dllName);
                fromEmbedded = path != null;
            }
            if (path == null)
            {
                NewPlugin.LogStatic("[MRWebRTC] " + dllName + " not found (disk or embedded) for host arch " + arch + "; falling back.");
                return false;
            }

            // Bitness assertion: the native engine MUST match the host process,
            // the same constraint bass.dll already has.
            ushort machine;
            if (TryReadPeMachine(path, out machine))
            {
                bool dll64 = (machine == 0x8664 || machine == 0xAA64); // AMD64 / ARM64
                bool dll32 = (machine == 0x014C);                      // I386
                bool hostMatches = Environment.Is64BitProcess ? dll64 : dll32;
                if (!hostMatches)
                {
                    NewPlugin.LogStatic("[MRWebRTC] BITNESS MISMATCH: host is "
                        + (Environment.Is64BitProcess ? "64-bit" : "32-bit")
                        + " but " + path + " reports machine=0x" + machine.ToString("X4")
                        + "; falling back.");
                    return false;
                }
            }
            else
            {
                NewPlugin.LogStatic("[MRWebRTC] WARNING: could not read PE header of " + path + "; attempting load anyway.");
            }

            IntPtr handle = LoadLibrary(path);
            if (handle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                NewPlugin.LogStatic("[MRWebRTC] LoadLibrary failed for " + path + " (win32=" + err + "); falling back.");
                return false;
            }

            NewPlugin.LogStatic("[MRWebRTC] Loaded native engine " + path + " (host " + arch
                + (fromEmbedded ? ", embedded" : ", on-disk") + ").");
            _available = true;
            return true;
        }
        catch (Exception ex)
        {
            NewPlugin.LogStatic("[MRWebRTC] TryLoad error: " + ex.Message + "; falling back.");
            return false;
        }
    }

    private static string ResolveDllPath(string nativeDir, string dllName, string arch)
    {
        var candidates = new List<string>();
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (baseDir == null) baseDir = "";

        if (nativeDir != null && nativeDir.Length > 0)
        {
            candidates.Add(Path.Combine(nativeDir, dllName));
            candidates.Add(Path.Combine(Path.Combine(nativeDir, arch), dllName));
        }

        // The plugin's own directory (the PlayIt Live Plugins folder where
        // PartylinePlugin.dll lives) is the most natural drop spot for the native
        // DLL, but it is NOT the AppDomain base dir (that's the host exe folder),
        // so probe it explicitly first.
        string pluginDir = null;
        try
        {
            string loc = typeof(MixedRealityWebRtcLoader).Assembly.Location;
            if (!string.IsNullOrEmpty(loc)) pluginDir = Path.GetDirectoryName(loc);
        }
        catch { }
        if (!string.IsNullOrEmpty(pluginDir))
        {
            candidates.Add(Path.Combine(pluginDir, dllName));
            candidates.Add(Path.Combine(Path.Combine(pluginDir, arch), dllName));
            candidates.Add(Path.Combine(pluginDir, Path.Combine("runtimes", Path.Combine("win-" + arch, Path.Combine("native", dllName)))));
        }

        candidates.Add(Path.Combine(baseDir, dllName));
        candidates.Add(Path.Combine(Path.Combine(baseDir, arch), dllName));
        // NuGet runtimes layout: runtimes/win-x64/native/mrwebrtc.dll
        candidates.Add(Path.Combine(baseDir, Path.Combine("runtimes", Path.Combine("win-" + arch, Path.Combine("native", dllName)))));

        for (int i = 0; i < candidates.Count; i++)
        {
            try
            {
                if (File.Exists(candidates[i])) { NewPlugin.LogStatic("[MRWebRTC] found native engine at " + candidates[i]); return candidates[i]; }
            }
            catch { }
        }
        return null;
    }

    // Extracts the architecture-appropriate mrwebrtc.dll embedded inside
    // PartylinePlugin.dll (single-file deployment) to a stable temp folder and
    // returns its path. Returns null if the resource is absent or extraction
    // fails. The bytes are embedded by the csproj (PartylineMrWebRtc build) as
    // manifest resources "Partyline.native.<arch>.mrwebrtc.dll".
    private static string ExtractEmbeddedNative(string arch, string dllName)
    {
        try
        {
            var asm = typeof(MixedRealityWebRtcLoader).Assembly;
            string resName = "Partyline.native." + arch + "." + dllName;
            using (var rs = asm.GetManifestResourceStream(resName))
            {
                if (rs == null)
                {
                    NewPlugin.LogStatic("[MRWebRTC] embedded native resource not found: " + resName
                        + " (was the plugin built with -p:PartylineMrWebRtc=true?).");
                    return null;
                }
                string ver = asm.GetName().Version != null ? asm.GetName().Version.ToString() : "0";
                string dir = Path.Combine(Path.Combine(Path.GetTempPath(), "Partyline.native"), ver + "." + arch);
                Directory.CreateDirectory(dir);
                string outPath = Path.Combine(dir, dllName);

                bool needWrite = true;
                try { if (File.Exists(outPath) && new FileInfo(outPath).Length == rs.Length) needWrite = false; }
                catch { }

                if (needWrite)
                {
                    using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        rs.CopyTo(fs);
                    NewPlugin.LogStatic("[MRWebRTC] extracted embedded native to " + outPath);
                }
                else
                {
                    NewPlugin.LogStatic("[MRWebRTC] using previously extracted native " + outPath);
                }
                return outPath;
            }
        }
        catch (Exception ex)
        {
            NewPlugin.LogStatic("[MRWebRTC] embedded native extraction failed: " + ex.Message);
            return null;
        }
    }

    // Reads the COFF "machine" field from a PE/DLL file. Returns false if the
    // file is not a valid PE image.
    private static bool TryReadPeMachine(string path, out ushort machine)
    {
        machine = 0;
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var br = new BinaryReader(fs))
            {
                if (fs.Length < 0x40) return false;
                fs.Seek(0, SeekOrigin.Begin);
                if (br.ReadUInt16() != 0x5A4D) return false;   // "MZ"
                fs.Seek(0x3C, SeekOrigin.Begin);
                int peOffset = br.ReadInt32();
                if (peOffset <= 0 || peOffset + 6 > fs.Length) return false;
                fs.Seek(peOffset, SeekOrigin.Begin);
                if (br.ReadUInt32() != 0x00004550) return false; // "PE\0\0"
                machine = br.ReadUInt16();
                return true;
            }
        }
        catch
        {
            return false;
        }
    }
}

#if PARTYLINE_MRWEBRTC
namespace Partyline.WebRtc
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;
    using Microsoft.MixedReality.WebRTC;

    /// <summary>
    /// Primary <see cref="IWebRtcPeer"/> implementation over
    /// Microsoft.MixedReality.WebRTC (Google libwebrtc). One PeerConnection is
    /// maintained per remote peerId. A single shared ExternalAudioTrackSource
    /// carries the BASS main-mix tap outbound (encoded once as mono Opus by
    /// libwebrtc and sent to every peer); decoded remote frames are raised per
    /// peer via OnRemoteAudioFrame for the AudioMixer ingest path.
    ///
    /// SDP negotiation in MR-WebRTC is event-driven (CreateOffer/CreateAnswer
    /// complete asynchronously through LocalSdpReadytoSend). The IWebRtcPeer
    /// contract is synchronous, so each offer/answer is bridged through a
    /// per-peer TaskCompletionSource that the LocalSdpReadytoSend handler
    /// completes with the SDP text.
    /// </summary>
    public class MixedRealityWebRtcPeer : IWebRtcPeer, IDisposable
    {
        private const int OutboundSampleRate = 48000;   // mono Opus capture rate
        private const int SdpTimeoutMs = 10000;
        private const int InitTimeoutMs = 10000;

        private readonly object _sync = new object();
        private readonly Dictionary<string, PeerEntry> _peers = new Dictionary<string, PeerEntry>();
        private List<IceServer> _iceServers = new List<IceServer>();

        // Single outbound source shared by every peer connection (encode once).
        private ExternalAudioTrackSource _outboundSource;
        private readonly object _ringLock = new object();
        private short[] _ring = new short[OutboundSampleRate];   // ~1s mono ring
        private int _ringRead;
        private int _ringWrite;
        private int _ringCount;

        // --- IWebRtcPeer callbacks (Action delegates for C# 5 parity) --------
        public Action<string, string> OnLocalIceCandidate { get; set; }
        public Action<string, short[], int, int> OnRemoteAudioFrame { get; set; }
        public Action<string, string> OnConnectionStateChanged { get; set; }

        private class PeerEntry
        {
            public string PeerId;
            public PeerConnection Pc;
            public LocalAudioTrack LocalTrack;
            public Transceiver Transceiver;
            public RemoteAudioTrack RemoteTrack;
            public volatile TaskCompletionSource<string> SdpReady;
        }

        // --- ICE configuration ----------------------------------------------

        public void SetIceServers(WebRtcIceServer[] iceServers)
        {
            var list = new List<IceServer>();
            if (iceServers != null)
            {
                for (int i = 0; i < iceServers.Length; i++)
                {
                    WebRtcIceServer s = iceServers[i];
                    if (s == null || s.Urls == null || s.Urls.Length == 0) continue;
                    var srv = new IceServer();
                    srv.Urls = new List<string>(s.Urls);
                    if (s.Username != null) srv.TurnUserName = s.Username;
                    if (s.Credential != null) srv.TurnPassword = s.Credential;
                    list.Add(srv);
                }
            }
            lock (_sync) { _iceServers = list; }
            NewPlugin.LogStatic("[MRWebRTC] Applied " + list.Count + " ICE server entries.");
        }

        private List<IceServer> BuildIceServers()
        {
            lock (_sync)
            {
                return _iceServers != null ? new List<IceServer>(_iceServers) : new List<IceServer>();
            }
        }

        // --- Connection lifecycle -------------------------------------------

        public void CreatePeerConnection(string peerId)
        {
            if (peerId == null) return;
            lock (_sync)
            {
                if (_peers.ContainsKey(peerId)) return;
            }

            EnsureOutboundSource();

            var pc = new PeerConnection();
            var config = new PeerConnectionConfiguration();
            config.IceServers = BuildIceServers();
            // Unified Plan is required for browser interop (Chromium peers).
            config.SdpSemantic = SdpSemantic.UnifiedPlan;

            try
            {
                pc.InitializeAsync(config).Wait(InitTimeoutMs);
            }
            catch (Exception ex)
            {
                NewPlugin.LogStatic("[MRWebRTC] InitializeAsync failed for " + peerId + ": " + ex.Message);
                try { pc.Dispose(); } catch { }
                return;
            }

            var entry = new PeerEntry();
            entry.PeerId = peerId;
            entry.Pc = pc;
            WirePeerEvents(entry);

            try
            {
                // Outbound mono track from the shared external source.
                var trackConfig = new LocalAudioTrackInitConfig();
                trackConfig.trackName = "partyline-out";
                LocalAudioTrack localTrack = LocalAudioTrack.CreateFromSource(_outboundSource, trackConfig);
                entry.LocalTrack = localTrack;

                var txSettings = new TransceiverInitSettings();
                txSettings.Name = "audio-" + peerId;
                txSettings.InitialDesiredDirection = Transceiver.Direction.SendReceive;
                Transceiver tx = pc.AddTransceiver(MediaKind.Audio, txSettings);
                tx.LocalAudioTrack = localTrack;
                entry.Transceiver = tx;
            }
            catch (Exception ex)
            {
                NewPlugin.LogStatic("[MRWebRTC] track setup failed for " + peerId + ": " + ex.Message);
            }

            lock (_sync)
            {
                if (_peers.ContainsKey(peerId))
                {
                    // Lost a race; dispose the duplicate.
                    DisposeEntry(entry);
                    return;
                }
                _peers[peerId] = entry;
            }
            NewPlugin.LogStatic("[MRWebRTC] PeerConnection created for " + peerId);
        }

        public void ClosePeerConnection(string peerId)
        {
            PeerEntry e;
            lock (_sync)
            {
                if (peerId == null || !_peers.TryGetValue(peerId, out e)) return;
                _peers.Remove(peerId);
            }
            DisposeEntry(e);
            NewPlugin.LogStatic("[MRWebRTC] PeerConnection closed for " + peerId);
        }

        public void CloseAll()
        {
            List<PeerEntry> all;
            lock (_sync)
            {
                all = new List<PeerEntry>(_peers.Values);
                _peers.Clear();
            }
            for (int i = 0; i < all.Count; i++) DisposeEntry(all[i]);

            lock (_sync)
            {
                if (_outboundSource != null)
                {
                    try { _outboundSource.Dispose(); } catch { }
                    _outboundSource = null;
                }
            }
        }

        private void DisposeEntry(PeerEntry e)
        {
            if (e == null) return;
            try { if (e.LocalTrack != null) e.LocalTrack.Dispose(); } catch { }
            try { if (e.Pc != null) e.Pc.Close(); } catch { }
            try { if (e.Pc != null) e.Pc.Dispose(); } catch { }
        }

        // --- SDP negotiation -------------------------------------------------

        public string CreateOffer(string peerId)
        {
            PeerEntry e = Get(peerId);
            if (e == null) return null;
            var tcs = new TaskCompletionSource<string>();
            e.SdpReady = tcs;
            try
            {
                if (!e.Pc.CreateOffer())
                {
                    e.SdpReady = null;
                    NewPlugin.LogStatic("[MRWebRTC] CreateOffer returned false for " + peerId);
                    return null;
                }
            }
            catch (Exception ex)
            {
                e.SdpReady = null;
                NewPlugin.LogStatic("[MRWebRTC] CreateOffer error for " + peerId + ": " + ex.Message);
                return null;
            }
            if (tcs.Task.Wait(SdpTimeoutMs)) return tcs.Task.Result;
            NewPlugin.LogStatic("[MRWebRTC] CreateOffer timed out for " + peerId);
            return null;
        }

        public string CreateAnswer(string peerId)
        {
            PeerEntry e = Get(peerId);
            if (e == null) return null;
            var tcs = new TaskCompletionSource<string>();
            e.SdpReady = tcs;
            try
            {
                if (!e.Pc.CreateAnswer())
                {
                    e.SdpReady = null;
                    NewPlugin.LogStatic("[MRWebRTC] CreateAnswer returned false for " + peerId);
                    return null;
                }
            }
            catch (Exception ex)
            {
                e.SdpReady = null;
                NewPlugin.LogStatic("[MRWebRTC] CreateAnswer error for " + peerId + ": " + ex.Message);
                return null;
            }
            if (tcs.Task.Wait(SdpTimeoutMs)) return tcs.Task.Result;
            NewPlugin.LogStatic("[MRWebRTC] CreateAnswer timed out for " + peerId);
            return null;
        }

        public void ApplyRemoteDescription(string peerId, string type, string sdp)
        {
            PeerEntry e = Get(peerId);
            if (e == null || sdp == null) return;
            var msg = new SdpMessage();
            msg.Type = (type == "offer") ? SdpMessageType.Offer : SdpMessageType.Answer;
            msg.Content = sdp;
            try
            {
                e.Pc.SetRemoteDescriptionAsync(msg).Wait(SdpTimeoutMs);
            }
            catch (Exception ex)
            {
                NewPlugin.LogStatic("[MRWebRTC] SetRemoteDescription error for " + peerId + ": " + ex.Message);
            }
        }

        // --- Trickle ICE -----------------------------------------------------

        public void AddIceCandidate(string peerId, string candidateJson)
        {
            PeerEntry e = Get(peerId);
            if (e == null || candidateJson == null) return;

            // Parse the RTCIceCandidateInit JSON: { candidate, sdpMid, sdpMLineIndex }.
            string candidate = JsonString(candidateJson, "candidate");
            string sdpMid = JsonString(candidateJson, "sdpMid");
            int sdpMLineIndex = JsonInt(candidateJson, "sdpMLineIndex", 0);
            if (candidate == null) return;

            var c = new IceCandidate();
            c.Content = candidate;
            c.SdpMid = sdpMid;
            c.SdpMlineIndex = sdpMLineIndex;
            try
            {
                e.Pc.AddIceCandidate(c);
            }
            catch (Exception ex)
            {
                NewPlugin.LogStatic("[MRWebRTC] AddIceCandidate error for " + peerId + ": " + ex.Message);
            }
        }

        // --- Outbound audio (BASS main-mix tap -> shared external source) ----

        public void PushOutboundAudio(short[] pcm, int sampleCount, int sampleRate, int channels)
        {
            if (pcm == null || sampleCount <= 0) return;
            EnsureOutboundSource();

            lock (_ringLock)
            {
                if (channels <= 1)
                {
                    int n = Math.Min(sampleCount, pcm.Length);
                    for (int i = 0; i < n; i++) Enqueue(pcm[i]);
                }
                else
                {
                    // Interleaved multi-channel -> mono average.
                    for (int f = 0; f < sampleCount; f++)
                    {
                        int sum = 0;
                        int count = 0;
                        for (int c = 0; c < channels; c++)
                        {
                            int idx = f * channels + c;
                            if (idx < pcm.Length) { sum += pcm[idx]; count++; }
                        }
                        if (count == 0) break;
                        Enqueue((short)(sum / count));
                    }
                }
            }
        }

        private void Enqueue(short sample)
        {
            // Caller holds _ringLock.
            _ring[_ringWrite] = sample;
            _ringWrite = (_ringWrite + 1) % _ring.Length;
            if (_ringCount < _ring.Length)
            {
                _ringCount++;
            }
            else
            {
                // Full: drop the oldest sample to bound latency.
                _ringRead = (_ringRead + 1) % _ring.Length;
            }
        }

        private void EnsureOutboundSource()
        {
            if (_outboundSource != null) return;
            lock (_sync)
            {
                if (_outboundSource != null) return;
                // Opus mono is configured by libwebrtc itself; we guarantee the
                // mono layout by always completing frame requests with
                // channelCount = 1 at 48 kHz.
                _outboundSource = ExternalAudioTrackSource.CreateFromCallback(OnAudioFrameRequest);
            }
        }

        // Pull callback: libwebrtc asks the external source for a frame. We drain
        // the BASS-fed ring buffer, padding with silence on underrun.
        private void OnAudioFrameRequest(in AudioFrameRequest request)
        {
            int rate = request.sampleRate > 0 ? request.sampleRate : OutboundSampleRate;
            int needed = request.sampleCount;
            if (needed <= 0) needed = rate / 100; // 10 ms default chunk

            short[] buf = new short[needed];
            lock (_ringLock)
            {
                for (int i = 0; i < needed; i++)
                {
                    if (_ringCount > 0)
                    {
                        buf[i] = _ring[_ringRead];
                        _ringRead = (_ringRead + 1) % _ring.Length;
                        _ringCount--;
                    }
                    else
                    {
                        buf[i] = 0; // underrun -> silence
                    }
                }
            }

            GCHandle gc = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                var frame = new AudioFrame();
                frame.audioData = gc.AddrOfPinnedObject();
                frame.bitsPerSample = 16;
                frame.sampleRate = (uint)rate;
                frame.channelCount = 1;     // mono Opus
                frame.sampleCount = (uint)needed;
                request.CompleteRequest(frame);
            }
            finally
            {
                gc.Free();
            }
        }

        // --- Event wiring ----------------------------------------------------

        private void WirePeerEvents(PeerEntry e)
        {
            string peerId = e.PeerId;
            PeerConnection pc = e.Pc;

            pc.LocalSdpReadytoSend += (SdpMessage msg) =>
            {
                TaskCompletionSource<string> t = e.SdpReady;
                if (t != null)
                {
                    e.SdpReady = null;
                    t.TrySetResult(msg.Content);
                }
            };

            pc.IceCandidateReadytoSend += (IceCandidate cand) =>
            {
                Action<string, string> h = OnLocalIceCandidate;
                if (h == null || cand == null) return;
                string json = BuildCandidateJson(cand.Content, cand.SdpMid, cand.SdpMlineIndex);
                h(peerId, json);
            };

            pc.IceStateChanged += (IceConnectionState st) =>
            {
                Action<string, string> h = OnConnectionStateChanged;
                if (h != null) h(peerId, MapIceState(st));
            };

            pc.AudioTrackAdded += (RemoteAudioTrack track) =>
            {
                e.RemoteTrack = track;
                track.AudioFrameReady += (AudioFrame frame) => OnRemoteFrame(peerId, frame);
            };
        }

        private void OnRemoteFrame(string peerId, AudioFrame frame)
        {
            Action<string, short[], int, int> h = OnRemoteAudioFrame;
            if (h == null || frame.audioData == IntPtr.Zero) return;
            if (frame.bitsPerSample != 16) return; // mixer ingest expects PCM16

            int channels = (int)frame.channelCount;
            if (channels < 1) channels = 1;
            int perChannel = (int)frame.sampleCount;
            int total = perChannel * channels;
            if (total <= 0) return;

            short[] interleaved = new short[total];
            Marshal.Copy(frame.audioData, interleaved, 0, total);

            short[] mono;
            if (channels == 1)
            {
                mono = interleaved;
            }
            else
            {
                mono = new short[perChannel];
                for (int f = 0; f < perChannel; f++)
                {
                    int sum = 0;
                    for (int c = 0; c < channels; c++) sum += interleaved[f * channels + c];
                    mono[f] = (short)(sum / channels);
                }
            }
            h(peerId, mono, perChannel, (int)frame.sampleRate);
        }

        // Map MR-WebRTC ICE connection state to the string contract, including
        // "failed" for per-peer failure reporting (Requirement 1.5).
        private static string MapIceState(IceConnectionState s)
        {
            switch (s)
            {
                case IceConnectionState.New: return "new";
                case IceConnectionState.Checking: return "checking";
                case IceConnectionState.Connected: return "connected";
                case IceConnectionState.Completed: return "completed";
                // Both Failed and Disconnected are surfaced as "failed" so the
                // initiating peer reports the connection failure (Requirement 1.5).
                case IceConnectionState.Failed: return "failed";
                case IceConnectionState.Disconnected: return "failed";
                case IceConnectionState.Closed: return "closed";
                default: return s.ToString().ToLowerInvariant();
            }
        }

        private PeerEntry Get(string peerId)
        {
            if (peerId == null) return null;
            lock (_sync)
            {
                PeerEntry e;
                return _peers.TryGetValue(peerId, out e) ? e : null;
            }
        }

        public void Dispose()
        {
            CloseAll();
        }

        // --- Lightweight JSON helpers (same style as WebRtcMeshClient) -------

        private static string BuildCandidateJson(string candidate, string sdpMid, int sdpMLineIndex)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"candidate\":\"");
            sb.Append(EscapeJson(candidate));
            sb.Append("\",\"sdpMid\":");
            if (sdpMid == null) sb.Append("null");
            else { sb.Append("\""); sb.Append(EscapeJson(sdpMid)); sb.Append("\""); }
            sb.Append(",\"sdpMLineIndex\":");
            sb.Append(sdpMLineIndex);
            sb.Append("}");
            return sb.ToString();
        }

        private static string EscapeJson(string value)
        {
            if (value == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static string JsonString(string json, string key)
        {
            if (json == null) return null;
            string searchKey = "\"" + key + "\"";
            int keyIdx = json.IndexOf(searchKey, StringComparison.Ordinal);
            if (keyIdx < 0) return null;
            int colonIdx = json.IndexOf(':', keyIdx + searchKey.Length);
            if (colonIdx < 0) return null;

            int i = colonIdx + 1;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
            if (i >= json.Length || json[i] != '"') return null;

            int valueStart = i;
            int valueEnd = valueStart + 1;
            while (valueEnd < json.Length)
            {
                if (json[valueEnd] == '"' && json[valueEnd - 1] != '\\') break;
                valueEnd++;
            }
            if (valueEnd >= json.Length) return null;
            return json.Substring(valueStart + 1, valueEnd - valueStart - 1)
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }

        private static int JsonInt(string json, string key, int fallback)
        {
            if (json == null) return fallback;
            string searchKey = "\"" + key + "\"";
            int keyIdx = json.IndexOf(searchKey, StringComparison.Ordinal);
            if (keyIdx < 0) return fallback;
            int colonIdx = json.IndexOf(':', keyIdx + searchKey.Length);
            if (colonIdx < 0) return fallback;

            int i = colonIdx + 1;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
            int start = i;
            if (i < json.Length && (json[i] == '-' || json[i] == '+')) i++;
            while (i < json.Length && json[i] >= '0' && json[i] <= '9') i++;
            if (i == start) return fallback;
            int result;
            if (int.TryParse(json.Substring(start, i - start), out result)) return result;
            return fallback;
        }
    }
}
#endif
// =====================  END MR-WEBRTC PRIMARY ADAPTER  =======================

// =============================================================================
// ==============  SIPSORCERY + CONCENTUS FALLBACK ADAPTER (task 5.3)  =========
// =============================================================================
// Everything between this banner and the matching "END SIPSORCERY" banner was
// added by task 5.3 (the SIPSorcery + Concentus fallback IWebRtcPeer adapter).
// It is self-contained so it sits alongside the MR-WebRTC primary adapter
// (task 5.2) without touching it, IWebRtcPeer, WebRtcMeshClient, or NoOpWebRtcPeer.
//
// Selection: MixedRealityWebRtcLoader.TryLoad() is the load/bitness check the
// startup wiring (task 5.4+) runs first. When the native mrwebrtc.dll cannot be
// loaded for the host process (missing per-arch native binary, bitness mismatch,
// or codec/init failure), the startup code constructs THIS class instead. This
// class is the documented load-failure fallback.
//
// Unlike the MR-WebRTC adapter, this block is NOT behind a build symbol: SIPSorcery
// and Concentus are pure-managed packages (referenced unconditionally in
// PartylinePlugin.csproj) that restore on any OS and add no native DLL/bitness
// dependency, so the types resolve and the file compiles everywhere.
// =============================================================================

namespace Partyline.WebRtc
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using Newtonsoft.Json.Linq;
    using SIPSorcery.Net;
    using SIPSorceryMedia.Abstractions;
    using Concentus.Enums;
    using Concentus.Structs;

    /// <summary>
    /// Fallback <see cref="IWebRtcPeer"/> implementation over SIPSorcery (a pure
    /// managed C# WebRTC stack) plus Concentus (a pure managed C# Opus codec).
    ///
    /// IMPORTANT — Requirement 5.1 amendment: SIPSorcery is <b>NOT</b> Google
    /// libwebrtc. Requirement 5.1 mandates a "Google libwebrtc-based" binding,
    /// which the MR-WebRTC primary adapter (task 5.2) satisfies. Selecting THIS
    /// adapter at startup (because the native mrwebrtc.dll failed to load) means
    /// the plugin is running on a non-libwebrtc managed stack, which is the
    /// documented R5.1 amendment captured in the design's "C# WebRTC Binding
    /// Recommendation" (SIPSorcery + Concentus fallback). This is a deliberate
    /// requirements tradeoff, surfaced here rather than taken silently.
    ///
    /// Audio is mono Opus on the wire. PCM crosses the IWebRtcPeer boundary as
    /// PCM16 (short[]). A single shared Concentus encoder turns the BASS main-mix
    /// tap into 20 ms / 960-sample mono Opus frames at 48 kHz that are sent to
    /// every connected peer (encode once, send many). Each remote peer owns its
    /// own Concentus decoder; inbound RTP Opus payloads are decoded to PCM16 and
    /// raised via OnRemoteAudioFrame for the AudioMixer ingest path.
    /// </summary>
    public class SipSorceryWebRtcPeer : IWebRtcPeer, IDisposable
    {
        private const int OpusSampleRate = 48000;       // mono Opus capture/transport rate
        private const int FrameSamples = 960;           // 20 ms @ 48 kHz mono
        private const int MaxPacketBytes = 4000;        // generous Opus packet ceiling
        private const int MaxDecodeSamples = 5760;      // 120 ms @ 48 kHz mono (max Opus frame)
        private const int OpusPayloadId = 111;          // dynamic payload type used by browsers for Opus
        private const int SdpTimeoutMs = 10000;

        private readonly object _sync = new object();
        private readonly Dictionary<string, PeerEntry> _peers = new Dictionary<string, PeerEntry>();

        // ICE config applied to every (existing + future) connection.
        private RTCConfiguration _config = new RTCConfiguration { iceServers = new List<RTCIceServer>() };

        // Single shared outbound encoder (encode once, send to all peers).
        private readonly object _encLock = new object();
        private OpusEncoder _encoder;
        private readonly List<short> _pending = new List<short>(FrameSamples * 4); // mono 48k accumulator

        // --- IWebRtcPeer callbacks (Action delegates for C# 5 parity) --------
        public Action<string, string> OnLocalIceCandidate { get; set; }
        public Action<string, short[], int, int> OnRemoteAudioFrame { get; set; }
        public Action<string, string> OnConnectionStateChanged { get; set; }

        private class PeerEntry
        {
            public string PeerId;
            public RTCPeerConnection Pc;
            public OpusDecoder Decoder;
            public readonly object DecodeLock = new object();
            // Diagnostics: one-time log flags + counters for the media bridge.
            public bool FirstRtpLogged;
            public long RtpReceived;
            public bool FirstSendLogged;
            public bool FirstSendErrorLogged;
        }

        // --- ICE configuration ----------------------------------------------

        public void SetIceServers(WebRtcIceServer[] iceServers)
        {
            var config = new RTCConfiguration();
            config.iceServers = new List<RTCIceServer>();
            // Cap the gathered servers. SIPSorcery spins up a STUN/TURN client per
            // entry; in the 32-bit host (with the isolated AppDomain doubling the
            // footprint) ~10 entries exhausted thread-stack address space and
            // crashed PlayItLive. We keep at most one STUN (for srflx) and two TURN
            // URLs (one relay path is enough on CGNAT/Starlink).
            int stunKept = 0, turnKept = 0;
            const int MaxStun = 1, MaxTurn = 2;
            if (iceServers != null)
            {
                for (int i = 0; i < iceServers.Length; i++)
                {
                    WebRtcIceServer s = iceServers[i];
                    if (s == null || s.Urls == null || s.Urls.Length == 0) continue;
                    for (int u = 0; u < s.Urls.Length; u++)
                    {
                        string url = s.Urls[u];
                        if (string.IsNullOrEmpty(url)) continue;
                        bool isTurn = url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
                                   || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase);
                        if (isTurn) { if (turnKept >= MaxTurn) continue; turnKept++; }
                        else { if (stunKept >= MaxStun) continue; stunKept++; }

                        var srv = new RTCIceServer();
                        srv.urls = url;
                        if (s.Username != null) srv.username = s.Username;
                        if (s.Credential != null) srv.credential = s.Credential;
                        config.iceServers.Add(srv);
                    }
                }
            }

            List<PeerEntry> existing;
            lock (_sync)
            {
                _config = config;
                existing = new List<PeerEntry>(_peers.Values);
            }
            // Apply to already-open connections so a mid-session re-fetch takes effect.
            for (int i = 0; i < existing.Count; i++)
            {
                try { existing[i].Pc.setConfiguration(config); }
                catch (Exception ex) { NewPlugin.LogStatic("[SIPSorcery] setConfiguration failed for " + existing[i].PeerId + ": " + ex.Message); }
            }
            NewPlugin.LogStatic("[SIPSorcery] Applied " + config.iceServers.Count + " ICE server entries (capped: " + stunKept + " STUN, " + turnKept + " TURN).");
        }

        // --- Connection lifecycle (one RTCPeerConnection per remote peerId) --

        // Constructs an RTCPeerConnection tolerant of SIPSorcery version differences.
        // The compiled `new RTCPeerConnection(config)` call binds to a specific ctor
        // overload (with that version's optional params); if a different SIPSorcery
        // assembly is loaded at runtime (e.g. one shipped by the PlayIt Live host),
        // that exact ctor may not exist -> MethodNotFound. We instead pick whatever
        // ctor the loaded assembly exposes whose first parameter is RTCConfiguration,
        // and fill the remaining params with their defaults.
        private static bool _versionLogged;
        private static RTCPeerConnection NewRTCPeerConnection(RTCConfiguration config)
        {
            if (!_versionLogged)
            {
                _versionLogged = true;
                try
                {
                    var asm = typeof(RTCPeerConnection).Assembly.GetName();
                    NewPlugin.LogStatic("[SIPSorcery] loaded assembly: " + asm.Name + " " + asm.Version);
                }
                catch { }
            }

            System.Reflection.ConstructorInfo chosen = null;
            foreach (var ci in typeof(RTCPeerConnection).GetConstructors())
            {
                var ps = ci.GetParameters();
                if (ps.Length >= 1 && ps[0].ParameterType == typeof(RTCConfiguration))
                {
                    if (chosen == null || ps.Length < chosen.GetParameters().Length) chosen = ci;
                }
            }
            if (chosen == null)
            {
                // Last resort: let Activator bind a single-arg ctor.
                return (RTCPeerConnection)Activator.CreateInstance(typeof(RTCPeerConnection), new object[] { config });
            }

            var pars = chosen.GetParameters();
            object[] args = new object[pars.Length];
            args[0] = config;
            for (int i = 1; i < pars.Length; i++)
            {
                if (pars[i].HasDefaultValue) args[i] = pars[i].DefaultValue;
                else if (pars[i].ParameterType.IsValueType) args[i] = Activator.CreateInstance(pars[i].ParameterType);
                else args[i] = null;
            }
            return (RTCPeerConnection)chosen.Invoke(args);
        }

        public void CreatePeerConnection(string peerId)
        {
            if (peerId == null) return;
            RTCConfiguration config;
            lock (_sync)
            {
                if (_peers.ContainsKey(peerId)) return;
                config = _config;
            }

            RTCPeerConnection pc;
            try
            {
                pc = NewRTCPeerConnection(config);
            }
            catch (Exception ex)
            {
                Exception inner = (ex is System.Reflection.TargetInvocationException && ex.InnerException != null) ? ex.InnerException : ex;
                NewPlugin.LogStatic("[SIPSorcery] RTCPeerConnection ctor failed for " + peerId + ": " + inner.Message);
                return;
            }

            var entry = new PeerEntry();
            entry.PeerId = peerId;
            entry.Pc = pc;
            try { entry.Decoder = OpusDecoder.Create(OpusSampleRate, 1); }
            catch (Exception ex) { NewPlugin.LogStatic("[SIPSorcery] Opus decoder create failed for " + peerId + ": " + ex.Message); }

            try
            {
                // Mono Opus track (sendrecv). RTP rtpmap convention is opus/48000/2;
                // the actual PCM bridged via Concentus is mono (channels = 1).
                var opus = new AudioFormat(AudioCodecsEnum.OPUS, OpusPayloadId, OpusSampleRate, 2, null);
                var track = new MediaStreamTrack(opus, MediaStreamStatusEnum.SendRecv);
                pc.addTrack(track);
            }
            catch (Exception ex)
            {
                NewPlugin.LogStatic("[SIPSorcery] track setup failed for " + peerId + ": " + ex.Message);
            }

            WirePeerEvents(entry);

            lock (_sync)
            {
                if (_peers.ContainsKey(peerId))
                {
                    // Lost a race; tear down the duplicate.
                    DisposeEntry(entry);
                    return;
                }
                _peers[peerId] = entry;
            }
            NewPlugin.LogStatic("[SIPSorcery] PeerConnection created for " + peerId);
        }

        public void ClosePeerConnection(string peerId)
        {
            PeerEntry e;
            lock (_sync)
            {
                if (peerId == null || !_peers.TryGetValue(peerId, out e)) return;
                _peers.Remove(peerId);
            }
            DisposeEntry(e);
            NewPlugin.LogStatic("[SIPSorcery] PeerConnection closed for " + peerId);
        }

        public void CloseAll()
        {
            List<PeerEntry> all;
            lock (_sync)
            {
                all = new List<PeerEntry>(_peers.Values);
                _peers.Clear();
            }
            for (int i = 0; i < all.Count; i++) DisposeEntry(all[i]);
        }

        private void DisposeEntry(PeerEntry e)
        {
            if (e == null) return;
            try { if (e.Pc != null) e.Pc.close(); } catch { }
            try { if (e.Pc != null) e.Pc.Dispose(); } catch { }
        }

        // --- SDP negotiation -------------------------------------------------

        public string CreateOffer(string peerId)
        {
            PeerEntry e = Get(peerId);
            if (e == null) return null;
            try
            {
                RTCSessionDescriptionInit offer = e.Pc.createOffer(null);
                if (offer == null) return null;
                e.Pc.setLocalDescription(offer).Wait(SdpTimeoutMs);
                return offer.sdp;
            }
            catch (Exception ex)
            {
                NewPlugin.LogStatic("[SIPSorcery] CreateOffer failed for " + peerId + ": " + ex.Message);
                return null;
            }
        }

        public string CreateAnswer(string peerId)
        {
            PeerEntry e = Get(peerId);
            if (e == null) return null;
            try
            {
                // Requires the remote offer to have been applied first.
                RTCSessionDescriptionInit answer = e.Pc.createAnswer(null);
                if (answer == null) return null;
                e.Pc.setLocalDescription(answer).Wait(SdpTimeoutMs);
                return answer.sdp;
            }
            catch (Exception ex)
            {
                NewPlugin.LogStatic("[SIPSorcery] CreateAnswer failed for " + peerId + ": " + ex.Message);
                return null;
            }
        }

        public void ApplyRemoteDescription(string peerId, string type, string sdp)
        {
            PeerEntry e = Get(peerId);
            if (e == null || sdp == null) return;
            try
            {
                var init = new RTCSessionDescriptionInit();
                init.type = (type == "offer") ? RTCSdpType.offer : RTCSdpType.answer;
                init.sdp = sdp;
                SetDescriptionResultEnum result = e.Pc.setRemoteDescription(init);
                if (result != SetDescriptionResultEnum.OK)
                    NewPlugin.LogStatic("[SIPSorcery] setRemoteDescription(" + type + ") for " + peerId + " returned " + result + ".");
            }
            catch (Exception ex)
            {
                NewPlugin.LogStatic("[SIPSorcery] ApplyRemoteDescription failed for " + peerId + ": " + ex.Message);
            }
        }

        // --- Trickle ICE -----------------------------------------------------

        public void AddIceCandidate(string peerId, string candidateJson)
        {
            PeerEntry e = Get(peerId);
            if (e == null || candidateJson == null) return;
            try
            {
                // Parse the RTCIceCandidateInit JSON with Newtonsoft.Json.
                JObject jo = JObject.Parse(candidateJson);
                var init = new RTCIceCandidateInit();
                init.candidate = (string)jo["candidate"];

                JToken mid = jo["sdpMid"];
                if (mid != null && mid.Type != JTokenType.Null) init.sdpMid = (string)mid;

                JToken idx = jo["sdpMLineIndex"];
                if (idx != null && idx.Type != JTokenType.Null) init.sdpMLineIndex = (ushort)(int)idx;

                JToken uf = jo["usernameFragment"];
                if (uf != null && uf.Type != JTokenType.Null) init.usernameFragment = (string)uf;

                e.Pc.addIceCandidate(init);
            }
            catch (Exception ex)
            {
                NewPlugin.LogStatic("[SIPSorcery] AddIceCandidate failed for " + peerId + ": " + ex.Message);
            }
        }

        // --- Outbound audio (BASS main-mix tap -> shared Opus encoder) -------

        public void PushOutboundAudio(short[] pcm, int sampleCount, int sampleRate, int channels)
        {
            if (pcm == null || sampleCount <= 0) return;
            if (sampleRate <= 0) sampleRate = OpusSampleRate;
            if (channels <= 0) channels = 1;

            // 1. Downmix interleaved input to mono.
            short[] mono = Downmix(pcm, sampleCount, channels);
            // 2. Resample to the 48 kHz Opus rate if needed.
            short[] mono48 = (sampleRate == OpusSampleRate) ? mono : ResampleTo48k(mono, sampleRate);

            // 3. Accumulate and drain whole 20 ms frames, encoding each once.
            List<byte[]> encodedFrames = null;
            lock (_encLock)
            {
                EnsureEncoder();
                if (_encoder == null) return;

                for (int i = 0; i < mono48.Length; i++) _pending.Add(mono48[i]);

                while (_pending.Count >= FrameSamples)
                {
                    var frame = new short[FrameSamples];
                    _pending.CopyTo(0, frame, 0, FrameSamples);
                    _pending.RemoveRange(0, FrameSamples);

                    var outBytes = new byte[MaxPacketBytes];
                    int len;
                    try { len = _encoder.Encode(frame, 0, FrameSamples, outBytes, 0, outBytes.Length); }
                    catch (Exception ex) { NewPlugin.LogStatic("[SIPSorcery] Opus encode failed: " + ex.Message); break; }

                    if (len <= 0) continue;
                    var packet = new byte[len];
                    Array.Copy(outBytes, packet, len);
                    if (encodedFrames == null) encodedFrames = new List<byte[]>(2);
                    encodedFrames.Add(packet);
                }
            }

            if (encodedFrames == null) return;

            // Send outside the encoder lock. Encode once, transmit to every peer.
            List<PeerEntry> targets;
            lock (_sync) { targets = new List<PeerEntry>(_peers.Values); }
            for (int p = 0; p < targets.Count; p++)
            {
                for (int f = 0; f < encodedFrames.Count; f++)
                {
                    try
                    {
                        targets[p].Pc.SendAudio((uint)FrameSamples, encodedFrames[f]);
                        if (!targets[p].FirstSendLogged)
                        {
                            targets[p].FirstSendLogged = true;
                            NewPlugin.LogStatic("[SIPSorcery] first outbound audio RTP sent to " + targets[p].PeerId);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log the first failure per peer (don't silently swallow forever).
                        if (!targets[p].FirstSendErrorLogged)
                        {
                            targets[p].FirstSendErrorLogged = true;
                            NewPlugin.LogStatic("[SIPSorcery] SendAudio failed for " + targets[p].PeerId + ": " + ex.Message);
                        }
                    }
                }
            }
        }

        private void EnsureEncoder()
        {
            // Caller holds _encLock.
            if (_encoder != null) return;
            try
            {
                _encoder = OpusEncoder.Create(OpusSampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            }
            catch (Exception ex)
            {
                NewPlugin.LogStatic("[SIPSorcery] Opus encoder create failed: " + ex.Message);
            }
        }

        // Interleaved multi-channel PCM16 -> mono (averaged). sampleCount is the
        // per-channel frame count (matches the MR-WebRTC adapter's convention).
        private static short[] Downmix(short[] pcm, int sampleCount, int channels)
        {
            if (channels <= 1)
            {
                int n = Math.Min(sampleCount, pcm.Length);
                var m = new short[n];
                Array.Copy(pcm, m, n);
                return m;
            }
            var mono = new short[sampleCount];
            int produced = 0;
            for (int f = 0; f < sampleCount; f++)
            {
                int sum = 0, count = 0;
                for (int c = 0; c < channels; c++)
                {
                    int idx = f * channels + c;
                    if (idx < pcm.Length) { sum += pcm[idx]; count++; }
                }
                if (count == 0) break;
                mono[produced++] = (short)(sum / count);
            }
            if (produced == mono.Length) return mono;
            var trimmed = new short[produced];
            Array.Copy(mono, trimmed, produced);
            return trimmed;
        }

        // Simple linear resampler to the 48 kHz Opus rate. The BASS pump already
        // delivers 48 kHz mono frames (task 5.4), so this is a safety path for
        // off-rate input only.
        private static short[] ResampleTo48k(short[] mono, int srcRate)
        {
            if (mono.Length == 0 || srcRate == OpusSampleRate) return mono;
            long outLen = (long)mono.Length * OpusSampleRate / srcRate;
            if (outLen <= 0) return new short[0];
            var outBuf = new short[outLen];
            double step = (double)srcRate / OpusSampleRate;
            double pos = 0;
            for (int i = 0; i < outBuf.Length; i++)
            {
                int idx = (int)pos;
                if (idx >= mono.Length - 1)
                {
                    outBuf[i] = mono[mono.Length - 1];
                }
                else
                {
                    double frac = pos - idx;
                    outBuf[i] = (short)(mono[idx] * (1.0 - frac) + mono[idx + 1] * frac);
                }
                pos += step;
            }
            return outBuf;
        }

        // --- Event wiring ----------------------------------------------------

        private void WirePeerEvents(PeerEntry e)
        {
            string peerId = e.PeerId;
            RTCPeerConnection pc = e.Pc;

            pc.onicecandidate += (RTCIceCandidate cand) =>
            {
                Action<string, string> h = OnLocalIceCandidate;
                if (cand == null) return;
                // Log candidate types so we can see whether TURN relay candidates
                // are gathered (essential on CGNAT/Starlink). "typ relay" = TURN.
                try
                {
                    string cstr = cand.candidate ?? cand.ToString();
                    NewPlugin.LogStatic("[SIPSorcery] local ICE cand (" + peerId + "): " + cstr);
                }
                catch { }
                if (h == null) return;
                h(peerId, BuildCandidateJson(cand));
            };

            try
            {
                pc.oniceconnectionstatechange += (RTCIceConnectionState st) =>
                {
                    NewPlugin.LogStatic("[SIPSorcery] ICE state [" + peerId + "]: " + st);
                };
            }
            catch (Exception ex) { NewPlugin.LogStatic("[SIPSorcery] oniceconnectionstatechange unavailable: " + ex.Message); }

            pc.onconnectionstatechange += (RTCPeerConnectionState st) =>
            {
                Action<string, string> h = OnConnectionStateChanged;
                if (h != null) h(peerId, MapState(st));
            };

            // Inbound RTP Opus -> Concentus decode -> PCM16 -> OnRemoteAudioFrame.
            pc.OnRtpPacketReceived += (IPEndPoint endpoint, SDPMediaTypesEnum media, RTPPacket pkt) =>
            {
                if (media != SDPMediaTypesEnum.audio) return;
                if (pkt == null || pkt.Payload == null || pkt.Payload.Length == 0) return;
                e.RtpReceived++;
                if (!e.FirstRtpLogged)
                {
                    e.FirstRtpLogged = true;
                    NewPlugin.LogStatic("[SIPSorcery] first inbound audio RTP from " + peerId
                        + " (pt=" + pkt.Header.PayloadType + ", " + pkt.Payload.Length + " bytes)");
                }
                DecodeRemote(e, pkt.Payload);
            };
        }

        private void DecodeRemote(PeerEntry e, byte[] payload)
        {
            OpusDecoder decoder = e.Decoder;
            if (decoder == null) return;

            Action<string, short[], int, int> h = OnRemoteAudioFrame;
            if (h == null) return;

            var outPcm = new short[MaxDecodeSamples];
            int decoded;
            try
            {
                lock (e.DecodeLock)
                {
                    decoded = decoder.Decode(payload, 0, payload.Length, outPcm, 0, MaxDecodeSamples, false);
                }
            }
            catch (Exception ex)
            {
                NewPlugin.LogStatic("[SIPSorcery] Opus decode failed for " + e.PeerId + ": " + ex.Message);
                return;
            }
            if (decoded <= 0) return;

            var frame = new short[decoded];
            Array.Copy(outPcm, frame, decoded);
            h(e.PeerId, frame, decoded, OpusSampleRate);
        }

        // Map SIPSorcery's connection state to the IWebRtcPeer string contract.
        // Both failed and disconnected surface as "failed" so the initiating peer
        // reports the connection failure (Requirement 1.5).
        private static string MapState(RTCPeerConnectionState s)
        {
            switch (s)
            {
                case RTCPeerConnectionState.@new: return "new";
                case RTCPeerConnectionState.connecting: return "connecting";
                case RTCPeerConnectionState.connected: return "connected";
                case RTCPeerConnectionState.failed: return "failed";
                case RTCPeerConnectionState.disconnected: return "failed";
                case RTCPeerConnectionState.closed: return "closed";
                default: return s.ToString().ToLowerInvariant();
            }
        }

        private PeerEntry Get(string peerId)
        {
            if (peerId == null) return null;
            lock (_sync)
            {
                PeerEntry e;
                return _peers.TryGetValue(peerId, out e) ? e : null;
            }
        }

        public void Dispose()
        {
            CloseAll();
        }

        // --- Lightweight JSON helper (Newtonsoft, same intent as the MR adapter) --

        private static string BuildCandidateJson(RTCIceCandidate cand)
        {
            var jo = new JObject();
            jo["candidate"] = cand.candidate != null ? cand.candidate : "";
            jo["sdpMid"] = cand.sdpMid != null ? (JToken)cand.sdpMid : JValue.CreateNull();
            jo["sdpMLineIndex"] = (int)cand.sdpMLineIndex;
            return jo.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
// ==================  END SIPSORCERY + CONCENTUS FALLBACK ADAPTER  ============

// =============================================================================
// =========  ISOLATED SIPSORCERY 6.2.4 (child AppDomain) — Task 9  ============
// =============================================================================
// PlayIt Live ships its OWN SIPSorcery 5.2.3 in its app directory, which the CLR
// probes FIRST (unsigned -> matched by simple name), so our bundled 6.2.4 never
// wins in the default AppDomain. 5.2.3's RTCPeerConnection does not gather
// STUN/TURN candidates, so ICE fails on CGNAT/Starlink.
//
// Fix: run the SIPSorcery peer in a CHILD AppDomain whose ApplicationBase is a
// clean temp folder (NOT the host directory). There, the host's 5.2.3 is never
// on the probe path, and Costura (inside our copied PartylinePlugin.dll) resolves
// the embedded 6.2.4. The default domain talks to the child via MarshalByRefObject
// proxies; audio frames (short[]) and signaling (strings) marshal by value.
namespace Partyline.WebRtc
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;

    /// <summary>Serializable decoded-remote-audio frame marshaled child -> default.</summary>
    [Serializable]
    public sealed class AudioFrameDto
    {
        public string PeerId;
        public short[] Pcm;
        public int Count;
        public int Rate;
    }

    /// <summary>Serializable signaling event (local ICE candidate or state change).</summary>
    [Serializable]
    public sealed class SignalEventDto
    {
        public string Kind;   // "ice" | "state"
        public string PeerId;
        public string Data;   // candidate JSON, or connection-state string
    }

    /// <summary>
    /// Lives INSIDE the child AppDomain. Wraps a real <see cref="SipSorceryWebRtcPeer"/>
    /// (which binds to SIPSorcery 6.2.4 here). SIPSorcery's callbacks fire on its own
    /// worker threads; those threads must NOT cross the AppDomain boundary (cross-domain
    /// marshaling needs a deep stack and overflowed SIPSorcery's small thread stacks,
    /// crashing the 32-bit host). So callbacks only ENQUEUE into in-domain buffers; the
    /// default domain pulls them via Drain* (those calls run on a healthy default-domain
    /// stack).
    /// </summary>
    public sealed class SipSorceryDomainWorker : MarshalByRefObject
    {
        private SipSorceryWebRtcPeer _peer;
        private readonly object _qlock = new object();
        private readonly Queue<SignalEventDto> _events = new Queue<SignalEventDto>();
        private readonly Queue<AudioFrameDto> _audio = new Queue<AudioFrameDto>();
        private const int MaxAudioQueued = 300;   // ~6s of 20ms frames; drop oldest beyond
        private const int MaxEventsQueued = 2000;

        public void Init()
        {
            _peer = new SipSorceryWebRtcPeer();
            _peer.OnLocalIceCandidate = (peerId, json) =>
            {
                lock (_qlock)
                {
                    if (_events.Count < MaxEventsQueued)
                        _events.Enqueue(new SignalEventDto { Kind = "ice", PeerId = peerId, Data = json });
                }
            };
            _peer.OnConnectionStateChanged = (peerId, state) =>
            {
                lock (_qlock)
                {
                    if (_events.Count < MaxEventsQueued)
                        _events.Enqueue(new SignalEventDto { Kind = "state", PeerId = peerId, Data = state });
                }
            };
            _peer.OnRemoteAudioFrame = (peerId, pcm, count, rate) =>
            {
                // Copy the buffer: SIPSorcery may reuse it after the callback returns.
                short[] copy = new short[count];
                if (pcm != null) Array.Copy(pcm, copy, Math.Min(count, pcm.Length));
                lock (_qlock)
                {
                    if (_audio.Count >= MaxAudioQueued) _audio.Dequeue();
                    _audio.Enqueue(new AudioFrameDto { PeerId = peerId, Pcm = copy, Count = count, Rate = rate });
                }
            };
        }

        // --- Pulled by the default domain (runs on a healthy stack) ----------
        public SignalEventDto[] DrainEvents()
        {
            lock (_qlock)
            {
                if (_events.Count == 0) return null;
                var arr = _events.ToArray();
                _events.Clear();
                return arr;
            }
        }

        public AudioFrameDto[] DrainRemoteAudio()
        {
            lock (_qlock)
            {
                if (_audio.Count == 0) return null;
                var arr = _audio.ToArray();
                _audio.Clear();
                return arr;
            }
        }

        // --- Forwarded IWebRtcPeer operations (default -> child) -------------
        // Heavy SIPSorcery operations run on a dedicated thread with a LARGE stack.
        // The cross-AppDomain transition consumes stack before SIPSorcery's deep
        // peer-connection/DTLS/ICE setup runs; on the default 1 MB caller stack that
        // overflowed and crashed the 32-bit host ("guard page cannot be created").
        // A 16 MB executor stack gives that synchronous setup room, and serializes
        // SIPSorcery access. PushOutboundAudio stays direct (shallow ring-buffer write).
        public void SetIceServers(WebRtcIceServer[] iceServers) { Exec(() => _peer.SetIceServers(iceServers)); }
        public void CreatePeerConnection(string peerId) { Exec(() => _peer.CreatePeerConnection(peerId)); }
        public void ClosePeerConnection(string peerId) { Exec(() => _peer.ClosePeerConnection(peerId)); }
        public void CloseAll() { if (_peer != null) Exec(() => _peer.CloseAll()); }
        public string CreateOffer(string peerId) { return Exec(() => _peer.CreateOffer(peerId)); }
        public string CreateAnswer(string peerId) { return Exec(() => _peer.CreateAnswer(peerId)); }
        public void ApplyRemoteDescription(string peerId, string type, string sdp) { Exec(() => _peer.ApplyRemoteDescription(peerId, type, sdp)); }
        public void AddIceCandidate(string peerId, string candidateJson) { Exec(() => _peer.AddIceCandidate(peerId, candidateJson)); }
        public void PushOutboundAudio(short[] pcm, int sampleCount, int sampleRate, int channels) { _peer.PushOutboundAudio(pcm, sampleCount, sampleRate, channels); }

        // --- Large-stack single-threaded executor ---------------------------
        private Thread _execThread;
        private readonly object _execQLock = new object();
        private readonly Queue<Action> _execQueue = new Queue<Action>();
        private readonly AutoResetEvent _execSignal = new AutoResetEvent(false);

        private void EnsureExecThread()
        {
            if (_execThread != null) return;
            lock (_execQLock)
            {
                if (_execThread != null) return;
                _execThread = new Thread(ExecLoop, 16 * 1024 * 1024) { IsBackground = true, Name = "SipSorceryExec" };
                _execThread.Start();
            }
        }

        private void ExecLoop()
        {
            while (true)
            {
                Action a = null;
                lock (_execQLock) { if (_execQueue.Count > 0) a = _execQueue.Dequeue(); }
                if (a == null) { _execSignal.WaitOne(100); continue; }
                try { a(); } catch (Exception ex) { NewPlugin.LogStatic("[Isolated] exec op error: " + ex.Message); }
            }
        }

        private void Exec(Action a)
        {
            EnsureExecThread();
            Exception err = null;
            using (var done = new ManualResetEventSlim(false))
            {
                lock (_execQLock)
                {
                    _execQueue.Enqueue(() => { try { a(); } catch (Exception e) { err = e; } finally { done.Set(); } });
                }
                _execSignal.Set();
                done.Wait();
            }
            if (err != null) NewPlugin.LogStatic("[Isolated] op threw: " + err.Message);
        }

        private T Exec<T>(Func<T> f)
        {
            T result = default(T);
            Exec(() => { result = f(); });
            return result;
        }

        public override object InitializeLifetimeService() { return null; }
    }

    /// <summary>
    /// Default-domain <see cref="IWebRtcPeer"/> that hosts a child AppDomain running
    /// SIPSorcery 6.2.4 in isolation from the host's shadowing 5.2.3. A poll thread in
    /// THIS domain pulls buffered candidates/audio/state from the worker so SIPSorcery's
    /// own threads never make cross-domain calls.
    /// </summary>
    public sealed class IsolatedSipSorceryPeer : IWebRtcPeer, IDisposable
    {
        private AppDomain _domain;
        private SipSorceryDomainWorker _worker;
        private Thread _pump;
        private volatile bool _running;

        public Action<string, string> OnLocalIceCandidate { get; set; }
        public Action<string, short[], int, int> OnRemoteAudioFrame { get; set; }
        public Action<string, string> OnConnectionStateChanged { get; set; }

        public IsolatedSipSorceryPeer()
        {
            var selfAsm = typeof(IsolatedSipSorceryPeer).Assembly;
            string selfSrc = selfAsm.Location;
            string ver = selfAsm.GetName().Version != null ? selfAsm.GetName().Version.ToString() : "0";
            string baseDir = Path.Combine(Path.Combine(Path.GetTempPath(), "Partyline.webrtc"), ver);
            Directory.CreateDirectory(baseDir);

            // Copy our plugin DLL into the clean base dir so the child domain loads
            // it (and resolves SIPSorcery 6.2.4 from Costura) WITHOUT probing the
            // host directory where 5.2.3 lives.
            string selfDst = Path.Combine(baseDir, Path.GetFileName(selfSrc));
            try { File.Copy(selfSrc, selfDst, true); }
            catch (Exception ex) { NewPlugin.LogStatic("[Isolated] plugin DLL copy skipped: " + ex.Message); }
            if (!File.Exists(selfDst))
                throw new InvalidOperationException("could not stage plugin DLL at " + selfDst);

            var setup = new AppDomainSetup();
            setup.ApplicationBase = baseDir;
            _domain = AppDomain.CreateDomain("PartylineWebRtc", null, setup);

            _worker = (SipSorceryDomainWorker)_domain.CreateInstanceAndUnwrap(
                selfAsm.FullName, typeof(SipSorceryDomainWorker).FullName);
            _worker.Init();

            _running = true;
            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "PartylineWebRtcPump" };
            _pump.Start();
            NewPlugin.LogStatic("[Isolated] SIPSorcery worker started in child AppDomain (base=" + baseDir + ").");
        }

        // Default-domain poll loop: pulls buffered events/audio from the child worker
        // and raises them locally. Cross-domain calls run on THIS thread's healthy stack.
        private void PumpLoop()
        {
            while (_running)
            {
                bool any = false;
                try
                {
                    SignalEventDto[] events = _worker.DrainEvents();
                    if (events != null)
                    {
                        any = true;
                        for (int i = 0; i < events.Length; i++)
                        {
                            var ev = events[i];
                            if (ev == null) continue;
                            if (ev.Kind == "ice") { var h = OnLocalIceCandidate; if (h != null) h(ev.PeerId, ev.Data); }
                            else if (ev.Kind == "state") { var h = OnConnectionStateChanged; if (h != null) h(ev.PeerId, ev.Data); }
                        }
                    }

                    AudioFrameDto[] frames = _worker.DrainRemoteAudio();
                    if (frames != null)
                    {
                        any = true;
                        var h = OnRemoteAudioFrame;
                        if (h != null)
                            for (int i = 0; i < frames.Length; i++)
                            {
                                var f = frames[i];
                                if (f != null) h(f.PeerId, f.Pcm, f.Count, f.Rate);
                            }
                    }
                }
                catch (Exception ex)
                {
                    NewPlugin.LogStatic("[Isolated] pump error: " + ex.Message);
                }
                if (!any) Thread.Sleep(10);
            }
        }

        public void SetIceServers(WebRtcIceServer[] iceServers) { _worker.SetIceServers(iceServers); }
        public void CreatePeerConnection(string peerId) { _worker.CreatePeerConnection(peerId); }
        public void ClosePeerConnection(string peerId) { _worker.ClosePeerConnection(peerId); }
        public void CloseAll() { try { if (_worker != null) _worker.CloseAll(); } catch { } }
        public string CreateOffer(string peerId) { return _worker.CreateOffer(peerId); }
        public string CreateAnswer(string peerId) { return _worker.CreateAnswer(peerId); }
        public void ApplyRemoteDescription(string peerId, string type, string sdp) { _worker.ApplyRemoteDescription(peerId, type, sdp); }
        public void AddIceCandidate(string peerId, string candidateJson) { _worker.AddIceCandidate(peerId, candidateJson); }
        public void PushOutboundAudio(short[] pcm, int sampleCount, int sampleRate, int channels) { _worker.PushOutboundAudio(pcm, sampleCount, sampleRate, channels); }

        public void Dispose()
        {
            _running = false;
            try { if (_pump != null) _pump.Join(1000); } catch { }
            try { if (_worker != null) _worker.CloseAll(); } catch { }
            try { if (_domain != null) AppDomain.Unload(_domain); } catch { }
            _domain = null; _worker = null;
        }
    }
}
// =========  END ISOLATED SIPSORCERY 6.2.4 (child AppDomain)  =================


// =============================================================================
// =========  OUT-OF-PROCESS WEBRTC HOST CLIENT (Task 9)  ======================
// =============================================================================
// Runs SIPSorcery 6.2.4 in a separate 64-bit process (PartylineWebRtcHost.exe),
// embedded as a zip resource and extracted at runtime. This sidesteps BOTH the
// host's shadowing SIPSorcery 5.2.3 AND the 32-bit address-space/stack limits
// that crashed PlayItLive when 6.2.4 ran in-process or in a child AppDomain. If
// the helper crashes, only the helper dies — PlayIt Live keeps running.
namespace Partyline.WebRtc
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Text;
    using System.Threading;
    using Newtonsoft.Json.Linq;

    public sealed class OutOfProcessWebRtcPeer : IWebRtcPeer, IDisposable
    {
        private Process _proc;
        private Stream _stdin;
        private Stream _stdout;
        private Thread _reader;
        private volatile bool _running;
        private readonly object _writeLock = new object();
        private readonly string _extractDir; // temp folder holding the extracted exe + dlls

        // Pending CreateOffer/CreateAnswer awaiters, keyed by peerId.
        private readonly object _pendLock = new object();
        private readonly Dictionary<string, SdpWaiter> _offerWaiters = new Dictionary<string, SdpWaiter>();
        private readonly Dictionary<string, SdpWaiter> _answerWaiters = new Dictionary<string, SdpWaiter>();

        public Action<string, string> OnLocalIceCandidate { get; set; }
        public Action<string, short[], int, int> OnRemoteAudioFrame { get; set; }
        public Action<string, string> OnConnectionStateChanged { get; set; }

        private sealed class SdpWaiter
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public string Sdp;
        }

        public OutOfProcessWebRtcPeer()
        {
            string exePath = ExtractHost();
            _extractDir = Path.GetDirectoryName(exePath);
            var psi = new ProcessStartInfo(exePath);
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exePath);

            _proc = Process.Start(psi);
            _stdin = _proc.StandardInput.BaseStream;
            _stdout = _proc.StandardOutput.BaseStream;

            _running = true;
            _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "WebRtcHostReader" };
            _reader.Start();

            var errThread = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = _proc.StandardError.ReadLine()) != null)
                        NewPlugin.LogStatic("[host:stderr] " + line);
                }
                catch { }
            }) { IsBackground = true, Name = "WebRtcHostStderr" };
            errThread.Start();

            NewPlugin.LogStatic("[OutOfProc] WebRTC host launched: " + exePath + " (pid " + _proc.Id + ").");
        }

        // Extract the embedded webrtchost.zip to a stable temp dir; returns the exe path.
        private static string ExtractHost()
        {
            var self = typeof(OutOfProcessWebRtcPeer).Assembly;

            string resName = null;
            foreach (var n in self.GetManifestResourceNames())
                if (n.IndexOf("webrtchost", StringComparison.OrdinalIgnoreCase) >= 0 && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                { resName = n; break; }
            if (resName == null) throw new InvalidOperationException("embedded webrtchost.zip not found (build the plugin so the host is bundled).");

            // Read the embedded zip bytes and key the extraction dir on their hash, so
            // a rebuilt helper ALWAYS re-extracts. (The assembly version is constant at
            // 1.0.0.0, so versioning the dir left stale helpers running after rebuilds.)
            byte[] zipBytes;
            using (var rs = self.GetManifestResourceStream(resName))
            using (var ms = new MemoryStream())
            {
                if (rs == null) throw new InvalidOperationException("webrtchost.zip resource stream null.");
                rs.CopyTo(ms);
                zipBytes = ms.ToArray();
            }
            string hash;
            using (var md5 = System.Security.Cryptography.MD5.Create())
                hash = BitConverter.ToString(md5.ComputeHash(zipBytes), 0, 6).Replace("-", "").ToLowerInvariant();

            string baseDir = Path.Combine(Path.Combine(Path.GetTempPath(), "Partyline.webrtchost"), hash);
            string exePath = Path.Combine(baseDir, "PartylineWebRtcHost.exe");
            if (File.Exists(exePath)) return exePath; // this exact build already extracted

            // Fresh extract (clear any partial leftovers for this hash).
            try { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true); } catch { }
            Directory.CreateDirectory(baseDir);

            string zipPath = Path.Combine(baseDir, "host.zip");
            File.WriteAllBytes(zipPath, zipBytes);
            ZipFile.ExtractToDirectory(zipPath, baseDir);
            try { File.Delete(zipPath); } catch { }

            if (!File.Exists(exePath)) throw new InvalidOperationException("host exe missing after extract: " + exePath);
            NewPlugin.LogStatic("[OutOfProc] extracted helper build " + hash + " to " + baseDir);
            return exePath;
        }

        // --- IWebRtcPeer (encode command -> send frame) ----------------------

        public void SetIceServers(WebRtcIceServer[] iceServers)
        {
            var arr = new JArray();
            if (iceServers != null)
            {
                foreach (var s in iceServers)
                {
                    if (s == null) continue;
                    var jo = new JObject();
                    var urls = new JArray();
                    if (s.Urls != null) foreach (var u in s.Urls) urls.Add(u);
                    jo["urls"] = urls;
                    if (s.Username != null) jo["username"] = s.Username;
                    if (s.Credential != null) jo["credential"] = s.Credential;
                    arr.Add(jo);
                }
            }
            SendText(1, arr.ToString(Newtonsoft.Json.Formatting.None));
        }

        public void CreatePeerConnection(string peerId) { SendText(2, peerId); }
        public void ClosePeerConnection(string peerId) { SendText(3, peerId); }
        public void CloseAll() { try { SendText(4, ""); } catch { } }

        public string CreateOffer(string peerId) { return AwaitSdp(5, peerId, _offerWaiters); }
        public string CreateAnswer(string peerId) { return AwaitSdp(6, peerId, _answerWaiters); }

        private string AwaitSdp(byte cmd, string peerId, Dictionary<string, SdpWaiter> waiters)
        {
            var w = new SdpWaiter();
            lock (_pendLock) { waiters[peerId] = w; }
            SendText(cmd, peerId);
            if (!w.Done.Wait(12000))
            {
                lock (_pendLock) { waiters.Remove(peerId); }
                NewPlugin.LogStatic("[OutOfProc] SDP wait timed out for " + peerId);
                return null;
            }
            return w.Sdp;
        }

        public void ApplyRemoteDescription(string peerId, string type, string sdp)
        {
            var jo = new JObject();
            jo["peerId"] = peerId; jo["type"] = type; jo["sdp"] = sdp;
            SendText(7, jo.ToString(Newtonsoft.Json.Formatting.None));
        }

        public void AddIceCandidate(string peerId, string candidateJson)
        {
            try
            {
                var jo = new JObject();
                jo["peerId"] = peerId;
                jo["candidate"] = candidateJson != null ? JToken.Parse(candidateJson) : JValue.CreateNull();
                SendText(8, jo.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex) { NewPlugin.LogStatic("[OutOfProc] AddIceCandidate encode failed: " + ex.Message); }
        }

        public void PushOutboundAudio(short[] pcm, int sampleCount, int sampleRate, int channels)
        {
            if (pcm == null || sampleCount <= 0) return;
            int n = Math.Min(sampleCount, pcm.Length);
            byte[] payload = new byte[1 + 12 + n * 2];
            payload[0] = 9;
            WriteIntLE(payload, 1, n);
            WriteIntLE(payload, 5, sampleRate);
            WriteIntLE(payload, 9, channels);
            int off = 13;
            for (int i = 0; i < n; i++)
            {
                payload[off++] = (byte)(pcm[i] & 0xFF);
                payload[off++] = (byte)((pcm[i] >> 8) & 0xFF);
            }
            SendRaw(payload);
        }

        // --- frame IO --------------------------------------------------------

        private void SendText(byte type, string text)
        {
            byte[] body = text != null ? Encoding.UTF8.GetBytes(text) : new byte[0];
            byte[] payload = new byte[1 + body.Length];
            payload[0] = type;
            Array.Copy(body, 0, payload, 1, body.Length);
            SendRaw(payload);
        }

        // payload already includes the leading type byte.
        private void SendRaw(byte[] payload)
        {
            if (!_running) return;
            byte[] frame = new byte[4 + payload.Length];
            int len = payload.Length;
            frame[0] = (byte)(len & 0xFF);
            frame[1] = (byte)((len >> 8) & 0xFF);
            frame[2] = (byte)((len >> 16) & 0xFF);
            frame[3] = (byte)((len >> 24) & 0xFF);
            Array.Copy(payload, 0, frame, 4, payload.Length);
            try
            {
                lock (_writeLock) { _stdin.Write(frame, 0, frame.Length); _stdin.Flush(); }
            }
            catch (Exception ex) { NewPlugin.LogStatic("[OutOfProc] write failed: " + ex.Message); }
        }

        private static void WriteIntLE(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF);
            b[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        private void ReaderLoop()
        {
            try
            {
                while (_running)
                {
                    byte[] lenB = ReadFull(4);
                    if (lenB == null) break;
                    int len = lenB[0] | (lenB[1] << 8) | (lenB[2] << 16) | (lenB[3] << 24);
                    if (len <= 0) continue;
                    byte[] payload = ReadFull(len);
                    if (payload == null) break;
                    HandleEvent(payload);
                }
            }
            catch (Exception ex) { NewPlugin.LogStatic("[OutOfProc] reader error: " + ex.Message); }
            if (_running) NewPlugin.LogStatic("[OutOfProc] WebRTC host stream ended (process exited?).");
        }

        private void HandleEvent(byte[] payload)
        {
            byte type = payload[0];
            int dlen = payload.Length - 1;
            switch (type)
            {
                case 100: // LocalIce {peerId, candidate}
                {
                    JObject jo = JObject.Parse(Encoding.UTF8.GetString(payload, 1, dlen));
                    var h = OnLocalIceCandidate;
                    if (h != null) h((string)jo["peerId"], (string)jo["candidate"]);
                    break;
                }
                case 101: // RemoteAudio [pidLen][pid][count][rate][int16*]
                {
                    int o = 1;
                    int pidLen = ReadIntLE(payload, o); o += 4;
                    string peerId = Encoding.UTF8.GetString(payload, o, pidLen); o += pidLen;
                    int count = ReadIntLE(payload, o); o += 4;
                    int rate = ReadIntLE(payload, o); o += 4;
                    short[] pcm = new short[count];
                    for (int i = 0; i < count; i++) { pcm[i] = (short)(payload[o] | (payload[o + 1] << 8)); o += 2; }
                    var h = OnRemoteAudioFrame;
                    if (h != null) h(peerId, pcm, count, rate);
                    break;
                }
                case 102: // State {peerId,state}
                {
                    JObject jo = JObject.Parse(Encoding.UTF8.GetString(payload, 1, dlen));
                    var h = OnConnectionStateChanged;
                    if (h != null) h((string)jo["peerId"], (string)jo["state"]);
                    break;
                }
                case 103: CompleteSdp(_offerWaiters, payload, dlen); break;
                case 104: CompleteSdp(_answerWaiters, payload, dlen); break;
                case 105: // Log
                    NewPlugin.LogStatic(Encoding.UTF8.GetString(payload, 1, dlen));
                    break;
                default:
                    NewPlugin.LogStatic("[OutOfProc] unknown event type " + type);
                    break;
            }
        }

        private void CompleteSdp(Dictionary<string, SdpWaiter> waiters, byte[] payload, int dlen)
        {
            JObject jo = JObject.Parse(Encoding.UTF8.GetString(payload, 1, dlen));
            string peerId = (string)jo["peerId"];
            string sdp = (string)jo["sdp"];
            SdpWaiter w = null;
            lock (_pendLock) { if (waiters.TryGetValue(peerId, out w)) waiters.Remove(peerId); }
            if (w != null) { w.Sdp = sdp; w.Done.Set(); }
        }

        private static int ReadIntLE(byte[] b, int off)
        {
            return b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);
        }

        private byte[] ReadFull(int n)
        {
            byte[] buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r;
                try { r = _stdout.Read(buf, off, n - off); }
                catch { return null; }
                if (r <= 0) return null;
                off += r;
            }
            return buf;
        }

        public void Dispose()
        {
            _running = false;
            try { CloseAll(); } catch { }
            try { if (_stdin != null) _stdin.Close(); } catch { }
            try
            {
                if (_proc != null && !_proc.WaitForExit(1500)) _proc.Kill();
            }
            catch { }
            try { if (_reader != null) _reader.Join(1000); } catch { }
            // The process has exited; remove the decompressed helper (exe + DLLs) so
            // nothing is left on disk after shutdown.
            try { CleanupExtractedHost(_extractDir); } catch { }
        }

        // Deletes the extracted helper folder (and any stale sibling builds). The exe
        // can stay locked for a moment after exit, so retry briefly.
        private static void CleanupExtractedHost(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            for (int i = 0; i < 15; i++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    break;
                }
                catch { System.Threading.Thread.Sleep(100); }
            }
            // Sweep sibling builds left under %TEMP%\Partyline.webrtchost (e.g. from a
            // prior crash that never reached Dispose).
            try
            {
                string parent = Path.GetDirectoryName(dir);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)
                    && Path.GetFileName(parent).Equals("Partyline.webrtchost", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var sub in Directory.GetDirectories(parent))
                    {
                        try { Directory.Delete(sub, true); } catch { }
                    }
                    try { Directory.Delete(parent, false); } catch { }
                }
            }
            catch { }
            NewPlugin.LogStatic("[OutOfProc] cleaned up extracted helper files.");
        }
    }
}
// =========  END OUT-OF-PROCESS WEBRTC HOST CLIENT  ===========================
