using System;
using System.IO;
using UnityEngine;

namespace LiveKit.Rooms.Streaming
{
    [Serializable]
    public readonly struct StreamKey : IEquatable<StreamKey>
    {
        public readonly string identity;
        public readonly string sid;

        public StreamKey(string identity, string sid)
        {
            this.identity = identity;
            this.sid = sid;
        }

        public bool Equals(StreamKey other) =>
            identity == other.identity
            && sid == other.sid;

        public override bool Equals(object? obj)
        {
            return obj is StreamKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(identity, sid);
        }
    }

    public static class StreamKeyUtils
    {
        public static string PersistentFilePathByName(string name)
        {
            string rootDir = Application.persistentDataPath!;
            string fileName = $"{name}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            string filePath = Path.Combine(rootDir, "livekit_audio_wav", fileName);
            return filePath;
        }

        public static string PersistentFilePathByStreamKey(StreamKey key, string postfix)
        {
            string rootDir = Application.persistentDataPath!;
            string fileName = $"{key.identity}_{key.sid}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{postfix}";
            string filePath = Path.Combine(rootDir, "livekit_audio_wav", fileName);
            return filePath;
        }
    }
}