using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>Builds an isolated PCVR interaction scene from the existing imported component assets.</summary>
public static class XRModelTestSceneBuilder
{
    private const string ScenePath = "Assets/Scenes/XRModelTestScene.unity";
    private const string OriginPath = "Assets/Samples/XR Interaction Toolkit/3.5.1/Starter Assets/Prefabs/XR Origin (XR Rig).prefab";
    private const float TableTop = 0.82f;

    [MenuItem("XR Project/Build Model Test Scene")]
    public static void Build()
    {
        for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var existing = SceneManager.GetSceneAt(i);
            if (existing.path == ScenePath && !existing.isDirty)
                EditorSceneManager.CloseScene(existing, true);
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = "XRModelTestScene";

        var interactionManagerObject = new GameObject("XR Interaction Manager");
        var interactionManager = interactionManagerObject.AddComponent<XRInteractionManager>();

        CreateRoom(interactionManager);
        CreateXROrigin();
        CreateComponents(interactionManager);

        EditorSceneManager.SaveScene(scene, ScenePath);
        AddSceneToBuildSettings(ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("XRModelTestScene created. Model bounds and material checks are listed below; see XR_MODEL_TEST_SETUP.md for Quest Link steps.");
    }

    private static void CreateRoom(XRInteractionManager manager)
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Floor";
        floor.transform.position = new Vector3(0f, -0.08f, 0f);
        floor.transform.localScale = new Vector3(8f, 0.16f, 8f);
        floor.GetComponent<Renderer>().sharedMaterial = MakeMaterial("Floor - warm grey", new Color(0.22f, 0.24f, 0.27f));

        var table = new GameObject("Workbench");
        var top = GameObject.CreatePrimitive(PrimitiveType.Cube);
        top.name = "Workbench Top";
        top.transform.SetParent(table.transform, false);
        top.transform.localPosition = new Vector3(0f, TableTop - 0.06f, 1.05f);
        top.transform.localScale = new Vector3(3.6f, 0.12f, 1.8f);
        top.GetComponent<Renderer>().sharedMaterial = MakeMaterial("Workbench - charcoal", new Color(0.12f, 0.15f, 0.18f));

        var legs = new Vector3[]
        {
            new Vector3(-1.62f, 0.35f, 0.30f), new Vector3(1.62f, 0.35f, 0.30f),
            new Vector3(-1.62f, 0.35f, 1.80f), new Vector3(1.62f, 0.35f, 1.80f)
        };
        for (var i = 0; i < legs.Length; i++)
        {
            var leg = GameObject.CreatePrimitive(PrimitiveType.Cube);
            leg.name = "Workbench Leg " + (i + 1);
            leg.transform.SetParent(table.transform, false);
            leg.transform.localPosition = legs[i];
            leg.transform.localScale = new Vector3(0.10f, 0.70f, 0.10f);
            leg.GetComponent<Renderer>().sharedMaterial = MakeMaterial("Workbench legs", new Color(0.07f, 0.08f, 0.10f));
        }

        var lightObject = new GameObject("Key Light");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        light.color = new Color(1f, 0.93f, 0.82f);
        lightObject.transform.rotation = Quaternion.Euler(50f, -25f, 0f);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.48f, 0.50f, 0.54f);
        RenderSettings.ambientIntensity = 1f;
    }

    private static void CreateXROrigin()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OriginPath);
        if (prefab == null)
        {
            Debug.LogError("XRI Starter Assets XR Origin prefab was not found: " + OriginPath);
            return;
        }

        var origin = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        origin.name = "XR Origin (Controllers)";
        origin.transform.position = new Vector3(0f, 0f, -0.45f);
        origin.transform.rotation = Quaternion.identity;
        var cameraOffset = origin.transform.Find("Camera Offset");
        if (cameraOffset != null)
            cameraOffset.localPosition = new Vector3(0f, 1.6f, 0f);

        var camera = origin.GetComponentInChildren<Camera>(true);
        if (camera != null)
        {
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 100f;
        }
    }

    private static void CreateComponents(XRInteractionManager manager)
    {
        var paths = new[]
        {
            "Assets/PCComponents/pc_case.glb",
            "Assets/PCComponents/Case.glb",
            "Assets/PCComponents/PC_Case.fbx",
            "Assets/PCComponents/Motherboard.glb",
            "Assets/PCComponents/GraphicCard.glb",
            "Assets/PCComponents/PSU.fbx",
            "Assets/PCComponents/CPUfan.fbx",
            "Assets/PCComponents/CPU.glb",
            "Assets/PCComponents/RAM.glb",
            "Assets/PCComponents/ssd-kit.glb"
        };

        var root = new GameObject("PC Components");
        for (var i = 0; i < paths.Length; i++)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(paths[i]);
            if (model == null)
            {
                Debug.LogWarning("Model asset could not be loaded: " + paths[i]);
                continue;
            }

            var item = new GameObject(System.IO.Path.GetFileNameWithoutExtension(paths[i]));
            item.transform.SetParent(root.transform, false);
            var visual = (GameObject)PrefabUtility.InstantiatePrefab(model, item.transform);
            visual.name = "Imported Model (source unchanged)";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = GetTestScale(paths[i]);

            var renderers = visual.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                Debug.LogWarning(paths[i] + " has no Renderer and was left without an interactable collider.");
                continue;
            }

            var bounds = renderers[0].bounds;
            var materialsMissing = 0;
            var vertexCount = 0;
            var triangleCount = 0;
            var meshesWithoutNormals = 0;
            var nonReadableMeshes = 0;
            var checkedMeshes = new HashSet<Mesh>();
            for (var r = 0; r < renderers.Length; r++)
            {
                bounds.Encapsulate(renderers[r].bounds);
                foreach (var material in renderers[r].sharedMaterials)
                    if (material == null) materialsMissing++;

                var meshRenderer = renderers[r] as MeshRenderer;
                var meshFilter = meshRenderer != null ? meshRenderer.GetComponent<MeshFilter>() : null;
                var mesh = meshFilter != null ? meshFilter.sharedMesh : null;
                var skinned = renderers[r] as SkinnedMeshRenderer;
                if (mesh == null && skinned != null) mesh = skinned.sharedMesh;
                if (mesh != null && checkedMeshes.Add(mesh))
                {
                    if (!mesh.isReadable)
                    {
                        nonReadableMeshes++;
                        continue;
                    }
                    vertexCount += mesh.vertexCount;
                    triangleCount += mesh.triangles.Length / 3;
                    if (mesh.normals == null || mesh.normals.Length != mesh.vertexCount)
                        meshesWithoutNormals++;
                }
            }

            if (materialsMissing > 0)
                Debug.LogWarning(paths[i] + " contains " + materialsMissing + " missing material slot(s).");
            if (meshesWithoutNormals > 0)
                Debug.LogWarning(paths[i] + " has " + meshesWithoutNormals + " mesh(es) with missing or incomplete normals.");
            if (triangleCount > 150000)
                Debug.LogWarning(paths[i] + " contains " + triangleCount + " triangles; inspect VR performance before using it in a larger scene.");

            item.transform.position = new Vector3(((i % 5) - 2) * 0.68f, 0f, 0.58f + (i / 5) * 0.98f);
            item.transform.position += Vector3.up * (TableTop + 0.012f - bounds.min.y);

            var box = item.AddComponent<BoxCollider>();
            box.center = bounds.center - item.transform.position;
            box.size = Vector3.Max(bounds.size * 1.04f, new Vector3(0.025f, 0.025f, 0.025f));

            var body = item.AddComponent<Rigidbody>();
            body.mass = 0.5f;
            body.useGravity = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.sleepThreshold = 0.01f;

            var grab = item.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
            grab.movementType = UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable.MovementType.VelocityTracking;
            grab.throwOnDetach = true;
            grab.trackPosition = true;
            grab.trackRotation = true;
            grab.retainTransformParent = false;
            var serialized = new SerializedObject(grab);
            var managerProperty = serialized.FindProperty("m_InteractionManager");
            if (managerProperty != null)
                managerProperty.objectReferenceValue = manager;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Debug.Log(string.Format("XR model audit | {0} | world bounds {1} m | pivot-to-bounds center {2} m | renderers {3} | meshes {4} | vertices {5} | triangles {6} | missing materials {7} | meshes without normals {8} | non-readable meshes {9} | BoxCollider",
                paths[i], bounds.size.ToString("F3"), (bounds.center - item.transform.position).ToString("F3"),
                renderers.Length, checkedMeshes.Count, vertexCount, triangleCount, materialsMissing, meshesWithoutNormals, nonReadableMeshes));
        }
    }

    private static Material MakeMaterial(string name, Color color)
    {
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.name = name;
        material.color = color;
        return material;
    }

    // These are scene-instance wrapper scales only. The imported model assets remain untouched.
    // They bring the raw imported bounds into plausible desktop-PC component ranges for an initial VR scale check.
    private static Vector3 GetTestScale(string assetPath)
    {
        var scale = 1f;
        switch (System.IO.Path.GetFileName(assetPath))
        {
            case "pc_case.glb": scale = 0.001f; break;
            case "Case.glb": scale = 0.1f; break;
            case "PC_Case.fbx": scale = 0.1f; break;
            case "Motherboard.glb": scale = 0.1f; break;
            case "GraphicCard.glb": scale = 0.03f; break;
            case "PSU.fbx": scale = 0.01f; break;
            case "CPUfan.fbx": scale = 30f; break;
            case "CPU.glb": scale = 0.01f; break;
            case "RAM.glb": scale = 0.03f; break;
            case "ssd-kit.glb": scale = 0.04f; break;
        }
        return Vector3.one * scale;
    }

    private static void AddSceneToBuildSettings(string scenePath)
    {
        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        for (var i = 0; i < scenes.Count; i++)
            if (scenes[i].path == scenePath) return;
        scenes.Add(new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }
}
