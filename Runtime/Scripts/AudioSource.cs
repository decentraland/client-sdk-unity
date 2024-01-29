using System;
using UnityEngine;
using LiveKit.Proto;
using LiveKit.Internal;
using System.Threading;
using LiveKit.Internal.FFIClients.Requests;

namespace LiveKit
{
    public class RtcAudioSource
    {
        private AudioSource _audioSource;
        private AudioFilter _audioFilter;

        private FfiHandle _handle;

        internal FfiHandle Handle => _handle;

        protected AudioSourceInfo _info;

        // Used on the AudioThread
        private AudioFrame _frame;
        private Thread _readAudioThread;
        private object _lock = new object();
        private float[] _data;
        private int _channels;
        private int _sampleRate;
        private volatile bool _pending = false;

        public RtcAudioSource(AudioSource source, AudioFilter audioFilter)
        {
            try
            {
                using var request = FFIBridge.Instance.NewRequest<NewAudioSourceRequest>();
                var newAudioSource = request.request;
                newAudioSource.Type = AudioSourceType.AudioSourceNative;
                newAudioSource.NumChannels = 2;
                newAudioSource.SampleRate = 48000;

                using var response = request.Send();
                FfiResponse res = response;
                _info = res.NewAudioSource.Source.Info;
                //TODO pooling handles
                _handle = new FfiHandle((IntPtr)res.NewAudioSource.Source.Handle.Id);
                UpdateSource(source, audioFilter);
            } catch(Exception e)
            {
                Debug.LogException(e);
            }
        }

        public void Start()
        {
            Stop();
            _readAudioThread = new Thread(Update);
            _readAudioThread.Start();

            _audioFilter.AudioRead += OnAudioRead;
            _audioSource.Play();
        }

        public void Stop()
        {
            _readAudioThread?.Abort();
            if(_audioFilter) _audioFilter.AudioRead -= OnAudioRead;
            if(_audioSource) _audioSource.Stop();
        }

        private void Update()
        {
            while (true)
            {
                Thread.Sleep(Constants.TASK_DELAY);
                if (_pending)
                {
                    ReadAudio();
                }
            }
        }

        private void ReadAudio()
        {
            _pending = false;
            lock (_lock)
            {
                var samplesPerChannel = _data.Length / _channels;
                if (_frame == null
                    || _frame.NumChannels != _channels
                    || _frame.SampleRate != _sampleRate
                    || _frame.SamplesPerChannel != samplesPerChannel)
                {
                    _frame = new AudioFrame(_sampleRate, _channels, samplesPerChannel);
                }

                try
                {
                    static short FloatToS16(float v)
                    {
                        v *= 32768f;
                        v = Math.Min(v, 32767f);
                        v = Math.Max(v, -32768f);
                        return (short)(v + Math.Sign(v) * 0.5f);
                    }

                    unsafe
                    {
                        var frameData = new Span<short>(_frame.Data.ToPointer(), _frame.Length / sizeof(short));
                        for (int i = 0; i < _data.Length; i++)
                        {
                            frameData[i] = FloatToS16(_data[i]); 
                        }

                        // Don't play the audio locally
                        Array.Clear(_data, 0, _data.Length);

                        using var request = FFIBridge.Instance.NewRequest<CaptureAudioFrameRequest>();
                        var pushFrame = request.request;
                        pushFrame.SourceHandle = (ulong)Handle.DangerousGetHandle();
                        pushFrame.Buffer = new AudioFrameBufferInfo
                        {
                            DataPtr = (ulong)_frame.Data,
                            NumChannels = _frame.NumChannels,
                            SampleRate = _frame.SampleRate,
                            SamplesPerChannel = _frame.SamplesPerChannel
                        };
                        //Debug.Log($"Num Channels {_frame.NumChannels} Sample {_frame.SampleRate} and {_frame.SamplesPerChannel}");
                        using var response = request.Send();
                    }
                }
                catch (Exception e)
                {
                    Utils.Error("Audio Framedata error: " + e.Message);
                }
            }
        }

        private void UpdateSource(AudioSource source, AudioFilter audioFilter)
        {
            _audioSource = source;
            _audioFilter = audioFilter;
        }

        private void OnAudioRead(float[] data, int channels, int sampleRate)
        {
            lock (_lock)
            {
                _data = data;
                _channels = channels;
                _sampleRate = sampleRate;
                _pending = true;
            }
        }

        

    }
}