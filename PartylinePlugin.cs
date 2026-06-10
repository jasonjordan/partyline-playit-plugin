using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PlayIt.PluginEngine;

namespace Partyline
{
    public class PartylinePlugin : Plugin<IPlayItLiveApp>
    {
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Thread _serverThread;
        private CoHostManager _coHostManager;
        private ToolStripMenuItem _menuItem;
        private bool _running;
        private int _activePort;
        private const int PRIMARY_PORT = 25433;
        private const int FALLBACK_PORT = 8080;
        private const string URL_PREFIX = "/partyline/";

        public override void Run()
        {
            _cts = new CancellationTokenSource();
            _coHostManager = new CoHostManager(App.AudioPipeline);

            // Register embedded status strip in PlayIt Live's UI
            PartylineStatusStrip statusStrip = null;
            App.RegisterUserControl(() =>
            {
                statusStrip = new PartylineStatusStrip();
                statusStrip.SetManager(_coHostManager);
                return statusStrip;
            }, UserControlLocation.BelowTrackList, priority: 100);

            // Start HTTP server
            _serverThread = new Thread(StartServer) { IsBackground = true };
            _serverThread.Start();

            // Add menu items
            _menuItem = new ToolStripMenuItem("Partyline");
            var statusItem = new ToolStripMenuItem("Status: Starting...");
            statusItem.Enabled = false;
            _menuItem.DropDownItems.Add(statusItem);
            _menuItem.DropDownItems.Add(new ToolStripMenuItem("Show Partyline Mixer", null, (s, e) => PartylineMixerForm.ShowInstance(_coHostManager)));
            _menuItem.DropDownItems.Add(new ToolStripMenuItem("Copy Co-Host Link", null, OnCopyLink));
            _menuItem.DropDownItems.Add(new ToolStripSeparator());
            _menuItem.DropDownItems.Add(new ToolStripMenuItem("Disconnect All", null, OnDisconnectAll));

            App.GetMenuStrip()?.Items.Add(_menuItem);
        }

        private void StartServer()
        {
            // Try primary port (sharing with PlayIt Live via HTTP.sys)
            if (TryStartListener($"http://+:{PRIMARY_PORT}{URL_PREFIX}"))
            {
                _activePort = PRIMARY_PORT;
            }
            else
            {
                // Try registering URL ACL and retry
                if (RegisterUrlAcl(PRIMARY_PORT) && TryStartListener($"http://+:{PRIMARY_PORT}{URL_PREFIX}"))
                {
                    _activePort = PRIMARY_PORT;
                }
                else
                {
                    // Fallback to dedicated port
                    if (TryStartListener($"http://+:{FALLBACK_PORT}{URL_PREFIX}"))
                    {
                        _activePort = FALLBACK_PORT;
                    }
                    else
                    {
                        App.Log("Partyline: Failed to start HTTP server on any port");
                        return;
                    }
                }
            }

            _running = true;
            UpdateStatus($"Listening on port {_activePort}");
            App.Log($"Partyline: HTTP server started on port {_activePort}");

            // Accept requests
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var context = _listener.GetContext();
                    Task.Run(() => HandleRequest(context));
                }
                catch (HttpListenerException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    App.Log($"Partyline: Request error: {ex.Message}");
                }
            }
        }

        private bool TryStartListener(string prefix)
        {
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();
                _listener = listener;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool RegisterUrlAcl(int port)
        {
            try
            {
                var result = MessageBox.Show(
                    "Partyline needs administrator permission to share port " + port + " with PlayIt Live.\n\n" +
                    "Click Yes to grant permission (requires UAC elevation).\n" +
                    "Click No to use a separate port instead.",
                    "Partyline Setup",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result != DialogResult.Yes) return false;

                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"http add urlacl url=http://+:{port}{URL_PREFIX} user=Everyone",
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true
                };

                var proc = Process.Start(psi);
                proc?.WaitForExit(10000);
                return proc?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            var path = context.Request.Url.AbsolutePath.Replace("/partyline", "").TrimStart('/');
            var response = context.Response;

            try
            {
                switch (path)
                {
                    case "":
                    case "join":
                        ServeCoHostPage(response);
                        break;

                    case "api/offer":
                        HandleOffer(context);
                        break;

                    case "api/ice":
                        HandleIceCandidate(context);
                        break;

                    case "api/status":
                        ServeJson(response, _coHostManager.GetStatus());
                        break;

                    default:
                        response.StatusCode = 404;
                        ServeText(response, "Not found");
                        break;
                }
            }
            catch (Exception ex)
            {
                response.StatusCode = 500;
                ServeText(response, ex.Message);
            }
        }

        private void HandleOffer(HttpListenerContext context)
        {
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = 405;
                ServeText(context.Response, "Method not allowed");
                return;
            }

            using (var reader = new StreamReader(context.Request.InputStream))
            {
                var body = reader.ReadToEnd();
                var answer = _coHostManager.AcceptOffer(body);
                ServeJson(context.Response, answer);
            }
        }

        private void HandleIceCandidate(HttpListenerContext context)
        {
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = 405;
                ServeText(context.Response, "Method not allowed");
                return;
            }

            using (var reader = new StreamReader(context.Request.InputStream))
            {
                var body = reader.ReadToEnd();
                _coHostManager.AddIceCandidate(body);
                ServeText(context.Response, "OK");
            }
        }

        private void ServeCoHostPage(HttpListenerResponse response)
        {
            response.ContentType = "text/html; charset=utf-8";
            var html = CoHostPage.GetHtml();
            var buffer = System.Text.Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private void ServeJson(HttpListenerResponse response, string json)
        {
            response.ContentType = "application/json";
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            var buffer = System.Text.Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private void ServeText(HttpListenerResponse response, string text)
        {
            response.ContentType = "text/plain";
            var buffer = System.Text.Encoding.UTF8.GetBytes(text);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private void UpdateStatus(string status)
        {
            if (_menuItem?.DropDownItems.Count > 0)
            {
                _menuItem.DropDownItems[0].Text = $"Status: {status}";
            }
        }

        private void OnCopyLink(object sender, EventArgs e)
        {
            var host = Dns.GetHostName();
            var link = $"http://{host}:{_activePort}/partyline/join";
            Clipboard.SetText(link);
            MessageBox.Show($"Co-host link copied:\n{link}", "Partyline");
        }

        private void OnDisconnectAll(object sender, EventArgs e)
        {
            _coHostManager.DisconnectAll();
        }

        public override void Cleanup()
        {
            _cts?.Cancel();
            _listener?.Stop();
            _coHostManager?.Dispose();
        }
    }
}
