using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>Builds the main menu scene the game launches into, and puts it first in Build Settings.</summary>
public static class MainMenuSceneBuilder
{
    private const string ScenePath = "Assets/Scenes/" + SceneFlow.MenuScene + ".unity";
    private const string GameScenePath = "Assets/Scenes/" + SceneFlow.GameScene + ".unity";
    private const string OriginPath = "Assets/Samples/XR Interaction Toolkit/3.5.1/Starter Assets/Prefabs/XR Origin (XR Rig).prefab";
    private const string MaterialFolder = "Assets/Materials/Main Menu";

    private static readonly Color Background = new Color(0.04f, 0.05f, 0.07f);
    private static readonly Color Accent = new Color(0.15f, 0.42f, 0.95f);

    [MenuItem("XR Project/Build Main Menu Scene")]
    public static void Build()
    {
        for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var existing = SceneManager.GetSceneAt(i);
            if (existing.path == ScenePath && !existing.isDirty)
                EditorSceneManager.CloseScene(existing, true);
        }

        // An untitled scene cannot stay open next to a new one; replace it when it has nothing to lose.
        var active = SceneManager.GetActiveScene();
        bool replaceUntitled = SceneManager.sceneCount == 1 && string.IsNullOrEmpty(active.path) && !active.isDirty;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
            replaceUntitled ? NewSceneMode.Single : NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        scene.name = SceneFlow.MenuScene;

        new GameObject("XR Interaction Manager").AddComponent<XRInteractionManager>();
        CreateRoom();
        Camera camera = CreateXROrigin();
        CreateMenu(camera);

        EditorSceneManager.SaveScene(scene, ScenePath);
        PutFirstInBuildSettings(ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log($"{SceneFlow.MenuScene} created at {ScenePath} and set as the first scene in Build Settings.");
    }

    private static void CreateRoom()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        floor.name = "Floor";
        floor.transform.position = new Vector3(0f, -0.02f, 0f);
        floor.transform.localScale = new Vector3(4f, 0.02f, 4f);
        floor.GetComponent<Renderer>().sharedMaterial = GetMaterial("Menu Floor", new Color(0.10f, 0.11f, 0.14f));

        // A thin accent ring under the player's feet, so the floor reads in the dark.
        var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "Floor Ring";
        Object.DestroyImmediate(ring.GetComponent<Collider>());
        ring.transform.position = new Vector3(0f, 0f, 0f);
        ring.transform.localScale = new Vector3(1.2f, 0.005f, 1.2f);
        ring.GetComponent<Renderer>().sharedMaterial = GetMaterial("Menu Floor Accent", Accent * 0.6f);

        var lightObject = new GameObject("Key Light");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 0.8f;
        light.color = new Color(0.85f, 0.90f, 1f);
        lightObject.transform.rotation = Quaternion.Euler(55f, -20f, 0f);

        RenderSettings.skybox = null;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.25f, 0.27f, 0.32f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = Background;
        RenderSettings.fogStartDistance = 3f;
        RenderSettings.fogEndDistance = 12f;
    }

    private static Camera CreateXROrigin()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OriginPath);
        if (prefab == null)
        {
            Debug.LogError("XRI Starter Assets XR Origin prefab was not found: " + OriginPath);
            return null;
        }

        var origin = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        origin.name = "XR Origin (Controllers)";
        origin.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        var cameraOffset = origin.transform.Find("Camera Offset");
        if (cameraOffset != null)
            cameraOffset.localPosition = new Vector3(0f, 1.6f, 0f);

        var camera = origin.GetComponentInChildren<Camera>(true);
        if (camera != null)
        {
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Background;
        }
        return camera;
    }

    private static void CreateMenu(Camera camera)
    {
        var panel = new GameObject("Main Menu");
        panel.transform.position = new Vector3(0f, 1.6f, 2f);

        // Drifts back in front of the player when they look away, facing them and staying upright.
        var follow = panel.AddComponent<LazyFollow>();
        follow.target = camera != null ? camera.transform : null;
        follow.targetOffset = new Vector3(0f, 0f, 2f);
        follow.positionFollowMode = LazyFollow.PositionFollowMode.Follow;
        follow.rotationFollowMode = LazyFollow.RotationFollowMode.LookAtWithWorldUp;
        follow.snapOnEnable = true;
        follow.maxDistanceAllowed = 0.3f;
        follow.maxAngleAllowed = 25f;

        AddText(panel.transform, "Title", "VR PC BUILDER", new Vector3(0f, 0.18f, 0f),
            new Vector2(1.6f, 0.3f), 2.4f, FontStyles.Bold, Color.white);
        TextMeshPro prompt = AddText(panel.transform, "Prompt", "Press any button to start", new Vector3(0f, -0.12f, 0f),
            new Vector2(1.4f, 0.12f), 0.9f, FontStyles.Normal, new Color(0.75f, 0.85f, 1f));

        var menu = panel.AddComponent<MainMenu>();
        menu.prompt = prompt;
        menu.gameScene = SceneFlow.GameScene;
    }

    private static TextMeshPro AddText(Transform parent, string name, string text, Vector3 position, Vector2 size,
        float maxFontSize, FontStyles style, Color color)
    {
        var textObject = new GameObject(name);
        textObject.transform.SetParent(parent, false);
        // LazyFollow points the panel's +Z away from the player, the side TextMeshPro reads from.
        textObject.transform.localPosition = position;
        var tmp = textObject.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontStyle = style;
        tmp.color = color;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 0.1f;
        tmp.fontSizeMax = maxFontSize;
        tmp.rectTransform.sizeDelta = size;
        return tmp;
    }

    // The menu goes first so a build launches into it; the game scene must be listed too so it can be loaded.
    private static void PutFirstInBuildSettings(string scenePath)
    {
        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        scenes.RemoveAll(s => s.path == scenePath);
        scenes.Insert(0, new EditorBuildSettingsScene(scenePath, true));
        int game = scenes.FindIndex(s => s.path == GameScenePath);
        if (game < 0) scenes.Add(new EditorBuildSettingsScene(GameScenePath, true));
        else scenes[game].enabled = true;
        EditorBuildSettings.scenes = scenes.ToArray();
    }

    private static Material GetMaterial(string name, Color color)
    {
        string path = $"{MaterialFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) return material;
        System.IO.Directory.CreateDirectory(MaterialFolder);
        material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name, color = color };
        AssetDatabase.CreateAsset(material, path);
        return material;
    }
}
