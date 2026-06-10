using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using PlayIt.PluginEngine;

public class NewPlugin : Plugin<IPlayItLiveApp>
{
    private CancellationTokenSource _cts;
    private int _activePort = 25434;
    private static string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Partyline", "partyline.log");

    // Co-host sessions
    private ConcurrentDictionary<string, CoHostConnection> _cohosts = new ConcurrentDictionary<string, CoHostConnection>();

    // Audio ring buffer for mixed co-host audio (44100Hz mono 16-bit)
    private short[] _ringBuffer = new short[44100 * 2];
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

    public static void LogStatic(string message)
    {
        Log(message);
    }

    public override void Run()
    {
        try
        {
            _cts = new CancellationTokenSource();
            Log("Plugin starting...");

            // Register audio stream into PlayIt Live's main mix
            // (delayed until server is ready to avoid file locking conflicts)
            //Log("Registering audio stream...");
            //App.AudioPipeline.RegisterSpecialAudioStream("partyline", new PartylineStream(this));
            //Log("Audio stream registered.");

            // Register embedded UI control
            // (disabled temporarily to isolate server startup issue)
            //Log("Registering UI control...");
            //App.RegisterUserControl(() => new PartylineStatusControl(this), UserControlLocation.BelowTrackList, 100);
            //Log("UI control registered.");

            // Hook into PlayIt Live's ServiceStack HTTP server on port 25434
            // Wait for the server to start (user clicks "Start Server" manually)
            var hookThread = new Thread(() => WaitAndHook()) { IsBackground = true, Name = "PartylineHook" };
            hookThread.Start();

            Log("Plugin started successfully.");
        }
        catch (Exception ex)
        {
            Log("FATAL in Run(): " + ex.ToString());
        }
    }

    private void WaitAndHook()
    {
        Log("Waiting for ServiceStack server...");
        // Give PlayIt Live a few seconds to start
        Thread.Sleep(5000);

        try
        {
            Type hostType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                hostType = asm.GetType("ServiceStack.ServiceStackHost");
                if (hostType != null) break;
            }

            if (hostType == null)
            {
                Log("ERROR: ServiceStackHost type not found");
                return;
            }

            var instanceProp = hostType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var instance = instanceProp.GetValue(null);

            if (instance == null)
            {
                Log("Instance is null after 5s, waiting more...");
                for (int i = 0; i < 60; i++)
                {
                    Thread.Sleep(2000);
                    if (_cts.IsCancellationRequested) return;
                    instance = instanceProp.GetValue(null);
                    if (instance != null) break;
                }
            }

            if (instance == null)
            {
                Log("ERROR: Instance never became non-null");
                return;
            }

            Log("Found instance: " + instance.GetType().FullName + ". Hooking now.");
            HookIntoServiceStack();
        }
        catch (Exception ex)
        {
            Log("WaitAndHook error: " + ex.ToString());
        }
    }

    private void HookIntoServiceStack()
    {
        Log("Hooking into ServiceStack on port 25434...");

        // Find ServiceStackHost.Instance
        Type hostType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            hostType = asm.GetType("ServiceStack.ServiceStackHost");
            if (hostType != null) break;
        }

        if (hostType == null)
        {
            Log("ERROR: ServiceStack.ServiceStackHost type not found in loaded assemblies");
            return;
        }

        Log("Found ServiceStackHost type: " + hostType.FullName);

        var instanceProp = hostType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        if (instanceProp == null)
        {
            Log("ERROR: Instance property not found");
            return;
        }

        var instance = instanceProp.GetValue(null);
        if (instance == null)
        {
            Log("ERROR: ServiceStackHost.Instance is null");
            return;
        }

        Log("Got AppHost instance: " + instance.GetType().FullName);

        // Get RawHttpHandlers list
        var rawProp = instance.GetType().GetProperty("RawHttpHandlers", BindingFlags.Public | BindingFlags.Instance);
        if (rawProp == null)
        {
            // Try on the base type
            rawProp = hostType.GetProperty("RawHttpHandlers", BindingFlags.Public | BindingFlags.Instance);
        }

        if (rawProp == null)
        {
            Log("ERROR: RawHttpHandlers property not found");
            return;
        }

        var handlersList = rawProp.GetValue(instance);
        Log("Got RawHttpHandlers list: " + handlersList.GetType().FullName);

        // The list is List<Func<IHttpRequest, IHttpHandler>>
        // Find the types
        Type iHttpRequestType = null;
        Type iHttpHandlerType = null;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (iHttpRequestType == null)
                iHttpRequestType = asm.GetType("ServiceStack.Web.IHttpRequest");
            if (iHttpHandlerType == null)
                iHttpHandlerType = asm.GetType("ServiceStack.Web.IHttpHandler");
        }

        if (iHttpRequestType == null)
        {
            Log("ERROR: Could not find ServiceStack.Web.IHttpRequest");
            return;
        }
        if (iHttpHandlerType == null)
        {
            Log("ERROR: Could not find ServiceStack.Web.IHttpHandler - trying System.Web.IHttpHandler");
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                iHttpHandlerType = asm.GetType("System.Web.IHttpHandler");
                if (iHttpHandlerType != null) break;
            }
        }

        // The list is List<Func<ServiceStack.Web.IHttpRequest, System.Web.IHttpHandler>>
        // We need to add a Func that accepts IHttpRequest and returns IHttpHandler (or null).
        // We'll use CustomActionHandler (ServiceStack's built-in handler class)
        // which implements IServiceStackHandler properly.

        var pathInfoProp = iHttpRequestType.GetProperty("PathInfo");
        if (pathInfoProp == null)
        {
            foreach (var iface in iHttpRequestType.GetInterfaces())
            {
                pathInfoProp = iface.GetProperty("PathInfo");
                if (pathInfoProp != null) break;
            }
        }
        Log("PathInfo: " + (pathInfoProp != null ? "found" : "NOT FOUND"));

        // Find CustomActionHandler type
        Type customHandlerType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            customHandlerType = asm.GetType("ServiceStack.Host.Handlers.CustomActionHandler");
            if (customHandlerType != null) break;
        }
        Log("CustomActionHandler: " + (customHandlerType != null ? "found" : "NOT FOUND"));

        // Find IRequest and IResponse types for the Action<IRequest, IResponse>
        Type iRequestType = null;
        Type iResponseType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (iRequestType == null) iRequestType = asm.GetType("ServiceStack.Web.IRequest");
            if (iResponseType == null) iResponseType = asm.GetType("ServiceStack.Web.IResponse");
        }
        Log("IRequest: " + (iRequestType != null ? "found" : "NOT FOUND"));
        Log("IResponse: " + (iResponseType != null ? "found" : "NOT FOUND"));

        // Store references for runtime use
        _pathInfoProp = pathInfoProp;
        _customHandlerType = customHandlerType;
        _iRequestType = iRequestType;
        _iResponseType = iResponseType;

        // Build expression: (req) => this.CreateHandler(req.PathInfo)
        // CreateHandler returns System.Web.IHttpHandler (CustomActionHandler implements it)
        var reqParam = Expression.Parameter(iHttpRequestType, "req");
        var castExpr = Expression.Convert(reqParam, pathInfoProp.DeclaringType);
        var pathExpr = Expression.Property(castExpr, pathInfoProp);

        var createHandlerMethod = typeof(NewPlugin).GetMethod("CreateHandler", BindingFlags.Public | BindingFlags.Instance);
        var pluginConst = Expression.Constant(this);
        var callExpr = Expression.Call(pluginConst, createHandlerMethod, pathExpr);

        var funcTypeFromList = handlersList.GetType().GetGenericArguments()[0];
        var lambda = Expression.Lambda(funcTypeFromList, callExpr, reqParam);
        var del = lambda.Compile();
        Log("Lambda compiled");

        // Insert at position 0
        var insertMethod = handlersList.GetType().GetMethod("Insert");
        insertMethod.Invoke(handlersList, new object[] { 0, del });
        Log("Handler inserted");

        // CRITICAL: Rebuild the internal cached array
        // ServiceStack reads from RawHttpHandlersArray (Func[]), not the List
        Type searchType = instance.GetType();
        bool rebuilt = false;
        while (searchType != null && !rebuilt)
        {
            foreach (var f in searchType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (f.Name.Contains("RawHttpHandler") && f.FieldType.IsArray)
                {
                    var toArray = handlersList.GetType().GetMethod("ToArray");
                    var newArray = toArray.Invoke(handlersList, null);
                    f.SetValue(instance, newArray);
                    Log("Rebuilt cached array: " + f.Name + " (length=" + ((Array)newArray).Length + ")");
                    rebuilt = true;
                    break;
                }
            }
            searchType = searchType.BaseType;
        }
        if (!rebuilt)
        {
            Log("WARNING: Could not find cached array field. Listing candidate fields:");
            searchType = instance.GetType();
            while (searchType != null)
            {
                foreach (var f in searchType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.FieldType.IsArray || f.Name.Contains("Handler") || f.Name.Contains("Raw"))
                        Log("  " + searchType.Name + "." + f.Name + " : " + f.FieldType.Name);
                }
                searchType = searchType.BaseType;
            }
        }

        Log("Partyline available at https://localhost:" + _activePort + "/partyline/join");
    }

    // These are set during hook setup
    private PropertyInfo _pathInfoProp;
    private Type _customHandlerType;
    private Type _iRequestType;
    private Type _iResponseType;

    // Called by the expression tree lambda for each request
    public System.Web.IHttpHandler CreateHandler(string pathInfo)
    {
        if (pathInfo == null || !pathInfo.StartsWith("/partyline")) return null;

        try
        {
            // Create Action<IRequest, IResponse> delegate
            var actionType = typeof(Action<,>).MakeGenericType(_iRequestType, _iResponseType);
            var handleMethod = typeof(NewPlugin).GetMethod("HandlePartylineRequest", BindingFlags.Public | BindingFlags.Instance);
            var action = Delegate.CreateDelegate(actionType, this, handleMethod);

            // new CustomActionHandler(action)
            var ctor = _customHandlerType.GetConstructor(new Type[] { actionType });
            var handler = ctor.Invoke(new object[] { action });
            return (System.Web.IHttpHandler)handler;
        }
        catch (Exception ex)
        {
            Log("CreateHandler error: " + ex.Message);
            return null;
        }
    }

    // Called by CustomActionHandler when it processes the request
    public void HandlePartylineRequest(object request, object response)
    {
        try
        {
            // Get PathInfo from request
            string pathInfo = _pathInfoProp.GetValue(request) as string;
            string subPath = (pathInfo ?? "").Replace("/partyline", "").TrimStart('/');

            // Get response OutputStream and ContentType
            var resType = response.GetType();
            var contentTypeProp = resType.GetProperty("ContentType");
            var outputStreamProp = resType.GetProperty("OutputStream");

            string body;
            string contentType;

            if (subPath == "" || subPath == "join" || subPath == "join/")
            {
                contentType = "text/html; charset=utf-8";
                body = GetCoHostPageHtml();
            }
            else if (subPath == "api/status")
            {
                contentType = "application/json";
                body = "{\"connected\":" + _cohosts.Count + "}";
            }
            else
            {
                contentType = "text/plain";
                body = "Not found";
            }

            contentTypeProp.SetValue(response, contentType);
            var stream = outputStreamProp.GetValue(response) as Stream;
            if (stream != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex)
        {
            Log("HandlePartylineRequest error: " + ex.Message);
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
                _ringBuffer[readPos] = 0;
                readPos = (readPos + 1) % _ringBuffer.Length;
            }
        }

        Marshal.Copy(output, 0, buffer, sampleCount);
        return length;
    }

    internal void WritePcmSamples(byte[] data, int count)
    {
        int sampleCount = count / 2;
        lock (_audioLock)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(data, i * 2);
                int mixed = _ringBuffer[_writePos] + sample;
                _ringBuffer[_writePos] = (short)Math.Max(-32768, Math.Min(32767, mixed));
                _writePos = (_writePos + 1) % _ringBuffer.Length;
            }
        }
    }

    public int GetCoHostCount() { return _cohosts.Count; }
    public string GetLink() { return "https://" + Dns.GetHostName() + ":25434/partyline/join"; }

    public void MuteAll() { foreach (var c in _cohosts.Values) c.Muted = true; }
    public void UnmuteAll() { foreach (var c in _cohosts.Values) c.Muted = false; }
    public void KickAll() { _cohosts.Clear(); }

    internal string GetCoHostPageHtml() { return GetCoHostPage(); }

    public override void Cleanup()
    {
        if (_cts != null) _cts.Cancel();
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
let ws,ctx,src,proc,sending=false;
const SR=44100,BUF=4096;
async function connect(){
 document.getElementById('st').className='status s-wait';
 document.getElementById('st').innerText='Connecting...';
 document.getElementById('conn').disabled=true;
 try{
  const stream=await navigator.mediaDevices.getUserMedia({audio:{sampleRate:SR,channelCount:1,echoCancellation:true}});
  ctx=new(window.AudioContext||window.webkitAudioContext)({sampleRate:SR});
  src=ctx.createMediaStreamSource(stream);
  proc=ctx.createScriptProcessor(BUF,1,1);
  src.connect(proc);proc.connect(ctx.destination);
  ws=new WebSocket((location.protocol==='https:'?'wss:':'ws:')+'//'+location.host+'/partyline/ws');
  ws.binaryType='arraybuffer';
  ws.onopen=()=>{document.getElementById('st').className='status s-on';document.getElementById('st').innerText='Connected ✓';document.getElementById('ptt').disabled=false;};
  ws.onclose=()=>{document.getElementById('st').className='status s-off';document.getElementById('st').innerText='Disconnected';document.getElementById('ptt').disabled=true;document.getElementById('conn').disabled=false;sending=false;};
  ws.onerror=()=>{ws.close();};
  proc.onaudioprocess=(e)=>{
   if(!sending||!ws||ws.readyState!==1)return;
   const d=e.inputBuffer.getChannelData(0);const p=new Int16Array(d.length);
   for(let i=0;i<d.length;i++)p[i]=Math.max(-32768,Math.min(32767,Math.round(d[i]*32767)));
   ws.send(p.buffer);
   let m=0;for(let i=0;i<d.length;i++){let v=Math.abs(d[i]);if(v>m)m=v;}
   document.getElementById('vu').style.width=(m*100)+'%';
  };
 }catch(err){document.getElementById('st').className='status s-off';document.getElementById('st').innerText='Error: '+err.message;document.getElementById('conn').disabled=false;}
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
    public float Volume { get; set; }
    public bool Muted { get; set; }
    public float LastLevel { get; set; }

    public CoHostConnection(string id)
    {
        Id = id;
        Volume = 1.0f;
        Muted = false;
        LastLevel = 0f;
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
            _mixerBtn.Text = "Copied!";
            var t = new System.Windows.Forms.Timer();
            t.Interval = 2000;
            t.Tick += (s2, e2) => { _mixerBtn.Text = "Copy Link"; t.Stop(); t.Dispose(); };
            t.Start();
        };

        layout.Controls.Add(_label);
        layout.Controls.Add(_mixerBtn);
        Controls.Add(layout);

        _timer = new System.Windows.Forms.Timer();
        _timer.Interval = 1000;
        _timer.Tick += (s, e) => { _label.Text = "🎙️ Partyline: " + _plugin.GetCoHostCount() + " connected"; };
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (_timer != null) { _timer.Stop(); _timer.Dispose(); }
        base.Dispose(disposing);
    }
}
