using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Feedback;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

[RequireComponent(typeof(XRGrabInteractable), typeof(Rigidbody))]
public class MountablePart : MonoBehaviour, IXRSelectFilter
{
    [Header("Component Data")]
    public SlotType compatibleSlotType;

    [Header("Alignment & Detection")]
    [Tooltip("Mating surface pose. Its position and axes must match the socket target when seated. Defaults to this object's transform.")]
    public Transform alignmentPivot;
    [Min(0f)] public float snapSearchRadius = 0.1f;
    [Tooltip("Log why a snap attempt failed when this part is released.")]
    public bool logSnapDiagnostics;

    public SnapPoint MountedSlot { get; private set; }
    public bool IsMounted => MountedSlot != null;
    public Transform AlignmentPivot => alignmentPivot != null ? alignmentPivot : transform;
    public bool canProcess => isActiveAndEnabled;

    private XRGrabInteractable grabInteractable;
    private Rigidbody partRigidbody;
    private Coroutine pendingRelease;
    private Transform parentBeforeMount;
    private bool wasKinematic;
    private bool usedGravity;
    private bool retainParentBeforeMount;
    private bool restoreAfterGrab;
    private bool seatedGripActive;
    private bool savedTrackPosition;
    private bool savedTrackRotation;
    private bool savedThrowOnDetach;
    private IXRSelectInteractor pullingInteractor;
    private Vector3 pullStartInTargetSpace;
    private float pullTimer;
    private float nextPullHapticTime;
    private bool pullBaselineReady;
    private SnapPoint reportedRemovalBlocker;
    private readonly List<(Collider part, Collider host)> ignoredHostCollisions = new List<(Collider, Collider)>();

    private void Awake()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();
        partRigidbody = GetComponent<Rigidbody>();
    }

    private void OnEnable()
    {
        grabInteractable.selectEntered.AddListener(OnGrabbed);
        grabInteractable.selectExited.AddListener(OnReleased);
        grabInteractable.selectFilters.Add(this);
    }

    private void OnDisable()
    {
        grabInteractable.selectEntered.RemoveListener(OnGrabbed);
        grabInteractable.selectExited.RemoveListener(OnReleased);
        grabInteractable.selectFilters.Remove(this);
        if (pendingRelease != null) StopCoroutine(pendingRelease);
        pendingRelease = null;
        EndSeatedGrip();
        ForceUnmount();
        RestoreAfterGrab();
    }

    public bool Process(IXRSelectInteractor interactor, IXRSelectInteractable interactable)
    {
        if (!IsMounted || interactor.IsSelecting(interactable)) return true;
        return MountedSlot.CanRemoveFrom(GetInteractorPosition(interactor, interactable), grabInteractable);
    }

    public static Vector3 GetInteractorPosition(IXRInteractor interactor, IXRInteractable interactable)
    {
        if (interactor is NearFarInteractor nearFar && nearFar.nearInteractionCaster != null)
        {
            var caster = nearFar.nearInteractionCaster;
            Transform origin = caster.effectiveCastOrigin != null ? caster.effectiveCastOrigin : caster.castOrigin;
            if (origin != null) return origin.position;
        }
        if (interactor is XRDirectInteractor direct) return direct.GetAttachTransform(interactable).position;
        return interactor.transform.position;
    }

    private void OnGrabbed(SelectEnterEventArgs args)
    {
        if (pendingRelease != null) StopCoroutine(pendingRelease);
        pendingRelease = null;
        if (!IsMounted) return;
        if (MountedSlot.TryGetBlockingDependent(out var blocker))
        {
            if (!seatedGripActive) BeginSeatedGrip(args.interactorObject);
            else MaintainSeatedGrip();
            ReportRemovalBlocker(blocker);
            return;
        }
        if (MountedSlot.requirePullToRemove)
        {
            if (!seatedGripActive) BeginSeatedGrip(args.interactorObject);
            else MaintainSeatedGrip();
        }
        else Unmount();
    }

    private void BeginSeatedGrip(IXRSelectInteractor interactor)
    {
        seatedGripActive = true;
        savedTrackPosition = grabInteractable.trackPosition;
        savedTrackRotation = grabInteractable.trackRotation;
        savedThrowOnDetach = grabInteractable.throwOnDetach;
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
        if (!seatedGripActive || !IsMounted || !grabInteractable.isSelected) return;
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
        Vector3 handPosition = GetInteractorPosition(pullingInteractor, grabInteractable);
        float distance = MountedSlot.MeasurePullDistance(pullStartInTargetSpace, handPosition);
        if (distance > 0.003f && Time.time >= nextPullHapticTime)
        {
            float progress = Mathf.Clamp01(distance / Mathf.Max(0.001f, MountedSlot.pullDistance));
            SendPullHaptic(pullingInteractor, Mathf.Lerp(0.1f, 0.25f, progress), 0.05f);
            nextPullHapticTime = Time.time + 0.2f;
        }
        pullTimer = distance >= MountedSlot.pullDistance ? pullTimer + Time.deltaTime : 0f;
        if (distance >= MountedSlot.pullDistance && pullTimer >= MountedSlot.pullHoldSeconds)
        {
            SendPullHaptic(pullingInteractor, 0.35f, 0.08f);
            Unmount();
        }
    }

    private static void SendPullHaptic(IXRInteractor interactor, float amplitude, float duration)
    {
        var feedback = interactor.transform.GetComponent<SimpleHapticFeedback>();
        if (feedback != null && feedback.hapticImpulsePlayer != null)
            feedback.hapticImpulsePlayer.SendHapticImpulse(amplitude, duration);
    }

    private void ReportRemovalBlocker(SnapPoint blocker)
    {
        if (reportedRemovalBlocker == blocker) return;
        reportedRemovalBlocker = blocker;
        MountedSlot.ReportOrderRejected(AssemblyOrderViolationKind.DependentStillMounted, this, blocker);
    }

    private bool IsHostHeldByAnotherInteractor()
    {
        XRGrabInteractable host = MountedSlot.GetComponentInParent<XRGrabInteractable>();
        if (host == null) return false;
        foreach (var interactor in host.interactorsSelecting)
            if (interactor != pullingInteractor) return true;
        return false;
    }

    private void ResetPullBaseline()
    {
        pullTimer = 0f;
        pullBaselineReady = pullingInteractor != null && IsMounted;
        if (pullBaselineReady)
            pullStartInTargetSpace = MountedSlot.Target.InverseTransformPoint(GetInteractorPosition(pullingInteractor, grabInteractable));
    }

    private void EndSeatedGrip()
    {
        if (!seatedGripActive) return;
        grabInteractable.trackPosition = savedTrackPosition;
        grabInteractable.trackRotation = savedTrackRotation;
        grabInteractable.throwOnDetach = savedThrowOnDetach;
        grabInteractable.retainTransformParent = retainParentBeforeMount;
        seatedGripActive = false;
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
        yield return new WaitForEndOfFrame();
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
        Vector3 angularBefore = partRigidbody.angularVelocity;
        if (!grabInteractable.throwOnDetach && !partRigidbody.isKinematic)
            partRigidbody.angularVelocity = Vector3.zero;
        if (logSnapDiagnostics)
            Debug.Log($"{name}: Release rotation: before={angularBefore}, after={partRigidbody.angularVelocity}, " +
                $"kinematic={partRigidbody.isKinematic}, throwOnDetach={grabInteractable.throwOnDetach}.", this);
        TryMountToNearbySlot();

        if (logSnapDiagnostics && !IsMounted)
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            if (!grabInteractable.isSelected && !IsMounted)
                Debug.Log($"{name}: Rotation after physics: angularVelocity={partRigidbody.angularVelocity}, " +
                    $"kinematic={partRigidbody.isKinematic}.", this);
        }
    }

    public bool TryMountToNearbySlot()
    {
        if (IsMounted || grabInteractable.isSelected) return false;
        Physics.SyncTransforms();
        Transform pivot = AlignmentPivot;
        Collider[] hits = Physics.OverlapSphere(pivot.position, snapSearchRadius,
            Physics.AllLayers, QueryTriggerInteraction.Collide);
        SnapPoint bestFit = null;
        SnapPoint nearest = null;
        SnapPoint blockedFit = null;
        SnapPoint missingPrerequisite = null;
        float bestDistance = float.MaxValue;
        float nearestDistance = float.MaxValue;
        float blockedDistance = float.MaxValue;

        foreach (Collider hit in hits)
        {
            SnapPoint slot = hit.GetComponentInParent<SnapPoint>();
            if (slot == null || !slot.CanConsider(this)) continue;
            float distance = Vector3.Distance(pivot.position, slot.Target.position);
            if (distance < nearestDistance) { nearest = slot; nearestDistance = distance; }
            if (distance <= slot.maxPositionError && slot.TryGetMissingPrerequisite(out var missing))
            {
                if (distance < blockedDistance)
                {
                    blockedFit = slot;
                    missingPrerequisite = missing;
                    blockedDistance = distance;
                }
                continue;
            }
            if (distance < bestDistance && slot.EvaluatePrecisionFit(pivot))
            {
                bestFit = slot;
                bestDistance = distance;
            }
        }

        if (bestFit != null && bestFit.TrySeat(this)) return true;
        if (blockedFit != null)
        {
            blockedFit.ReportOrderRejected(AssemblyOrderViolationKind.MissingPrerequisite, this, missingPrerequisite);
            return false;
        }
        if (logSnapDiagnostics)
        {
            if (nearest == null)
                Debug.Log($"{name}: No available compatible snap point found within {snapSearchRadius:F3} m of the alignment pivot.", this);
            else
                Debug.Log($"{name}: Snap rejected by {nearest.name}. Distance: {nearestDistance:F3} m (maximum {nearest.maxPositionError:F3}); " +
                    $"forward alignment: {Vector3.Dot(pivot.forward, nearest.Target.forward):F3}, " +
                    $"up alignment: {Vector3.Dot(pivot.up, nearest.Target.up):F3} (minimum {nearest.minOrientationAlignment:F3}).", this);
        }
        return false;
    }

    internal bool SeatIn(SnapPoint slot)
    {
        if (IsMounted || slot == null || grabInteractable.isSelected) return false;
        Transform pivot = AlignmentPivot;
        Transform target = slot.Target;
        Quaternion rotationDelta = target.rotation * Quaternion.Inverse(pivot.rotation);
        Vector3 rootOffset = transform.position - pivot.position;
        Vector3 targetPosition = target.position + rotationDelta * rootOffset;
        Quaternion targetRotation = rotationDelta * transform.rotation;

        parentBeforeMount = transform.parent;
        wasKinematic = partRigidbody.isKinematic;
        usedGravity = partRigidbody.useGravity;
        retainParentBeforeMount = grabInteractable.retainTransformParent;
        if (!partRigidbody.isKinematic)
        {
            partRigidbody.linearVelocity = Vector3.zero;
            partRigidbody.angularVelocity = Vector3.zero;
        }
        partRigidbody.isKinematic = true;
        partRigidbody.useGravity = false;
        IgnoreHostCollisions(slot);
        transform.SetParent(target, true);
        transform.SetPositionAndRotation(targetPosition, targetRotation);
        MountedSlot = slot;
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
        bool resumeHeldBody = seatedGripActive && grabInteractable.isSelected;
        EndSeatedGrip();
        SnapPoint slot = MountedSlot;
        MountedSlot = null;
        slot.ClearSeat(this);
        transform.SetParent(parentBeforeMount, true);
        RestoreHostCollisions();
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
            partRigidbody.isKinematic = wasKinematic;
            partRigidbody.useGravity = usedGravity;
            grabInteractable.retainTransformParent = retainParentBeforeMount;
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
        partRigidbody.isKinematic = wasKinematic;
        partRigidbody.useGravity = usedGravity;
        grabInteractable.retainTransformParent = retainParentBeforeMount;
        restoreAfterGrab = false;
    }

    private void IgnoreHostCollisions(SnapPoint slot)
    {
        Rigidbody host = slot.GetComponentInParent<Rigidbody>();
        if (host == null || host == partRigidbody) return;

        Collider[] partColliders = GetComponentsInChildren<Collider>(true);
        Collider[] hostColliders = host.GetComponentsInChildren<Collider>(true);
        foreach (Collider partCollider in partColliders)
        {
            if (!partCollider.enabled || !partCollider.gameObject.activeInHierarchy ||
                partCollider.attachedRigidbody != partRigidbody || partCollider.isTrigger) continue;
            foreach (Collider hostCollider in hostColliders)
            {
                if (!hostCollider.enabled || !hostCollider.gameObject.activeInHierarchy ||
                    hostCollider.attachedRigidbody != host || hostCollider.isTrigger ||
                    Physics.GetIgnoreCollision(partCollider, hostCollider)) continue;
                Physics.IgnoreCollision(partCollider, hostCollider, true);
                ignoredHostCollisions.Add((partCollider, hostCollider));
            }
        }
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
