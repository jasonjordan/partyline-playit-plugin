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
    private const string SignalingBaseUrl = "https://signalling.compressed.stream";

    private static string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Partyline", "partyline.log");
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

    // Return audio (BASS mixer capture)
    private int _mixerHandle;
    private int _mixerChannels = 2; // default stereo, updated from BASS_ChannelGetInfo
    private int _mixerFreq = 44100; // default 44.1kHz, updated from BASS_ChannelGetInfo
    private byte[] _returnBuffer = new byte[44100 * 2 * 4]; // 4 seconds of 16-bit mono at 44.1kHz
    private int _returnWritePos;
    private int _returnReadPos;
    private int _returnAvailable;
    private readonly object _returnLock = new object();
    private Thread _captureThread;
    private volatile bool _captureRunning;

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

    public void Run(IPlayItLiveApp app)
    {
        try
        {
            _cts = new CancellationTokenSource();
            _app = app;
            Log("Plugin starting...");

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
                // Default path (PARTYLINE_MRWEBRTC off, or MR load/construction failed):
                // pure-managed SIPSorcery + Concentus fallback (R5.1 amendment is
                // documented on the adapter type).
                peer = new Partyline.WebRtc.SipSorceryWebRtcPeer();
                Log("WebRTC binding: SIPSorcery + Concentus (managed fallback).");
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
            string[] meta = _settingsManager.LoadMeta(); // [roomName, stationName, djName]
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
                        Log("Mixer format: freq=" + info.freq + " chans=" + info.chans + " flags=" + info.flags);
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
        if (!_dspFirstCallLogged)
        {
            _dspFirstCallLogged = true;
            Log("DspCallback firing: length=" + length + " channels=" + _mixerChannels);
        }

        if (!_meshActive) return;

        try
        {
            int floatSamples = length / 4;
            int channels = _mixerChannels;
            int monoSamples = floatSamples / channels;

            // Copy float data from native buffer
            float[] floatData = new float[floatSamples];
            Marshal.Copy(buffer, floatData, 0, floatSamples);

            // Convert to PCM16 mono
            byte[] pcm16 = new byte[monoSamples * 2];
            for (int i = 0; i < monoSamples; i++)
            {
                float sum = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    int idx = i * channels + ch;
                    if (idx < floatSamples)
                    {
                        sum += floatData[idx];
                    }
                }
                float mono = sum / channels;
                // Reduce level by 6dB to prevent clipping
                mono = mono * 0.5f;
                if (mono > 1f) mono = 1f;
                if (mono < -1f) mono = -1f;
                short s16 = (short)(mono * 32767f);
                pcm16[i * 2] = (byte)(s16 & 0xFF);
                pcm16[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
            }

            lock (_returnLock)
            {
                for (int i = 0; i < pcm16.Length; i++)
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
        var form = new PartylineConfigForm(_settingsManager);
        if (form.ShowDialog() == DialogResult.OK)
        {
            // Push saved co-host accounts + display names to the running mesh so
            // invite links and room/station/DJ names take effect without a restart.
            try
            {
                List<CoHostAccount> accounts = _settingsManager.Load();
                string[] meta = _settingsManager.LoadMeta();
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
        }
        else if (s == "failed" || s == "closed" || s == "disconnected")
        {
            bool ignored;
            _connectedPeers.TryRemove(peerId, out ignored);
            // Drop any stale quality reading so the strip doesn't show a dead peer.
            CohostNetStat removed;
            _cohostNetStats.TryRemove(peerId, out removed);
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
        short[] frame = new short[frameSamples];
        bool firstPushLogged = false;

        while (_meshActive)
        {
            try
            {
                // Always drain a frame so the ring buffer does not accumulate latency.
                bool haveFrame = TryReadMainMixFrame(frame);

                // Co-hosts always hear the program: the captured main mix is sent to
                // every connected peer unconditionally (no mic-toggle gating). Snapshot
                // the field so a concurrent peer swap is safe.
                IWebRtcPeer peer = _webRtcPeer;
                if (haveFrame && peer != null)
                {
                    peer.PushOutboundAudio(frame, frameSamples, 48000, 1);
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
            Thread.Sleep(20);
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
        int srcFreq = _mixerFreq > 0 ? _mixerFreq : 44100;

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
            // Nearest-sample resample srcFreq -> 48 kHz.
            int srcIdx = (int)((long)i * srcFreq / 48000);
            if (srcIdx >= inputSamples) srcIdx = inputSamples - 1;
            int lo = raw[srcIdx * 2];
            int hi = raw[srcIdx * 2 + 1];
            frame[i] = (short)(lo | (hi << 8));
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
            if (File.Exists(IdentityPath))
            {
                string json = File.ReadAllText(IdentityPath, Encoding.UTF8);
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
            string dir = Path.GetDirectoryName(IdentityPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string body = "{\"roomId\":\"" + roomId + "\",\"djKey\":\"" + djKey + "\"}";
            File.WriteAllText(IdentityPath, body, Encoding.UTF8);
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
            if (File.Exists(MetaPath))
            {
                string json = File.ReadAllText(MetaPath, Encoding.UTF8);
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

    public void SaveMeta(string roomName, string stationName, string djName)
    {
        try
        {
            string dir = Path.GetDirectoryName(MetaPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.Append("{\"roomName\":").Append(EscapeJsonString(roomName ?? ""));
            sb.Append(",\"stationName\":").Append(EscapeJsonString(stationName ?? ""));
            sb.Append(",\"djName\":").Append(EscapeJsonString(djName ?? ""));
            sb.Append("}");
            File.WriteAllText(MetaPath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            NewPlugin.LogStatic("ERROR writing meta.json: " + ex.Message);
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
            if (!File.Exists(SettingsPath))
            {
                NewPlugin.LogStatic("Settings file not found at " + SettingsPath + ", starting with empty list");
                return new List<CoHostAccount>();
            }

            string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
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
            if (!File.Exists(SettingsPath))
            {
                return "Partyline Co-Host";
            }

            string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
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
            if (!File.Exists(SettingsPath))
            {
                return "";
            }

            string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
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
            if (!File.Exists(SettingsPath))
            {
                return "";
            }

            string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
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
            string dir = Path.GetDirectoryName(SettingsPath);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

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

            File.WriteAllText(SettingsPath, sb.ToString(), Encoding.UTF8);
            NewPlugin.LogStatic("Saved " + accounts.Count + " co-host accounts to settings");
        }
        catch (Exception ex)
        {
            NewPlugin.LogStatic("ERROR saving settings: " + ex.Message);
            throw;
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
    private string _roomName;
    private string _djName;
    private Button _btnSave;
    private Button _btnCancel;
    private Button _btnAdd;
    private int _editingIndex;

    public PartylineConfigForm(SettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        _accounts = _settingsManager.Load();
        string[] meta = _settingsManager.LoadMeta();
        _roomName = meta[0];
        _stationName = meta[1];
        _djName = meta[2];
        _relayUrl = _settingsManager.LoadRelayUrl();
        _stationKey = _settingsManager.LoadStationKey();
        _editingIndex = -1;
        InitializeFormComponents();
        LoadGrid();
    }

    private void InitializeFormComponents()
    {
        Text = "Partyline Co-Host Configuration";
        Width = 550;
        Height = 720;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        // Display metadata shown to co-hosts on their screen. These are labels only
        // (the room id stays auto-generated); the plugin publishes them to the
        // signaling server so the co-host page can render them.
        Label lblStationName = new Label();
        lblStationName.Text = "Station Name:";
        lblStationName.Location = new System.Drawing.Point(12, 15);
        lblStationName.AutoSize = true;
        lblStationName.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
        Controls.Add(lblStationName);
        _txtStationName = new TextBox();
        _txtStationName.Location = new System.Drawing.Point(130, 12);
        _txtStationName.Size = new System.Drawing.Size(390, 22);
        _txtStationName.Text = _stationName;
        Controls.Add(_txtStationName);

        Label lblRoomName = new Label();
        lblRoomName.Text = "Room Name:";
        lblRoomName.Location = new System.Drawing.Point(12, 45);
        lblRoomName.AutoSize = true;
        lblRoomName.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
        Controls.Add(lblRoomName);
        _txtRoomName = new TextBox();
        _txtRoomName.Location = new System.Drawing.Point(130, 42);
        _txtRoomName.Size = new System.Drawing.Size(390, 22);
        _txtRoomName.Text = _roomName;
        Controls.Add(_txtRoomName);

        Label lblDjName = new Label();
        lblDjName.Text = "DJ Name:";
        lblDjName.Location = new System.Drawing.Point(12, 75);
        lblDjName.AutoSize = true;
        lblDjName.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
        Controls.Add(lblDjName);
        _txtDjName = new TextBox();
        _txtDjName.Location = new System.Drawing.Point(130, 72);
        _txtDjName.Size = new System.Drawing.Size(390, 22);
        _txtDjName.Text = _djName;
        Controls.Add(_txtDjName);

        Label lblIntro = new Label();
        lblIntro.Text = "Add a co-host below and set their password. Use the Copy button to send each co-host their personal join link.";
        lblIntro.Location = new System.Drawing.Point(12, 104);
        lblIntro.Size = new System.Drawing.Size(510, 32);
        lblIntro.ForeColor = System.Drawing.SystemColors.GrayText;
        Controls.Add(lblIntro);

        Label lblTitle = new Label();
        lblTitle.Text = "Co-Host Accounts:";
        lblTitle.Location = new System.Drawing.Point(12, 140);
        lblTitle.AutoSize = true;
        lblTitle.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
        Controls.Add(lblTitle);

        // DataGridView for account list
        _grid = new DataGridView();
        _grid.Location = new System.Drawing.Point(12, 162);
        _grid.Size = new System.Drawing.Size(510, 200);
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
        colDisplay.FillWeight = 20;
        _grid.Columns.Add(colDisplay);

        DataGridViewTextBoxColumn colJoinUrl = new DataGridViewTextBoxColumn();
        colJoinUrl.Name = "JoinUrl";
        colJoinUrl.HeaderText = "Join URL";
        colJoinUrl.FillWeight = 26;
        _grid.Columns.Add(colJoinUrl);

        DataGridViewButtonColumn colCopy = new DataGridViewButtonColumn();
        colCopy.Name = "Copy";
        colCopy.HeaderText = "";
        colCopy.Text = "Copy";
        colCopy.UseColumnTextForButtonValue = true;
        colCopy.FillWeight = 9;
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

        // Add button
        _btnAdd = new Button();
        _btnAdd.Text = "+ Add Co-Host";
        _btnAdd.Location = new System.Drawing.Point(12, 354);
        _btnAdd.Size = new System.Drawing.Size(120, 28);
        _btnAdd.Click += OnAddClick;
        Controls.Add(_btnAdd);

        // Edit panel
        _editPanel = new Panel();
        _editPanel.Location = new System.Drawing.Point(12, 390);
        _editPanel.Size = new System.Drawing.Size(510, 150);
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

        // Save & Close button at bottom-right
        Button btnSaveClose = new Button();
        btnSaveClose.Text = "Save && Close";
        btnSaveClose.Location = new System.Drawing.Point(Width - 220, Height - 60);
        btnSaveClose.Size = new System.Drawing.Size(100, 30);
        btnSaveClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        btnSaveClose.Click += OnSaveCloseClick;
        Controls.Add(btnSaveClose);

        // Close button (no save)
        Button btnClose = new Button();
        btnClose.Text = "Close";
        btnClose.Location = new System.Drawing.Point(Width - 110, Height - 60);
        btnClose.Size = new System.Drawing.Size(80, 30);
        btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        btnClose.Click += OnCloseClick;
        Controls.Add(btnClose);
    }

    private void OnSaveCloseClick(object sender, EventArgs e)
    {
        // Persist display metadata (shown to co-hosts) and the co-host accounts.
        _stationName = _txtStationName.Text.Trim();
        _roomName = _txtRoomName.Text.Trim();
        _djName = _txtDjName.Text.Trim();
        _settingsManager.SaveMeta(_roomName, _stationName, _djName);
        _settingsManager.Save(_accounts);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnCloseClick(object sender, EventArgs e)
    {
        DialogResult = DialogResult.Cancel;
        Close();
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
        string origin = "https://signalling.compressed.stream";
        string slug = _settingsManager.LoadRoomId();
        if (string.IsNullOrEmpty(slug)) return "";

        string url = origin + "/room/" + slug;
        if (acct != null && !string.IsNullOrEmpty(acct.Hash))
        {
            url += "?invite=" + acct.Hash;
        }
        return url;
    }

    private void LoadGrid()
    {
        _grid.Rows.Clear();
        for (int i = 0; i < _accounts.Count; i++)
        {
            CoHostAccount acct = _accounts[i];
            string joinUrl = BuildJoinUrl(acct);
            _grid.Rows.Add(acct.Username, acct.DisplayName, joinUrl);
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
    private Button _micButton;
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

        // Latching Mic toggle (task 5.6) — replaces any push-to-talk control. Each
        // click flips the plugin's outbound mic state via NewPlugin (sets the gate the
        // AudioPumpLoop checks and broadcasts mic-state). Never momentary, never
        // always-open. Label/color reflect on-air vs muted (Req 7.3-7.8).
        _micButton = new Button();
        _micButton.FlatStyle = FlatStyle.Flat;
        _micButton.Size = new System.Drawing.Size(96, 22);
        _micButton.Dock = DockStyle.Right;
        _micButton.Font = new System.Drawing.Font("Segoe UI", 8f, System.Drawing.FontStyle.Bold);
        _micButton.FlatAppearance.BorderSize = 0;
        _micButton.Cursor = Cursors.Hand;
        _micButton.Click += OnMicToggleClick;
        titlePanel.Controls.Add(_micButton);
        _toolTip.SetToolTip(_micButton, "Toggle your microphone on/off air");
        UpdateMicButton(NewPlugin.IsMicOn);

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
        muteBtn.Tag = account.Username;
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
        kickBtn.Tag = account.Username;
        kickBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        kickBtn.Click += OnKickClick;
        rowPanel.Controls.Add(kickBtn);
        row.KickButton = kickBtn;
        _toolTip.SetToolTip(kickBtn, "Disconnect co-host");

        // Live toggle button (anchored to right)
        Button liveBtn = new Button();
        liveBtn.Text = "Go Live";
        liveBtn.FlatStyle = FlatStyle.Flat;
        liveBtn.Size = new System.Drawing.Size(60, 22);
        liveBtn.Font = new System.Drawing.Font("Segoe UI", 7.5f, System.Drawing.FontStyle.Bold);
        liveBtn.ForeColor = System.Drawing.Color.Gray;
        liveBtn.BackColor = System.Drawing.Color.FromArgb(50, 50, 60);
        liveBtn.FlatAppearance.BorderSize = 0;
        liveBtn.Cursor = Cursors.Hand;
        liveBtn.Tag = account.Username;
        liveBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        liveBtn.Click += OnLiveClick;
        rowPanel.Controls.Add(liveBtn);
        row.LiveButton = liveBtn;
        _toolTip.SetToolTip(liveBtn, "Toggle co-host audio on/off air");

        // Position buttons from right edge
        int rightEdge = rowPanel.Width;
        liveBtn.Location = new System.Drawing.Point(rightEdge - 64, 2);
        kickBtn.Location = new System.Drawing.Point(rightEdge - 108, 2);
        muteBtn.Location = new System.Drawing.Point(rightEdge - 162, 2);

        Controls.Add(rowPanel);
        return row;
    }

    private void OnLiveClick(object sender, EventArgs e)
    {
        Button btn = sender as Button;
        if (btn == null) return;
        string cohostId = btn.Tag as string;
        if (cohostId == null) return;

        bool currentLive = _audioMixer.GetLive(cohostId);
        _audioMixer.SetLive(cohostId, !currentLive);

        UpdateRowState(cohostId);
    }

    private void OnMuteClick(object sender, EventArgs e)
    {
        Button btn = sender as Button;
        if (btn == null) return;
        string cohostId = btn.Tag as string;
        if (cohostId == null) return;

        bool currentMuted = _audioMixer.GetMuted(cohostId);
        _audioMixer.SetMuted(cohostId, !currentMuted);

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

        UpdateRowState(cohostId);
    }

    private void OnConfigureClick(object sender, EventArgs e)
    {
        var form = new PartylineConfigForm(_settingsManager);
        form.ShowDialog();
    }

    /// <summary>
    /// Latching Mic toggle handler (task 5.6). Reads the current outbound mic state,
    /// flips it, and pushes the new state to the plugin via
    /// <see cref="NewPlugin.RequestMicToggle"/> (which sets the AudioPumpLoop gate and
    /// broadcasts mic-state). Then repaints the button to the on-air/muted style. There
    /// is no mouse-down/mouse-up (momentary) behavior and no always-open mode
    /// (Req 7.4, 7.5, 7.6).
    /// </summary>
    private void OnMicToggleClick(object sender, EventArgs e)
    {
        bool next = !NewPlugin.IsMicOn;
        NewPlugin.RequestMicToggle(next);
        UpdateMicButton(NewPlugin.IsMicOn);
    }

    /// <summary>
    /// Paints the Mic button to reflect the plugin's outbound mic state: a red
    /// "ON AIR" indicator while transmitting (Req 7.7) and a muted indicator while off
    /// (Req 7.8).
    /// </summary>
    private void UpdateMicButton(bool on)
    {
        if (_micButton == null) return;

        if (on)
        {
            // U+1F399 studio microphone + on-air, red.
            _micButton.Text = "\uD83C\uDF99 ON AIR";
            _micButton.ForeColor = System.Drawing.Color.White;
            _micButton.BackColor = System.Drawing.Color.FromArgb(239, 68, 68);
        }
        else
        {
            // U+1F507 muted speaker.
            _micButton.Text = "\uD83D\uDD07 Muted";
            _micButton.ForeColor = System.Drawing.Color.FromArgb(200, 200, 210);
            _micButton.BackColor = System.Drawing.Color.FromArgb(60, 60, 70);
        }
    }

    private void UpdateRowState(string cohostId)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            CoHostRow row = _rows[i];
            if (row.CohostId == cohostId)
            {
                bool isLive = _audioMixer.GetLive(cohostId);
                bool isMuted = _audioMixer.GetMuted(cohostId);

                // Update live button
                if (isLive)
                {
                    row.LiveButton.Text = "\u25CF LIVE";
                    row.LiveButton.ForeColor = System.Drawing.Color.FromArgb(34, 197, 94);
                    row.LiveButton.BackColor = System.Drawing.Color.FromArgb(20, 60, 30);
                }
                else
                {
                    row.LiveButton.Text = "Go Live";
                    row.LiveButton.ForeColor = System.Drawing.Color.Gray;
                    row.LiveButton.BackColor = System.Drawing.Color.FromArgb(50, 50, 60);
                }

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

                // Update row background
                if (isLive)
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

        // Keep the Mic button in sync with the plugin's outbound mic state so the
        // on-air/muted indicator stays correct if state changes outside a click.
        if (_micButton != null)
        {
            UpdateMicButton(NewPlugin.IsMicOn);
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
    private Button _liveButton;
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

    public Button LiveButton
    {
        get { return _liveButton; }
        set { _liveButton = value; }
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

        while (_running && !_cts.IsCancellationRequested)
        {
            try
            {
                Provision();
                Authenticate();
                PublishInvitesOnce();
                PublishMetaOnce();
                FetchRtcConfig();
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
        string body = "{\"djKey\":\"" + EscapeJson(_password) + "\"}";
        string resp = HttpPost(url, body);
        if (resp == null)
        {
            throw new Exception("Room provision failed (no response).");
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
        for (int i = 0; i < _accounts.Count; i++)
        {
            CoHostAccount a = _accounts[i];
            if (a == null || string.IsNullOrEmpty(a.Hash) || string.IsNullOrEmpty(a.Password)) continue;
            string name = !string.IsNullOrEmpty(a.DisplayName) ? a.DisplayName : a.Username;
            if (string.IsNullOrEmpty(name)) name = a.Hash;
            if (n > 0) sb.Append(",");
            sb.Append("{\"inviteId\":\"").Append(EscapeJson(a.Hash)).Append("\",");
            sb.Append("\"name\":\"").Append(EscapeJson(name)).Append("\",");
            sb.Append("\"password\":\"").Append(EscapeJson(a.Password)).Append("\"}");
            n++;
        }
        sb.Append("]}");

        if (n == 0) { _invitesPublished = true; return; }

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
            // Offer only to peers we have not seen before, so the periodic join
            // heartbeat refreshes presence without re-offering to connected peers.
            bool isNew = !_knownPeers.ContainsKey(pid);
            _knownPeers[pid] = 1;
            if (isNew && IsInitiator(pid)) InitiateOffer(pid);
        }
    }

    // Perfect-negotiation initiator rule: the lexicographically smaller peerId
    // initiates the offer, so each pair negotiates exactly one connection.
    private bool IsInitiator(string remotePeerId)
    {
        return string.CompareOrdinal(_peerId, remotePeerId) < 0;
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
            _peer.AddIceCandidate(from, candidate);
        }
        else if (type == "join")
        {
            // A new peer announced itself; offer if we are the initiator.
            if (from == null) return;
            _knownPeers[from] = 1;
            if (IsInitiator(from)) InitiateOffer(from);
        }
        else if (type == "leave")
        {
            if (from == null) return;
            byte ignored;
            _knownPeers.TryRemove(from, out ignored);
            _peer.ClosePeerConnection(from);
            Log("Peer left: " + from);
        }
        else if (type == "mic-state")
        {
            // On-air/muted indicators are surfaced by the UI layer; just log here.
            string micOn = ExtractJsonValue(payload, "micOn");
            Log("mic-state from " + (from ?? "?") + ": " + (micOn ?? "?"));
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
            if (path == null)
            {
                NewPlugin.LogStatic("[MRWebRTC] " + dllName + " not found for host arch " + arch + "; falling back.");
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

            NewPlugin.LogStatic("[MRWebRTC] Loaded native engine " + path + " (host " + arch + ").");
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
        candidates.Add(Path.Combine(baseDir, dllName));
        candidates.Add(Path.Combine(Path.Combine(baseDir, arch), dllName));
        // NuGet runtimes layout: runtimes/win-x64/native/mrwebrtc.dll
        candidates.Add(Path.Combine(baseDir, Path.Combine("runtimes", Path.Combine("win-" + arch, Path.Combine("native", dllName)))));

        for (int i = 0; i < candidates.Count; i++)
        {
            try
            {
                if (File.Exists(candidates[i])) return candidates[i];
            }
            catch { }
        }
        return null;
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
            if (iceServers != null)
            {
                for (int i = 0; i < iceServers.Length; i++)
                {
                    WebRtcIceServer s = iceServers[i];
                    if (s == null || s.Urls == null || s.Urls.Length == 0) continue;
                    var srv = new RTCIceServer();
                    // SIPSorcery's RTCIceServer.urls is a single (comma-separated) string.
                    srv.urls = string.Join(",", s.Urls);
                    if (s.Username != null) srv.username = s.Username;
                    if (s.Credential != null) srv.credential = s.Credential;
                    config.iceServers.Add(srv);
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
            NewPlugin.LogStatic("[SIPSorcery] Applied " + config.iceServers.Count + " ICE server entries.");
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
                if (h == null || cand == null) return;
                h(peerId, BuildCandidateJson(cand));
            };

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
