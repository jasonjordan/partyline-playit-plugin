namespace Partyline.Tests
{
    /// <summary>
    /// Host-independent MIRROR of the plugin's latching mic gate (<c>_micOn</c> /
    /// <c>SetMicOn</c> in PartylineScript.cs and the task 5.6 UI button). The plugin's
    /// real control is a single latching button: each activation flips <c>_micOn</c>;
    /// there is no momentary (hold/release) or permanently-open behavior. This pure
    /// parity model captures exactly that semantics for property testing.
    /// </summary>
    public static class MicToggle
    {
        /// <summary>A single activation flips the transmission state (latching).</summary>
        public static bool Toggle(bool state) => !state;

        /// <summary>
        /// Closed-form latching result: state after <paramref name="activations"/> activations
        /// from <paramref name="initial"/> is <c>initial XOR (activations is odd)</c>.
        /// </summary>
        public static bool StateAfter(bool initial, int activations)
            => initial ^ ((activations & 1) == 1);
    }
}
