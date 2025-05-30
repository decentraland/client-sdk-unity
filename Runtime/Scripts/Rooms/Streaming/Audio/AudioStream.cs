using System;
using LiveKit.Internal;
using LiveKit.Proto;
using System.Runtime.InteropServices;
using Livekit.Utils;
using UnityEngine;

namespace LiveKit.Rooms.Streaming.Audio
{
    public class AudioStream : IAudioStream
    {
        private readonly IAudioStreams audioStreams;
        private readonly IAudioRemixConveyor audioRemixConveyor;
        private readonly FfiHandle handle;
        private readonly AudioStreamInfo info;

        private IAudioFilter _audioFilter;
        private Mutex<RingBuffer>? _buffer;
        private short[] _tempBuffer;
        private uint _numChannels = 0;
        private uint _sampleRate;

        private readonly object _lock = new();

        private bool disposed;

        private uint _streamNativeChannels = 0;
        private uint _streamNativeSampleRate = 0;
        private bool _hasStreamFormat = false;

        public AudioStream(
            IAudioStreams audioStreams,
            OwnedAudioStream ownedAudioStream,
            IAudioRemixConveyor audioRemixConveyor
        )
        {
            this.audioStreams = audioStreams;
            this.audioRemixConveyor = audioRemixConveyor;
            handle = IFfiHandleFactory.Default.NewFfiHandle(ownedAudioStream.Handle!.Id);
            info = ownedAudioStream.Info!;
            FfiClient.Instance.AudioStreamEventReceived += OnAudioStreamEvent;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;

            handle.Dispose();
            if (_buffer != null)
            {
                using var guard = _buffer.Lock();
                guard.Value.Dispose();
            }

            FfiClient.Instance.AudioStreamEventReceived -= OnAudioStreamEvent;
            audioStreams.Release(this);
        }

        public void ReadAudio(float[] data, int channels, int sampleRate)
        {
            lock (_lock)
            {
                // Use stream's native format if available, otherwise fall back to Unity's format
                uint targetChannels = _hasStreamFormat ? _streamNativeChannels : (uint)channels;
                uint targetSampleRate = _hasStreamFormat ? _streamNativeSampleRate : (uint)sampleRate;

                if (targetChannels != _numChannels || targetSampleRate != _sampleRate || data.Length != _tempBuffer?.Length)
                {
                    int size = (int)(targetChannels * targetSampleRate * 0.07); // 70 ms buffer to reduce latency
                    if (_buffer != null)
                    {
                        using var guard = _buffer.Lock();
                        guard.Value.Dispose();
                    }

                    _buffer = new Mutex<RingBuffer>(new RingBuffer(size * sizeof(short)));

                    _tempBuffer = new short[data.Length]; //todo avoid allocation of this buffer
                    _numChannels = targetChannels;
                    _sampleRate = targetSampleRate;
                }

                static float S16ToFloat(short v)
                {
                    return v / 32768f;
                }

                // "Send" the data to Unity
                var temp = MemoryMarshal.Cast<short, byte>(_tempBuffer.AsSpan().Slice(0, data.Length));

                {
                    using var guard = _buffer!.Lock();
                    int read = guard.Value.Read(temp);
                }

                Array.Clear(data, 0, data.Length);
                for (int i = 0; i < data.Length; i++) data[i] = S16ToFloat(_tempBuffer[i]);
            }
        }

        private void OnAudioStreamEvent(AudioStreamEvent e)
        {
            if (e.StreamHandle != (ulong)handle.DangerousGetHandle())
                return;

            if (e.MessageCase != AudioStreamEvent.MessageOneofCase.FrameReceived)
                return;

            var frame = new OwnedAudioFrame(e.FrameReceived.Frame);
            
            // Capture the stream's native format from the first frame
            if (!_hasStreamFormat)
            {
                lock (_lock)
                {
                    if (!_hasStreamFormat)
                    {
                        _streamNativeChannels = frame.numChannels;
                        _streamNativeSampleRate = frame.sampleRate;
                        _hasStreamFormat = true;
                    }
                }
            }

            if (_numChannels == 0)
                return;

            if (_buffer == null)
            {
                Utils.Error("Invalid case, buffer is not set yet");
                // prevent leak
                frame.Dispose();
                return;
            }

            audioRemixConveyor.Process(frame, _buffer, _numChannels, _sampleRate);
        }
    }
}