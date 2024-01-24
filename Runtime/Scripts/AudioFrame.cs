using System;
using LiveKit.Proto;
using LiveKit.Internal;

namespace LiveKit
{
    public class AudioFrame : IDisposable
    {
        private AudioFrameBufferInfo _info;
        private bool _disposed = false;
        private FfiHandle _handle;

        internal FfiHandle Handle => _handle;

        public uint NumChannels => _info.NumChannels;
        public uint SampleRate => _info.SampleRate;
        public uint SamplesPerChannel => _info.SamplesPerChannel;

        public IntPtr Data => (IntPtr)_info.DataPtr;
        public int Length => (int)(SamplesPerChannel * NumChannels * sizeof(short));

        internal AudioFrame(FfiHandle handle, AudioFrameBufferInfo info)
        {
            _handle = handle;
            _info = info;
        }

        internal AudioFrame(int sampleRate, int numChannels, int samplesPerChannel)
        {
            //TODO inner requests pooling
            var alloc = new AllocAudioBufferRequest();
            alloc.SampleRate = (uint)sampleRate;
            alloc.NumChannels = (uint)numChannels;
            alloc.SamplesPerChannel = (uint)samplesPerChannel;

            using var resWrap = FfiClient.Instance.SendRequest(
                request => request.AllocAudioBuffer = alloc,
                request => request.AllocAudioBuffer = null
            );
            FfiResponse res = resWrap;
            
            var bufferInfo = res.AllocAudioBuffer.Buffer.Info;
            _handle = new FfiHandle((IntPtr)res.AllocAudioBuffer.Buffer.Handle.Id);
            _info = bufferInfo;

            res.AllocAudioBuffer = null;
        }

        ~AudioFrame()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                Handle.Dispose();
                _disposed = true;
            }
        }
    }
}