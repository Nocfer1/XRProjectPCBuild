using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

// A handheld remote holding the game's menu actions. Its RemoteButtons can only be pressed by a
// poke from one hand while the other hand holds the remote, so a dropped or thrown remote never
// presses anything, and neither does the hand holding it.
[RequireComponent(typeof(XRGrabInteractable), typeof(Rigidbody))]
public class RemoteControl : MonoBehaviour
{
    [Tooltip("Found in the scene when left empty.")]
    public BuildProgression progression;

    [Tooltip("Called by Back To Menu just before the main menu scene loads.")]
    public UnityEvent onBackToMenu = new UnityEvent();

    public XRGrabInteractable Grab { get; private set; }
    public bool IsHeld => Grab != null && Grab.isSelected;

    private void Awake()
    {
        Grab = GetComponent<XRGrabInteractable>();
        if (progression == null) progression = FindFirstObjectByType<BuildProgression>();
    }

    // True when presser is a poke from a hand other than the one(s) holding the remote.
    public bool CanPress(IXRInteractor presser)
    {
        if (!isActiveAndEnabled || !IsHeld || !(presser is XRPokeInteractor)) return false;
        foreach (IXRSelectInteractor holder in Grab.interactorsSelecting)
            if (SameHand(holder, presser)) return false;
        return true;
    }

    private static bool SameHand(IXRInteractor a, IXRInteractor b)
    {
        if (a.handedness != InteractorHandedness.None && b.handedness != InteractorHandedness.None)
            return a.handedness == b.handedness;
        // Handedness not set: the interactors of one controller share a parent in the XR rig.
        return a.transform.parent == b.transform.parent;
    }

    public void RestartFullPC()
    {
        if (progression != null) progression.FullRestart();
        else Debug.LogWarning($"{name}: No Build Progression to restart.", this);
    }

    public void ResetStage()
    {
        if (progression != null) progression.ResetCurrentStage();
        else Debug.LogWarning($"{name}: No Build Progression to reset.", this);
    }

    // Leaves the build for the main menu. The build is not kept: starting again begins from the first stage.
    public void BackToMenu()
    {
        onBackToMenu.Invoke();
        SceneFlow.LoadMenu();
    }
}
