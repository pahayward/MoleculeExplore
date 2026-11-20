using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class PocSetup
{
    const string ScenePath = "Assets/Scenes/Caffeine.unity";

    static Material Mat(string name, Color c, float smooth)
    {
        string path = $"Assets/Materials/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            Directory.CreateDirectory("Assets/Materials");
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetColor("_BaseColor", c);
        m.SetFloat("_Smoothness", smooth);
        m.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(m);
        return m;
    }

    static Material Unlit(string name, Color c)
    {
        string path = $"Assets/Materials/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            Directory.CreateDirectory("Assets/Materials");
            m = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetColor("_BaseColor", c);
        EditorUtility.SetDirty(m);
        return m;
    }

    [MenuItem("POC/Create Caffeine Scene")]
    public static void CreateScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // CPK colours
        var carbon = Mat("Carbon", new Color(0.35f, 0.35f, 0.38f), 0.5f);
        var hydrogen = Mat("Hydrogen", new Color(0.95f, 0.95f, 0.95f), 0.5f);
        var nitrogen = Mat("Nitrogen", new Color(0.2f, 0.35f, 0.95f), 0.5f);
        var oxygen = Mat("Oxygen", new Color(0.95f, 0.15f, 0.12f), 0.5f);
        AssetDatabase.SaveAssets();

        // Light + ambient
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.0f;
        light.shadows = LightShadows.Soft;
        light.color = new Color(1f, 0.98f, 0.95f);
        lightGo.transform.rotation = Quaternion.Euler(40f, -35f, 0f);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.32f, 0.33f, 0.38f);

        // Camera
        var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.04f, 0.04f, 0.05f);
        cam.fieldOfView = 40f;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 100f;
        camGo.transform.position = new Vector3(0, 0, -13);

        // Molecule
        var molGo = new GameObject("Caffeine");
        var mb = molGo.AddComponent<MoleculeBuilder>();
        mb.carbon = carbon; mb.hydrogen = hydrogen; mb.nitrogen = nitrogen; mb.oxygen = oxygen; mb.bond = carbon;

        // Controllers
        var sys = new GameObject("System");
        var sc = sys.AddComponent<SceneController>();
        sc.cam = cam; sc.molecule = molGo.transform;
        sc.blockWhite = Unlit("BlockWhite", Color.white);
        sc.blockBlack = Unlit("BlockBlack", Color.black);
        var ss = sys.AddComponent<StreamServer>();
        ss.cam = cam; ss.scene = sc;

        EditorSceneManager.SaveScene(scene, ScenePath);

        // Player settings for the server build: D3D11 (NVENC GPU texture path), windowed 720p.
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        PlayerSettings.runInBackground = true;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.productName = "CaffeineServer";
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
        Debug.Log("[POC] scene created: " + ScenePath);
    }

    [MenuItem("POC/Build Windows Server")]
    public static void Build()
    {
        string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../build"));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = Path.Combine(outDir, "CaffeineServer.exe"),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });
        Debug.Log("[POC] build " + report.summary.result + " " + report.summary.totalSize / (1024 * 1024) + "MB -> " + outDir);
    }
}
