using System;
using UnityEngine;
using LiveKit.Internal;
using LiveKit.Proto;
using System.Threading;
using LiveKit.Internal.FFIClients.Requests;
using System.Runtime.InteropServices;

namespace LiveKit
{
    public class AudioStream
    {
        //internal readonly FfiHandle Handle;
        private FfiHandle _handle;
        internal FfiHandle Handle
        {
            get { return _handle; }
        }
        private AudioSource _audioSource;
        private AudioFilter _audioFilter;
        private RingBuffer _buffer;
        private short[] _tempBuffer;
        private uint _numChannels;
        private uint _sampleRate;
        private AudioResampler _resampler = new AudioResampler();
        private object _lock = new object();


        private Thread? _readAudioThread; 
        private bool _pending = false;

      //  private float[] _data;
        private int _channels;
        private int _pendingSampleRate;
        private bool _playing = false;

        public AudioStream(IAudioTrack audioTrack, AudioSource source)
        {
            if (!audioTrack.Room.TryGetTarget(out var room))
                throw new InvalidOperationException("audiotrack's room is invalid");

            if (!audioTrack.Participant.TryGetTarget(out var participant))
                throw new InvalidOperationException("audiotrack's participant is invalid");
             
            using var request = FFIBridge.Instance.NewRequest<NewAudioStreamRequest>();
            var newAudioStream = request.request;
            newAudioStream.TrackHandle = (ulong)audioTrack.Handle.DangerousGetHandle();
            newAudioStream.Type = AudioStreamType.AudioStreamNative;
            using var response = request.Send();
            FfiResponse res = response;
            var streamInfo = res.NewAudioStream.Stream;

            _handle = new FfiHandle((IntPtr)streamInfo.Handle.Id);

            UpdateSource(source);
        }

        private void UpdateSource(AudioSource source)
        {
            _audioSource = source;
            _audioFilter = source.gameObject.AddComponent<AudioFilter>();
        }

        public void Start()
        {
            Stop();
            _playing = true;
            //_readAudioThread = new Thread(Update);
            //_readAudioThread.Start();

            _audioFilter.AudioRead += OnAudioRead;
            _audioSource.Play();

            FfiClient.Instance.AudioStreamEventReceived += OnAudioStreamEvent;
        }

        public void Stop()
        {
            _playing = false;
            _readAudioThread?.Abort();

            if(_audioFilter)
                _audioFilter.AudioRead -= OnAudioRead;
            if (_audioSource) _audioSource.Stop();

            if(FfiClient.Instance !=null)
                FfiClient.Instance.AudioStreamEventReceived -= OnAudioStreamEvent;
        }

        // Called on Unity audio thread
        private void OnAudioRead(float[] data, int channels, int sampleRate)
        {
            lock (_lock)
            {
               // _data = data;
                _channels = channels;
                _pendingSampleRate = sampleRate;
                _pending = true;
                //if (data != null && data.Length > 1)
                //{
                //    if(data[1]!=0) Debug.LogError("Sec: " + data[1]);
                //}
        //    }
        //}

        //private void Update()
        //{
        //    while (true)
        //    {
        //        Thread.Sleep(Constants.TASK_DELAY);

        //        if (_pending)
        //        { 
        //            lock (_lock)
        //            {
                        _pending = false;
                        if (_buffer == null || _channels != _numChannels || _pendingSampleRate != _sampleRate || _data.Length != _tempBuffer.Length)
                        {
                            int size = (int)(_channels * _pendingSampleRate * .2f);
                            _buffer = new RingBuffer(size * sizeof(short));
                            _tempBuffer = new short[data.Length];
                            _numChannels = (uint)_channels;
                            _sampleRate = (uint)_pendingSampleRate;
                        }


                        static float S16ToFloat(short v)
                        {
                            return v / 32768f;
                        }

                        // "Send" the data to Unity
                        var temp = MemoryMarshal.Cast<short, byte>(_tempBuffer.AsSpan().Slice(0, data.Length));
                        int read = _buffer.Read(temp);
                        Array.Clear(data, 0, data.Length);
                        for (int i = 0; i < data.Length; i++)
                        {
                    data[i] = S16ToFloat(_tempBuffer[i]);
                            //if (i == 1 && _data[1] != 0) Debug.LogError("Buffer Read: " + _data[1]);
                            //if (i == 1 && temp[1] != 0) Debug.LogError("Buffer Temp: " + temp[1]);
                            //if (i == 1 && _tempBuffer[1] != 0) Debug.LogError("Buffer Temp B: " + _tempBuffer[1]);
                        }

                       // _audioSource.clip.SetData(_data, 0);
                    } 
                }
        //    }
        //}


        // Called on the MainThread (See FfiClient)
        private void OnAudioStreamEvent(AudioStreamEvent e)
        {
            if (!_playing) return;

            if (e.StreamHandle != (ulong)Handle.DangerousGetHandle())
                return;

            if (e.MessageCase != AudioStreamEvent.MessageOneofCase.FrameReceived)
                return;

            var info = e.FrameReceived.Frame.Info;

            Debug.Log("Stream Event Start "+e.FrameReceived.Frame.Info.ToString() +" : "+e.FrameReceived.Frame.Info.SamplesPerChannel);
            var handle = new FfiHandle((IntPtr)e.FrameReceived.Frame.Handle.Id);
            var frame = new AudioFrame(handle, info);

            lock (_lock)
            {
                if (_numChannels == 0)
                    return;


                unsafe
                {
                    var uFrame = _resampler.RemixAndResample(frame, _numChannels, _sampleRate);
                    var data = new Span<byte>(uFrame.Data.ToPointer(), uFrame.Length);
                    _buffer?.Write(data);
                }
            }
             
        }
    }
}
