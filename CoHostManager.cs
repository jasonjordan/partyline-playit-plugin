using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json;
using PlayIt.PluginEngine;
using SIPSorcery.Net;

namespace Partyline
{
    /// <summary>
    /// Manages WebRTC co-host connections and injects their audio into PlayIt Live's main mix.
    /// </summary>
    public class CoHostManager : IDisposable
    {
        private readonly IAudioPipeline _pipeline;
        private readonly IAudioPipelineMixer _mainMix;
        private readonly ConcurrentDictionary<string, CoHostSession> _sessions = new();
        private readonly object _audioLock = new object();

        // Ring buffer for mixed co-host audio (48kHz stereo, 16-bit)
        private readonly short[] _mixBuffer = new short[48000 * 2]; // 1 second buffer
        private int _writePos;
        private int _readPos;

        public CoHostManager(IAudioPipeline pipeline)
        {
            _pipeline = pipeline;
            _mainMix = pipeline.GetMainMix();

            // Register our audio stream into PlayIt Live's mixer
            pipeline.RegisterSpecialAudioStream("partyline-cohost", new PartylineAudioStream(this));
        }

        /// <summary>
        /// Accept a WebRTC offer from a co-host and return the SDP answer.
        /// </summary>
        public string AcceptOffer(string offerJson)
        {
            var offerData = JsonConvert.DeserializeObject<Dictionary<string, string>>(offerJson);
            var sdpOffer = offerData["sdp"];
            var sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);

            var pc = new RTCPeerConnection(new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                }
            });

            // We want to receive audio from the co-host
            pc.addTrack(new MediaStreamTrack(SDPMediaTypesEnum.audio, false, new List<SDPAudioVideoMediaFormat>()));

            // Handle incoming audio from co-host
            pc.OnRtpPacketReceived += (ep, mediaType, rtpPacket) =>
            {
                if (mediaType == SDPMediaTypesEnum.audio)
                {
                    // Decode → PCM and write to mix buffer
                    CoHostSession session;
                    _sessions.TryGetValue(sessionId, out session);
                    session?.OnAudioReceived(rtpPacket.Payload);
                }
            };

            pc.onconnectionstatechange += (state) =>
            {
                if (state == RTCPeerConnectionState.disconnected ||
                    state == RTCPeerConnectionState.failed ||
                    state == RTCPeerConnectionState.closed)
                {
                    _sessions.TryRemove(sessionId, out _);
                }
            };

            var session2 = new CoHostSession(sessionId, pc, this);
            _sessions[sessionId] = session2;

            var offer = SDP.ParseSDPDescription(sdpOffer);
            pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = sdpOffer
            });

            var answer = pc.createAnswer(null);
            pc.setLocalDescription(answer);

            return JsonConvert.SerializeObject(new
            {
                sdp = answer.sdp,
                type = "answer",
                sessionId
            });
        }

        public void AddIceCandidate(string json)
        {
            var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            string sessionId = null;
            string candidate = null;
            string sdpMid = null;
            data.TryGetValue("sessionId", out sessionId);
            data.TryGetValue("candidate", out candidate);
            data.TryGetValue("sdpMid", out sdpMid);

            CoHostSession session;
            if (sessionId != null && _sessions.TryGetValue(sessionId, out session))
            {
                session.PeerConnection.addIceCandidate(new RTCIceCandidateInit
                {
                    candidate = candidate,
                    sdpMid = sdpMid
                });
            }
        }

        /// <summary>
        /// Called by PartylineAudioStream when PlayIt Live needs audio samples.
        /// Fills the buffer with mixed co-host audio.
        /// </summary>
        internal int FillBuffer(int length, IntPtr buffer)
        {
            lock (_audioLock)
            {
                int samplesNeeded = length / 2; // 16-bit samples
                int available = (_writePos - _readPos + _mixBuffer.Length) % _mixBuffer.Length;

                if (available < samplesNeeded)
                {
                    // Not enough data, fill with silence
                    Marshal.Copy(new short[samplesNeeded], 0, buffer, samplesNeeded);
                    return length;
                }

                var output = new short[samplesNeeded];
                for (int i = 0; i < samplesNeeded; i++)
                {
                    output[i] = _mixBuffer[_readPos];
                    _readPos = (_readPos + 1) % _mixBuffer.Length;
                }

                Marshal.Copy(output, 0, buffer, samplesNeeded);
                return length;
            }
        }

        /// <summary>
        /// Write decoded PCM samples from a co-host into the mix buffer.
        /// </summary>
        internal void WriteSamples(short[] samples)
        {
            lock (_audioLock)
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    // Mix (add) with existing content, clamping to prevent overflow
                    int mixed = _mixBuffer[_writePos] + samples[i];
                    _mixBuffer[_writePos] = (short)Math.Max(-32768, Math.Min(32767, mixed));
                    _writePos = (_writePos + 1) % _mixBuffer.Length;
                }
            }
        }

        public string GetStatus()
        {
            return JsonConvert.SerializeObject(new
            {
                connected = _sessions.Count,
                sessions = _sessions.Keys
            });
        }

        public int GetSessionCount()
        {
            return _sessions.Count;
        }

        public Dictionary<string, CoHostSession> GetSessions()
        {
            return new Dictionary<string, CoHostSession>(_sessions);
        }

        public void MuteAll()
        {
            foreach (var session in _sessions.Values)
            {
                session.SetMuted(true);
            }
        }

        public void KickSession(string sessionId)
        {
            if (_sessions.TryRemove(sessionId, out var session))
            {
                session.PeerConnection.close();
            }
        }

        public void DisconnectAll()
        {
            foreach (var kvp in _sessions)
            {
                kvp.Value.PeerConnection.close();
            }
            _sessions.Clear();
        }

        public void Dispose()
        {
            DisconnectAll();
        }
    }

    /// <summary>
    /// Implements PlayIt Live's ISpecialAudioStream to inject co-host audio into the mixer.
    /// </summary>
    internal class PartylineAudioStream : ISpecialAudioStream
    {
        private readonly CoHostManager _manager;

        public PartylineAudioStream(CoHostManager manager)
        {
            _manager = manager;
        }

        public IStreamContainer CreateStream(string sParams)
        {
            return new PartylineStreamContainer(_manager);
        }
    }

    internal class PartylineStreamContainer : IStreamContainer
    {
        private readonly CoHostManager _manager;

        public PartylineStreamContainer(CoHostManager manager)
        {
            _manager = manager;
        }

        public int NumberOfChannels => 2;
        public int SampleRate => 48000;

        public StreamFunc GetStreamFunc()
        {
            return _manager.FillBuffer;
        }

        public void Cleanup() { }
    }

    /// <summary>
    /// Represents a single co-host WebRTC session.
    /// </summary>
    public class CoHostSession
    {
        public string Id { get; }
        public RTCPeerConnection PeerConnection { get; }
        private readonly CoHostManager _manager;
        private readonly OpusDecoder _decoder;
        private float _volume = 1.0f;
        private bool _muted;
        private float _currentLevel;

        public CoHostSession(string id, RTCPeerConnection pc, CoHostManager manager)
        {
            Id = id;
            PeerConnection = pc;
            _manager = manager;
            _decoder = new OpusDecoder();
        }

        public void SetVolume(float volume)
        {
            _volume = Math.Max(0f, Math.Min(1f, volume));
        }

        public void SetMuted(bool muted)
        {
            _muted = muted;
        }

        public float GetLevel()
        {
            return _currentLevel;
        }

        public void OnAudioReceived(byte[] opusPayload)
        {
            try
            {
                var pcm = _decoder.Decode(opusPayload);
                if (pcm == null || pcm.Length == 0) return;

                // Calculate level for VU meter
                float maxSample = 0;
                for (int i = 0; i < pcm.Length; i++)
                {
                    float abs = Math.Abs(pcm[i] / 32768f);
                    if (abs > maxSample) maxSample = abs;
                }
                _currentLevel = maxSample;

                // Apply mute and volume
                if (_muted) return;

                if (_volume < 1.0f)
                {
                    for (int i = 0; i < pcm.Length; i++)
                    {
                        pcm[i] = (short)(pcm[i] * _volume);
                    }
                }

                _manager.WriteSamples(pcm);
            }
            catch { }
        }
    }

    /// <summary>
    /// Minimal Opus decoder wrapper using OpusDotNet (already bundled with PlayIt Live).
    /// </summary>
    internal class OpusDecoder
    {
        private IntPtr _decoder;
        private const int SAMPLE_RATE = 48000;
        private const int CHANNELS = 2;
        private const int FRAME_SIZE = 960; // 20ms at 48kHz

        public OpusDecoder()
        {
            int error;
            _decoder = OpusNative.opus_decoder_create(SAMPLE_RATE, CHANNELS, out error);
        }

        public short[] Decode(byte[] input)
        {
            var output = new short[FRAME_SIZE * CHANNELS];
            int samples = OpusNative.opus_decode(_decoder, input, input.Length, output, FRAME_SIZE, 0);
            if (samples > 0)
            {
                var result = new short[samples * CHANNELS];
                Array.Copy(output, result, result.Length);
                return result;
            }
            return null;
        }

        ~OpusDecoder()
        {
            if (_decoder != IntPtr.Zero)
            {
                OpusNative.opus_decoder_destroy(_decoder);
                _decoder = IntPtr.Zero;
            }
        }
    }

    internal static class OpusNative
    {
        // PlayIt Live already ships opus.dll — we use it directly
        [DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr opus_decoder_create(int Fs, int channels, out int error);

        [DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_decode(IntPtr st, byte[] data, int len,
            [Out] short[] pcm, int frame_size, int decode_fec);

        [DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
        public static extern void opus_decoder_destroy(IntPtr st);
    }
}
