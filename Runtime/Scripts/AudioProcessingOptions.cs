namespace LiveKit
{
    public struct AudioProcessingOptions
    {
        public bool EchoCancellation { get; set; }
        public bool NoiseSuppression { get; set; }
        public bool AutoGainControl { get; set; }
        public bool EnableQueue { get; set; }

        public AudioProcessingOptions(bool echoCancellation = true, bool noiseSuppression = true, bool autoGainControl = true, bool enableQueue = true)
        {
            EchoCancellation = echoCancellation;
            NoiseSuppression = noiseSuppression;
            AutoGainControl = autoGainControl;
            EnableQueue = enableQueue;
        }

        /// <summary>
        /// Preset optimized for high-quality audio - minimal processing to preserve fidelity
        /// </summary>
        public static AudioProcessingOptions HighQuality => new()
        {
            EchoCancellation = false,
            NoiseSuppression = false,
            AutoGainControl = false,
            EnableQueue = true
        };


        /// <summary>
        /// Default preset - enables all processing for clean audio
        /// </summary>
        public static AudioProcessingOptions Default => new(true, true, true, true);
    }
} 