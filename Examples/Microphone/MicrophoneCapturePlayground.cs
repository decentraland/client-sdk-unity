using System.Text;
using LiveKit.Scripts.Audio;
using RustAudio;
using UnityEngine;

namespace Livekit.Examples.Microphone
{
    public class MicrophoneCapturePlayground : MonoBehaviour
    {
        [SerializeField] private string microphoneName;
        private MicrophoneAudioFilter? audioFilter;

        private void Start()
        {
            var microphone = MicrophoneAudioFilter.New(microphoneName);
            if (microphone.Success == false)
            {
                Debug.LogError($"Microphone error: {microphone.ErrorMessage}");
                return;
            }

            microphone.Value.Dispose();
            microphone = MicrophoneAudioFilter.New(microphoneName);
            if (microphone.Success == false)
            {
                Debug.LogError($"Microphone error: {microphone.ErrorMessage}");
                return;
            }

            audioFilter = microphone.Value;

            var gm = new GameObject("test");
            var source = gm.AddComponent<AudioSource>();
            var playback = gm.AddComponent<PlaybackTestFilter>();
            playback.Construct(microphone.Value);

            source.Play();
            audioFilter.StartCapture();
        }

        [ContextMenu(nameof(PrintDevices))]
        public void PrintDevices()
        {
            var array = MicrophoneAudioFilter.AvailableDeviceNamesOrEmpty();
            var sb = new StringBuilder();
            sb.AppendLine($"Total count: {array.Length}, Available:");
            foreach (var name in array)
            {
                sb.AppendLine(name);
            }

            Debug.Log(sb.ToString());
        }
        
        [ContextMenu(nameof(PrintStatus))]
        public void PrintStatus()
        {
            var status = RustAudioClient.SystemStatus();
            var sb = new StringBuilder();
            sb.AppendLine(JsonUtility.ToJson(status));
            Debug.Log(sb.ToString());
        }
        
        [ContextMenu(nameof(ReInit))]
        public void ReInit()
        {
            RustAudioClient.ForceReInit();
        }

        private void OnDestroy()
        {
            audioFilter?.Dispose();
            audioFilter = null;
        }
    }
}