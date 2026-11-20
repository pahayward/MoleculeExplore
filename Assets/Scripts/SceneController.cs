using System.Collections.Concurrent;
using UnityEngine;

[System.Serializable]
public class InputMsg
{
    public long seq;
    public double t;
    public string type;   // move | down | up | wheel | key
    public float x, y;    // normalised 0..1 over the rendered frame (y down)
    public int button;
    public float deltaY;
    public string key;
}

/// <summary>
/// Server-authoritative scene control. Client input is queued from the network, then each
/// frame: drain queued input -> update camera -> update photon block -> (Unity renders).
/// The client only sends intents (pointer state and normalised coordinates).
/// </summary>
public class SceneController : MonoBehaviour
{
    public Camera cam;
    public Transform molecule;
    public Material blockWhite, blockBlack;

    public float orbitDegreesPerFrameHeight = 360f;
    public float grabDegreesPerFrameHeight = 360f;
    public float minDistance = 4f, maxDistance = 30f, startDistance = 13f;
    public bool drawPhotonBlock = true;

    public readonly ConcurrentQueue<InputMsg> Queue = new ConcurrentQueue<InputMsg>();
    public long LastAppliedSeq { get; private set; }
    public int InputsApplied { get; private set; }
    public bool Grabbing => mode == Mode.Grab;

    enum Mode { None, Orbit, Grab }
    Mode mode;
    float yaw = 30f, pitch = 20f, distance;
    float lastX, lastY;

    // Photon block: 16 cells encoding LastAppliedSeq, drawn into the camera's own frame.
    const int Bits = 16;
    const int CellPx = 12;
    Renderer[] cells;
    Transform blockRoot;
    int builtW, builtH;

    void Awake() { distance = startDistance; }

    public void Enqueue(InputMsg m) { Queue.Enqueue(m); }

    void Update()
    {
        // 1. queued input, in arrival order
        while (Queue.TryDequeue(out var m))
        {
            Apply(m);
            if (m.seq > LastAppliedSeq) LastAppliedSeq = m.seq;
            InputsApplied++;
        }
        // 2. camera
        UpdateCamera();
        // 3. photon block (shows the seq applied before this frame is rendered)
        UpdateBlock();
    }

    void Apply(InputMsg m)
    {
        switch (m.type)
        {
            case "down":
                lastX = m.x; lastY = m.y;
                mode = PickAtom(m.x, m.y) ? Mode.Grab : Mode.Orbit;
                break;
            case "up":
                mode = Mode.None;
                break;
            case "move":
                {
                    float dx = m.x - lastX, dy = m.y - lastY;
                    float aspect = (float)cam.pixelWidth / cam.pixelHeight;
                    if (mode == Mode.Orbit)
                    {
                        yaw -= dx * aspect * orbitDegreesPerFrameHeight;
                        pitch = Mathf.Clamp(pitch + dy * orbitDegreesPerFrameHeight, -85f, 85f);
                    }
                    else if (mode == Mode.Grab)
                    {
                        // Trackball: drag delta -> rotation about camera up / right, applied in world space.
                        var q = Quaternion.AngleAxis(-dx * aspect * grabDegreesPerFrameHeight, cam.transform.up)
                              * Quaternion.AngleAxis(-dy * grabDegreesPerFrameHeight, cam.transform.right);
                        molecule.rotation = q * molecule.rotation;
                    }
                    lastX = m.x; lastY = m.y;
                    break;
                }
            case "wheel":
                distance = Mathf.Clamp(distance * Mathf.Exp(m.deltaY * 0.001f), minDistance, maxDistance);
                break;
            case "key":
                if (m.key == "r" || m.key == "R")
                {
                    yaw = 30f; pitch = 20f; distance = startDistance;
                    molecule.rotation = Quaternion.identity;
                }
                break;
        }
    }

    bool PickAtom(float nx, float ny)
    {
        var ray = cam.ViewportPointToRay(new Vector3(nx, 1f - ny, 0f));
        return Physics.Raycast(ray, out _, 100f);   // only atoms carry colliders (slightly enlarged)
    }

    void UpdateCamera()
    {
        var rot = Quaternion.Euler(pitch, yaw, 0f);
        cam.transform.position = rot * new Vector3(0f, 0f, -distance);   // focus point = origin
        cam.transform.rotation = rot;
    }

    // ---------------------------------------------------------------- photon block

    void BuildBlock()
    {
        if (blockRoot != null) Destroy(blockRoot.gameObject);
        builtW = cam.pixelWidth; builtH = cam.pixelHeight;
        cells = new Renderer[Bits];
        var root = new GameObject("PhotonBlock").transform;
        blockRoot = root;
        root.SetParent(cam.transform, false);
        const float d = 0.5f;
        float worldPerPx = 2f * d * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / cam.pixelHeight;
        for (int i = 0; i < Bits; i++)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.name = "bit" + i;
            q.transform.SetParent(root, false);
            // bottom-left corner, 1 cell inset; bit 15 (MSB) leftmost
            float px = (1 + i) * CellPx + CellPx * 0.5f, py = 1 * CellPx + CellPx * 0.5f;
            var vp = new Vector3(px / cam.pixelWidth, py / cam.pixelHeight, d);
            q.transform.position = cam.ViewportToWorldPoint(vp);
            q.transform.rotation = cam.transform.rotation;
            q.transform.localScale = Vector3.one * (CellPx * worldPerPx);
            var r = q.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sharedMaterial = blockBlack;
            cells[i] = r;
        }
        // Black backing strip so white bits stay readable over any background.
        var back = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(back.GetComponent<Collider>());
        back.name = "backing";
        back.transform.SetParent(root, false);
        float bw = (Bits + 2) * CellPx, bh = 3 * CellPx;
        var bvp = new Vector3((bw * 0.5f) / cam.pixelWidth, (bh * 0.5f) / cam.pixelHeight, d + 0.01f);
        back.transform.position = cam.ViewportToWorldPoint(bvp);
        back.transform.rotation = cam.transform.rotation;
        back.transform.localScale = new Vector3(bw * worldPerPx, bh * worldPerPx, 1f);
        var br = back.GetComponent<MeshRenderer>();
        br.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        br.sharedMaterial = blockBlack;
    }

    void UpdateBlock()
    {
        if (!drawPhotonBlock || cam.targetTexture == null) return;
        if (cells == null || builtW != cam.pixelWidth || builtH != cam.pixelHeight) BuildBlock();
        // Camera moves each frame; keep the block locked to it (it is parented, so only the
        // local transform matters and nothing else is needed). Update the bit colours.
        int v = (int)(LastAppliedSeq & 0xFFFF);
        for (int i = 0; i < Bits; i++)
        {
            bool on = ((v >> (Bits - 1 - i)) & 1) != 0;
            cells[i].sharedMaterial = on ? blockWhite : blockBlack;
        }
    }
}
