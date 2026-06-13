using System;
using System.Collections.Generic;
using System.Net;
using Newtonsoft.Json.Linq;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Concentus.Enums;
using Concentus.Structs;

namespace Partyline.WebRtcHost
{
    /// <summary>Logging hook (routed to a "log" IPC frame by Program.cs).</summary>
    internal static class HostLog
    {
        public static Action<string> Write = _ => { };
    }

    /// <summary>One ICE server entry (mirrors the plugin's WebRtcIceServer).</summary>
    public sealed class IceServerCfg
    {
        public string[] Urls;
        public string Username;
        public string Credential;
    }

    /// <summary>
    /// SIPSorcery 6.2.4 driver, identical in behaviour to the plugin's in-proc
    /// adapter but self-contained for the out-of-process host. Runs in a clean
    /// 64-bit process so ICE/DTLS threads have normal stacks and there is no
    /// version conflict with the host's SIPSorcery 5.2.3.
    /// </summary>
    public sealed class SipPeer : IDisposable
    {
        private const int OpusSampleRate = 48000;       // mono Opus capture/transport rate
        private const int FrameSamples = 960;           // 20 ms @ 48 kHz mono
        private const int MaxPacketBytes = 4000;        // generous Opus packet ceiling
        private const int MaxDecodeSamples = 5760;      // 120 ms @ 48 kHz mono (max Opus frame)
        private const int OpusPayloadId = 111;          // dynamic payload type browsers use for Opus
        private const int SdpTimeoutMs = 10000;

        private readonly object _sync = new object();
        private readonly Dictionary<string, PeerEntry> _peers = new Dictionary<string, PeerEntry>();

        private RTCConfiguration _config = new RTCConfiguration { iceServers = new List<RTCIceServer>() };

        private readonly object _encLock = new object();
        private OpusEncoder _encoder;
        private readonly List<short> _pending = new List<short>(FrameSamples * 4);

        public Action<string, string> OnLocalIceCandidate { get; set; }
        public Action<string, short[], int, int> OnRemoteAudioFrame { get; set; }
        public Action<string, string> OnConnectionStateChanged { get; set; }

        private class PeerEntry
        {
            public string PeerId;
            public RTCPeerConnection Pc;
            public OpusDecoder Decoder;
            public readonly object DecodeLock = new object();
            public bool FirstRtpLogged;
            public long RtpReceived;
            public bool FirstSendLogged;
            public bool FirstSendErrorLogged;
        }

        // --- ICE configuration ----------------------------------------------

        public void SetIceServers(IceServerCfg[] iceServers)
        {
            var config = new RTCConfiguration();
            config.iceServers = new List<RTCIceServer>();
            int stunKept = 0, turnKept = 0;
            const int MaxStun = 1, MaxTurn = 2;
            if (iceServers != null)
            {
                for (int i = 0; i < iceServers.Length; i++)
                {
                    IceServerCfg s = iceServers[i];
                    if (s == null || s.Urls == null || s.Urls.Length == 0) continue;
                    for (int u = 0; u < s.Urls.Length; u++)
                    {
                        string url = s.Urls[u];
                        if (string.IsNullOrEmpty(url)) continue;
                        bool isTurn = url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
                                   || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase);
                        if (isTurn) { if (turnKept >= MaxTurn) continue; turnKept++; }
                        else { if (stunKept >= MaxStun) continue; stunKept++; }

                        // Resolve the STUN/TURN hostname to an IP ourselves (OS resolver)
                        // so SIPSorcery never relies on its DnsClient auto-detection, which
                        // often fails on Windows and leaves it gathering ONLY host candidates.
                        string resolved = ResolveIceHostToIp(url);

                        var srv = new RTCIceServer();
                        srv.urls = resolved;
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
            for (int i = 0; i < existing.Count; i++)
            {
                try { existing[i].Pc.setConfiguration(config); }
                catch (Exception ex) { HostLog.Write("[SIPSorcery] setConfiguration failed for " + existing[i].PeerId + ": " + ex.Message); }
            }
            HostLog.Write("[SIPSorcery] Applied " + config.iceServers.Count + " ICE server entries (capped: " + stunKept + " STUN, " + turnKept + " TURN).");
        }

        // --- Connection lifecycle -------------------------------------------

        private static bool _versionLogged;
        private static RTCPeerConnection NewRTCPeerConnection(RTCConfiguration config)
        {
            if (!_versionLogged)
            {
                _versionLogged = true;
                try
                {
                    var asm = typeof(RTCPeerConnection).Assembly.GetName();
                    HostLog.Write("[SIPSorcery] loaded assembly: " + asm.Name + " " + asm.Version);
                }
                catch { }
            }
            return new RTCPeerConnection(config);
        }

        // Resolve a STUN/TURN URL's hostname to an IP using the OS resolver, returning
        // a URL with the IP in place of the host (scheme/port/?transport preserved).
        // SIPSorcery then treats it as an explicit endpoint and skips its DnsClient
        // path (whose name-server auto-detection often fails on Windows, leaving it
        // gathering only host candidates). Returns the original URL on any failure.
        private static string ResolveIceHostToIp(string url)
        {
            try
            {
                int schemeIdx = url.IndexOf(':');
                if (schemeIdx <= 0) return url;
                string scheme = url.Substring(0, schemeIdx);
                string rest = url.Substring(schemeIdx + 1);

                string query = "";
                int q = rest.IndexOf('?');
                if (q >= 0) { query = rest.Substring(q); rest = rest.Substring(0, q); }

                string host, portPart = "";
                int colon = rest.LastIndexOf(':');
                if (colon >= 0) { host = rest.Substring(0, colon); portPart = rest.Substring(colon); }
                else { host = rest; }

                System.Net.IPAddress already;
                if (System.Net.IPAddress.TryParse(host, out already)) return url; // already an IP

                System.Net.IPAddress[] addrs = System.Net.Dns.GetHostAddresses(host);
                System.Net.IPAddress chosen = null;
                for (int i = 0; i < addrs.Length; i++)
                    if (addrs[i].AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { chosen = addrs[i]; break; }
                if (chosen == null && addrs.Length > 0) chosen = addrs[0];
                if (chosen == null) { HostLog.Write("[SIPSorcery] DNS: no address for " + host); return url; }

                string outUrl = scheme + ":" + chosen.ToString() + portPart + query;
                HostLog.Write("[SIPSorcery] resolved " + host + " -> " + chosen + " (" + outUrl + ")");
                return outUrl;
            }
            catch (Exception ex)
            {
                HostLog.Write("[SIPSorcery] DNS resolve failed for '" + url + "': " + ex.Message);
                return url;
            }
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
            try { pc = NewRTCPeerConnection(config); }
            catch (Exception ex)
            {
                HostLog.Write("[SIPSorcery] RTCPeerConnection ctor failed for " + peerId + ": " + ex.Message);
                return;
            }

            var entry = new PeerEntry();
            entry.PeerId = peerId;
            entry.Pc = pc;
            try { entry.Decoder = OpusDecoder.Create(OpusSampleRate, 1); }
            catch (Exception ex) { HostLog.Write("[SIPSorcery] Opus decoder create failed for " + peerId + ": " + ex.Message); }

            try
            {
                var opus = new AudioFormat(AudioCodecsEnum.OPUS, OpusPayloadId, OpusSampleRate, 2, null);
                var track = new MediaStreamTrack(opus, MediaStreamStatusEnum.SendRecv);
                pc.addTrack(track);
            }
            catch (Exception ex) { HostLog.Write("[SIPSorcery] track setup failed for " + peerId + ": " + ex.Message); }

            WirePeerEvents(entry);

            lock (_sync)
            {
                if (_peers.ContainsKey(peerId)) { DisposeEntry(entry); return; }
                _peers[peerId] = entry;
            }
            HostLog.Write("[SIPSorcery] PeerConnection created for " + peerId);
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
            HostLog.Write("[SIPSorcery] PeerConnection closed for " + peerId);
        }

        public void CloseAll()
        {
            List<PeerEntry> all;
            lock (_sync) { all = new List<PeerEntry>(_peers.Values); _peers.Clear(); }
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
            catch (Exception ex) { HostLog.Write("[SIPSorcery] CreateOffer failed for " + peerId + ": " + ex.Message); return null; }
        }

        public string CreateAnswer(string peerId)
        {
            PeerEntry e = Get(peerId);
            if (e == null) return null;
            try
            {
                RTCSessionDescriptionInit answer = e.Pc.createAnswer(null);
                if (answer == null) return null;
                e.Pc.setLocalDescription(answer).Wait(SdpTimeoutMs);
                return answer.sdp;
            }
            catch (Exception ex) { HostLog.Write("[SIPSorcery] CreateAnswer failed for " + peerId + ": " + ex.Message); return null; }
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
                    HostLog.Write("[SIPSorcery] setRemoteDescription(" + type + ") for " + peerId + " returned " + result + ".");
            }
            catch (Exception ex) { HostLog.Write("[SIPSorcery] ApplyRemoteDescription failed for " + peerId + ": " + ex.Message); }
        }

        // --- Trickle ICE -----------------------------------------------------

        public void AddIceCandidate(string peerId, string candidateJson)
        {
            PeerEntry e = Get(peerId);
            if (e == null || candidateJson == null) return;
            try
            {
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
            catch (Exception ex) { HostLog.Write("[SIPSorcery] AddIceCandidate failed for " + peerId + ": " + ex.Message); }
        }

        // --- Outbound audio --------------------------------------------------

        public void PushOutboundAudio(short[] pcm, int sampleCount, int sampleRate, int channels)
        {
            if (pcm == null || sampleCount <= 0) return;
            if (sampleRate <= 0) sampleRate = OpusSampleRate;
            if (channels <= 0) channels = 1;

            short[] mono = Downmix(pcm, sampleCount, channels);
            short[] mono48 = (sampleRate == OpusSampleRate) ? mono : ResampleTo48k(mono, sampleRate);

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
                    catch (Exception ex) { HostLog.Write("[SIPSorcery] Opus encode failed: " + ex.Message); break; }
                    if (len <= 0) continue;
                    var packet = new byte[len];
                    Array.Copy(outBytes, packet, len);
                    if (encodedFrames == null) encodedFrames = new List<byte[]>(2);
                    encodedFrames.Add(packet);
                }
            }

            if (encodedFrames == null) return;

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
                            HostLog.Write("[SIPSorcery] first outbound audio RTP sent to " + targets[p].PeerId);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!targets[p].FirstSendErrorLogged)
                        {
                            targets[p].FirstSendErrorLogged = true;
                            HostLog.Write("[SIPSorcery] SendAudio failed for " + targets[p].PeerId + ": " + ex.Message);
                        }
                    }
                }
            }
        }

        private void EnsureEncoder()
        {
            if (_encoder != null) return;
            try { _encoder = OpusEncoder.Create(OpusSampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP); }
            catch (Exception ex) { HostLog.Write("[SIPSorcery] Opus encoder create failed: " + ex.Message); }
        }

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
                if (idx >= mono.Length - 1) outBuf[i] = mono[mono.Length - 1];
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
                if (cand == null) return;
                try { HostLog.Write("[SIPSorcery] local ICE cand (" + peerId + "): " + (cand.candidate ?? cand.ToString())); }
                catch { }
                Action<string, string> h = OnLocalIceCandidate;
                if (h != null) h(peerId, BuildCandidateJson(cand));
            };

            try
            {
                pc.oniceconnectionstatechange += (RTCIceConnectionState st) =>
                {
                    HostLog.Write("[SIPSorcery] ICE state [" + peerId + "]: " + st);
                };
            }
            catch (Exception ex) { HostLog.Write("[SIPSorcery] oniceconnectionstatechange unavailable: " + ex.Message); }

            try
            {
                pc.onicegatheringstatechange += (RTCIceGatheringState gs) =>
                {
                    HostLog.Write("[SIPSorcery] ICE gathering [" + peerId + "]: " + gs);
                };
            }
            catch (Exception ex) { HostLog.Write("[SIPSorcery] onicegatheringstatechange unavailable: " + ex.Message); }

            pc.onconnectionstatechange += (RTCPeerConnectionState st) =>
            {
                Action<string, string> h = OnConnectionStateChanged;
                if (h != null) h(peerId, MapState(st));
            };

            pc.OnRtpPacketReceived += (IPEndPoint endpoint, SDPMediaTypesEnum media, RTPPacket pkt) =>
            {
                if (media != SDPMediaTypesEnum.audio) return;
                if (pkt == null || pkt.Payload == null || pkt.Payload.Length == 0) return;
                e.RtpReceived++;
                if (!e.FirstRtpLogged)
                {
                    e.FirstRtpLogged = true;
                    HostLog.Write("[SIPSorcery] first inbound audio RTP from " + peerId
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
            catch (Exception ex) { HostLog.Write("[SIPSorcery] Opus decode failed for " + e.PeerId + ": " + ex.Message); return; }
            if (decoded <= 0) return;
            var frame = new short[decoded];
            Array.Copy(outPcm, frame, decoded);
            h(e.PeerId, frame, decoded, OpusSampleRate);
        }

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

        public void Dispose() { CloseAll(); }

        private static string BuildCandidateJson(RTCIceCandidate cand)
        {
            var jo = new JObject();
            // The browser's addIceCandidate requires the "candidate:" prefix on the
            // candidate attribute; SIPSorcery's .candidate omits it, which caused the
            // browser's "OperationError: Error processing ICE candidate".
            string c = cand.candidate != null ? cand.candidate : "";
            if (c.Length > 0 && !c.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase)) c = "candidate:" + c;
            jo["candidate"] = c;
            jo["sdpMid"] = cand.sdpMid != null ? (JToken)cand.sdpMid : JValue.CreateNull();
            jo["sdpMLineIndex"] = (int)cand.sdpMLineIndex;
            return jo.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
