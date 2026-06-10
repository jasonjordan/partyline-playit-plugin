using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PlayIt.PluginEngine;

public class NewPlugin : Plugin<IPlayItLiveApp>
{
    private HttpListener _listener;
    private CancellationTokenSource _cts;
    private Thread _serverThread;
    private int _activePort = 25433;
    private const string URL_PATH = "/partyline/";
    private static string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Partyline", "partyline.log");

    // Co-host sessions
    private ConcurrentDictionary<string, CoHostConnection> _cohosts = new ConcurrentDictionary<string, CoHostConnection>();

    // Audio ring buffer for mixed co-host audio (44100Hz mono 16-bit)
    private short[] _ringBuffer = new short[44100 * 2]; // ~2 seconds
    private int _writePos = 0;
    private readonly object _audioLock = new object();

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

    public override void Run()
    {
        try
        {
            _cts = new CancellationTokenSource();
            Log("Plugin starting...");

            // Register audio stream into PlayIt Live's main mix
            Log("Registering audio stream...");
            App.AudioPipeline.RegisterSpecialAudioStream("partyline", new PartylineStream(this));
            Log("Audio stream registered.");

            // Register embedded UI control
            Log("Registering UI control...");
            App.RegisterUserControl(() => new PartylineStatusControl(this), UserControlLocation.BelowTrackList, 100);
            Log("UI control registered.");

            // Start HTTP/WebSocket server
            _serverThread = new Thread(StartServer) { IsBackground = true, Name = "Partyline" };
            _serverThread.Start();

            Log("Plugin started successfully.");
        }
        catch (Exception ex)
        {
            Log("FATAL in Run(): " + ex.ToString());
        }
    }

    private void StartServer()
    {
        try
        {
            string prefix = "http://+:" + _activePort + URL_PATH;
            Log("Attempting to listen on: " + prefix);

            // First, try to listen (will work if URL ACL already registered)
            if (!TryListen(prefix))
            {
                // URL ACL not registered yet — register it
                Log("Listen failed, attempting to register URL ACL...");
                bool aclResult = TryRegisterUrlAcl(_activePort);
                Log("URL ACL registration result: " + aclResult);

                // Retry after registration
                if (!TryListen(prefix))
                {
                    Log("Still cannot listen after URL ACL registration. Trying fallback port 8080...");
                    _activePort = 8080;
                    prefix = "http://+:" + _activePort + URL_PATH;
                    if (!TryListen(prefix))
                    {
                        Log("Fallback port 8080 also failed. Giving up.");
                        return;
                    }
                }
            }

            Log("HTTP listener started successfully on port " + _activePort);

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
                catch (HttpListenerException ex)
                {
                    Log("HttpListenerException: " + ex.Message);
                    break;
                }
                catch (Exception ex) { Log("Request loop error: " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            Log("FATAL in StartServer: " + ex.ToString());
        }
    }

    private bool TryListen(string prefix)
    {
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add(prefix);
            l.Start();
            _listener = l;
            Log("Successfully bound to " + prefix);
            return true;
        }
        catch (Exception ex)
        {
            Log("TryListen failed for " + prefix + ": " + ex.Message);
            return false;
        }
    }

    private bool TryRegisterUrlAcl(int port)
    {
        try
        {
            string args = "http add urlacl url=http://+:" + port + URL_PATH + " user=Everyone";
            Log("Running netsh: " + args);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true
            };
            var proc = System.Diagnostics.Process.Start(psi);
            proc.WaitForExit(10000);
            Log("netsh exit code: " + proc.ExitCode);
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log("TryRegisterUrlAcl error: " + ex.Message);
            return false;
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url.AbsolutePath.Replace("/partyline", "").TrimStart('/');

        try
        {
            if (ctx.Request.IsWebSocketRequest)
            {
                HandleWebSocket(ctx).Wait();
                return;
            }

            switch (path)
            {
                case "":
                case "join":
                    ServeHtml(ctx.Response, GetCoHostPage());
                    break;
                case "api/status":
                    ServeJson(ctx.Response, "{\"connected\":" + _cohosts.Count + "}");
                    break;
                default:
                    ctx.Response.StatusCode = 404;
                    ServeText(ctx.Response, "Not found");
                    break;
            }
        }
        catch (Exception ex)
        {
            try { ctx.Response.StatusCode = 500; ServeText(ctx.Response, ex.Message); } catch { }
        }
    }

    private async Task HandleWebSocket(HttpListenerContext ctx)
    {
        var wsCtx = await ctx.AcceptWebSocketAsync(null);
        var ws = wsCtx.WebSocket;
        var id = Guid.NewGuid().ToString("N").Substring(0, 8);
        var cohost = new CoHostConnection(id, ws);
        _cohosts[id] = cohost;
        App.Log("Partyline: Co-host " + id + " connected");

        var buffer = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                if (result.MessageType == WebSocketMessageType.Binary && !cohost.Muted)
                {
                    // Buffer contains 16-bit PCM mono samples from the browser
                    int sampleCount = result.Count / 2;
                    lock (_audioLock)
                    {
                        for (int i = 0; i < sampleCount; i++)
                        {
                            short sample = BitConverter.ToInt16(buffer, i * 2);
                            // Apply volume
                            sample = (short)(sample * cohost.Volume);
                            // Mix (add) into ring buffer
                            int mixed = _ringBuffer[_writePos] + sample;
                            _ringBuffer[_writePos] = (short)Math.Max(-32768, Math.Min(32767, mixed));
                            _writePos = (_writePos + 1) % _ringBuffer.Length;
                        }
                    }
                    cohost.LastLevel = sampleCount > 0 ? Math.Abs(BitConverter.ToInt16(buffer, 0)) / 32768f : 0f;
                }
            }
        }
        catch { }
        finally
        {
            CoHostConnection removed;
            _cohosts.TryRemove(id, out removed);
            App.Log("Partyline: Co-host " + id + " disconnected");
            try { ws.Dispose(); } catch { }
        }
    }

    // Called by PlayIt Live's audio pipeline when it needs samples
    internal int FillAudioBuffer(int length, IntPtr buffer)
    {
        int sampleCount = length / 2;
        var output = new short[sampleCount];

        lock (_audioLock)
        {
            int readPos = (_writePos - sampleCount + _ringBuffer.Length) % _ringBuffer.Length;
            for (int i = 0; i < sampleCount; i++)
            {
                output[i] = _ringBuffer[readPos];
                _ringBuffer[readPos] = 0; // Clear after reading
                readPos = (readPos + 1) % _ringBuffer.Length;
            }
        }

        Marshal.Copy(output, 0, buffer, sampleCount);
        return length;
    }

    // Public accessors for UI
    public int GetCoHostCount() { return _cohosts.Count; }
    public string GetLink() { return "http://" + Dns.GetHostName() + ":" + _activePort + "/partyline/join"; }

    public void MuteAll()
    {
        foreach (var c in _cohosts.Values) c.Muted = true;
    }

    public void UnmuteAll()
    {
        foreach (var c in _cohosts.Values) c.Muted = false;
    }

    public void KickAll()
    {
        foreach (var c in _cohosts.Values)
        {
            try { c.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Kicked", CancellationToken.None); } catch { }
        }
    }

    public IEnumerable<CoHostConnection> GetConnections() { return _cohosts.Values; }

    public void Kick(string id)
    {
        CoHostConnection c;
        if (_cohosts.TryGetValue(id, out c))
        {
            try { c.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Kicked", CancellationToken.None); } catch { }
        }
    }

    private void ServeHtml(HttpListenerResponse resp, string html)
    {
        resp.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(html);
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes, 0, bytes.Length);
        resp.OutputStream.Close();
    }

    private void ServeJson(HttpListenerResponse resp, string json)
    {
        resp.ContentType = "application/json";
        resp.Headers.Add("Access-Control-Allow-Origin", "*");
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes, 0, bytes.Length);
        resp.OutputStream.Close();
    }

    private void ServeText(HttpListenerResponse resp, string text)
    {
        resp.ContentType = "text/plain";
        var bytes = Encoding.UTF8.GetBytes(text);
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes, 0, bytes.Length);
        resp.OutputStream.Close();
    }

    public override void Cleanup()
    {
        if (_cts != null) _cts.Cancel();
        if (_listener != null) _listener.Stop();
        KickAll();
    }

    private string GetCoHostPage()
    {
        return @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>Partyline Co-Host</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:-apple-system,sans-serif;background:#1a1a2e;color:#fff;min-height:100vh;display:flex;align-items:center;justify-content:center}
.c{max-width:500px;width:100%;padding:2rem}
h1{font-size:1.5rem;margin-bottom:1.5rem;text-align:center}
.status{padding:1rem;border-radius:8px;margin-bottom:1.5rem;text-align:center;font-weight:bold}
.s-off{background:rgba(239,68,68,.2);border:1px solid #ef4444}
.s-on{background:rgba(34,197,94,.2);border:1px solid #22c55e}
.s-wait{background:rgba(234,179,8,.2);border:1px solid #eab308}
.btn{width:100%;padding:1rem;font-size:1.1rem;border:none;border-radius:8px;cursor:pointer;font-weight:bold;margin-bottom:1rem}
.btn-blue{background:#3b82f6;color:#fff}
.ptt{padding:2rem;font-size:1.5rem;border-radius:12px;user-select:none;transition:all .1s}
.ptt-off{background:#64748b;color:#fff}
.ptt-on{background:#ef4444;color:#fff;transform:scale(.98)}
.ptt:disabled{opacity:.5;cursor:not-allowed}
.vu{height:8px;background:rgba(255,255,255,.1);border-radius:4px;overflow:hidden;margin-top:1rem}
.vu-fill{height:100%;background:#22c55e;width:0%;transition:width .05s}
.info{margin-top:1rem;font-size:.8rem;color:#94a3b8;text-align:center}
</style>
</head>
<body>
<div class='c'>
<h1>🎙️ Partyline Co-Host</h1>
<div id='st' class='status s-off'>Disconnected</div>
<button id='conn' class='btn btn-blue' onclick='connect()'>Connect</button>
<button id='ptt' class='btn ptt ptt-off' disabled
 onmousedown='pttOn()' onmouseup='pttOff()' onmouseleave='pttOff()'
 ontouchstart='pttOn();event.preventDefault()' ontouchend='pttOff();event.preventDefault()'>
PUSH TO TALK</button>
<div class='vu'><div id='vu' class='vu-fill'></div></div>
<div class='info'>Hold the button to talk. Release to mute.</div>
</div>
<script>
let ws, ctx, src, proc, sending=false;
const SAMPLE_RATE=44100, BUFFER_SIZE=4096;

async function connect(){
 document.getElementById('st').className='status s-wait';
 document.getElementById('st').innerText='Connecting...';
 document.getElementById('conn').disabled=true;
 try{
  const stream=await navigator.mediaDevices.getUserMedia({audio:{sampleRate:SAMPLE_RATE,channelCount:1,echoCancellation:true}});
  ctx=new(window.AudioContext||window.webkitAudioContext)({sampleRate:SAMPLE_RATE});
  src=ctx.createMediaStreamSource(stream);
  proc=ctx.createScriptProcessor(BUFFER_SIZE,1,1);
  src.connect(proc);
  proc.connect(ctx.destination);

  const proto=location.protocol==='https:'?'wss:':'ws:';
  ws=new WebSocket(proto+'//'+location.host+'/partyline/ws');
  ws.binaryType='arraybuffer';

  ws.onopen=()=>{
   document.getElementById('st').className='status s-on';
   document.getElementById('st').innerText='Connected ✓';
   document.getElementById('ptt').disabled=false;
  };
  ws.onclose=()=>{
   document.getElementById('st').className='status s-off';
   document.getElementById('st').innerText='Disconnected';
   document.getElementById('ptt').disabled=true;
   document.getElementById('conn').disabled=false;
   sending=false;
  };
  ws.onerror=()=>{ ws.close(); };

  proc.onaudioprocess=(e)=>{
   if(!sending||!ws||ws.readyState!==1)return;
   const input=e.inputBuffer.getChannelData(0);
   const pcm16=new Int16Array(input.length);
   for(let i=0;i<input.length;i++){
    pcm16[i]=Math.max(-32768,Math.min(32767,Math.round(input[i]*32767)));
   }
   ws.send(pcm16.buffer);
   // VU
   let max=0;
   for(let i=0;i<input.length;i++){let v=Math.abs(input[i]);if(v>max)max=v;}
   document.getElementById('vu').style.width=(max*100)+'%';
  };
 }catch(err){
  document.getElementById('st').className='status s-off';
  document.getElementById('st').innerText='Error: '+err.message;
  document.getElementById('conn').disabled=false;
 }
}

function pttOn(){sending=true;document.getElementById('ptt').className='btn ptt ptt-on';document.getElementById('ptt').innerText='🎙️ LIVE';}
function pttOff(){sending=false;document.getElementById('ptt').className='btn ptt ptt-off';document.getElementById('ptt').innerText='PUSH TO TALK';document.getElementById('vu').style.width='0%';}
</script>
</body>
</html>";
    }
}

// --- Supporting classes ---

public class CoHostConnection
{
    public string Id { get; set; }
    public WebSocket Socket { get; set; }
    public float Volume { get; set; }
    public bool Muted { get; set; }
    public float LastLevel { get; set; }

    public CoHostConnection(string id, WebSocket socket)
    {
        Id = id;
        Socket = socket;
        Volume = 1.0f;
        Muted = false;
        LastLevel = 0f;
    }
}

// Implements PlayIt Live's ISpecialAudioStream
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

// Embedded status control for PlayIt Live UI
public class PartylineStatusControl : UserControl
{
    private NewPlugin _plugin;
    private Label _label;
    private Button _mixerBtn;
    private System.Windows.Forms.Timer _timer;

    public PartylineStatusControl(NewPlugin plugin)
    {
        _plugin = plugin;
        Height = 32;
        Dock = DockStyle.Fill;
        BackColor = System.Drawing.Color.FromArgb(30, 30, 50);

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };

        _label = new Label
        {
            Text = "🎙️ Partyline: 0 connected",
            ForeColor = System.Drawing.Color.White,
            Font = new System.Drawing.Font("Segoe UI", 9f),
            AutoSize = true,
            Margin = new Padding(4, 7, 10, 0)
        };

        _mixerBtn = new Button
        {
            Text = "Copy Link",
            FlatStyle = FlatStyle.Flat,
            BackColor = System.Drawing.Color.FromArgb(59, 130, 246),
            ForeColor = System.Drawing.Color.White,
            Font = new System.Drawing.Font("Segoe UI", 8f),
            Size = new System.Drawing.Size(80, 24),
            Margin = new Padding(0, 3, 6, 0),
            Cursor = Cursors.Hand
        };
        _mixerBtn.FlatAppearance.BorderSize = 0;
        _mixerBtn.Click += (s, e) =>
        {
            Clipboard.SetText(_plugin.GetLink());
            _mixerBtn.Text = "Copied ✓";
            var t = new System.Windows.Forms.Timer { Interval = 2000 };
            t.Tick += (s2, e2) => { _mixerBtn.Text = "Copy Link"; t.Stop(); t.Dispose(); };
            t.Start();
        };

        layout.Controls.Add(_label);
        layout.Controls.Add(_mixerBtn);
        Controls.Add(layout);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (s, e) => { _label.Text = "🎙️ Partyline: " + _plugin.GetCoHostCount() + " connected"; };
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (_timer != null) { _timer.Stop(); _timer.Dispose(); }
        base.Dispose(disposing);
    }
}
