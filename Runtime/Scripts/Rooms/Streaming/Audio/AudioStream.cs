using System;
using System.Runtime.InteropServices;
using UnityEngine;
using LiveKit.Internal;
using LiveKit.Proto;
using Livekit.Utils;

namespace LiveKit.Rooms.Streaming.Audio
{
    public class AudioStream : IAudioStream
    {
        private readonly IAudioStreams _audioStreams;
        private readonly IAudioRemixConveyor _audioRemixConveyor;
        private readonly FfiHandle _handle;
        private readonly AudioStreamInfo _info;
        private readonly object _lockObject = new();

        private Mutex<RingBuffer>? _buffer;
        private short[]? _tempBuffer;
        private uint _numChannels;
        private uint _sampleRate;
        private bool _disposed;

        public AudioStream(
            IAudioStreams audioStreams,
            OwnedAudioStream ownedAudioStream,
            IAudioRemixConveyor audioRemixConveyor
        )
        {
            _audioStreams = audioStreams;
            _audioRemixConveyor = audioRemixConveyor;
            _handle = IFfiHandleFactory.Default.NewFfiHandle(ownedAudioStream.Handle!.Id);
            _info = ownedAudioStream.Info!;
            FfiClient.Instance.AudioStreamEventReceived += OnAudioStreamEvent;
        }

        public void ReadAudio(float[] data, int channels, int sampleRate)
        {
            Debug.LogError($"AudioStream: ReadAudio called - data.Length={data.Length}, channels={channels}, sampleRate={sampleRate}");
            
            lock (_lockObject)
            {
                if (channels != _numChannels || sampleRate != _sampleRate || _tempBuffer == null || data.Length != _tempBuffer.Length)
                {
                    int size = (int)(channels * sampleRate * 0.1); // Reduced from 0.2 (200ms) to 0.05 (50ms) for lower latency
                    if (_buffer != null)
                    {
                        using var guard = _buffer.Lock();
                        guard.Value.Dispose();
                    }

                    _buffer = new Mutex<RingBuffer>(new RingBuffer(size * sizeof(short)));

                    _tempBuffer = new short[data.Length]; //todo avoid allocation of this buffer
                    _numChannels = (uint)channels;
                    _sampleRate = (uint)sampleRate;
                }

                static float S16ToFloat(short v)
                {
                    return v / 32768f;
                }

                // "Send" the data to Unity
                var temp = MemoryMarshal.Cast<short, byte>(_tempBuffer!.AsSpan().Slice(0, data.Length));

                {
                    using var guard = _buffer!.Lock();
                    int read = guard.Value.Read(temp);
                }

                Array.Clear(data, 0, data.Length);
                for (int i = 0; i < data.Length; i++) data[i] = S16ToFloat(_tempBuffer[i]);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            _handle.Dispose();
            if (_buffer != null)
            {
                using var guard = _buffer.Lock();
                guard.Value.Dispose();
            }

            FfiClient.Instance.AudioStreamEventReceived -= OnAudioStreamEvent;
            _audioStreams.Release(this);
        }

        private void OnAudioStreamEvent(AudioStreamEvent @event)
        {
            if (@event.StreamHandle != (ulong)_handle.DangerousGetHandle())
                return;

            if (@event.MessageCase != AudioStreamEvent.MessageOneofCase.FrameReceived)
                return;

            if (_numChannels == 0)
                return;

            if (_buffer == null)
            {
                Debug.LogError("Invalid case, buffer is not set yet");
                // prevent leak
                var tempHandle = IFfiHandleFactory.Default.NewFfiHandle(@event.FrameReceived.Frame.Handle.Id);
                tempHandle.Dispose();
                return;
            }

            var frame = new OwnedAudioFrame(@event.FrameReceived.Frame);
            Debug.LogError($"AudioStream: About to call Process - frame: {frame.numChannels}ch@{frame.sampleRate}Hz, target: {_numChannels}ch@{_sampleRate}Hz");
            _audioRemixConveyor.Process(frame, _buffer, _numChannels, _sampleRate);
        }
    }
}