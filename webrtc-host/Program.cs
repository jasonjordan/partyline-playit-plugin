using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Partyline.WebRtcHost
{
    // Minimal ILogger that routes SIPSorcery's internal logging into our IPC log
    // frames, so the plugin's partylinelog.txt shows exactly why ICE gathering
    // (STUN/TURN) succeeds or fails. Implemented against the Abstractions package
    // only (no concrete logging dependency).
    internal sealed class HostLoggerFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new HostLogger();
        public void Dispose() { }
    }

    internal sealed class HostLogger : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            try
            {
                if (logLevel < LogLevel.Debug) return;
                string msg = formatter != null ? formatter(state, exception) : (state != null ? state.ToString() : "");
                if (exception != null) msg += " :: " + exception.Message;
                HostLog.Write("[sip:" + logLevel + "] " + msg);
            }
            catch { }
        }
    }

    /// <summary>
    /// Out-of-process WebRTC host. Reads length-prefixed binary frames from stdin
    /// (commands from the plugin) and writes event frames to stdout. All numbers
    /// are little-endian. Frame = [int32 length][byte type][payload].
    ///
    /// Commands (plugin -> host):
    ///   1 SetIce      payload = UTF8 JSON array [{urls:[],username,credential}]
    ///   2 CreatePeer  payload = UTF8 peerId
    ///   3 ClosePeer   payload = UTF8 peerId
    ///   4 CloseAll
    ///   5 CreateOffer payload = UTF8 peerId      -> emits 103
    ///   6 CreateAnswer payload = UTF8 peerId     -> emits 104
    ///   7 ApplyRemote payload = UTF8 JSON {peerId,type,sdp}
    ///   8 AddIce      payload = UTF8 JSON {peerId,candidate:{...}}
    ///   9 OutAudio    payload = [int32 count][int32 rate][int32 channels][int16 pcm...]
    ///
    /// Events (host -> plugin):
    ///   100 LocalIce    UTF8 JSON {peerId,candidate}  (candidate = RTCIceCandidateInit JSON string)
    ///   101 RemoteAudio [int32 peerIdLen][peerId utf8][int32 count][int32 rate][int16 pcm...]
    ///   102 State       UTF8 JSON {peerId,state}
    ///   103 OfferResult UTF8 JSON {peerId,sdp}
    ///   104 AnswerResult UTF8 JSON {peerId,sdp}
    ///   105 Log         UTF8 text
    /// </summary>
    internal static class Program
    {
        private static Stream _in;
        private static Stream _out;
        private static readonly object _writeLock = new object();
        private static SipPeer _peer;

        private static int Main(string[] args)
        {
            _in = Console.OpenStandardInput();
            _out = Console.OpenStandardOutput();

            HostLog.Write = (s) => WriteFrame(105, Encoding.UTF8.GetBytes(s ?? ""));

            // Route SIPSorcery's internal logging into our IPC log so ICE gathering
            // diagnostics (STUN/TURN, DNS, candidate errors) are visible in the plugin log.
            try { SIPSorcery.LogFactory.Set(new HostLoggerFactory()); }
            catch (Exception ex) { HostLog.Write("[host] could not set SIPSorcery logger: " + ex.Message); }

            _peer = new SipPeer();
            _peer.OnLocalIceCandidate = (peerId, candJson) =>
            {
                var jo = new JObject();
                jo["peerId"] = peerId;
                jo["candidate"] = candJson;
                WriteFrame(100, Encoding.UTF8.GetBytes(jo.ToString(Newtonsoft.Json.Formatting.None)));
            };
            _peer.OnConnectionStateChanged = (peerId, state) =>
            {
                var jo = new JObject();
                jo["peerId"] = peerId;
                jo["state"] = state;
                WriteFrame(102, Encoding.UTF8.GetBytes(jo.ToString(Newtonsoft.Json.Formatting.None)));
            };
            _peer.OnRemoteAudioFrame = (peerId, pcm, count, rate) =>
            {
                byte[] pid = Encoding.UTF8.GetBytes(peerId ?? "");
                using (var ms = new MemoryStream())
                using (var bw = new BinaryWriter(ms))
                {
                    bw.Write(pid.Length);
                    bw.Write(pid);
                    bw.Write(count);
                    bw.Write(rate);
                    for (int i = 0; i < count && i < pcm.Length; i++) bw.Write(pcm[i]);
                    bw.Flush();
                    WriteFrame(101, ms.ToArray());
                }
            };

            HostLog.Write("[host] PartylineWebRtcHost started (pid " + System.Diagnostics.Process.GetCurrentProcess().Id + ").");

            try
            {
                while (true)
                {
                    int len = ReadInt32();
                    if (len < 0) break;          // EOF
                    if (len == 0) continue;
                    byte[] payload = ReadFull(len);
                    if (payload == null) break;  // EOF mid-frame
                    Dispatch(payload);
                }
            }
            catch (Exception ex)
            {
                try { HostLog.Write("[host] fatal: " + ex.Message); } catch { }
            }
            finally
            {
                try { if (_peer != null) _peer.CloseAll(); } catch { }
            }
            return 0;
        }

        private static void Dispatch(byte[] payload)
        {
            byte type = payload[0];
            int dlen = payload.Length - 1;
            switch (type)
            {
                case 1: // SetIce
                {
                    string json = Encoding.UTF8.GetString(payload, 1, dlen);
                    _peer.SetIceServers(ParseIceServers(json));
                    break;
                }
                case 2: _peer.CreatePeerConnection(Str(payload)); break;
                case 3: _peer.ClosePeerConnection(Str(payload)); break;
                case 4: _peer.CloseAll(); break;
                case 5: // CreateOffer
                {
                    string peerId = Str(payload);
                    string sdp = _peer.CreateOffer(peerId);
                    EmitSdp(103, peerId, sdp);
                    break;
                }
                case 6: // CreateAnswer
                {
                    string peerId = Str(payload);
                    string sdp = _peer.CreateAnswer(peerId);
                    EmitSdp(104, peerId, sdp);
                    break;
                }
                case 7: // ApplyRemote
                {
                    JObject jo = JObject.Parse(Encoding.UTF8.GetString(payload, 1, dlen));
                    _peer.ApplyRemoteDescription((string)jo["peerId"], (string)jo["type"], (string)jo["sdp"]);
                    break;
                }
                case 8: // AddIce
                {
                    JObject jo = JObject.Parse(Encoding.UTF8.GetString(payload, 1, dlen));
                    string peerId = (string)jo["peerId"];
                    JToken cand = jo["candidate"];
                    string candJson = cand != null ? cand.ToString(Newtonsoft.Json.Formatting.None) : null;
                    _peer.AddIceCandidate(peerId, candJson);
                    break;
                }
                case 9: // OutAudio
                {
                    using (var ms = new MemoryStream(payload, 1, dlen))
                    using (var br = new BinaryReader(ms))
                    {
                        int count = br.ReadInt32();
                        int rate = br.ReadInt32();
                        int channels = br.ReadInt32();
                        short[] pcm = new short[count];
                        for (int i = 0; i < count; i++) pcm[i] = br.ReadInt16();
                        _peer.PushOutboundAudio(pcm, count, rate, channels);
                    }
                    break;
                }
                default:
                    HostLog.Write("[host] unknown command type " + type);
                    break;
            }
        }

        private static void EmitSdp(byte type, string peerId, string sdp)
        {
            var jo = new JObject();
            jo["peerId"] = peerId;
            jo["sdp"] = sdp ?? "";
            WriteFrame(type, Encoding.UTF8.GetBytes(jo.ToString(Newtonsoft.Json.Formatting.None)));
        }

        private static IceServerCfg[] ParseIceServers(string json)
        {
            var list = new System.Collections.Generic.List<IceServerCfg>();
            try
            {
                JArray arr = JArray.Parse(json);
                foreach (JToken t in arr)
                {
                    var cfg = new IceServerCfg();
                    JToken urls = t["urls"];
                    if (urls is JArray ua)
                    {
                        var us = new System.Collections.Generic.List<string>();
                        foreach (JToken u in ua) us.Add((string)u);
                        cfg.Urls = us.ToArray();
                    }
                    else if (urls != null) cfg.Urls = new[] { (string)urls };
                    cfg.Username = (string)t["username"];
                    cfg.Credential = (string)t["credential"];
                    list.Add(cfg);
                }
            }
            catch (Exception ex) { HostLog.Write("[host] ParseIceServers failed: " + ex.Message); }
            return list.ToArray();
        }

        private static string Str(byte[] payload)
        {
            return Encoding.UTF8.GetString(payload, 1, payload.Length - 1);
        }

        // --- framed IO -------------------------------------------------------

        private static void WriteFrame(byte type, byte[] data)
        {
            int len = 1 + (data != null ? data.Length : 0);
            byte[] frame = new byte[4 + len];
            frame[0] = (byte)(len & 0xFF);
            frame[1] = (byte)((len >> 8) & 0xFF);
            frame[2] = (byte)((len >> 16) & 0xFF);
            frame[3] = (byte)((len >> 24) & 0xFF);
            frame[4] = type;
            if (data != null && data.Length > 0) Array.Copy(data, 0, frame, 5, data.Length);
            lock (_writeLock)
            {
                _out.Write(frame, 0, frame.Length);
                _out.Flush();
            }
        }

        private static int ReadInt32()
        {
            byte[] b = ReadFull(4);
            if (b == null) return -1;
            return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
        }

        private static byte[] ReadFull(int n)
        {
            byte[] buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = _in.Read(buf, off, n - off);
                if (r <= 0) return null; // EOF
                off += r;
            }
            return buf;
        }
    }
}
