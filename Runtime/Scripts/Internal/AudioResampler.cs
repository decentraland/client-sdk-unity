using System;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;
using LiveKit.Proto;

namespace LiveKit
{
    public class AudioResampler
    {
        internal FfiHandle Handle { get; }

        public AudioResampler()
        {
            using var request = FFIBridge.Instance.NewRequest<NewAudioResamplerRequest>();
            using var response = request.Send();
            FfiResponse res = response;
            Handle = new FfiHandle((IntPtr)res.NewAudioResampler.Resampler.Handle.Id);
        }

        public AudioFrame RemixAndResample(AudioFrame frame, uint numChannels, uint sampleRate)
        {
            using var request = FFIBridge.Instance.NewRequest<RemixAndResampleRequest>();
            var remix = request.request;
            remix.ResamplerHandle = (ulong)Handle.DangerousGetHandle();
            //TODO pooling inner buffers
            remix.Buffer = new AudioFrameBufferInfo() { DataPtr = (ulong)frame.Handle.DangerousGetHandle() };
            Utils.Debug(
                "TODO MindTrust: Most likely we want this second one and not use the frame's handler. Should be data. Based on AudioSource. Ref Python FFI -mg");
            //remix.Buffer = new AudioFrameBufferInfo() { DataPtr = (ulong) frame.Handle.DangerousGetHandle(), NumChannels = frame.NumChannels, SampleRate = frame.SampleRate/100, SamplesPerChannel = frame.SamplesPerChannel};
            remix.NumChannels = numChannels;
            remix.SampleRate = sampleRate;
            using var response = request.Send();
            FfiResponse res = response;
            var bufferInfo = res.RemixAndResample.Buffer;
            var handle = new FfiHandle((IntPtr)bufferInfo.Handle.Id);
            return new AudioFrame(handle, remix.Buffer);
        }
    }
}