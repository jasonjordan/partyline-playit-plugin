using System;
using System.Collections.Generic;

namespace Partyline.Tests
{
    /// <summary>
    /// Host-independent MIRROR of the pure audio-bridge math in
    /// <c>playit-plugin/PartylineScript.cs</c> (<c>TryReadMainMixFrame</c> and
    /// <c>ResampleToMixerRate</c>). The plugin assembly is net48 + Windows/PlayIt-host
    /// bound and cannot build off-Windows, so these functions are reproduced verbatim
    /// here (same integer floor / nearest-sample arithmetic) to LOCK the plugin's
    /// behavior under property tests. Keep this in sync with PartylineScript.cs.
    /// </summary>
    public static class AudioBridge
    {
        /// <summary>20 ms @ 48 kHz mono = 960 samples (the Opus frame the bridge produces).</summary>
        public const int OutputFrameSamples = 960;

        /// <summary>The fixed WebRTC/Opus transport sample rate the bridge targets.</summary>
        public const int TransportRate = 48000;

        /// <summary>The bridge transports a single (mono) channel end to end.</summary>
        public const int ChannelCount = 1;

        /// <summary>
        /// Mirror of the source-sample count <c>TryReadMainMixFrame</c> requests to produce
        /// one 960-sample 48 kHz frame:
        /// <c>inputSamplesNeeded = (int)((long)960 * srcFreq / 48000)</c>, floored, min 1.
        /// </summary>
        public static int InputSamplesNeeded(int srcFreq)
        {
            int n = (int)((long)OutputFrameSamples * srcFreq / TransportRate);
            if (n < 1) n = 1;
            return n;
        }

        /// <summary>
        /// Mirror of the nearest-sample resample inside <c>TryReadMainMixFrame</c>: maps a
        /// mono PCM16 source buffer (captured at <paramref name="srcFreq"/>) into a fixed
        /// 960-sample 48 kHz mono frame using <c>srcIdx = (int)((long)i * srcFreq / 48000)</c>
        /// clamped to the last available source sample.
        /// </summary>
        public static short[] ReadMainMixFrame(short[] src, int srcFreq)
        {
            var frame = new short[OutputFrameSamples];
            int inputSamples = src == null ? 0 : src.Length;
            if (inputSamples <= 0)
            {
                // The real method returns false ("no audio this tick"); the pure mirror
                // returns a zeroed (silent) mono frame so callers still see 960 samples.
                return frame;
            }
            for (int i = 0; i < OutputFrameSamples; i++)
            {
                int srcIdx = (int)((long)i * srcFreq / TransportRate);
                if (srcIdx >= inputSamples) srcIdx = inputSamples - 1;
                frame[i] = src[srcIdx];
            }
            return frame;
        }

        /// <summary>
        /// Verbatim mirror of <c>PartylineScript.ResampleToMixerRate</c>: nearest-sample
        /// resample of mono PCM16 from <paramref name="srcRate"/> to <paramref name="dstRate"/>,
        /// returning little-endian PCM16 bytes. NOTE: output sample count uses INTEGER FLOOR
        /// (<c>(int)((long)inputSamples * dstRate / srcRate)</c>), not rounding — the test
        /// locks the actual plugin arithmetic.
        /// </summary>
        public static byte[] ResampleToMixerRate(short[] pcm, int sampleCount, int srcRate, int dstRate)
        {
            if (pcm == null || sampleCount <= 0 || srcRate <= 0 || dstRate <= 0)
                return new byte[0];

            int inputSamples = sampleCount <= pcm.Length ? sampleCount : pcm.Length;
            if (inputSamples <= 0) return new byte[0];

            int outputSamples = (int)((long)inputSamples * dstRate / srcRate);
            if (outputSamples < 1) outputSamples = 1;

            byte[] outBytes = new byte[outputSamples * 2];
            for (int i = 0; i < outputSamples; i++)
            {
                int srcIdx = (int)((long)i * srcRate / dstRate);
                if (srcIdx >= inputSamples) srcIdx = inputSamples - 1;
                short s = pcm[srcIdx];
                outBytes[i * 2] = (byte)(s & 0xFF);
                outBytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            return outBytes;
        }

        /// <summary>
        /// Splits a mono PCM16 buffer into whole 20 ms (960-sample) frames. Models the
        /// outbound framing the pump performs (the pump reads one 960-sample frame per tick).
        /// Returns one short[960] per full frame; the trailing partial frame is not emitted.
        /// </summary>
        public static List<short[]> SplitIntoFrames(short[] mono)
        {
            var frames = new List<short[]>();
            if (mono == null) return frames;
            int full = mono.Length / OutputFrameSamples;
            for (int f = 0; f < full; f++)
            {
                var frame = new short[OutputFrameSamples];
                Array.Copy(mono, f * OutputFrameSamples, frame, 0, OutputFrameSamples);
                frames.Add(frame);
            }
            return frames;
        }

        /// <summary>Helper: decode little-endian PCM16 bytes back into a short[].</summary>
        public static short[] BytesToShorts(byte[] bytes)
        {
            if (bytes == null) return new short[0];
            var s = new short[bytes.Length / 2];
            for (int i = 0; i < s.Length; i++)
                s[i] = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
            return s;
        }
    }
}
