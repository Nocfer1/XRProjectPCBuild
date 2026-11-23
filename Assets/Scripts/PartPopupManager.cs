using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// Shows an info card for the held part the user is looking at, plus one compact tag per kind of part
// mounted on it (identical parts are combined, e.g. "RAM ×4"). Only one part is inspected at a time,
// and everything hides while a part is being placed so popups never cover precise work.
// Created automatically in scenes with mountable parts; add one by hand to change its settings.
[DefaultExecutionOrder(1100)] // After MountablePart (1000) has moved mounted parts this frame.
public class PartPopupManager : MonoBehaviour
{
    [Header("Visibility")]
    public bool popupsEnabled = true;
    [Tooltip("Optional button that turns popups on and off.")]
    public InputActionReference toggleAction;
    [Tooltip("Show tags for parts mounted on the inspected part.")]
    public bool showMountedPartTags = true;

    [Header("Gaze")]
    [Tooltip("A held part must be within this angle of the view direction to start showing its popup.")]
    [Range(1f, 60f)] public float showAngle = 15f;
    [Tooltip("The inspected part keeps its popup until it leaves this angle.")]
    [Range(1f, 90f)] public float hideAngle = 25f;
    [Tooltip("Seconds of looking at a part before its popup appears.")]
    [Min(0f)] public float showDelay = 0.5f;
    [Min(0.01f)] public float fadeInSeconds = 0.2f;
    [Min(0.01f)] public float fadeOutSeconds = 0.12f;

    [Header("Hide While Placing")]
    [Tooltip("Popups hide when a held part is this many times its snap search radius from a slot that accepts it.")]
    [Min(0f)] public float slotProximityFactor = 1.5f;
    [Tooltip("Popups hide when two held parts are closer than this (metres).")]
    [Min(0f)] public float heldPartsProximity = 0.08f;

    [Header("Layout")]
    [Tooltip("Card size: metres per canvas unit, per metre of distance from the eye.")]
    [Min(0.0001f)] public float cardScale = 0.0011f;
    [Min(0.0001f)] public float tagScale = 0.001f;
    [Tooltip("Gap between the held part and its card (metres).")]
    [Min(0f)] public float cardMargin = 0.03f;
    [Tooltip("How far tags sit in front of their parts, towards the eye (metres).")]
    [Min(0f)] public float tagLift = 0.03f;
    [Tooltip("Popups never come closer to the eye than this (metres).")]
    [Min(0.05f)] public float minReadingDistance = 0.35f;
    [Tooltip("How quickly the card catches up with the part. Higher is stiffer.")]
    [Min(0.1f)] public float followSharpness = 10f;

    private static PartPopupManager instance;

    private struct TagGroup
    {
        public string name;
        public int count;
        public Vector3 centerSum;
    }

    private readonly List<MountablePart> held = new List<MountablePart>();
    private readonly List<MountablePart> mountedBuffer = new List<MountablePart>();
    private readonly List<TagGroup> groups = new List<TagGroup>();
    private readonly Dictionary<object, int> groupIndex = new Dictionary<object, int>();
    private readonly List<PartPopupView> tags = new List<PartPopupView>();
    private readonly StringBuilder text = new StringBuilder();

    private Camera head;
    private PartPopupView card;
    private MountablePart candidate;  // Part the user is currently looking at.
    private float candidateSince;
    private MountablePart shown;      // Part whose popups are on screen (possibly fading).
    private float alpha;
    private float lastAssemblyTime = float.NegativeInfinity;
    private Vector3 cardPosition;
    private bool cardPlaced;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        EnsureExists();
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => EnsureExists();

    private static void EnsureExists()
    {
        if (instance == null && MountablePart.Active.Count > 0)
            new GameObject("Part Popups").AddComponent<PartPopupManager>();
    }

    private void Awake()
    {
        card = PartPopupView.Create(transform, true);
    }

    private void OnEnable()
    {
        if (instance == null) instance = this;
        if (toggleAction != null && toggleAction.action != null)
        {
            toggleAction.action.performed += OnToggle;
            toggleAction.action.Enable();
        }
    }

    private void OnDisable()
    {
        if (instance == this) instance = null;
        if (toggleAction != null && toggleAction.action != null)
            toggleAction.action.performed -= OnToggle;
    }

    private void OnToggle(InputAction.CallbackContext context) => popupsEnabled = !popupsEnabled;

    private void LateUpdate()
    {
        if (head == null || !head.isActiveAndEnabled) head = Camera.main;
        if (head == null) return;

        CollectHeldParts(out bool assemblyInProgress);
        if (assemblyInProgress) lastAssemblyTime = Time.time;
        UpdateCandidate();

        // The same delay applies after placing work stops, so moving near a slot edge doesn't flicker.
        bool wantVisible = popupsEnabled && candidate != null &&
                           Time.time - candidateSince >= showDelay && Time.time - lastAssemblyTime >= showDelay;
        if (wantVisible && shown != candidate)
        {
            // Switch parts only once the previous popups have faded out.
            if (alpha <= 0.001f)
            {
                shown = candidate;
                cardPlaced = false;
                card.SetContent(PartInfo.NameOf(shown), BuildCardBody(shown));
            }
            else wantVisible = false;
        }

        float fadeTime = wantVisible ? fadeInSeconds : fadeOutSeconds;
        alpha = Mathf.MoveTowards(alpha, wantVisible ? 1f : 0f, Time.deltaTime / fadeTime);
        if (shown == null || !shown.isActiveAndEnabled) alpha = 0f;
        if (alpha <= 0.001f && !wantVisible) shown = null;

        if (shown == null)
        {
            card.Alpha = 0f;
            HideTagsFrom(0);
            return;
        }
        LayoutCard();
        LayoutTags();
    }

    private void CollectHeldParts(out bool assemblyInProgress)
    {
        held.Clear();
        assemblyInProgress = false;
        foreach (MountablePart part in MountablePart.Active)
        {
            if (part.Grab == null || !part.Grab.isSelected) continue;
            // A mounted part being pulled out of its slot is assembly work too.
            if (part.IsMounted) assemblyInProgress = true;
            else held.Add(part);
        }

        for (int i = 0; i < held.Count && !assemblyInProgress; i++)
        {
            MountablePart part = held[i];
            float radius = part.snapSearchRadius * slotProximityFactor;
            Vector3 pivot = part.AlignmentPivot.position;
            foreach (SnapPoint slot in SnapPoint.ActiveSlots)
            {
                if (slot.CanConsider(part) && (slot.Target.position - pivot).sqrMagnitude <= radius * radius)
                {
                    assemblyInProgress = true;
                    break;
                }
            }

            if (assemblyInProgress) break;
            Bounds bounds = GetBounds(part);
            bounds.Expand(heldPartsProximity * 2f);
            for (int j = i + 1; j < held.Count; j++)
            {
                if (bounds.Intersects(GetBounds(held[j])))
                {
                    assemblyInProgress = true;
                    break;
                }
            }
        }
    }

    private void UpdateCandidate()
    {
        MountablePart best = null;
        // The current candidate holds on until it leaves the wider hide angle.
        if (candidate != null && held.Contains(candidate) && ViewAngle(candidate) <= hideAngle)
            best = candidate;
        else
        {
            float bestAngle = showAngle;
            foreach (MountablePart part in held)
            {
                float angle = ViewAngle(part);
                if (angle <= bestAngle)
                {
                    bestAngle = angle;
                    best = part;
                }
            }
        }

        if (best != candidate)
        {
            candidate = best;
            candidateSince = Time.time;
        }
    }

    private float ViewAngle(MountablePart part) =>
        Vector3.Angle(head.transform.forward, GetBounds(part).center - head.transform.position);

    private string BuildCardBody(MountablePart part)
    {
        text.Clear();
        PartInfo info = PartInfo.Resolve(part);
        if (info != null && !string.IsNullOrWhiteSpace(info.description)) text.Append(info.description.Trim());

        // Derived from the scene so it always matches the slots that actually exist.
        var hosts = new List<string>();
        foreach (SnapPoint slot in SnapPoint.ActiveSlots)
        {
            if (slot.acceptedSlotType != part.compatibleSlotType || slot.Target.IsChildOf(part.transform)) continue;
            string host = HostName(slot);
            if (!hosts.Contains(host)) hosts.Add(host);
        }
        if (hosts.Count > 0)
        {
            if (text.Length > 0) text.Append("\n\n");
            text.Append("<b>Mounts on:</b> ").Append(string.Join(", ", hosts));
        }
        return text.ToString();
    }

    private static string HostName(SnapPoint slot)
    {
        var hostPart = slot.GetComponentInParent<MountablePart>();
        if (hostPart != null) return PartInfo.NameOf(hostPart);
        var hostBody = slot.GetComponentInParent<Rigidbody>();
        return hostBody != null ? hostBody.name : slot.transform.root.name;
    }

    private void LayoutCard()
    {
        Transform eye = head.transform;
        Bounds bounds = GetBounds(shown);
        Vector3 right = eye.right;

        // Put the card on the outer side of the holding hand, away from the space between the hands.
        float side = 1f;
        if (shown.Grab.interactorsSelecting.Count > 0)
        {
            Vector3 hand = shown.Grab.interactorsSelecting[0].transform.position;
            side = Vector3.Dot(hand - eye.position, right) >= 0f ? 1f : -1f;
        }

        Vector3 extents = bounds.extents;
        float partHalfWidth = Mathf.Abs(extents.x * right.x) + Mathf.Abs(extents.y * right.y) + Mathf.Abs(extents.z * right.z);
        float distance = Mathf.Max(minReadingDistance, Vector3.Distance(eye.position, bounds.center));
        float scale = cardScale * distance;
        float cardHalfWidth = card.WidthUnits * scale * 0.5f;
        Vector3 target = bounds.center + right * (side * (partHalfWidth + cardMargin + cardHalfWidth)) + eye.up * (cardHalfWidth * 0.25f);
        target = KeepReadable(eye.position, target);

        cardPosition = cardPlaced ? Vector3.Lerp(cardPosition, target, 1f - Mathf.Exp(-followSharpness * Time.deltaTime)) : target;
        cardPlaced = true;
        card.Place(cardPosition, FaceEye(eye, cardPosition), scale);
        card.Alpha = alpha;
    }

    private void LayoutTags()
    {
        groups.Clear();
        groupIndex.Clear();
        if (showMountedPartTags)
        {
            shown.GetComponentsInChildren(false, mountedBuffer);
            foreach (MountablePart part in mountedBuffer)
            {
                if (part == shown || !part.IsMounted) continue;
                PartInfo info = PartInfo.Resolve(part);
                string name = PartInfo.NameOf(part);
                object key = info != null ? info : (object)name;
                Vector3 center = GetBounds(part).center;
                if (groupIndex.TryGetValue(key, out int index))
                {
                    TagGroup group = groups[index];
                    group.count++;
                    group.centerSum += center;
                    groups[index] = group;
                }
                else
                {
                    groupIndex.Add(key, groups.Count);
                    groups.Add(new TagGroup { name = name, count = 1, centerSum = center });
                }
            }
        }

        Transform eye = head.transform;
        for (int i = 0; i < groups.Count; i++)
        {
            if (i == tags.Count) tags.Add(PartPopupView.Create(transform, false));
            PartPopupView tag = tags[i];
            TagGroup group = groups[i];
            tag.gameObject.SetActive(true);
            tag.SetContent(group.count > 1 ? $"{group.name} ×{group.count}" : group.name);

            Vector3 anchor = group.centerSum / group.count;
            Vector3 position = KeepReadable(eye.position, anchor + (eye.position - anchor).normalized * tagLift);
            tag.Place(position, FaceEye(eye, position), tagScale * Vector3.Distance(eye.position, position));
            tag.Alpha = alpha;
        }
        HideTagsFrom(groups.Count);
    }

    private void HideTagsFrom(int first)
    {
        for (int i = first; i < tags.Count; i++)
        {
            if (!tags[i].gameObject.activeSelf) continue;
            tags[i].Alpha = 0f;
            tags[i].gameObject.SetActive(false);
        }
    }

    private Vector3 KeepReadable(Vector3 eye, Vector3 position)
    {
        Vector3 offset = position - eye;
        return offset.magnitude >= minReadingDistance ? position : eye + offset.normalized * minReadingDistance;
    }

    private static Quaternion FaceEye(Transform eye, Vector3 position)
    {
        Vector3 forward = position - eye.position;
        return forward.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(forward, eye.up) : eye.rotation;
    }

    // World bounds of a part's own interaction colliders, falling back to its centre of mass.
    private static Bounds GetBounds(MountablePart part)
    {
        Bounds bounds = default;
        bool any = false;
        foreach (Collider collider in part.Grab.colliders)
        {
            if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
            if (any) bounds.Encapsulate(collider.bounds);
            else
            {
                bounds = collider.bounds;
                any = true;
            }
        }
        return any ? bounds : new Bounds(part.Body.worldCenterOfMass, Vector3.zero);
    }
}
