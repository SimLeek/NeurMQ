using UnityEngine;
using NetMQ;
using NetMQ.Sockets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FFMpegCore;
using FFMpegCore.Pipes;
using System.Threading.Tasks;
using System.Diagnostics;

[RequireComponent(typeof(ArticulationBody), typeof(AudioListener))]
public class ArticulationBodyNetMQ : MonoBehaviour
{
    [SerializeField] private string publisherAddress = "tcp://*:5555";
    [SerializeField] private string subscriberAddress = "tcp://*:5556";
    [SerializeField] private int baseUdpPort = 8888;
    [SerializeField] private int audioUdpPort = 8887;
    [SerializeField, Min(1)] private int renderWidth = 1280;
    [SerializeField, Min(1)] private int renderHeight = 720;

    private ArticulationBody articulationBody;
    private PublisherSocket publisherSocket;
    private SubscriberSocket subscriberSocket;
    private List<CameraStream> cameraStreams = new List<CameraStream>();
    private List<float> audioBuffer = new List<float>();
    private bool isStreaming = false;
    private List<Task> ffmpegTasks = new List<Task>();
    private int audioChannels;

    private class CameraStream
    {
        public Camera Camera;
        public RenderTexture RenderTexture;
        public Texture2D TempTexture;
        public int UdpPort;
    }

    void Reset()
    {
        articulationBody = GetComponent<ArticulationBody>();
        if (articulationBody == null)
        {
            Debug.LogError("ArticulationBody component not found.", this);
            return;
        }

        Camera[] cameras = GetComponentsInChildren<Camera>();
        if (cameras.Length == 0)
        {
            Debug.LogWarning("No Cameras found in hierarchy. Video streaming unavailable.", this);
        }

        cameraStreams.Clear();
        foreach (var camera in cameras)
        {
            var stream = new CameraStream
            {
                Camera = camera,
                UdpPort = baseUdpPort + cameraStreams.Count
            };
            UpdateCameraTextures(stream);
            cameraStreams.Add(stream);
            Debug.Log($"Camera {camera.name} assigned, will stream to udp://127.0.0.1:{stream.UdpPort}");
        }
    }

    void OnValidate()
    {
        if (renderWidth < 1) renderWidth = 1;
        if (renderHeight < 1) renderHeight = 1;

        foreach (var stream in cameraStreams)
        {
            UpdateCameraTextures(stream);
        }
    }

    void UpdateCameraTextures(CameraStream stream)
    {
        if (stream.RenderTexture != null && (stream.RenderTexture.width != renderWidth || stream.RenderTexture.height != renderHeight))
        {
            Destroy(stream.RenderTexture);
            stream.RenderTexture = null;
        }
        if (stream.TempTexture != null && (stream.TempTexture.width != renderWidth || stream.TempTexture.height != renderHeight))
        {
            Destroy(stream.TempTexture);
            stream.TempTexture = null;
        }

        if (stream.RenderTexture == null)
        {
            stream.RenderTexture = new RenderTexture(renderWidth, renderHeight, 24);
            stream.Camera.targetTexture = stream.RenderTexture;
        }
        if (stream.TempTexture == null)
        {
            stream.TempTexture = new Texture2D(renderWidth, renderHeight, TextureFormat.RGB24, false);
        }
    }

    void Start()
    {
        articulationBody = GetComponent<ArticulationBody>();
        if (articulationBody == null)
        {
            Debug.LogError("ArticulationBody missing.", this);
            return;
        }

        // Check FFmpeg binaries
        string ffmpegLocalPath = Path.Combine(Application.dataPath, "FFmpeg", "bin", "ffmpeg");
        string ffmpegPath = ffmpegLocalPath;
        bool usingSystemFfmpeg = false;

        if (!File.Exists(ffmpegLocalPath))
        {
            try
            {
                Process process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-version",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    Debug.LogWarning($"FFmpeg not found at {ffmpegLocalPath}, but system FFmpeg detected. Using system FFmpeg.");
                    ffmpegPath = "ffmpeg";
                    usingSystemFfmpeg = true;
                }
                else
                {
                    Debug.LogError($"FFmpeg not found at {ffmpegLocalPath} or in system PATH. Please place ffmpeg in Assets/FFmpeg/bin or ensure ffmpeg is in PATH.", this);
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"FFmpeg not found at {ffmpegLocalPath} or in system PATH. Error checking system FFmpeg: {e.Message}. Place ffmpeg in Assets/FFmpeg/bin or add to PATH.", this);
                return;
            }
        }
        else
        {
            Debug.Log($"Using FFmpeg at {ffmpegLocalPath}");
        }

        if (!usingSystemFfmpeg)
        {
            GlobalFFOptions.Configure(new FFOptions { BinaryFolder = Path.Combine(Application.dataPath, "FFmpeg", "bin") });
        }

        // Determine audio channels
        audioChannels = AudioSettings.speakerMode switch
        {
            AudioSpeakerMode.Mono => 1,
            AudioSpeakerMode.Stereo => 2,
            AudioSpeakerMode.Quad => 4,
            AudioSpeakerMode.Surround => 5,
            AudioSpeakerMode.Mode5point1 => 6,
            AudioSpeakerMode.Mode7point1 => 8,
            _ => 2 // Default to stereo
        };
        Debug.Log($"Detected {audioChannels} audio channels based on AudioSettings.speakerMode.");

        // Initialize NetMQ
        AsyncIO.ForceDotNet.Force();
        publisherSocket = new PublisherSocket();
        try
        {
            publisherSocket.Bind(publisherAddress);
            Debug.Log($"Publisher socket bound to {publisherAddress}");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to bind publisher socket: {e.Message}");
            return;
        }

        subscriberSocket = new SubscriberSocket();
        try
        {
            subscriberSocket.Bind(subscriberAddress);
            subscriberSocket.Subscribe("");
            Debug.Log($"Subscriber socket bound to {subscriberAddress}");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to bind subscriber socket: {e.Message}");
            publisherSocket?.Close();
            publisherSocket?.Dispose();
            return;
        }

        // Start FFmpeg streaming
        isStreaming = true;
        ffmpegTasks.Add(Task.Run(() => StreamAudio()));
        foreach (var stream in cameraStreams)
        {
            ffmpegTasks.Add(Task.Run(() => StreamCamera(stream)));
        }
        Debug.Log($"Started FFmpeg streaming: {cameraStreams.Count} cameras, audio to udp://127.0.0.1:{audioUdpPort}");
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        if (!isStreaming) return;
        lock (audioBuffer)
        {
            audioBuffer.AddRange(data);
        }
    }

    async void StreamCamera(CameraStream stream)
    {
        try
        {
            var videoSource = new RawVideoPipeSource(GetVideoFrames(stream))
            {
                FrameRate = 30,
                Width = renderWidth,
                Height = renderHeight,
                PixelFormat = "rgb24"
            };

            await FFMpegArguments
                .FromPipeInput(videoSource, options => options.WithCustomArgument("-re"))
                .OutputToUrl($"udp://127.0.0.1:{stream.UdpPort}", options => options
                    .WithVideoCodec("libvpx-vp9")
                    .ForceFormat("webm")
                    .WithCustomArgument("-b:v 2M"))
                .ProcessAsynchronously();
        }
        catch (Exception ex)
        {
            Debug.LogError($"FFmpeg streaming error for camera {stream.Camera.name}: {ex.Message}");
        }
    }

    async void StreamAudio()
    {
        try
        {
            var audioSource = new RawAudioPipeSource(GetAudioSamples())
            {
                SampleRate = AudioSettings.outputSampleRate,
                Channels = audioChannels,
                SampleFormat = "s16"
            };

            await FFMpegArguments
                .FromPipeInput(audioSource)
                .OutputToUrl($"udp://127.0.0.1:{audioUdpPort}", options => options
                    .WithAudioCodec("opus")
                    .ForceFormat("webm")
                    .WithCustomArgument("-b:a 128k"))
                .ProcessAsynchronously();
        }
        catch (Exception ex)
        {
            Debug.LogError($"FFmpeg audio streaming error: {ex.Message}");
        }
    }

    IEnumerable<IVideoFrame> GetVideoFrames(CameraStream stream)
    {
        while (isStreaming)
        {
            RenderTexture.active = stream.RenderTexture;
            stream.TempTexture.ReadPixels(new Rect(0, 0, renderWidth, renderHeight), 0, 0);
            stream.TempTexture.Apply();
            RenderTexture.active = null;

            byte[] frameData = stream.TempTexture.GetRawTextureData();
            yield return new RawVideoFrame(frameData, renderWidth, renderHeight, "rgb24");
            yield return null;
        }
    }

    IEnumerable<IAudioSample> GetAudioSamples()
    {
        while (isStreaming)
        {
            lock (audioBuffer)
            {
                int samplesNeeded = 1024 * audioChannels;
                if (audioBuffer.Count >= samplesNeeded)
                {
                    float[] samples = audioBuffer.Take(samplesNeeded).ToArray();
                    audioBuffer.RemoveRange(0, samples.Length);
                    short[] pcmSamples = new short[samples.Length];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        pcmSamples[i] = (short)(samples[i] * 32767f);
                    }
                    yield return new RawAudioSample(pcmSamples, AudioSettings.outputSampleRate, audioChannels, "s16");
                }
            }
            yield return null;
        }
    }

    void Update()
    {
        while (subscriberSocket.TryReceiveFrameString(out string message))
        {
            string[] parts = message.Split(':', 2);
            string command = parts[0];
            string data = parts.Length > 1 ? parts[1] : "";
            string response = ProcessCommand(command, data);
            if (response != null)
            {
                publisherSocket.SendFrame(response);
            }
        }
    }

    string ProcessCommand(string command, string data)
    {
        try
        {
            switch (command)
            {
                case "SET_JOINT_FORCES":
                    var forces = data.Split(',').Select(float.Parse).ToList();
                    List<float> currentForces = new List<float>();
                    articulationBody.GetJointForces(currentForces);
                    for (int i = 0; i < forces.Count && i < currentForces.Count; i++)
                    {
                        currentForces[i] += forces[i];
                    }
                    articulationBody.SetJointForces(currentForces);
                    return "SET_JOINT_FORCES:OK";

                case "GET_DOF_START_INDICES":
                    List<int> indices = new List<int>();
                    articulationBody.GetDofStartIndices(indices);
                    return $"GET_DOF_START_INDICES:{string.Join(",", indices)}";

                case "GET_JOINT_POSITIONS":
                    List<float> positions = new List<float>();
                    articulationBody.GetJointPositions(positions);
                    return $"GET_JOINT_POSITIONS:{string.Join(",", positions)}";

                case "GET_JOINT_VELOCITIES":
                    List<float> velocities = new List<float>();
                    articulationBody.GetJointVelocities(velocities);
                    return $"GET_JOINT_VELOCITIES:{string.Join(",", velocities)}";

                case "GET_JOINT_FORCES":
                    List<float> forces = new List<float>();
                    articulationBody.GetJointForces(forces);
                    return $"GET_JOINT_FORCES:{string.Join(",", forces)}";

                case "GET_JOINT_EXTERNAL_FORCES":
                    List<float> externalForces = new List<float>();
                    int dofCount = articulationBody.GetJointExternalForces(externalForces, Time.fixedDeltaTime);
                    return $"GET_JOINT_EXTERNAL_FORCES:{dofCount},{string.Join(",", externalForces)}";

                default:
                    return $"ERROR:Unknown command:{command}";
            }
        }
        catch (Exception ex)
        {
            return $"ERROR:{ex.Message}";
        }
    }

    void OnDestroy()
    {
        isStreaming = false;
        foreach (var task in ffmpegTasks)
        {
            task.Wait();
        }

        publisherSocket?.Close();
        publisherSocket?.Dispose();
        subscriberSocket?.Close();
        subscriberSocket?.Dispose();
        NetMQConfig.Cleanup(false);

        foreach (var stream in cameraStreams)
        {
            if (stream.RenderTexture) Destroy(stream.RenderTexture);
            if (stream.TempTexture) Destroy(stream.TempTexture);
        }
    }
}