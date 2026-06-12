using System;
using FsCheck;
using FsCheck.Xunit;

namespace Partyline.Tests
{
    /// <summary>
    /// Feature: webrtc-opus-audio, Property 8: Audio bridge framing and Opus round-trip
    /// preserve structure.
    ///
    /// Validates: Requirements 5.2, 5.3, 5.5
    ///
    /// These property tests exercise the PURE framing/resampling math mirrored from
    /// PartylineScript.cs (Tests/AudioBridge.cs). The Opus encode/decode true round-trip
    /// requires the Concentus runtime and is covered by the task 5.10 integration test
    /// (Tests/BindingOpusRoundTripTests.cs); here we lock the framing/structure invariants.
    /// </summary>
    public class AudioBridgeFramingTests
    {
        private static readonly int[] Rates =
            { 8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000 };

        private static short[] MakePcm(int n)
        {
            var pcm = new short[n];
            for (int i = 0; i < n; i++)
                pcm[i] = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / 48000.0) * 12000.0);
            return pcm;
        }

        /// <summary>
        /// Framing a captured mono PCM buffer into 20 ms frames preserves the mono channel
        /// layout (one sample slot per frame index) and the expected per-frame sample count
        /// (exactly 960), with full-frame count = floor(N / 960).
        /// Validates: Requirements 5.5
        /// </summary>
        [Property(MaxTest = 200)]
        public bool Framing_preserves_mono_layout_and_per_frame_sample_count(int rawN)
        {
            int n = Math.Abs(rawN % 200000); // 0..199999 captured mono samples
            var pcm = MakePcm(n);

            var frames = AudioBridge.SplitIntoFrames(pcm);

            int expectedFullFrames = n / AudioBridge.OutputFrameSamples;
            if (frames.Count != expectedFullFrames) return false;

            // Every emitted frame is exactly 960 mono samples (no interleaving / no short frames).
            foreach (var frame in frames)
                if (frame.Length != AudioBridge.OutputFrameSamples) return false;

            // Mono channel layout is the bridge invariant.
            return AudioBridge.ChannelCount == 1;
        }

        /// <summary>
        /// Nearest-sample resample produces sample count = floor(in * dst / src) (min 1) and
        /// stays in range: every output sample is a copied input sample (no interpolation
        /// artifacts), so output never exceeds the input's value range.
        /// Validates: Requirements 5.2, 5.3
        /// </summary>
        [Property(MaxTest = 200)]
        public bool NearestResample_sample_count_matches_formula_and_stays_in_range(
            int rawN, int srcSel, int dstSel)
        {
            int n = 1 + Math.Abs(rawN % 96000);
            int src = Rates[Math.Abs(srcSel) % Rates.Length];
            int dst = Rates[Math.Abs(dstSel) % Rates.Length];

            var pcm = MakePcm(n);
            byte[] outBytes = AudioBridge.ResampleToMixerRate(pcm, n, src, dst);
            short[] outSamples = AudioBridge.BytesToShorts(outBytes);

            int expected = (int)((long)n * dst / src);
            if (expected < 1) expected = 1;
            if (outSamples.Length != expected) return false;

            // In-range: each output equals the nearest source sample at the mirrored index.
            for (int i = 0; i < outSamples.Length; i++)
            {
                int srcIdx = (int)((long)i * src / dst);
                if (srcIdx >= n) srcIdx = n - 1;
                if (outSamples[i] != pcm[srcIdx]) return false;
            }
            return true;
        }

        /// <summary>
        /// Round-trip rate conversion (src -> 48k -> src) preserves length within rounding
        /// tolerance. With src in [8k, 48k] the two integer floors lose at most 2 samples and
        /// never grow the buffer.
        /// Validates: Requirements 5.2, 5.5
        /// </summary>
        [Property(MaxTest = 200)]
        public bool RoundTrip_rate_conversion_preserves_length_within_tolerance(int rawN, int srcSel)
        {
            int n = 1 + Math.Abs(rawN % 96000);
            int src = Rates[Math.Abs(srcSel) % Rates.Length]; // all <= 48000

            var pcm = MakePcm(n);
            byte[] at48 = AudioBridge.ResampleToMixerRate(pcm, n, src, AudioBridge.TransportRate);
            short[] mid = AudioBridge.BytesToShorts(at48);
            byte[] back = AudioBridge.ResampleToMixerRate(mid, mid.Length, AudioBridge.TransportRate, src);

            int finalN = back.Length / 2;
            // Never grows; loses at most 2 samples to the double floor.
            return finalN <= n && finalN >= n - 2;
        }

        /// <summary>
        /// The bridge always materializes a fixed 960-sample mono frame at 48 kHz from a
        /// captured buffer regardless of capture rate (nearest-sample), preserving the
        /// per-frame sample count and mono layout demanded by the Opus transport.
        /// Validates: Requirements 5.2, 5.5
        /// </summary>
        [Property(MaxTest = 200)]
        public bool ReadMainMixFrame_always_yields_960_mono_samples(int rawN, int srcSel)
        {
            int srcFreq = Rates[Math.Abs(srcSel) % Rates.Length];
            int needed = AudioBridge.InputSamplesNeeded(srcFreq);
            // Provide a realistic-sized capture buffer (around what the pump reads per tick).
            int n = 1 + Math.Abs(rawN % (needed + 1));
            var src = MakePcm(n);

            short[] frame = AudioBridge.ReadMainMixFrame(src, srcFreq);
            return frame.Length == AudioBridge.OutputFrameSamples;
        }
    }
}
