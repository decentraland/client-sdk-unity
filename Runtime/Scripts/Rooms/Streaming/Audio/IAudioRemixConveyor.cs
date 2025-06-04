using System;
using LiveKit.Internal;
using Livekit.Utils;
using UnityEngine;

namespace LiveKit.Rooms.Streaming.Audio
{
    public interface IAudioRemixConveyor : IDisposable
    {
        void Process(OwnedAudioFrame ownedAudioFrame, Mutex<RingBuffer> outputBuffer, uint numChannels,
            uint sampleRate);

        class SameThreadAudioRemixConveyor : IAudioRemixConveyor
        {
            private AudioResampler.ThreadSafe resampler = new();
            private uint lastInputSampleRate = 0;
            private uint lastInputChannels = 0;

            public void Process(
                OwnedAudioFrame ownedAudioFrame,
                Mutex<RingBuffer> outputBuffer,
                uint numChannels,
                uint sampleRate
            )
            {
                // Check if INPUT format has changed - reset resampler if needed
                // Output format (Unity's) should remain constant, only input (microphone) changes
                if (ownedAudioFrame.sampleRate != lastInputSampleRate || 
                    ownedAudioFrame.numChannels != lastInputChannels)
                {
                    Debug.LogWarning($"AudioRemixConveyor: Input format change detected - resetting resampler " +
                                   $"(Input: {lastInputChannels}ch@{lastInputSampleRate}Hz -> {ownedAudioFrame.numChannels}ch@{ownedAudioFrame.sampleRate}Hz, " +
                                   $"Output remains: {numChannels}ch@{sampleRate}Hz)");
                    
                    // Dispose old resampler and create fresh one to clear any corrupted state
                    resampler?.Dispose();
                    resampler = new AudioResampler.ThreadSafe();
                    
                    // Update tracked INPUT formats only
                    lastInputSampleRate = ownedAudioFrame.sampleRate;
                    lastInputChannels = ownedAudioFrame.numChannels;
                }

                // Quick check: Skip processing empty or very quiet frames to avoid unnecessary resampling
                var audioSpan = ownedAudioFrame.AsSpan();
                bool isEmptyFrame = IsFrameSilentOrEmpty(audioSpan);
                
                if (isEmptyFrame)
                {
                    // For empty frames, just write silence at the target format without resampling
                    int targetSamples = (int)((audioSpan.Length / sizeof(short) / ownedAudioFrame.numChannels) * numChannels);
                    var silenceData = new byte[targetSamples * sizeof(short)];
                    
                    using var guard = outputBuffer.Lock();
                    guard.Value.Write(silenceData);
                    
                    Debug.Log($"AudioRemixConveyor: Skipped resampling for silent frame ({ownedAudioFrame.numChannels}ch@{ownedAudioFrame.sampleRate}Hz -> {numChannels}ch@{sampleRate}Hz)");
                    return;
                }

                // Optimization: Skip expensive resampling if formats already match
                if (ownedAudioFrame.numChannels == numChannels && ownedAudioFrame.sampleRate == sampleRate)
                {
                    // Direct copy - no resampling needed
                    Debug.LogError($"AudioRemixConveyor: Direct copy {ownedAudioFrame.numChannels}ch@{ownedAudioFrame.sampleRate}Hz -> {numChannels}ch@{sampleRate}Hz");
                    var data = ownedAudioFrame.AsSpan();
                    using var guard = outputBuffer.Lock();
                    guard.Value.Write(data);
                    
                    // Frame will be disposed when method exits
                }
                else
                {
                    // Resampling required - use FFI resampler with timing
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    
                    Debug.LogError($"AudioRemixConveyor: RESAMPLING STARTING {ownedAudioFrame.numChannels}ch@{ownedAudioFrame.sampleRate}Hz -> {numChannels}ch@{sampleRate}Hz");
                    
                    try
                    {
                        using var uFrame = resampler.RemixAndResample(ownedAudioFrame, numChannels, sampleRate);
                        stopwatch.Stop();
                        
                        var data = uFrame.AsSpan();
                        using var guard = outputBuffer.Lock();
                        guard.Value.Write(data);
                        
                        Debug.LogError($"AudioRemixConveyor: RESAMPLING COMPLETED in {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.TotalSeconds:F3}s)");
                    }
                    catch (Exception ex)
                    {
                        stopwatch.Stop();
                        Debug.LogError($"AudioRemixConveyor: RESAMPLING FAILED after {stopwatch.ElapsedMilliseconds}ms - Error: {ex.Message}");
                        throw;
                    }
                }
            }

            private static bool IsFrameSilentOrEmpty(Span<byte> audioData)
            {
                if (audioData.Length == 0) return true;

                // Convert to int16 samples and check for silence
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(audioData);
                
                // Quick check: if all samples are zero or very quiet, consider it silent
                const short SILENCE_THRESHOLD = 32; // Very quiet threshold
                
                for (int i = 0; i < samples.Length; i++)
                {
                    if (Math.Abs(samples[i]) > SILENCE_THRESHOLD)
                    {
                        return false; // Found non-silent audio
                    }
                }
                
                return true; // All samples are silent/quiet
            }

            public void Dispose()
            {
                resampler?.Dispose();
            }
        }
    }
}