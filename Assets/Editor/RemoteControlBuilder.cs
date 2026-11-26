using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;

/// <summary>Builds the handheld menu remote as a prefab and places it on the workbench of the open scene.</summary>
public static class RemoteControlBuilder
{
    private const string PrefabPath = "Assets/Prefabs/Remote Control.prefab";
    private const string MaterialFolder = "Assets/Materials/Remote";

    // Remote body, in metres. The buttons sit on the top (+Y) face; the front end is +Z.
    private static readonly Vector3 BodySize = new Vector3(0.06f, 0.022f, 0.2f);
    private static readonly Vector2 ButtonSize = new Vector2(0.044f, 0.042f); // x by z
    private const float CapHeight = 0.012f;
    private const float PressTravel = 0.008f;

    private static readonly Color BodyColor = new Color(0.10f, 0.11f, 0.13f);
    private static readonly Color Blue = new Color(0.15f, 0.42f, 0.95f);
    private static readonly Color Red = new Color(0.90f, 0.16f, 0.14f);

    [MenuItem("XR Project/Create Remote Control")]
    public static void Create()
    {
        var root = new GameObject("Remote Control");
        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Body";
        body.transform.SetParent(root.transform, false);
        body.transform.localScale = BodySize;
        body.GetComponent<Renderer>().sharedMaterial = GetMaterial("Remote Body", BodyColor);

        var rigidbody = root.AddComponent<Rigidbody>();
        rigidbody.mass = 0.15f;
        rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
        rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        var grab = root.AddComponent<XRGrabInteractable>();
        // Only the body is grabbable; the button colliders belong to the buttons' own interactables.
        grab.colliders.Clear();
        grab.colliders.Add(body.GetComponent<Collider>());
        grab.useDynamicAttach = true;

        var remote = root.AddComponent<RemoteControl>();

        float top = BodySize.y * 0.5f;
        float spacing = ButtonSize.y + 0.014f;
        // Red at the front (furthest from the hand holding it), so the two blue ones are not next to Back To Menu.
        AddButton(remote, "Back To Menu", "MENU", Red, new Vector3(0f, top, spacing), 0.5f, remote.BackToMenu);
        AddButton(remote, "Reset Stage", "RESET\nSTAGE", Blue, new Vector3(0f, top, 0f), 0.3f, remote.ResetStage);
        AddButton(remote, "Restart Full PC", "RESTART\nPC", Blue, new Vector3(0f, top, -spacing), 0.75f, remote.RestartFullPC);

        Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath));
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        PlaceOnWorkbench(instance.transform);
        var instanceRemote = instance.GetComponent<RemoteControl>();
        instanceRemote.progression = Object.FindFirstObjectByType<BuildProgression>();
        if (instanceRemote.progression == null)
            Debug.LogWarning("Remote Control: no Build Progression in the open scene; the blue buttons will do nothing until one is assigned.", instance);
        PrefabUtility.RecordPrefabInstancePropertyModifications(instanceRemote);

        var catcher = Object.FindFirstObjectByType<LostPartCatcher>();
        if (catcher != null && !catcher.extraBodies.Contains(instance.GetComponent<Rigidbody>()))
        {
            Undo.RecordObject(catcher, "Catch lost remote");
            catcher.extraBodies.Add(instance.GetComponent<Rigidbody>());
        }

        Undo.RegisterCreatedObjectUndo(instance, "Create Remote Control");
        Selection.activeGameObject = instance;
        EditorSceneManager.MarkSceneDirty(instance.scene);
        Debug.Log($"Remote Control saved to {PrefabPath} and placed in {instance.scene.name}. " +
                  "Hold it in one hand and poke the buttons with the other.", instance);
    }

    private static void AddButton(RemoteControl remote, string name, string label, Color color, Vector3 position,
        float holdSeconds, UnityAction action)
    {
        var buttonObject = new GameObject("Button - " + name);
        buttonObject.transform.SetParent(remote.transform, false);
        buttonObject.transform.localPosition = position;

        // Non-trigger: poke interactors ignore trigger colliders by default.
        var collider = buttonObject.AddComponent<BoxCollider>();
        collider.size = new Vector3(ButtonSize.x, CapHeight, ButtonSize.y);
        collider.center = new Vector3(0f, CapHeight * 0.5f, 0f);

        var interactable = buttonObject.AddComponent<XRSimpleInteractable>();

        var poke = buttonObject.AddComponent<XRPokeFilter>();
        poke.pokeInteractable = interactable;
        poke.pokeCollider = collider;
        poke.pokeConfiguration.Value.pokeDirection = PokeAxis.NegativeY;
        poke.pokeConfiguration.Value.enablePokeAngleThreshold = true;
        poke.pokeConfiguration.Value.pokeAngleThreshold = 45f;
        // XRPokeFilter adds itself to the interactable's select filters at runtime.

        // The cap is an empty so its label is not stretched by the cube's scale.
        var cap = new GameObject("Cap").transform;
        cap.SetParent(buttonObject.transform, false);
        var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visual.name = "Visual";
        Object.DestroyImmediate(visual.GetComponent<Collider>());
        visual.transform.SetParent(cap, false);
        visual.transform.localPosition = new Vector3(0f, CapHeight * 0.5f, 0f);
        visual.transform.localScale = new Vector3(ButtonSize.x, CapHeight, ButtonSize.y);
        visual.GetComponent<Renderer>().sharedMaterial = GetMaterial("Remote Button " + (color == Red ? "Red" : "Blue"), color);
        AddLabel(cap, label);

        var follow = buttonObject.AddComponent<XRPokeFollowAffordance>();
        follow.pokeFollowTransform = cap;
        follow.clampToMaxDistance = true;
        follow.maxDistance = PressTravel;
        follow.returnToInitialPosition = true;
        follow.smoothingSpeed = 16f;

        var click = buttonObject.AddComponent<AudioSource>();
        click.playOnAwake = false;
        click.spatialBlend = 1f;
        click.clip = AssetDatabase.LoadAssetAtPath<AudioClip>(FindAsset("ButtonClick t:AudioClip"));

        var button = buttonObject.AddComponent<RemoteButton>();
        button.remote = remote;
        button.holdSeconds = holdSeconds;
        button.clickSound = click;
        UnityEventTools.AddPersistentListener(button.onPressed, action);
    }

    private static void AddLabel(Transform cap, string text)
    {
        var labelObject = new GameObject("Label");
        labelObject.transform.SetParent(cap, false);
        labelObject.transform.localPosition = new Vector3(0f, CapHeight + 0.0005f, 0f);
        // Face up, reading towards the front end of the remote.
        labelObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        var tmp = labelObject.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = Color.white;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 0.01f;
        tmp.fontSizeMax = 0.08f;
        tmp.rectTransform.sizeDelta = new Vector2(ButtonSize.x - 0.006f, ButtonSize.y - 0.008f);
    }

    private static void PlaceOnWorkbench(Transform remote)
    {
        // Lying flat, buttons up, on the near-left of the XRModelTestScene workbench (top at y = 0.82).
        var bench = GameObject.Find("Workbench Top");
        if (bench != null)
        {
            Bounds bounds = bench.GetComponent<Renderer>().bounds;
            remote.position = new Vector3(bounds.center.x - bounds.extents.x * 0.4f, bounds.max.y + BodySize.y * 0.5f + 0.002f,
                bounds.min.z + 0.2f);
        }
        else
        {
            remote.position = new Vector3(0f, 1f, 0.5f);
        }
        remote.rotation = Quaternion.identity;
    }

    private static Material GetMaterial(string name, Color color)
    {
        string path = $"{MaterialFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) return material;
        Directory.CreateDirectory(MaterialFolder);
        material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name, color = color };
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    private static string FindAsset(string filter)
    {
        string[] guids = AssetDatabase.FindAssets(filter);
        return guids.Length > 0 ? AssetDatabase.GUIDToAssetPath(guids[0]) : null;
    }
}
