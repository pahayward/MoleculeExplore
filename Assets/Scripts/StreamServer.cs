using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.WebRTC;
using UnityEngine;

[Serializable] class Sig { public string type, sdp, candidate, sdpMid; public int sdpMLineIndex; }
[Serializable] class Telemetry
{
    public string type = "telemetry";
    public long lastAppliedSeq;
    public double serverTime;     // ms since server start (monotonic)
    public long unixMs;
    public int frameCount;
    public float frameMs;         // smoothed render frame time
    public float encodeMs;        // average encode time per frame (from WebRTC stats)
    public float encodeFps;
    public string encoder;
    public int inputsApplied;
}

/// <summary>
/// 1:1 WebRTC streaming server. Captures the camera into a RenderTexture (GPU texture path,
/// NVENC when available), sends H.264 video, takes input on data channels, sends telemetry back.
/// The server is the offerer: client connects to the WebSocket, server sends offer.
/// </summary>
[DefaultExecutionOrder(100)]   // after SceneController, so acks reflect this frame's applied input
public class StreamServer : MonoBehaviour
{
    public Camera cam;
    public SceneController scene;

    [Header("Config (override with -width -height -fps -bitrate -port -stun)")]
    public int width = 1280, height = 720, fps = 60;
    public int bitrateKbps = 5000;
    public int port = 8080;
    public string stunUrl = "";           // empty = host candidates only (LAN/localhost)
    public bool showLocalPreview = true;
    public float telemetryHz = 4f;

    WsSignalingServer ws;
    RTCPeerConnection pc;
    VideoStreamTrack track;
    RenderTexture rt;
    RTCDataChannel inputCh, controlCh;
    readonly List<RTCIceCandidateInit> pendingCandidates = new List<RTCIceCandidateInit>();
    bool remoteSet;

    float frameMs; string encoderImpl = "?"; float encodeMs, encodeFps;
    double lastFrames, lastEncTime; uint lastFramesEncoded;
    float nextTelemetry;
    long lastAckSent;
    StreamWriter csv;

    void OpenCsv()
    {
        if (csv != null) return;
        string clientDir = FindClientDir();
        string root = clientDir != null ? Path.GetDirectoryName(clientDir) : Application.persistentDataPath;
        string dir = Path.Combine(root, "logs", "telemetry");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"server_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        csv = new StreamWriter(path, false) { AutoFlush = true };
        csv.WriteLine("iso,unix_ms,server_time_ms,frame_count,frame_ms,encode_ms,encode_fps,encoder,last_applied_seq,inputs_applied,grabbing");
        Debug.Log("[StreamServer] telemetry CSV: " + path);
    }

    void Awake()
    {
        ParseArgs();
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = fps;
        Application.runInBackground = true;
    }

    IEnumerator Start()
    {
        yield return StartCoroutine(WebRTC.Update());   // runs forever; texture push each frame
    }

    void OnEnable()
    {
        ws = new WsSignalingServer();
        string dir = FindClientDir();
        ws.Start(port, dir);
        Debug.Log($"[StreamServer] signalling + client on http://localhost:{port}/ (client dir: {dir ?? "NOT FOUND"}) {width}x{height}@{fps} {bitrateKbps}kbps gfx={SystemInfo.graphicsDeviceType} gpu={SystemInfo.graphicsDeviceName}");
    }

    void OnDisable() { ws?.Dispose(); Teardown(); csv?.Dispose(); csv = null; }

    void ParseArgs()
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++)
        {
            switch (a[i])
            {
                case "-width": width = int.Parse(a[i + 1]); break;
                case "-height": height = int.Parse(a[i + 1]); break;
                case "-fps": fps = int.Parse(a[i + 1]); break;
                case "-bitrate": bitrateKbps = int.Parse(a[i + 1]); break;
                case "-port": port = int.Parse(a[i + 1]); break;
                case "-stun": stunUrl = a[i + 1]; break;
            }
        }
    }

    static string FindClientDir()
    {
        string exe = Path.GetDirectoryName(Application.dataPath);   // project root (editor) or build dir (player)
        foreach (var rel in new[] { "client", "../client", "../../client" })
        {
            var p = Path.GetFullPath(Path.Combine(exe, rel));
            if (File.Exists(Path.Combine(p, "index.html"))) return p;
        }
        return null;
    }

    // ---------------------------------------------------------------- per frame

    void Update()
    {
        frameMs = Mathf.Lerp(frameMs, Time.unscaledDeltaTime * 1000f, 0.05f);

        while (ws.Events.TryDequeue(out var ev))
        {
            if (ev.kind == WsSignalingServer.Kind.Connected) { Teardown(); StartSession(); }
            else if (ev.kind == WsSignalingServer.Kind.Closed) { /* keep the peer connection; ICE state decides */ }
            else HandleSignal(ev.text);
        }

        // Immediate ack whenever input has been applied (tiny, unreliable) so RTT is not quantised by the telemetry rate.
        if (inputCh != null && inputCh.ReadyState == RTCDataChannelState.Open && scene.LastAppliedSeq != lastAckSent)
        {
            lastAckSent = scene.LastAppliedSeq;
            inputCh.Send("{\"type\":\"ack\",\"lastAppliedSeq\":" + lastAckSent + ",\"grabbing\":" + (scene.Grabbing ? 1 : 0) + "}");
        }

        if (controlCh != null && controlCh.ReadyState == RTCDataChannelState.Open && Time.unscaledTime >= nextTelemetry)
        {
            nextTelemetry = Time.unscaledTime + 1f / telemetryHz;
            var t = new Telemetry
            {
                lastAppliedSeq = scene.LastAppliedSeq,
                serverTime = Time.realtimeSinceStartupAsDouble * 1000.0,
                unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                frameCount = Time.frameCount,
                frameMs = frameMs,
                encodeMs = encodeMs,
                encodeFps = encodeFps,
                encoder = encoderImpl,
                inputsApplied = scene.InputsApplied
            };
            controlCh.Send(JsonUtility.ToJson(t));
            OpenCsv();
            csv.WriteLine(string.Join(",", DateTime.UtcNow.ToString("o"), t.unixMs, t.serverTime.ToString("F2"), t.frameCount,
                t.frameMs.ToString("F2"), t.encodeMs.ToString("F2"), t.encodeFps, t.encoder, t.lastAppliedSeq, t.inputsApplied, scene.Grabbing ? 1 : 0));
        }
    }

    void OnGUI()
    {
        if (!showLocalPreview || rt == null) return;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), rt, ScaleMode.ScaleToFit, false);
        GUI.Label(new Rect(8, 8, 600, 24), $"{width}x{height}@{fps}  encoder={encoderImpl}  encode={encodeMs:F2}ms  frame={frameMs:F1}ms");
    }

    // ---------------------------------------------------------------- session

    void Teardown()
    {
        remoteSet = false; pendingCandidates.Clear();
        inputCh = controlCh = null;
        if (pc != null) { pc.Close(); pc.Dispose(); pc = null; }
        if (track != null) { track.Dispose(); track = null; }
        if (rt != null) { cam.targetTexture = null; rt.Release(); Destroy(rt); rt = null; }
    }

    void StartSession()
    {
        var cfg = new RTCConfiguration();
        if (!string.IsNullOrEmpty(stunUrl)) cfg.iceServers = new[] { new RTCIceServer { urls = new[] { stunUrl } } };
        pc = new RTCPeerConnection(ref cfg);
        pc.OnIceCandidate = c => ws.Send(JsonUtility.ToJson(new Sig
        { type = "candidate", candidate = c.Candidate, sdpMid = c.SdpMid, sdpMLineIndex = c.SdpMLineIndex ?? 0 }));
        pc.OnConnectionStateChange = s => Debug.Log("[StreamServer] connection " + s);
        pc.OnIceConnectionChange = s => Debug.Log("[StreamServer] ice " + s);

        // Video: camera -> RenderTexture (GPU) -> encoder.
        var fmt = WebRTC.GetSupportedRenderTextureFormat(SystemInfo.graphicsDeviceType);
        rt = new RenderTexture(width, height, 24, fmt);
        rt.Create();
        cam.targetTexture = rt;
        track = new VideoStreamTrack(rt);
        var tr = pc.AddTransceiver(track);
        tr.Direction = RTCRtpTransceiverDirection.SendOnly;
        var h264 = RTCRtpSender.GetCapabilities(TrackKind.Video).codecs.Where(c => c.mimeType == "video/H264").ToArray();
        Debug.Log("[StreamServer] H264 codec caps: " + string.Join(" | ", h264.Select(c => c.sdpFmtpLine)));
        var err = tr.SetCodecPreferences(h264);
        if (err != RTCErrorType.None) Debug.LogWarning("[StreamServer] SetCodecPreferences: " + err);

        var p = tr.Sender.GetParameters();
        foreach (var e in p.encodings)
        {
            e.maxBitrate = (ulong)bitrateKbps * 1000UL;
            e.minBitrate = (ulong)bitrateKbps * 1000UL / 2UL;
            e.maxFramerate = (uint)fps;
        }
        var perr = tr.Sender.SetParameters(p);
        if (perr.errorType != RTCErrorType.None) Debug.LogWarning("[StreamServer] SetParameters: " + perr.errorType);

        // Data channels (server creates them, client receives via ondatachannel).
        inputCh = pc.CreateDataChannel("input", new RTCDataChannelInit { ordered = false, maxRetransmits = 0 });
        controlCh = pc.CreateDataChannel("control", new RTCDataChannelInit { ordered = true });
        inputCh.OnMessage = OnInput;
        controlCh.OnMessage = OnInput;      // reliable channel carries down/up/key; telemetry goes back on it

        StartCoroutine(Negotiate());
        StartCoroutine(PollStats());
    }

    void OnInput(byte[] bytes)
    {
        try { scene.Enqueue(JsonUtility.FromJson<InputMsg>(System.Text.Encoding.UTF8.GetString(bytes))); }
        catch (Exception e) { Debug.LogWarning("[StreamServer] bad input: " + e.Message); }
    }

    IEnumerator Negotiate()
    {
        var op = pc.CreateOffer();
        yield return op;
        if (op.IsError) { Debug.LogError("[StreamServer] offer: " + op.Error.message); yield break; }
        var desc = op.Desc;
        var set = pc.SetLocalDescription(ref desc);
        yield return set;
        ws.Send(JsonUtility.ToJson(new Sig { type = "offer", sdp = desc.sdp }));
    }

    void HandleSignal(string json)
    {
        var s = JsonUtility.FromJson<Sig>(json);
        if (pc == null) return;
        if (s.type == "answer") StartCoroutine(SetAnswer(s.sdp));
        else if (s.type == "candidate")
        {
            var init = new RTCIceCandidateInit { candidate = s.candidate, sdpMid = s.sdpMid, sdpMLineIndex = s.sdpMLineIndex };
            if (remoteSet) pc.AddIceCandidate(new RTCIceCandidate(init)); else pendingCandidates.Add(init);
        }
    }

    IEnumerator SetAnswer(string sdp)
    {
        var d = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
        var op = pc.SetRemoteDescription(ref d);
        yield return op;
        if (op.IsError) { Debug.LogError("[StreamServer] answer: " + op.Error.message); yield break; }
        remoteSet = true;
        foreach (var c in pendingCandidates) pc.AddIceCandidate(new RTCIceCandidate(c));
        pendingCandidates.Clear();
    }

    IEnumerator PollStats()
    {
        var self = pc;
        while (pc == self && pc != null)
        {
            yield return new WaitForSeconds(1f);
            if (pc != self) yield break;
            var op = pc.GetStats();
            yield return op;
            if (op.IsError) continue;
            foreach (var st in op.Value.Stats.Values)
            {
                if (st is RTCOutboundRTPStreamStats o && o.kind == "video")
                {
                    encoderImpl = string.IsNullOrEmpty(o.encoderImplementation) ? encoderImpl : o.encoderImplementation;
                    if (o.framesEncoded > lastFramesEncoded)
                    {
                        encodeMs = (float)((o.totalEncodeTime - lastEncTime) * 1000.0 / (o.framesEncoded - lastFramesEncoded));
                        encodeFps = o.framesEncoded - lastFramesEncoded;
                    }
                    lastFramesEncoded = o.framesEncoded; lastEncTime = o.totalEncodeTime;
                    Debug.Log($"[stats] encoder={o.encoderImplementation} {o.frameWidth}x{o.frameHeight} fps={o.framesPerSecond:F0} encodeMs={encodeMs:F2} target={o.targetBitrate / 1000:F0}kbps qLimit={o.qualityLimitationReason} keyframes={o.keyFramesEncoded}");
                }
            }
        }
    }
}
