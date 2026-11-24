using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

[Serializable]
public class BuildStage
{
    public string name = "Stage";
    [Tooltip("The stage unlocks once all of these parts are mounted at the same time. Empty unlocks straight away.")]
    public List<MountablePart> requiredMounted = new List<MountablePart>();
    [Tooltip("Hidden until this stage unlocks, then dropped through the chute.")]
    public List<Rigidbody> partsToDrop = new List<Rigidbody>();
}

// Linear unlock order for the build. Each stage unlocks at most once: progress only moves forward,
// so taking a part off and putting it back never drops anything again.
[DefaultExecutionOrder(-100)]
public class BuildProgression : MonoBehaviour
{
    public DropChute chute;
    [Tooltip("Available from the start, left where they are placed in the scene.")]
    public List<Rigidbody> startingParts = new List<Rigidbody>();
    public List<BuildStage> stages = new List<BuildStage>();

    [Tooltip("Called with the index of the stage that just unlocked.")]
    public UnityEvent<int> onStageUnlocked = new UnityEvent<int>();
    public UnityEvent onBuildComplete = new UnityEvent();
    public static event Action<BuildProgression, BuildStage> StageUnlocked;
    public static event Action<BuildProgression> BuildCompleted;

    // Number of stages unlocked so far. stages[UnlockedStageCount] is the one the player is working towards.
    public int UnlockedStageCount { get; private set; }
    public bool IsComplete => UnlockedStageCount >= stages.Count;
    public BuildStage NextStage => IsComplete ? null : stages[UnlockedStageCount];

    private struct Home
    {
        public Transform parent;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public bool isKinematic;
        public bool useGravity;
        public CollisionDetectionMode collisionMode;
    }

    private readonly Dictionary<Rigidbody, Home> homes = new Dictionary<Rigidbody, Home>();
    private Coroutine restarting;

    private void Awake()
    {
        foreach (Rigidbody body in ManagedBodies())
        {
            if (homes.ContainsKey(body)) continue;
            homes.Add(body, new Home
            {
                parent = body.transform.parent,
                localPosition = body.transform.localPosition,
                localRotation = body.transform.localRotation,
                isKinematic = body.isKinematic,
                useGravity = body.useGravity,
                collisionMode = body.collisionDetectionMode
            });
        }
        HideLockedParts(0);
    }

    // A stage with no required parts, or whose parts were seated through an Initial Slot, unlocks without waiting for a mount.
    private void Start() => Evaluate();

    private void OnEnable() => SnapPoint.PartSeated += OnPartSeated;
    private void OnDisable() => SnapPoint.PartSeated -= OnPartSeated;

    private void OnPartSeated(SnapPoint slot, MountablePart part) => Evaluate();

    // Unlocks every stage whose parts are all mounted, in order.
    public void Evaluate()
    {
        if (restarting != null) return;
        while (!IsComplete && IsSatisfied(stages[UnlockedStageCount]))
            Unlock(UnlockedStageCount);
    }

    private static bool IsSatisfied(BuildStage stage)
    {
        foreach (MountablePart part in stage.requiredMounted)
            if (part != null && !part.IsMounted) return false;
        return true;
    }

    private void Unlock(int index)
    {
        UnlockedStageCount = index + 1;
        BuildStage stage = stages[index];
        DropParts(stage.partsToDrop);
        onStageUnlocked.Invoke(index);
        StageUnlocked?.Invoke(this, stage);
        if (!IsComplete) return;
        onBuildComplete.Invoke();
        BuildCompleted?.Invoke(this);
    }

    private void DropParts(List<Rigidbody> bodies)
    {
        if (chute != null && chute.isActiveAndEnabled)
        {
            chute.Drop(bodies);
            return;
        }
        // No chute: the parts simply appear where they were placed in the scene.
        foreach (Rigidbody body in bodies)
            if (body != null) body.gameObject.SetActive(true);
    }

    // Sends the parts of the latest drop that are lying loose back through the chute. Before anything
    // has dropped, the starting parts go back to where they began instead. Progress is not undone.
    [ContextMenu("Reset Current Stage")]
    public void ResetCurrentStage()
    {
        if (restarting != null) return;
        if (UnlockedStageCount == 0)
        {
            foreach (Rigidbody body in startingParts)
                if (body != null && !GrabUtility.IsHeld(body)) ReturnHome(body);
            return;
        }
        var loose = new List<Rigidbody>();
        foreach (Rigidbody body in stages[UnlockedStageCount - 1].partsToDrop)
        {
            if (body == null || GrabUtility.IsHeld(body) || (chute != null && chute.IsPending(body))) continue;
            var part = body.GetComponent<MountablePart>();
            if (part == null || !part.IsMounted) loose.Add(body);
        }
        if (loose.Count > 0) DropParts(loose);
    }

    // Takes the whole build apart and returns to the first stage.
    [ContextMenu("Full Restart")]
    public void FullRestart()
    {
        if (restarting != null) return;
        bool anyHeld = false;
        foreach (Rigidbody body in homes.Keys)
        {
            if (body == null || !GrabUtility.IsHeld(body)) continue;
            GrabUtility.ForceRelease(body);
            anyHeld = true;
        }
        if (chute != null) chute.CancelAll();
        // XR Grab finishes letting go during the frame; reset after that so it can't undo the reset.
        if (anyHeld && isActiveAndEnabled) restarting = StartCoroutine(RestartAfterRelease());
        else ResetAll();
    }

    private IEnumerator RestartAfterRelease()
    {
        yield return null;
        yield return new WaitForEndOfFrame();
        restarting = null;
        ResetAll();
    }

    private void ResetAll()
    {
        // Unmount everything first so no part is carried along by another.
        foreach (Rigidbody body in homes.Keys)
        {
            var part = body != null ? body.GetComponent<MountablePart>() : null;
            if (part != null && part.IsMounted) part.MountedSlot.Vacate();
        }
        foreach (Rigidbody body in homes.Keys)
            if (body != null) ReturnHome(body);
        UnlockedStageCount = 0;
        HideLockedParts(0);
    }

    private void ReturnHome(Rigidbody body)
    {
        if (!homes.TryGetValue(body, out Home home)) return;
        var part = body.GetComponent<MountablePart>();
        if (part != null && part.IsMounted) part.MountedSlot.Vacate();
        if (!body.isKinematic)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        body.isKinematic = home.isKinematic;
        body.useGravity = home.useGravity;
        body.detectCollisions = true;
        body.collisionDetectionMode = home.collisionMode;
        Transform t = body.transform;
        t.SetParent(home.parent, false);
        t.SetLocalPositionAndRotation(home.localPosition, home.localRotation);
        body.position = t.position;
        body.rotation = t.rotation;
    }

    private void HideLockedParts(int fromStage)
    {
        for (int i = fromStage; i < stages.Count; i++)
            foreach (Rigidbody body in stages[i].partsToDrop)
                if (body != null) body.gameObject.SetActive(false);
    }

    private IEnumerable<Rigidbody> ManagedBodies()
    {
        foreach (Rigidbody body in startingParts)
            if (body != null) yield return body;
        foreach (BuildStage stage in stages)
            foreach (Rigidbody body in stage.partsToDrop)
                if (body != null) yield return body;
    }

    private void OnValidate()
    {
        var seen = new HashSet<Rigidbody>();
        foreach (Rigidbody body in ManagedBodies())
            if (!seen.Add(body))
                Debug.LogWarning($"{name}: {body.name} is listed more than once in the build progression.", this);
    }
}
