using System;
using LiveKit.Internal.FFIClients.Requests;
using LiveKit.Proto;
using LiveKit.Rooms.Streaming.Audio;
using UnityEngine;

namespace LiveKit.Internal
{
    public readonly struct AudioResampler : IDisposable
    {
        private readonly FfiHandle handle;

        private AudioResampler(FfiHandle handle)
        {
            this.handle = handle;
        }

        public static AudioResampler New()
        {
            using var request = FFIBridge.Instance.NewRequest<NewAudioResamplerRequest>();
            using var response = request.Send();
            FfiResponse res = response;
            var handle = IFfiHandleFactory.Default.NewFfiHandle(res.NewAudioResampler!.Resampler!.Handle!.Id);
            return new AudioResampler(handle);
        }

        public void Dispose()
        {
            handle.Dispose();
        }

        public OwnedAudioFrame RemixAndResample(OwnedAudioFrame frame, uint numChannels, uint sampleRate)
        {
            using var request = FFIBridge.Instance.NewRequest<RemixAndResampleRequest>();
            using var audioFrameBufferInfo = request.TempResource<AudioFrameBufferInfo>();
            var remix = request.request;
            remix.ResamplerHandle = (ulong)handle.DangerousGetHandle();

            remix.Buffer = audioFrameBufferInfo;
            remix.Buffer.DataPtr = (ulong)frame.dataPtr;
            remix.Buffer.NumChannels = frame.numChannels;
            remix.Buffer.SampleRate = frame.sampleRate;
            remix.Buffer.SamplesPerChannel = frame.samplesPerChannel;

            remix.NumChannels = numChannels;
            remix.SampleRate = sampleRate;
            
            uint inputSamples = frame.samplesPerChannel * frame.numChannels;
            uint inputBytes = inputSamples * sizeof(short);
            float inputDurationMs = (float)frame.samplesPerChannel / frame.sampleRate * 1000f;
            
            Debug.LogError($"AudioResampler: Processing {inputBytes} bytes ({inputSamples} samples, {inputDurationMs:F1}ms audio) " +
                     $"from {frame.numChannels}ch@{frame.sampleRate}Hz to {numChannels}ch@{sampleRate}Hz");
            
            using var response = request.Send();
            FfiResponse res = response;
            var bufferInfo = res.RemixAndResample!.Buffer;
            return new OwnedAudioFrame(bufferInfo);
        }

        public class ThreadSafe : IDisposable
        {
            private readonly AudioResampler resampler = New();
            
            /// <summary>
            /// Takes ownership of the frame and is responsible for its disposal
            /// </summary>
            public OwnedAudioFrame RemixAndResample(OwnedAudioFrame frame, uint numChannels, uint sampleRate)
            {
                var overallStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var lockStopwatch = System.Diagnostics.Stopwatch.StartNew();
                
                using (frame)
                {
                    lock (this)
                    {
                        lockStopwatch.Stop();
                        var ffiStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        
                        try
                        {
                            var result = resampler.RemixAndResample(frame, numChannels, sampleRate);
                            ffiStopwatch.Stop();
                            overallStopwatch.Stop();
                            
                            // Log detailed timing if operation takes more than 5ms (should be <1ms for real-time audio)
                            if (overallStopwatch.ElapsedMilliseconds > 5)
                            {
                                uint inputSamples = frame.samplesPerChannel * frame.numChannels;
                                float inputDurationMs = (float)frame.samplesPerChannel / frame.sampleRate * 1000f;
                                
                                Debug.LogError($"AudioResampler.ThreadSafe: SLOW RESAMPLING - " +
                                          $"Total: {overallStopwatch.ElapsedMilliseconds}ms, " +
                                          $"Lock wait: {lockStopwatch.ElapsedMilliseconds}ms, " +
                                          $"FFI call: {ffiStopwatch.ElapsedMilliseconds}ms " +
                                          $"(Processing {inputDurationMs:F1}ms of audio, {inputSamples} samples, " +
                                          $"{frame.sampleRate}Hz→{sampleRate}Hz)");
                            }
                            
                            return result;
                        }
                        catch (Exception ex)
                        {
                            ffiStopwatch.Stop();
                            overallStopwatch.Stop();
                            Debug.LogError($"AudioResampler.ThreadSafe: FAILED after {overallStopwatch.ElapsedMilliseconds}ms " +
                                      $"(Lock: {lockStopwatch.ElapsedMilliseconds}ms, FFI: {ffiStopwatch.ElapsedMilliseconds}ms) - Error: {ex.Message}");
                            throw;
                        }
                    }
                }
            }

            public void Dispose()
            {
                resampler.Dispose();
            }
        }
    }
}