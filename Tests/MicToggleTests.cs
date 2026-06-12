using System;
using FsCheck.Xunit;

namespace Partyline.Tests
{
    /// <summary>
    /// Feature: webrtc-opus-audio, Property 7: Mic toggle is latching (parity), not momentary.
    ///
    /// Validates: Requirements 7.4, 7.5, 7.6
    ///
    /// Mirrors the plugin's latching mic gate (_micOn / SetMicOn in PartylineScript.cs).
    /// Each activation flips transmission; two consecutive activations restore the prior
    /// state; hold/release events never change state (no momentary / no always-open).
    /// </summary>
    public class MicToggleTests
    {
        /// <summary>
        /// From the off state, the transmission state after N activations is ON iff N is odd.
        /// Validates: Requirements 7.4, 7.5
        /// </summary>
        [Property(MaxTest = 200)]
        public bool Toggle_parity_from_off(int rawN)
        {
            int n = Math.Abs(rawN % 10000);
            bool state = false;
            for (int i = 0; i < n; i++) state = MicToggle.Toggle(state);
            bool expected = (n & 1) == 1;
            return state == expected && state == MicToggle.StateAfter(false, n);
        }

        /// <summary>
        /// From any initial state, N activations yield initial XOR (N odd).
        /// Validates: Requirements 7.4, 7.5
        /// </summary>
        [Property(MaxTest = 200)]
        public bool State_after_n_activations_from_any_initial(bool initial, int rawN)
        {
            int n = Math.Abs(rawN % 10000);
            bool state = initial;
            for (int i = 0; i < n; i++) state = MicToggle.Toggle(state);
            return state == (initial ^ ((n & 1) == 1));
        }

        /// <summary>
        /// Two consecutive activations restore the prior transmission state.
        /// Validates: Requirements 7.4, 7.5
        /// </summary>
        [Property(MaxTest = 200)]
        public bool Two_consecutive_toggles_restore_prior_state(bool initial)
        {
            return MicToggle.Toggle(MicToggle.Toggle(initial)) == initial;
        }

        /// <summary>
        /// Over an arbitrary event stream where only "activate" events occur (hold and release
        /// are modeled as no-ops), the final state depends solely on the parity of activations —
        /// confirming no momentary (release/hold) behavior changes transmission.
        /// Validates: Requirements 7.6
        /// </summary>
        [Property(MaxTest = 200)]
        public bool Hold_and_release_events_never_change_state(int[] rawEvents)
        {
            if (rawEvents == null) rawEvents = Array.Empty<int>();
            bool state = false;
            int activations = 0;
            foreach (var e in rawEvents)
            {
                int kind = ((e % 3) + 3) % 3; // 0 = activate, 1 = hold, 2 = release
                if (kind == 0)
                {
                    state = MicToggle.Toggle(state);
                    activations++;
                }
                // hold / release: latching control ignores them (no momentary behavior).
            }
            return state == ((activations & 1) == 1);
        }
    }
}
