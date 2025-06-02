using UnityEngine;

namespace LiveKit
{
    // from https://github.com/Unity-Technologies/com.unity.webrtc
    public class AudioFilter : MonoBehaviour, IAudioFilter
    {
        private int _sampleRate;

        /// <summary>
        /// Gets whether this audio filter is valid and can be used
        /// </summary>
        public bool IsValid => this != null;
        
        // Event is called from the Unity audio thread
        public event IAudioFilter.OnAudioDelegate? AudioRead;

        private void OnEnable()
        {
            OnAudioConfigurationChanged(false);
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
        }

        private void OnDisable()
        {
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
        }

        private void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            _sampleRate = AudioSettings.outputSampleRate;
        }

        // Called by Unity on the Audio thread
        private void OnAudioFilterRead(float[] data, int channels)
        {
            AudioRead?.Invoke(data, channels, _sampleRate);
        }

        private void OnDestroy()
        {
            AudioRead = null!;
        }
    }
}
