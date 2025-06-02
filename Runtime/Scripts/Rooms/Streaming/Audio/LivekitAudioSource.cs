using System;
using UnityEngine;

namespace LiveKit.Rooms.Streaming.Audio
{
    public class LivekitAudioSource : MonoBehaviour
    {
        private static ulong _counter;

        private AudioSource _audioSource = null!;
        private int _sampleRate;
        private WeakReference<IAudioStream>? _stream;

        public static LivekitAudioSource New(bool explicitName = false)
        {
            var gm = new GameObject();
            var audioSource = gm.AddComponent<AudioSource>();
            var source = gm.AddComponent<LivekitAudioSource>();
            source._audioSource = audioSource;
            if (explicitName) source.name = $"{nameof(LivekitAudioSource)}_{_counter++}";
            return source;
        }

        public void Construct(WeakReference<IAudioStream> audioStream)
        {
            _stream = audioStream;
        }

        public void Free()
        {
            _stream = null;
        }

        public void Play()
        {
            _audioSource.Play();
        }

        public void Stop()
        {
            _audioSource.Stop();
        }

        public void SetVolume(float target)
        {
            _audioSource.volume = target;
        }

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
            if (_stream != null && _stream.TryGetTarget(out var s))
            {
                s?.ReadAudio(data, channels, _sampleRate);
            }
        }
    }
}