using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LiveKit.Scripts.Audio;
using RustAudio;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class JoinMenu : MonoBehaviour
{
    public static string LivekitURL { get; private set; }
    public static string RoomToken { get; private set; }
    public static string MicrophoneSelection { get; private set; }

    public RawImage PreviewCamera;
    public InputField URLField;
    public InputField TokenField;
    public Dropdown Dropdown;
    public Button ConnectButton;

    void Start()
    {
        if (PlayerPrefs.HasKey(nameof(LivekitURL)))
        {
            URLField.text = PlayerPrefs.GetString(nameof(LivekitURL));
        }

        if (PlayerPrefs.HasKey(nameof(RoomToken)))
        {
            TokenField.text = PlayerPrefs.GetString(nameof(RoomToken));
        }

        var options = MicrophoneAudioFilter.AvailableDeviceNamesOrEmpty();
        Dropdown.options = options
            .Select(e => new Dropdown.OptionData(e))
            .ToList();
        if (PlayerPrefs.HasKey(nameof(MicrophoneSelection)))
        {
            var selected = PlayerPrefs.GetString(nameof(MicrophoneSelection));
            for (int i = 0; i < options.Length; i++)
            {
                if (options[i] == selected)
                {
                    Dropdown.value = i;
                    break;
                }
            }
        }

        StartCoroutine(StartPreviewCamera());

        ConnectButton.onClick.AddListener(() =>
        {
            PlayerPrefs.SetString(nameof(LivekitURL), URLField.text);
            PlayerPrefs.SetString(nameof(RoomToken), TokenField.text);
            PlayerPrefs.SetString(nameof(MicrophoneSelection), options[Dropdown.value]);

            LivekitURL = URLField.text;
            RoomToken = TokenField.text;

            if (string.IsNullOrWhiteSpace(RoomToken))
                return;

            SceneManager.LoadScene("RoomScene", LoadSceneMode.Single);
        });
    }

    private IEnumerator StartPreviewCamera()
    {
        Debug.LogError("Preview camera is not supported");
        yield break;
    }
}