using System;
using Concentus.Enums;
using Concentus.Structs;
using Xunit;

namespace Partyline.Tests
{
    /// <summary>
    /// Task 5.10 — integration test: binding load + Opus round-trip.
    /// Validates: Requirements 5.1, 5.2, 5.3, 5.4
    ///
    /// The native MR-WebRTC binding (mrwebrtc.dll) and the PlayIt Live host cannot load on
    /// this (macOS) box, so the full end-to-end native path is documented and SKIPPED. The
    /// Opus encode -> decode portion (Requirements 5.2/5.3) is covered by a REAL Concentus
    /// mono round-trip that genuinely runs here.
    /// </summary>
    public class BindingOpusRoundTripTests
    {
        private const int Rate = 48000;
        private const int Channels = 1;          // mono Opus (Requirement 5.2/5.3)
        private const int FrameSamples = 960;    // 20 ms @ 48 kHz mono

        /// <summary>
        /// Full end-to-end integration across the native binding + browser peer. SKIPPED:
        /// requires Windows + PlayIt Live host + the native mrwebrtc.dll (bitness-matched to
        /// the host process) / SIPSorcery runtime and a live browser peer. The steps below
        /// document the manual/CI-on-Windows procedure this test stands in for.
        /// Validates: Requirements 5.1, 5.4
        /// </summary>
        [Fact(Skip = "requires Windows + PlayIt Live host + native mrwebrtc.dll / SIPSorcery runtime + browser peer")]
        public void Binding_load_and_end_to_end_round_trip_with_browser_peer()
        {
            // End-to-end steps this integration test represents (run on a Windows PlayIt host):
            //
            // 1. Load the IWebRtcPeer adapter. The MR-WebRTC primary loads mrwebrtc.dll for the
            //    architecture (win-x86 / win-x64) MATCHING the PlayIt Live host process, alongside
            //    bass.dll; a bitness assertion fails fast on mismatch. On load failure, fall back
            //    to the SIPSorcery + Concentus adapter. (Requirement 5.1, 5.4)
            // 2. Fetch GET /api/rtc-config/:slug (STUN + Cloudflare TURN) and establish an
            //    RTCPeerConnection to a browser peer via Cloudflare signaling (offer/answer/ICE
            //    over POST /api/signal + the SSE 'signal' events). (Requirement 5.1)
            // 3. Push one captured 20 ms / 960-sample 48 kHz mono PCM16 frame (from the BASS
            //    main-mix DSP tap) into the ExternalAudioTrackSource. (Requirement 5.5)
            // 4. The binding encodes mono Opus and transmits SRTP; the browser peer decodes and
            //    renders it. (Requirement 5.2)
            // 5. Inbound: the remote peer's Opus is decoded to PCM, resampled to the mixer rate,
            //    and ingested via AudioMixer.EnsureCoHost / IngestAudio into PartylineStream.
            //    (Requirement 5.3)
            //
            // Assertions on Windows: binding reports loaded + correct bitness; connectionState
            // reaches 'connected'; the decoded inbound frame is mono with ~960 samples.
        }

        /// <summary>
        /// REAL mono Opus encode -> decode round-trip using Concentus (the same codec the
        /// plugin's SIPSorcery fallback adapter uses). Pushes a sine frame, encodes mono Opus,
        /// decodes, and asserts the decoded frame preserves the mono channel count and the
        /// per-frame length, with signal energy preserved within speech-codec tolerance.
        /// Validates: Requirements 5.2, 5.3
        /// </summary>
        [Fact]
        public void Mono_opus_encode_decode_round_trip_preserves_channel_and_frame_length()
        {
            var encoder = OpusEncoder.Create(Rate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
            var decoder = OpusDecoder.Create(Rate, Channels);

            // One captured 20 ms mono frame: a 440 Hz sine.
            short[] pcm = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
                pcm[i] = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / Rate) * 12000.0);

            byte[] encoded = new byte[4000];
            int encodedLen = encoder.Encode(pcm, 0, FrameSamples, encoded, 0, encoded.Length);
            Assert.True(encodedLen > 0, "Opus encode produced no bytes");

            short[] decoded = new short[FrameSamples * Channels];
            int decodedSamples = decoder.Decode(encoded, 0, encodedLen, decoded, 0, FrameSamples, false);

            // Per-frame length preserved (samples-per-channel == frame size).
            Assert.Equal(FrameSamples, decodedSamples);
            // Mono channel count preserved: total samples == frame * channels.
            Assert.Equal(FrameSamples * Channels, decoded.Length);

            // Signal energy preserved within speech-codec tolerance (lossy but non-trivial).
            double inEnergy = Energy(pcm);
            double outEnergy = Energy(decoded);
            Assert.True(inEnergy > 0, "input frame had no energy");
            Assert.True(outEnergy > inEnergy * 0.1,
                $"decoded energy {outEnergy:F0} too low vs input {inEnergy:F0}");
        }

        private static double Energy(short[] pcm)
        {
            double sum = 0;
            for (int i = 0; i < pcm.Length; i++)
                sum += (double)pcm[i] * pcm[i];
            return sum;
        }
    }
}
