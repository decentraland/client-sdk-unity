using System;
using System.Threading;
using System.Threading.Tasks;
using LiveKit.Internal;
using LiveKit.Proto;
using UnityEngine;

namespace LiveKit
{
    public class AudioResampler
    {
        private FfiHandle _handle;
        internal FfiHandle Handle
        {
            get { return _handle; }
        }

        public AudioResampler()
        {
            var newResampler = new NewAudioResamplerRequest();
            var request = new FfiRequest();
            request.NewAudioResampler = newResampler;
            var res = FfiClient.SendRequest(request);
            _handle = new FfiHandle((IntPtr)res.NewAudioResampler.Resampler.Handle.Id);
        }

        public AudioFrame RemixAndResample(AudioFrame frame, uint numChannels, uint sampleRate) {
            Debug.Log("Remix And Resample");
            var remix = new RemixAndResampleRequest();
            remix.ResamplerHandle = (ulong) Handle.DangerousGetHandle();
            remix.Buffer = new AudioFrameBufferInfo() { 
                DataPtr = (ulong)frame.Handle.DangerousGetHandle(), 
                NumChannels = frame.NumChannels, 
                SampleRate = frame.SampleRate, 
                SamplesPerChannel = frame.SamplesPerChannel 
            };
            remix.NumChannels = numChannels;
            remix.SampleRate = sampleRate;

            var request = new FfiRequest();
            request.RemixAndResample = remix;

            var res = FfiClient.SendRequest(request);
            var bufferInfo = res.RemixAndResample.Buffer;
            var handle = new FfiHandle((IntPtr)bufferInfo.Handle.Id);
            return new AudioFrame(handle, remix.Buffer);
        }
    }
}
