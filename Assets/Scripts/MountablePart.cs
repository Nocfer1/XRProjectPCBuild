using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

[RequireComponent(typeof(XRGrabInteractable), typeof(Rigidbody))]
[DefaultExecutionOrder(1000)]
public class MountablePart : MonoBehaviour, IXRSelectFilter, IXRHoverFilter
{
    [Header("Component Data")]
    public SlotType compatibleSlotType;
    [Tooltip("Popup text for this part. When empty, the default Part Info for its slot type is used.")]
    public PartInfo info;

    [Header("Alignment & Detection")]
    [Tooltip("Mating surface pose. Its position and axes must match the socket target when seated. Defaults to this object's transform.")]
    public Transform alignmentPivot;
    [Min(0f)] public float snapSearchRadius = 0.1f;
    [Tooltip("Log why a snap attempt failed when this part is released.")]
    public bool logSnapDiagnostics;

    // XRModelTestScene RAM slots: motherboard-local Z = 0.13 is the visually verified seated depth.
    [Tooltip("Where this part sits in the finished build. Reset Stage mounts it here when rebuilding the progress made so far.")]
    [FormerlySerializedAs("initialSlot")]
    public SnapPoint buildSlot;

    [Header("Feedback")]
    [Tooltip("Controller buzz when a held part is aligned with a slot and would snap in if let go. 0 turns it off.")]
    [Range(0f, 1f)] public float snapReadyHaptic = 0.4f;

    // Fired when a player lets go of a part and it snaps into a slot. Not fired for parts placed by code (Reset Stage).
    public static event System.Action<MountablePart, SnapPoint> Snapped;

    public SnapPoint MountedSlot { get; private set; }
    public bool IsMounted => MountedSlot != null;
    public Transform AlignmentPivot => alignmentPivot != null ? alignmentPivot : transform;
    public bool canProcess => isActiveAndEnabled;
    public XRGrabInteractable Grab => grabInteractable;
    public Rigidbody Body => partRigidbody;
    public static IReadOnlyCollection<MountablePart> Active => activeParts;

    private static readonly HashSet<MountablePart> activeParts = new HashSet<MountablePart>();

    // What the part looked like before it was seated, restored when it leaves the slot.
    private struct PreMountState
    {
        public Transform parent;
        public bool isKinematic;
        public bool useGravity;
        public bool retainTransformParent;

        public static PreMountState Capture(Transform transform, Rigidbody body, XRGrabInteractable grab) =>
            new PreMountState
            {
                parent = transform.parent,
                isKinematic = body.isKinematic,
                useGravity = body.useGravity,
                retainTransformParent = grab.retainTransformParent
            };

        public void RestoreBody(Rigidbody body, XRGrabInteractable grab)
        {
            body.isKinematic = isKinematic;
            body.useGravity = useGravity;
            grab.retainTransformParent = retainTransformParent;
        }
    }

    // Grab settings overridden while a hand holds the part in its slot.
    private struct GripOverride
    {
        public bool trackPosition;
        public bool trackRotation;
        public bool throwOnDetach;

        public static GripOverride Capture(XRGrabInteractable grab) =>
            new GripOverride
            {
                trackPosition = grab.trackPosition,
                trackRotation = grab.trackRotation,
                throwOnDetach = grab.throwOnDetach
            };

        public void Restore(XRGrabInteractable grab)
        {
            grab.trackPosition = trackPosition;
            grab.trackRotation = trackRotation;
            grab.throwOnDetach = throwOnDetach;
        }
    }

    // Shared, reused buffers: these queries never run re-entrantly, so one set avoids per-call garbage.
    private static readonly Collider[] overlapBuffer = new Collider[64];
    private static readonly List<Collider> partColliderBuffer = new List<Collider>();
    private static readonly List<Collider> hostColliderBuffer = new List<Collider>();
    private static readonly List<MountablePart> assemblyBuffer = new List<MountablePart>();
    private static readonly WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();

    private XRGrabInteractable grabInteractable;
    private Rigidbody partRigidbody;
    private Coroutine pendingRelease;
    private PreMountState preMount;
    private GripOverride? seatedGrip; // Set while a hand holds the part in its slot.
    private bool restoreAfterGrab;
    private XRGrabInteractable hostGrab;
    private IXRSelectInteractor pullingInteractor;
    private Vector3 pullStartInTargetSpace;
    private float pullTimer;
    private float nextPullHapticTime;
    private bool pullBaselineReady;
    private SnapPoint reportedRemovalBlocker;
    private bool? snapReady; // Null until first checked in a grab, so a part that starts out aligned does not buzz.
    private float nextSnapReadyHapticTime;
    private readonly List<(Collider part, Collider host)> ignoredHostCollisions = new List<(Collider, Collider)>();

    private void Awake()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();
        partRigidbody = GetComponent<Rigidbody>();
    }

    private void OnEnable()
    {
        activeParts.Add(this);
        grabInteractable.selectEntered.AddListener(OnGrabbed);
        grabInteractable.selectExited.AddListener(OnReleased);
        grabInteractable.selectFilters.Add(this);
        grabInteractable.hoverFilters.Add(this);
    }

    // Mounts the part in its build slot without the precision check, the way Reset Stage rebuilds progress.
    internal bool TrySeatInBuildSlot()
    {
        if (buildSlot == null)
        {
            Debug.LogWarning($"{name}: No Build Slot is set, so it cannot be put back into the build.", this);
            return false;
        }
        if (IsMounted && MountedSlot == buildSlot) return true;
        if (!buildSlot.CanAccept(this))
        {
            Debug.LogWarning($"{name}: Build slot {buildSlot.name} is unavailable, incompatible or still missing a prerequisite.", this);
            return false;
        }
        if (buildSlot.TrySeat(this, false)) return true;
        Debug.LogWarning($"{name}: Could not mount in build slot {buildSlot.name}.", this);
        return false;
    }

    private void OnValidate()
    {
        if (buildSlot != null && buildSlot.acceptedSlotType != compatibleSlotType)
            Debug.LogWarning($"{name}: Build Slot {buildSlot.name} accepts {buildSlot.acceptedSlotType}, but this part is {compatibleSlotType}.", this);
    }

    private void OnDisable()
    {
        activeParts.Remove(this);
        grabInteractable.selectEntered.RemoveListener(OnGrabbed);
        grabInteractable.selectExited.RemoveListener(OnReleased);
        grabInteractable.selectFilters.Remove(this);
        grabInteractable.hoverFilters.Remove(this);
        if (pendingRelease != null) StopCoroutine(pendingRelease);
        pendingRelease = null;
        EndSeatedGrip();
        // Vacate also removes parts that depend on this one, keeping the assembly order valid.
        if (IsMounted) MountedSlot.Vacate();
        RestoreAfterGrab();
    }

    // A seated part can only be hovered or grabbed from where its slot allows removal.
    public bool Process(IXRSelectInteractor interactor, IXRSelectInteractable interactable) =>
        interactor.IsSelecting(interactable) || CanBeTakenBy(interactor, interactable);

    public bool Process(IXRHoverInteractor interactor, IXRHoverInteractable interactable) =>
        (interactor is IXRSelectInteractor selecting && interactable is IXRSelectInteractable selectable &&
         selecting.IsSelecting(selectable)) || CanBeTakenBy(interactor, interactable);

    private bool CanBeTakenBy(IXRInteractor interactor, IXRInteractable interactable) =>
        !IsMounted || MountedSlot.CanRemoveFrom(GrabUtility.GetInteractorPosition(interactor, interactable), grabInteractable);

    private void OnGrabbed(SelectEnterEventArgs args)
    {
        if (pendingRelease != null) StopCoroutine(pendingRelease);
        pendingRelease = null;
        if (!IsMounted) return;
        if (MountedSlot.TryGetBlockingDependent(out var blocker))
        {
            HoldInSlot(args.interactorObject);
            ReportRemovalBlocker(blocker);
            return;
        }
        if (MountedSlot.requirePullToRemove) HoldInSlot(args.interactorObject);
        else Unmount();
    }

    private void HoldInSlot(IXRSelectInteractor interactor)
    {
        if (seatedGrip.HasValue)
        {
            MaintainSeatedGrip();
            return;
        }
        seatedGrip = GripOverride.Capture(grabInteractable);
        MaintainSeatedGrip();
        pullingInteractor = interactor;
        ResetPullBaseline();
    }

    private void MaintainSeatedGrip()
    {
        grabInteractable.trackPosition = false;
        grabInteractable.trackRotation = false;
        grabInteractable.throwOnDetach = false;
        grabInteractable.retainTransformParent = false;
        partRigidbody.isKinematic = true;
        partRigidbody.useGravity = false;
        transform.SetParent(MountedSlot.Target, true);
    }

    private void Update()
    {
        UpdateSnapReady();
        if (!seatedGrip.HasValue || !IsMounted || !grabInteractable.isSelected) return;
        if (pullingInteractor == null || !pullingInteractor.IsSelecting(grabInteractable))
        {
            pullingInteractor = grabInteractable.interactorsSelecting[0];
            ResetPullBaseline();
        }

        if (MountedSlot.TryGetBlockingDependent(out var blocker))
        {
            ReportRemovalBlocker(blocker);
            ResetPullBaseline();
            return;
        }
        reportedRemovalBlocker = null;
        if (!MountedSlot.requirePullToRemove)
        {
            Unmount();
            return;
        }

        if (MountedSlot.requireHostHeld && !IsHostHeldByAnotherInteractor())
        {
            ResetPullBaseline();
            return;
        }

        if (!pullBaselineReady) ResetPullBaseline();
        Vector3 handPosition = GrabUtility.GetInteractorPosition(pullingInteractor, grabInteractable);
        float distance = MountedSlot.MeasurePullDistance(pullStartInTargetSpace, handPosition);
        if (distance > 0.003f && Time.time >= nextPullHapticTime)
        {
            float progress = Mathf.Clamp01(distance / Mathf.Max(0.001f, MountedSlot.pullDistance));
            GrabUtility.SendHaptic(pullingInteractor, Mathf.Lerp(0.1f, 0.25f, progress), 0.05f);
            nextPullHapticTime = Time.time + 0.2f;
        }
        pullTimer = distance >= MountedSlot.pullDistance ? pullTimer + Time.deltaTime : 0f;
        if (distance >= MountedSlot.pullDistance && pullTimer >= MountedSlot.pullHoldSeconds)
        {
            GrabUtility.SendHaptic(pullingInteractor, 0.35f, 0.08f);
            Unmount();
        }
    }

    private void LateUpdate()
    {
        if (!IsMounted) return;

        // A mounted part has its own kinematic Rigidbody. Keep its physics pose
        // aligned to the moving host as well as maintaining the transform parent.
        Transform target = MountedSlot.Target;
        if (transform.parent != target)
            transform.SetParent(target, true);
        GetSeatedPose(target, out Vector3 position, out Quaternion rotation);
        transform.SetPositionAndRotation(position, rotation);
        partRigidbody.position = position;
        partRigidbody.rotation = rotation;
    }

    // Root pose that puts the alignment pivot exactly on the target.
    private void GetSeatedPose(Transform target, out Vector3 position, out Quaternion rotation)
    {
        Transform pivot = AlignmentPivot;
        Quaternion delta = target.rotation * Quaternion.Inverse(pivot.rotation);
        position = target.position + delta * (transform.position - pivot.position);
        rotation = delta * transform.rotation;
    }

    // Buzzes the holding hand(s) once each time the held part becomes aligned with a slot it would snap into.
    private void UpdateSnapReady()
    {
        if (IsMounted || !grabInteractable.isSelected)
        {
            snapReady = null;
            return;
        }
        bool ready = SearchNearbySlots().bestFit != null;
        // A short cooldown stops a hand trembling on the edge of the tolerance from buzzing repeatedly.
        if (ready && snapReady == false && snapReadyHaptic > 0f && Time.time >= nextSnapReadyHapticTime)
        {
            foreach (var interactor in grabInteractable.interactorsSelecting)
                GrabUtility.SendHaptic(interactor, snapReadyHaptic, 0.06f);
            nextSnapReadyHapticTime = Time.time + 0.3f;
        }
        snapReady = ready;
    }

    private void ReportRemovalBlocker(SnapPoint blocker)
    {
        if (reportedRemovalBlocker == blocker) return;
        reportedRemovalBlocker = blocker;
        MountedSlot.ReportOrderRejected(AssemblyOrderViolationKind.DependentStillMounted, this, blocker);
    }

    private bool IsHostHeldByAnotherInteractor()
    {
        if (hostGrab == null) return false;
        foreach (var interactor in hostGrab.interactorsSelecting)
            if (interactor != pullingInteractor) return true;
        return false;
    }

    private void ResetPullBaseline()
    {
        pullTimer = 0f;
        pullBaselineReady = pullingInteractor != null && IsMounted;
        if (pullBaselineReady)
            pullStartInTargetSpace = MountedSlot.Target.InverseTransformPoint(
                GrabUtility.GetInteractorPosition(pullingInteractor, grabInteractable));
    }

    private void EndSeatedGrip()
    {
        if (!seatedGrip.HasValue) return;
        seatedGrip.Value.Restore(grabInteractable);
        grabInteractable.retainTransformParent = preMount.retainTransformParent;
        seatedGrip = null;
        pullingInteractor = null;
        pullBaselineReady = false;
        pullTimer = 0f;
        nextPullHapticTime = 0f;
        reportedRemovalBlocker = null;
    }

    private void RebaseGrabAttachments()
    {
        if (!grabInteractable.useDynamicAttach) return;
        foreach (var interactor in grabInteractable.interactorsSelecting)
        {
            Transform partAttach = grabInteractable.GetAttachTransform(interactor);
            Transform handAttach = interactor.GetAttachTransform(grabInteractable);
            if (partAttach != null && partAttach != transform && partAttach.IsChildOf(transform) && handAttach != null)
                partAttach.SetPositionAndRotation(handAttach.position, handAttach.rotation);
        }
    }

    private void OnReleased(SelectExitEventArgs args)
    {
        if (grabInteractable.isSelected || !isActiveAndEnabled) return;
        if (pendingRelease != null) StopCoroutine(pendingRelease);
        pendingRelease = StartCoroutine(FinishRelease());
    }

    private IEnumerator FinishRelease()
    {
        // XR Grab completes its detach in Late update, after selectExited.
        yield return endOfFrame;
        pendingRelease = null;
        if (grabInteractable.isSelected) yield break;

        if (IsMounted)
        {
            EndSeatedGrip();
            partRigidbody.isKinematic = true;
            partRigidbody.useGravity = false;
            yield break;
        }
        RestoreAfterGrab();
        if (!grabInteractable.throwOnDetach && !partRigidbody.isKinematic)
            partRigidbody.angularVelocity = Vector3.zero;
        TryMountToNearbySlot();
    }

    private struct SlotSearch
    {
        public SnapPoint bestFit;             // Aligned, free and with every prerequisite in place.
        public SnapPoint blockedFit;          // Aligned, but still missing a prerequisite.
        public SnapPoint missingPrerequisite;
        public SnapPoint nearest;             // Nearest compatible slot, aligned or not.
        public float nearestDistance;
    }

    private SlotSearch SearchNearbySlots()
    {
        Transform pivot = AlignmentPivot;
        int hitCount = Physics.OverlapSphereNonAlloc(pivot.position, snapSearchRadius, overlapBuffer,
            Physics.AllLayers, QueryTriggerInteraction.Collide);
        var result = new SlotSearch { nearestDistance = float.MaxValue };
        float bestDistance = float.MaxValue;
        float blockedDistance = float.MaxValue;

        for (int i = 0; i < hitCount; i++)
        {
            SnapPoint slot = overlapBuffer[i].GetComponentInParent<SnapPoint>();
            if (slot == null || !slot.CanConsider(this)) continue;
            float distance = Vector3.Distance(pivot.position, slot.Target.position);
            if (distance < result.nearestDistance) { result.nearest = slot; result.nearestDistance = distance; }
            // Only a part that would actually fit is reported as blocked by assembly order;
            // a badly rotated part falls through to the normal "snap rejected" diagnostics.
            if (!slot.EvaluatePrecisionFit(pivot)) continue;
            if (slot.TryGetMissingPrerequisite(out var missing))
            {
                if (distance < blockedDistance)
                {
                    result.blockedFit = slot;
                    result.missingPrerequisite = missing;
                    blockedDistance = distance;
                }
                continue;
            }
            if (distance < bestDistance)
            {
                result.bestFit = slot;
                bestDistance = distance;
            }
        }
        System.Array.Clear(overlapBuffer, 0, hitCount);
        return result;
    }

    public bool TryMountToNearbySlot()
    {
        if (IsMounted || grabInteractable.isSelected) return false;
        Physics.SyncTransforms();
        SlotSearch search = SearchNearbySlots();

        if (search.bestFit != null && search.bestFit.TrySeat(this))
        {
            Snapped?.Invoke(this, search.bestFit);
            return true;
        }
        if (search.blockedFit != null)
        {
            search.blockedFit.ReportOrderRejected(AssemblyOrderViolationKind.MissingPrerequisite, this, search.missingPrerequisite);
            return false;
        }
        if (logSnapDiagnostics)
        {
            Transform pivot = AlignmentPivot;
            SnapPoint nearest = search.nearest;
            if (nearest == null)
                Debug.Log($"{name}: No available compatible snap point found within {snapSearchRadius:F3} m of the alignment pivot.", this);
            else
                Debug.Log($"{name}: Snap rejected by {nearest.name}. Distance: {search.nearestDistance:F3} m (maximum {nearest.maxPositionError:F3}); " +
                    $"forward alignment: {Vector3.Dot(pivot.forward, nearest.Target.forward):F3}, " +
                    $"up alignment: {Vector3.Dot(pivot.up, nearest.Target.up):F3} (minimum {nearest.minOrientationAlignment:F3}).", this);
        }
        return false;
    }

    internal bool SeatIn(SnapPoint slot)
    {
        if (IsMounted || slot == null || grabInteractable.isSelected) return false;
        Transform target = slot.Target;
        GetSeatedPose(target, out Vector3 targetPosition, out Quaternion targetRotation);

        preMount = PreMountState.Capture(transform, partRigidbody, grabInteractable);
        if (!partRigidbody.isKinematic)
        {
            partRigidbody.linearVelocity = Vector3.zero;
            partRigidbody.angularVelocity = Vector3.zero;
        }
        partRigidbody.isKinematic = true;
        partRigidbody.useGravity = false;
        transform.SetParent(target, true);
        transform.SetPositionAndRotation(targetPosition, targetRotation);
        MountedSlot = slot;
        hostGrab = slot.GetComponentInParent<XRGrabInteractable>();
        RefreshAssemblyCollisions(transform);
        return true;
    }

    public void Unmount()
    {
        if (!IsMounted) return;
        if (MountedSlot.TryGetBlockingDependent(out var blocker))
        {
            MountedSlot.ReportOrderRejected(AssemblyOrderViolationKind.DependentStillMounted, this, blocker);
            return;
        }
        ForceUnmount();
    }

    internal void ForceUnmount()
    {
        if (!IsMounted) return;
        bool resumeHeldBody = seatedGrip.HasValue && grabInteractable.isSelected;
        EndSeatedGrip();
        SnapPoint slot = MountedSlot;
        MountedSlot = null;
        hostGrab = null;
        slot.ClearSeat(this);
        // Unity forbids reparenting while this hierarchy is being deactivated or destroyed
        // (for example the case is disabled with parts inside); the part then stays with its host.
        if (gameObject.activeInHierarchy)
            transform.SetParent(preMount.parent, true);
        RestoreHostCollisions();
        // The assembly this part just left no longer needs its colliders ignored.
        RefreshAssemblyCollisions(slot.Target);
        if (grabInteractable.isSelected)
        {
            // Grab() already captured the seated Rigidbody and parent. Restore the
            // pre-seat values after XR Grab finishes dropping the part.
            grabInteractable.retainTransformParent = false;
            restoreAfterGrab = true;
            if (resumeHeldBody) ResumeHeldBody();
        }
        else
        {
            preMount.RestoreBody(partRigidbody, grabInteractable);
        }
    }

    private void ResumeHeldBody()
    {
        RebaseGrabAttachments();
        // XR Grab started this selection while the seated body was kinematic.
        var movement = grabInteractable.movementType;
        foreach (var interactor in grabInteractable.interactorsSelecting)
            if (interactor is XRBaseInteractor baseInteractor && baseInteractor.selectedInteractableMovementTypeOverride.HasValue)
                movement = baseInteractor.selectedInteractableMovementTypeOverride.Value;
        partRigidbody.isKinematic = movement != XRBaseInteractable.MovementType.VelocityTracking;
        partRigidbody.useGravity = false;
    }

    private void RestoreAfterGrab()
    {
        if (!restoreAfterGrab) return;
        preMount.RestoreBody(partRigidbody, grabInteractable);
        restoreAfterGrab = false;
    }

    // Seating or removing a part changes which colliders every mounted part above it carries,
    // so each of them refreshes its ignored pairs. This runs only when the assembly changes,
    // not every frame. Colliders toggled at runtime are picked up on the next assembly change.
    private static void RefreshAssemblyCollisions(Transform from)
    {
        if (from == null) return;
        from.GetComponentsInParent(false, assemblyBuffer);
        foreach (MountablePart part in assemblyBuffer)
            if (part.IsMounted) part.IgnoreHostCollisions(part.MountedSlot);
        assemblyBuffer.Clear();
    }

    private void IgnoreHostCollisions(SnapPoint slot)
    {
        Rigidbody host = slot.GetComponentInParent<Rigidbody>();
        if (host == null || host == partRigidbody) return;

        // A descendant can be removed while this assembly remains mounted.
        // Restore only the pairs this component created once they leave it.
        for (int i = ignoredHostCollisions.Count - 1; i >= 0; i--)
        {
            var pair = ignoredHostCollisions[i];
            if (pair.part != null && pair.host != null && pair.part.enabled && pair.host.enabled &&
                pair.part.gameObject.activeInHierarchy && pair.host.gameObject.activeInHierarchy &&
                pair.part.transform.IsChildOf(transform) && pair.host.attachedRigidbody == host)
                continue;
            if (pair.part != null && pair.host != null && pair.part.gameObject.activeInHierarchy &&
                pair.host.gameObject.activeInHierarchy)
                Physics.IgnoreCollision(pair.part, pair.host, false);
            ignoredHostCollisions.RemoveAt(i);
        }

        GetComponentsInChildren(true, partColliderBuffer);
        host.GetComponentsInChildren(true, hostColliderBuffer);
        foreach (Collider partCollider in partColliderBuffer)
        {
            if (!partCollider.enabled || !partCollider.gameObject.activeInHierarchy ||
                partCollider.attachedRigidbody == null || partCollider.attachedRigidbody == host ||
                partCollider.isTrigger) continue;
            foreach (Collider hostCollider in hostColliderBuffer)
            {
                if (!hostCollider.enabled || !hostCollider.gameObject.activeInHierarchy ||
                    hostCollider.attachedRigidbody != host || hostCollider.isTrigger ||
                    Physics.GetIgnoreCollision(partCollider, hostCollider)) continue;
                Physics.IgnoreCollision(partCollider, hostCollider, true);
                ignoredHostCollisions.Add((partCollider, hostCollider));
            }
        }
        partColliderBuffer.Clear();
        hostColliderBuffer.Clear();
    }

    private void RestoreHostCollisions()
    {
        foreach (var pair in ignoredHostCollisions)
        {
            if (pair.part != null && pair.host != null && pair.part.gameObject.activeInHierarchy &&
                pair.host.gameObject.activeInHierarchy)
                Physics.IgnoreCollision(pair.part, pair.host, false);
        }
        ignoredHostCollisions.Clear();
    }
}
