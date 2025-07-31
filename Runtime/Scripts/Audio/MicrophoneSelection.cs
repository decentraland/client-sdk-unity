using System;
using System.Linq;
using RichTypes;
using UnityEngine;

namespace LiveKit.Runtime.Scripts.Audio
{
    public readonly struct MicrophoneSelection
    {
        public readonly string name;

        private MicrophoneSelection(string name)
        {
            this.name = name;
        }

        public static Result<MicrophoneSelection> Default()
        {
            string? name = Microphone.devices!.FirstOrDefault();
            return name == null
                ? Result<MicrophoneSelection>.ErrorResult("Microphone devices are not available")
                : Result<MicrophoneSelection>.SuccessResult(new MicrophoneSelection(name));
        }

        public static Result<MicrophoneSelection> FromName(string microphoneName)
        {
            string[] devices = Microphone.devices ?? Array.Empty<string>();
            foreach (string device in devices)
            {
                if (device == microphoneName)
                    return Result<MicrophoneSelection>.SuccessResult(new MicrophoneSelection(microphoneName));
            }

            return Result<MicrophoneSelection>.ErrorResult($"Microphone with name '{microphoneName}' not found");
        }

        public static Result<MicrophoneSelection> FromIndex(int index)
        {
            string[] devices = Microphone.devices ?? Array.Empty<string>();
            if (index < 0 || index >= devices.Length)
                return Result<MicrophoneSelection>.ErrorResult($"Microphone index '{index}' is out of range");
            return Result<MicrophoneSelection>.SuccessResult(new MicrophoneSelection(devices[index]));
        }
    }
}