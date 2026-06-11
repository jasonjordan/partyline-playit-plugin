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
    private int _activePort;
    private static string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Partyline", "partyline.log");
    private SettingsManager _settingsManager = new SettingsManager();
    private string _stationName = "Partyline Co-Host";

    // Co-host sessions
    private ConcurrentDictionary<string, CoHostState> _cohosts = new ConcurrentDictionary<string, CoHostState>();

    // Authentication and audio subsystems (initialized in Run)
    private AuthenticationManager _authManager;
    private AudioMixer _audioMixer;

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

            // Load settings and initialize subsystems
            List<CoHostAccount> accounts = _settingsManager.Load();
            _stationName = _settingsManager.LoadStationName();

            _authManager = new AuthenticationManager();
            _authManager.SetAccounts(accounts);

            _audioMixer = new AudioMixer();

            Log("Initialized AuthenticationManager and AudioMixer.");

            // Register audio stream into PlayIt Live's main mix
            Log("Registering audio stream...");
            App.AudioPipeline.RegisterSpecialAudioStream("partyline", new PartylineStream(this));
            Log("Audio stream registered.");

            // Connect the partyline audio source to the main mix output
            try
            {
                Log("Connecting partyline source to main mix...");
                var source = App.AudioPipeline.CreateSource("partyline");
                Log("Created audio source: " + (source != null ? "OK" : "NULL"));

                if (source != null)
                {
                    var mainMix = App.AudioPipeline.GetMainMix();
                    Log("Got main mix: " + (mainMix != null ? "OK" : "NULL"));

                    if (mainMix != null)
                    {
                        App.AudioPipeline.Connect(source, mainMix);
                        Log("Connected source to main mix.");

                        source.Start();
                        Log("Audio source started.");
                    }
                    else
                    {
                        Log("WARNING: MainMix is null, audio will not be routed.");
                    }
                }
                else
                {
                    Log("WARNING: Could not create audio source, audio will not be routed.");
                }
            }
            catch (Exception audioEx)
            {
                Log("ERROR connecting audio pipeline: " + audioEx.ToString());
            }

            // Register embedded UI control
            Log("Registering UI control...");
            App.RegisterUserControl(() => new PartylineControlPanel(_audioMixer, _authManager, accounts, _settingsManager), UserControlLocation.AboveTrackGroupSelector, 100);
            Log("UI control registered.");

            // Hook into PlayIt Live's ServiceStack HTTP server on port 25434
            // Wait for the server to start (user clicks "Start Server" manually)
            var hookThread = new Thread(() => WaitAndHook()) { IsBackground = true, Name = "PartylineHook" };
            hookThread.Start();

            Log("Plugin started successfully.");

            // Keep plugin running - this blocks until plugin is stopped
            App.WaitForPluginStop();
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

            Log("Found instance: " + instance.GetType().FullName + ". Detecting port...");

            // Detect port from HttpListener prefixes via reflection
            int detectedPort = 25434; // fallback
            try
            {
                // Walk the type hierarchy looking for a Listener property or field
                object listener = null;
                Type searchType = instance.GetType();
                while (searchType != null && listener == null)
                {
                    PropertyInfo listenerProp = searchType.GetProperty("Listener", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (listenerProp != null)
                    {
                        listener = listenerProp.GetValue(instance);
                    }
                    else
                    {
                        FieldInfo listenerField = searchType.GetField("Listener", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (listenerField != null)
                        {
                            listener = listenerField.GetValue(instance);
                        }
                    }
                    searchType = searchType.BaseType;
                }

                if (listener == null)
                {
                    // Try common alternative names
                    searchType = instance.GetType();
                    while (searchType != null && listener == null)
                    {
                        foreach (var prop in searchType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (prop.PropertyType == typeof(HttpListener) || prop.PropertyType.Name == "HttpListener")
                            {
                                listener = prop.GetValue(instance);
                                if (listener != null) break;
                            }
                        }
                        if (listener == null)
                        {
                            foreach (var field in searchType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            {
                                if (field.FieldType == typeof(HttpListener) || field.FieldType.Name == "HttpListener")
                                {
                                    listener = field.GetValue(instance);
                                    if (listener != null) break;
                                }
                            }
                        }
                        searchType = searchType.BaseType;
                    }
                }

                if (listener != null)
                {
                    Log("Found listener object: " + listener.GetType().FullName);

                    // Get Prefixes from the HttpListener
                    PropertyInfo prefixesProp = listener.GetType().GetProperty("Prefixes", BindingFlags.Public | BindingFlags.Instance);
                    if (prefixesProp != null)
                    {
                        var prefixes = prefixesProp.GetValue(listener);
                        if (prefixes != null)
                        {
                            // Enumerate prefixes to find port - pattern: https://+:25434/
                            var enumerator = prefixes.GetType().GetMethod("GetEnumerator");
                            if (enumerator != null)
                            {
                                var iter = enumerator.Invoke(prefixes, null) as System.Collections.IEnumerator;
                                if (iter != null && iter.MoveNext())
                                {
                                    string prefix = iter.Current as string;
                                    if (prefix != null)
                                    {
                                        Log("Found prefix: " + prefix);
                                        // Parse port from URL like https://+:25434/ or http://localhost:12345/
                                        int colonIdx = prefix.LastIndexOf(':');
                                        if (colonIdx > 0)
                                        {
                                            int slashIdx = prefix.IndexOf('/', colonIdx);
                                            if (slashIdx < 0) slashIdx = prefix.Length;
                                            string portStr = prefix.Substring(colonIdx + 1, slashIdx - colonIdx - 1);
                                            int parsedPort;
                                            if (int.TryParse(portStr, out parsedPort) && parsedPort > 0 && parsedPort <= 65535)
                                            {
                                                detectedPort = parsedPort;
                                                Log("Detected port from prefix: " + detectedPort);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    Log("Could not find Listener on instance, using fallback port 25434");
                }
            }
            catch (Exception portEx)
            {
                Log("Port detection error (using fallback 25434): " + portEx.Message);
            }

            _activePort = detectedPort;
            Log("Active port set to: " + _activePort);

            HookIntoServiceStack();
        }
        catch (Exception ex)
        {
            Log("WaitAndHook error: " + ex.ToString());
        }
    }

    private void HookIntoServiceStack()
    {
        Log("Hooking into ServiceStack on port " + _activePort + "...");

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

            // --- POST /partyline/login ---
            if (subPath == "login")
            {
                string reqBody = ReadRequestBody(request);
                string username = ExtractJsonStringValueFromBody(reqBody, "username");
                string password = ExtractJsonStringValueFromBody(reqBody, "password");

                AuthResult authResult = _authManager.Authenticate(username, password);

                string jsonResponse;
                if (authResult.Success)
                {
                    jsonResponse = "{\"success\":true,\"token\":\"" + EscapeJsonStringValue(authResult.Token) + "\",\"displayName\":\"" + EscapeJsonStringValue(authResult.DisplayName) + "\"}";
                }
                else
                {
                    jsonResponse = "{\"success\":false,\"error\":\"" + EscapeJsonStringValue(authResult.Error) + "\"}";
                }

                contentTypeProp.SetValue(response, "application/json");
                var stream = outputStreamProp.GetValue(response) as Stream;
                if (stream != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(jsonResponse);
                    stream.Write(bytes, 0, bytes.Length);
                }
                return;
            }

            // --- POST /partyline/audio ---
            if (subPath == "audio")
            {
                // Extract X-Session-Token header via reflection
                string token = GetRequestHeader(request, "X-Session-Token");

                string cohostId;
                if (token == null || !_authManager.ValidateToken(token, out cohostId))
                {
                    // Return 401 Unauthorized
                    contentTypeProp.SetValue(response, "text/plain");
                    var statusCodeProp = resType.GetProperty("StatusCode");
                    if (statusCodeProp != null)
                    {
                        statusCodeProp.SetValue(response, 401);
                    }
                    var stream = outputStreamProp.GetValue(response) as Stream;
                    if (stream != null)
                    {
                        var bytes = Encoding.UTF8.GetBytes("Unauthorized");
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    return;
                }

                // Extract X-Audio-Timestamp header for latency calculation
                string timestampHeader = GetRequestHeader(request, "X-Audio-Timestamp");
                long clientTimestampMs = -1;
                if (timestampHeader != null)
                {
                    long.TryParse(timestampHeader, out clientTimestampMs);
                }

                // Read the request body (PCM audio data)
                var reqType = request.GetType();
                var inputStreamProp = reqType.GetProperty("InputStream");
                if (inputStreamProp != null)
                {
                    var inputStream = inputStreamProp.GetValue(request) as Stream;
                    if (inputStream != null)
                    {
                        using (var ms = new MemoryStream())
                        {
                            inputStream.CopyTo(ms);
                            var data = ms.ToArray();
                            if (data.Length > 0)
                            {
                                _audioMixer.IngestAudio(cohostId, data, data.Length, clientTimestampMs);
                            }
                        }
                    }
                }

                contentTypeProp.SetValue(response, "text/plain");
                var outStream = outputStreamProp.GetValue(response) as Stream;
                if (outStream != null)
                {
                    var bytes = Encoding.UTF8.GetBytes("OK");
                    outStream.Write(bytes, 0, bytes.Length);
                }
                return;
            }

            // --- GET /partyline/status ---
            if (subPath == "status")
            {
                string token = GetRequestHeader(request, "X-Session-Token");
                string cohostId;
                bool kicked = true;
                if (token != null && _authManager.ValidateToken(token, out cohostId))
                {
                    kicked = false;
                }

                string jsonResponse = "{\"kicked\":" + (kicked ? "true" : "false") + "}";
                contentTypeProp.SetValue(response, "application/json");
                var stream = outputStreamProp.GetValue(response) as Stream;
                if (stream != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(jsonResponse);
                    stream.Write(bytes, 0, bytes.Length);
                }
                return;
            }

            // --- GET /partyline/join ---
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
            var finalStream = outputStreamProp.GetValue(response) as Stream;
            if (finalStream != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                finalStream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex)
        {
            Log("HandlePartylineRequest error: " + ex.Message);
        }
    }

    private string ReadRequestBody(object request)
    {
        try
        {
            var reqType = request.GetType();
            var inputStreamProp = reqType.GetProperty("InputStream");
            if (inputStreamProp == null) return "";

            var inputStream = inputStreamProp.GetValue(request) as Stream;
            if (inputStream == null) return "";

            using (var reader = new StreamReader(inputStream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
        catch
        {
            return "";
        }
    }

    private string GetRequestHeader(object request, string headerName)
    {
        try
        {
            var reqType = request.GetType();
            var headersProp = reqType.GetProperty("Headers");
            if (headersProp == null) return null;

            var headers = headersProp.GetValue(request);
            if (headers == null) return null;

            // Try to call .Get(headerName) via reflection
            var getMethod = headers.GetType().GetMethod("Get", new Type[] { typeof(string) });
            if (getMethod != null)
            {
                return getMethod.Invoke(headers, new object[] { headerName }) as string;
            }

            // Fallback: try indexer
            var indexer = headers.GetType().GetProperty("Item", new Type[] { typeof(string) });
            if (indexer != null)
            {
                return indexer.GetValue(headers, new object[] { headerName }) as string;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private string ExtractJsonStringValueFromBody(string json, string key)
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

    private string EscapeJsonStringValue(string value)
    {
        if (value == null) return "";
        var sb = new StringBuilder();
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
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    // Called by PlayIt Live's audio pipeline when it needs samples
    // Now delegates to AudioMixer
    internal int FillAudioBuffer(int length, IntPtr buffer)
    {
        if (_audioMixer != null)
        {
            return _audioMixer.FillOutputBuffer(length, buffer);
        }

        // Fallback: output silence if mixer not initialized
        int sampleCount = length / 2;
        var silence = new short[sampleCount];
        Marshal.Copy(silence, 0, buffer, sampleCount);
        return length;
    }

    public int GetCoHostCount() { return _cohosts.Count; }
    public string GetLink() { return "https://" + Dns.GetHostName() + ":" + _activePort + "/partyline/join"; }

    public void MuteAll() { foreach (var c in _cohosts.Values) c.IsMuted = true; }
    public void UnmuteAll() { foreach (var c in _cohosts.Values) c.IsMuted = false; }
    public void KickAll() { _cohosts.Clear(); }

    internal string GetCoHostPageHtml() { return GetCoHostPage(); }

    public override void Cleanup()
    {
        if (_cts != null) _cts.Cancel();

        if (_authManager != null)
        {
            _authManager.InvalidateAllSessions();
        }

        KickAll();
        Log("Plugin cleanup completed.");
    }

    public override void Configure()
    {
        var form = new PartylineConfigForm(_settingsManager);
        form.ShowDialog();
    }

    private string GetCoHostPage()
    {
        string safeStationName = EscapeHtmlContent(_stationName ?? "Partyline Co-Host");
        string html = @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>{{STATION_NAME}}</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:-apple-system,sans-serif;background:#1a1a2e;color:#fff;min-height:100vh;display:flex;align-items:center;justify-content:center}
.c{max-width:500px;width:100%;padding:2rem}
h1{font-size:1.5rem;margin-bottom:1.5rem;text-align:center}
.status{padding:1rem;border-radius:8px;margin-bottom:1.5rem;text-align:center;font-weight:bold}
.s-off{background:rgba(239,68,68,.2);border:1px solid #ef4444}
.s-on{background:rgba(34,197,94,.2);border:1px solid #22c55e}
.s-wait{background:rgba(234,179,8,.2);border:1px solid #eab308}
.s-kick{background:rgba(239,68,68,.3);border:1px solid #ef4444}
.btn{width:100%;padding:1rem;font-size:1.1rem;border:none;border-radius:8px;cursor:pointer;font-weight:bold;margin-bottom:1rem}
.btn-blue{background:#3b82f6;color:#fff}
.btn-blue:disabled{opacity:.5;cursor:not-allowed}
.ptt{padding:2rem;font-size:1.5rem;border-radius:12px;user-select:none;transition:all .1s}
.ptt-off{background:#64748b;color:#fff}
.ptt-on{background:#ef4444;color:#fff;transform:scale(.98)}
.ptt:disabled{opacity:.5;cursor:not-allowed}
.vu{height:8px;background:rgba(255,255,255,.1);border-radius:4px;overflow:hidden;margin-top:1rem}
.vu-fill{height:100%;background:#22c55e;width:0%;transition:width .05s}
.info{margin-top:1rem;font-size:.8rem;color:#94a3b8;text-align:center}
.input{width:100%;padding:.75rem;font-size:1rem;border:1px solid #334155;border-radius:8px;margin-bottom:.75rem;background:#0f172a;color:#fff}
.input:focus{outline:none;border-color:#3b82f6}
.error{color:#ef4444;font-size:.9rem;text-align:center;margin-bottom:1rem;min-height:1.2em}
.welcome{font-size:1.1rem;text-align:center;margin-bottom:1rem;color:#22c55e}
.dot{display:inline-block;width:8px;height:8px;border-radius:50%;background:#22c55e;margin-right:6px}
.hidden{display:none}
</style>
</head>
<body>
<div class='c'>
<h1>🎙️ {{STATION_NAME}}</h1>

<!-- Login State -->
<div id='loginPanel'>
<input id='username' class='input' type='text' placeholder='Username' autocomplete='username'>
<input id='password' class='input' type='password' placeholder='Password' autocomplete='current-password'>
<div id='errorMsg' class='error'></div>
<button id='connectBtn' class='btn btn-blue' onclick='doLogin()'>Connect</button>
</div>

<!-- Connected State -->
<div id='connectedPanel' class='hidden'>
<div id='welcomeMsg' class='welcome'></div>
<div id='st' class='status s-on'><span class='dot'></span>Connected</div>
<button id='ptt' class='btn ptt ptt-off' disabled
 onclick='toggleMic()'>
MIC OFF</button>
<div class='vu'><div id='vu' class='vu-fill'></div></div>
<div class='info'>Click the button to toggle your microphone on or off.</div>
</div>

<!-- Kicked State -->
<div id='kickedPanel' class='hidden'>
<div class='status s-kick'>Disconnected by host</div>
<div class='info'>Your session has been ended by the DJ.</div>
</div>

</div>
<script>
var ctx,src,proc,sending=false,connected=false;
var sessionToken=null,displayName='';
var statusInterval=null;
var SR=44100,BUF=4096;
var BASE=location.origin+'/partyline';
var stationName='{{STATION_NAME_JS}}';

function doLogin(){
 var u=document.getElementById('username').value.trim();
 var p=document.getElementById('password').value;
 document.getElementById('errorMsg').innerText='';
 if(!u||!p){document.getElementById('errorMsg').innerText='Please enter username and password.';return;}
 document.getElementById('connectBtn').disabled=true;
 var xhr=new XMLHttpRequest();
 xhr.open('POST',BASE+'/login',true);
 xhr.setRequestHeader('Content-Type','application/json');
 xhr.onload=function(){
  document.getElementById('connectBtn').disabled=false;
  try{
   var r=JSON.parse(xhr.responseText);
   if(r.success){
    sessionToken=r.token;
    displayName=r.displayName||u;
    showConnected();
   }else{
    document.getElementById('errorMsg').innerText=r.error||'Login failed';
   }
  }catch(e){document.getElementById('errorMsg').innerText='Connection error';}
 };
 xhr.onerror=function(){
  document.getElementById('connectBtn').disabled=false;
  document.getElementById('errorMsg').innerText='Connection error';
 };
 xhr.send(JSON.stringify({username:u,password:p}));
}

function showConnected(){
 document.getElementById('loginPanel').className='hidden';
 document.getElementById('connectedPanel').className='';
 document.getElementById('welcomeMsg').innerText='Welcome to '+stationName+', '+displayName+'!';
 startAudio();
 statusInterval=setInterval(pollStatus,3000);
}

function pollStatus(){
 if(!sessionToken)return;
 var xhr=new XMLHttpRequest();
 xhr.open('GET',BASE+'/status',true);
 xhr.setRequestHeader('X-Session-Token',sessionToken);
 xhr.onload=function(){
  try{
   var r=JSON.parse(xhr.responseText);
   if(r.kicked){showKicked();}
  }catch(e){}
 };
 xhr.send();
}

function showKicked(){
 connected=false;
 sending=false;
 if(statusInterval){clearInterval(statusInterval);statusInterval=null;}
 if(ctx){try{ctx.close();}catch(e){}}
 document.getElementById('connectedPanel').className='hidden';
 document.getElementById('kickedPanel').className='';
}

function startAudio(){
 navigator.mediaDevices.getUserMedia({audio:{sampleRate:SR,channelCount:1,echoCancellation:true}}).then(function(stream){
  ctx=new(window.AudioContext||window.webkitAudioContext)({sampleRate:SR});
  src=ctx.createMediaStreamSource(stream);
  proc=ctx.createScriptProcessor(BUF,1,1);
  src.connect(proc);proc.connect(ctx.destination);
  connected=true;
  document.getElementById('ptt').disabled=false;
  proc.onaudioprocess=function(e){
   if(!sending||!connected)return;
   var d=e.inputBuffer.getChannelData(0);
   var p=new Int16Array(d.length);
   for(var i=0;i<d.length;i++)p[i]=Math.max(-32768,Math.min(32767,Math.round(d[i]*32767)));
   var xhr=new XMLHttpRequest();
   xhr.open('POST',BASE+'/audio',true);
   xhr.setRequestHeader('Content-Type','application/octet-stream');
   xhr.setRequestHeader('X-Session-Token',sessionToken);
   xhr.setRequestHeader('X-Audio-Timestamp',Date.now().toString());
   xhr.send(p.buffer);
   var m=0;for(var i=0;i<d.length;i++){var v=Math.abs(d[i]);if(v>m)m=v;}
   document.getElementById('vu').style.width=(m*100)+'%';
  };
 }).catch(function(err){
  document.getElementById('st').className='status s-off';
  document.getElementById('st').innerText='Mic error: '+err.message;
 });
}

function toggleMic(){
 if(!connected)return;
 sending=!sending;
 if(sending){
  document.getElementById('ptt').className='btn ptt ptt-on';
  document.getElementById('ptt').innerText='MIC ON';
 }else{
  document.getElementById('ptt').className='btn ptt ptt-off';
  document.getElementById('ptt').innerText='MIC OFF';
  document.getElementById('vu').style.width='0%';
 }
}
</script>
</body>
</html>";
        html = html.Replace("{{STATION_NAME}}", safeStationName);
        html = html.Replace("{{STATION_NAME_JS}}", EscapeJsonStringValue(safeStationName));
        return html;
    }

    private string EscapeHtmlContent(string value)
    {
        if (value == null) return "";
        return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
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

    public AudioMixer()
    {
        _coHosts = new ConcurrentDictionary<string, CoHostState>();
        _lastLatency = new ConcurrentDictionary<string, float>();
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
            return ParseAccountsJson(json);
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

    private List<CoHostAccount> ParseAccountsJson(string json)
    {
        // Simple JSON parser for our known format:
        // {"accounts":[{"username":"x","password":"y","displayName":"z"}, ...]}
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
        Save(accounts, null);
    }

    public void Save(List<CoHostAccount> accounts, string stationName)
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

            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"stationName\":");
            sb.Append(EscapeJsonString(stationName));
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
    private DataGridView _grid;
    private Panel _editPanel;
    private TextBox _txtUsername;
    private TextBox _txtPassword;
    private TextBox _txtDisplayName;
    private TextBox _txtStationName;
    private Button _btnSave;
    private Button _btnCancel;
    private Button _btnAdd;
    private int _editingIndex;

    public PartylineConfigForm(SettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        _accounts = _settingsManager.Load();
        _stationName = _settingsManager.LoadStationName();
        _editingIndex = -1;
        InitializeFormComponents();
        LoadGrid();
    }

    private void InitializeFormComponents()
    {
        Text = "Partyline Co-Host Configuration";
        Width = 550;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        // Station Name field at top
        Label lblStation = new Label();
        lblStation.Text = "Station Name:";
        lblStation.Location = new System.Drawing.Point(12, 12);
        lblStation.AutoSize = true;
        lblStation.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
        Controls.Add(lblStation);

        _txtStationName = new TextBox();
        _txtStationName.Location = new System.Drawing.Point(120, 10);
        _txtStationName.Size = new System.Drawing.Size(300, 22);
        _txtStationName.Text = _stationName;
        Controls.Add(_txtStationName);

        Label lblTitle = new Label();
        lblTitle.Text = "Co-Host Accounts:";
        lblTitle.Location = new System.Drawing.Point(12, 44);
        lblTitle.AutoSize = true;
        lblTitle.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
        Controls.Add(lblTitle);

        // DataGridView for account list
        _grid = new DataGridView();
        _grid.Location = new System.Drawing.Point(12, 68);
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
        colUsername.FillWeight = 35;
        _grid.Columns.Add(colUsername);

        DataGridViewTextBoxColumn colDisplay = new DataGridViewTextBoxColumn();
        colDisplay.Name = "DisplayName";
        colDisplay.HeaderText = "Display Name";
        colDisplay.FillWeight = 35;
        _grid.Columns.Add(colDisplay);

        DataGridViewButtonColumn colEdit = new DataGridViewButtonColumn();
        colEdit.Name = "Edit";
        colEdit.HeaderText = "";
        colEdit.Text = "Edit";
        colEdit.UseColumnTextForButtonValue = true;
        colEdit.FillWeight = 15;
        _grid.Columns.Add(colEdit);

        DataGridViewButtonColumn colDelete = new DataGridViewButtonColumn();
        colDelete.Name = "Delete";
        colDelete.HeaderText = "";
        colDelete.Text = "Delete";
        colDelete.UseColumnTextForButtonValue = true;
        colDelete.FillWeight = 15;
        _grid.Columns.Add(colDelete);

        _grid.CellContentClick += OnGridCellContentClick;
        Controls.Add(_grid);

        // Add button
        _btnAdd = new Button();
        _btnAdd.Text = "+ Add Co-Host";
        _btnAdd.Location = new System.Drawing.Point(12, 276);
        _btnAdd.Size = new System.Drawing.Size(120, 28);
        _btnAdd.Click += OnAddClick;
        Controls.Add(_btnAdd);

        // Edit panel
        _editPanel = new Panel();
        _editPanel.Location = new System.Drawing.Point(12, 312);
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
        string stationName = _txtStationName.Text.Trim();
        if (!string.IsNullOrEmpty(stationName))
        {
            _stationName = stationName;
        }
        _settingsManager.Save(_accounts, _stationName);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnCloseClick(object sender, EventArgs e)
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void LoadGrid()
    {
        _grid.Rows.Clear();
        for (int i = 0; i < _accounts.Count; i++)
        {
            CoHostAccount acct = _accounts[i];
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
            // Adding new account
            CoHostAccount newAcct = new CoHostAccount();
            newAcct.Username = username;
            newAcct.Password = password;
            newAcct.DisplayName = string.IsNullOrEmpty(displayName) ? username : displayName;
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
        titleLabel.Font = new System.Drawing.Font("Segoe UI", 11f, System.Drawing.FontStyle.Bold);
        titleLabel.Dock = DockStyle.Fill;
        titleLabel.Padding = new Padding(2, 4, 0, 0);
        titlePanel.Controls.Add(titleLabel);

        Button configBtn = new Button();
        configBtn.Text = "\u2699";
        configBtn.FlatStyle = FlatStyle.Flat;
        configBtn.Size = new System.Drawing.Size(24, 22);
        configBtn.Dock = DockStyle.Right;
        configBtn.Font = new System.Drawing.Font("Segoe UI", 10f);
        configBtn.ForeColor = System.Drawing.Color.FromArgb(180, 180, 190);
        configBtn.BackColor = System.Drawing.Color.FromArgb(50, 50, 55);
        configBtn.FlatAppearance.BorderSize = 0;
        configBtn.Cursor = Cursors.Hand;
        configBtn.Click += OnConfigureClick;
        titlePanel.Controls.Add(configBtn);
        _toolTip.SetToolTip(configBtn, "Configure co-host accounts");

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
        row.CohostId = account.Username;

        // Row panel
        Panel rowPanel = new Panel();
        rowPanel.Location = new System.Drawing.Point(4, yOffset);
        rowPanel.Size = new System.Drawing.Size(Width - 8, 26);
        rowPanel.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        rowPanel.BackColor = System.Drawing.Color.FromArgb(60, 60, 70);
        row.RowPanel = rowPanel;

        // Connected indicator (dot)
        Label connIndicator = new Label();
        connIndicator.Text = "\u25CB";
        connIndicator.ForeColor = System.Drawing.Color.Gray;
        connIndicator.Font = new System.Drawing.Font("Segoe UI", 7f);
        connIndicator.Location = new System.Drawing.Point(2, 6);
        connIndicator.Size = new System.Drawing.Size(12, 14);
        rowPanel.Controls.Add(connIndicator);
        row.ConnectedIndicator = connIndicator;

        // Display name label
        Label nameLabel = new Label();
        nameLabel.Text = account.DisplayName != null ? account.DisplayName : account.Username;
        nameLabel.ForeColor = System.Drawing.Color.White;
        nameLabel.Font = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold);
        nameLabel.Location = new System.Drawing.Point(14, 4);
        nameLabel.Size = new System.Drawing.Size(80, 20);
        nameLabel.AutoEllipsis = true;
        rowPanel.Controls.Add(nameLabel);
        _toolTip.SetToolTip(nameLabel, "Co-host display name");

        // VU meter container (outer panel)
        Panel vuOuter = new Panel();
        vuOuter.Location = new System.Drawing.Point(88, 6);
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

        // Latency label (small gray text next to VU meter)
        Label latencyLabel = new Label();
        latencyLabel.Text = "";
        latencyLabel.ForeColor = System.Drawing.Color.FromArgb(148, 163, 184);
        latencyLabel.Font = new System.Drawing.Font("Segoe UI", 6.5f);
        latencyLabel.Location = new System.Drawing.Point(150, 7);
        latencyLabel.Size = new System.Drawing.Size(34, 12);
        latencyLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        rowPanel.Controls.Add(latencyLabel);
        row.LatencyLabel = latencyLabel;

        // Mute button
        Button muteBtn = new Button();
        muteBtn.Text = "\U0001F50A";
        muteBtn.FlatStyle = FlatStyle.Flat;
        muteBtn.Size = new System.Drawing.Size(30, 22);
        muteBtn.Location = new System.Drawing.Point(186, 2);
        muteBtn.Font = new System.Drawing.Font("Segoe UI", 8f);
        muteBtn.ForeColor = System.Drawing.Color.White;
        muteBtn.BackColor = System.Drawing.Color.FromArgb(70, 70, 85);
        muteBtn.FlatAppearance.BorderSize = 0;
        muteBtn.Cursor = Cursors.Hand;
        muteBtn.Tag = account.Username;
        muteBtn.Click += OnMuteClick;
        rowPanel.Controls.Add(muteBtn);
        row.MuteButton = muteBtn;
        _toolTip.SetToolTip(muteBtn, "Mute/unmute co-host audio");

        // Kick button
        Button kickBtn = new Button();
        kickBtn.Text = "\u2715";
        kickBtn.FlatStyle = FlatStyle.Flat;
        kickBtn.Size = new System.Drawing.Size(26, 22);
        kickBtn.Location = new System.Drawing.Point(220, 2);
        kickBtn.Font = new System.Drawing.Font("Segoe UI", 8f);
        kickBtn.ForeColor = System.Drawing.Color.White;
        kickBtn.BackColor = System.Drawing.Color.FromArgb(180, 60, 60);
        kickBtn.FlatAppearance.BorderSize = 0;
        kickBtn.Cursor = Cursors.Hand;
        kickBtn.Tag = account.Username;
        kickBtn.Click += OnKickClick;
        rowPanel.Controls.Add(kickBtn);
        row.KickButton = kickBtn;
        _toolTip.SetToolTip(kickBtn, "Disconnect co-host");

        // Live toggle button
        Button liveBtn = new Button();
        liveBtn.Text = "Go Live";
        liveBtn.FlatStyle = FlatStyle.Flat;
        liveBtn.Size = new System.Drawing.Size(60, 22);
        liveBtn.Location = new System.Drawing.Point(250, 2);
        liveBtn.Font = new System.Drawing.Font("Segoe UI", 7.5f, System.Drawing.FontStyle.Bold);
        liveBtn.ForeColor = System.Drawing.Color.Gray;
        liveBtn.BackColor = System.Drawing.Color.FromArgb(50, 50, 60);
        liveBtn.FlatAppearance.BorderSize = 0;
        liveBtn.Cursor = Cursors.Hand;
        liveBtn.Tag = account.Username;
        liveBtn.Click += OnLiveClick;
        rowPanel.Controls.Add(liveBtn);
        row.LiveButton = liveBtn;
        _toolTip.SetToolTip(liveBtn, "Toggle co-host audio on/off air");

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
                    row.MuteButton.Text = "\U0001F507";
                    row.MuteButton.BackColor = System.Drawing.Color.FromArgb(180, 120, 30);
                }
                else
                {
                    row.MuteButton.Text = "\U0001F50A";
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

            // Update connected indicator (based on active session, not audio)
            bool connected = _authManager.HasActiveSession(row.CohostId);
            if (connected)
            {
                row.ConnectedIndicator.Text = "\u25CF";
                row.ConnectedIndicator.ForeColor = System.Drawing.Color.FromArgb(34, 197, 94);
            }
            else
            {
                row.ConnectedIndicator.Text = "\u25CB";
                row.ConnectedIndicator.ForeColor = System.Drawing.Color.Gray;
            }

            // Update latency display
            float latency = _audioMixer.GetLatency(row.CohostId);
            if (latency > 0 && connected)
            {
                row.LatencyLabel.Text = ((int)latency).ToString() + "ms";
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
