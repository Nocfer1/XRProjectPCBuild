using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Feedback;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

// Shared hand-position and reach checks used by the grab filters and mounting logic.
public static class GrabUtility
{
    // The point a hand grabs from: the near caster origin for Near-Far, the attach point for Direct.
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

    // True when any active interaction collider of the grab is within radius of position.
    public static bool AnyColliderWithin(XRGrabInteractable grab, Vector3 position, float radius)
    {
        float radiusSquared = radius * radius;
        foreach (Collider collider in grab.colliders)
        {
            if (collider != null && collider.enabled && collider.gameObject.activeInHierarchy &&
                (collider.ClosestPoint(position) - position).sqrMagnitude <= radiusSquared)
                return true;
        }
        return false;
    }

    public static bool IsHeld(Component component)
    {
        var grab = component != null ? component.GetComponent<XRGrabInteractable>() : null;
        return grab != null && grab.isSelected;
    }

    // Buzzes the controller behind interactor, through its SimpleHapticFeedback or a parent HapticImpulsePlayer.
    public static void SendHaptic(IXRInteractor interactor, float amplitude, float duration)
    {
        if (interactor == null || amplitude <= 0f) return;
        HapticImpulsePlayer player = null;
        var feedback = interactor.transform.GetComponent<SimpleHapticFeedback>();
        if (feedback != null) player = feedback.hapticImpulsePlayer;
        if (player == null) player = interactor.transform.GetComponentInParent<HapticImpulsePlayer>();
        if (player != null) player.SendHapticImpulse(amplitude, duration);
    }

    // Takes the object out of every hand holding it.
    public static void ForceRelease(Component component)
    {
        var grab = component != null ? component.GetComponent<XRGrabInteractable>() : null;
        if (grab != null && grab.isSelected && grab.interactionManager != null)
            grab.interactionManager.CancelInteractableSelection((IXRSelectInteractable)grab);
    }
}
