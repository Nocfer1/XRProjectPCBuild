using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Feedback;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

// A poke button on a RemoteControl. The filters stop every interactor the remote does not allow
// (see RemoteControl.CanPress) from even hovering it, so the button cap does not move for them.
// The poke itself is set up with an XRPokeFilter, and the cap's travel with an XRPokeFollowAffordance.
[RequireComponent(typeof(XRSimpleInteractable))]
public class RemoteButton : MonoBehaviour, IXRHoverFilter, IXRSelectFilter
{
    [Tooltip("Found in the parents when left empty.")]
    public RemoteControl remote;
    [Tooltip("How long the button must stay pressed before it fires. 0 fires as soon as it is pressed.")]
    [Min(0f)] public float holdSeconds;
    public UnityEvent onPressed = new UnityEvent();

    [Header("Feedback")]
    public AudioSource clickSound;
    [Range(0f, 1f)] public float pressHaptic = 0.15f;
    [Range(0f, 1f)] public float holdHapticMax = 0.3f;
    [Range(0f, 1f)] public float firedHaptic = 0.5f;

    public bool canProcess => isActiveAndEnabled;
    public bool IsPressed => presser != null;
    // 0 to 1 while a hold is in progress.
    public float HoldProgress => presser == null ? 0f : holdSeconds <= 0f ? 1f : Mathf.Clamp01((Time.time - pressStart) / holdSeconds);

    private XRSimpleInteractable interactable;
    private IXRSelectInteractor presser;
    private float pressStart;
    private float nextHoldHaptic;
    private bool fired;

    private void Awake()
    {
        interactable = GetComponent<XRSimpleInteractable>();
        if (remote == null) remote = GetComponentInParent<RemoteControl>();
    }

    private void OnEnable()
    {
        interactable.hoverFilters.Add(this);
        interactable.selectFilters.Add(this);
        interactable.selectEntered.AddListener(OnSelectEntered);
        interactable.selectExited.AddListener(OnSelectExited);
    }

    private void OnDisable()
    {
        interactable.hoverFilters.Remove(this);
        interactable.selectFilters.Remove(this);
        interactable.selectEntered.RemoveListener(OnSelectEntered);
        interactable.selectExited.RemoveListener(OnSelectExited);
        presser = null;
    }

    public bool Process(IXRHoverInteractor interactor, IXRHoverInteractable target) => Allows(interactor);
    public bool Process(IXRSelectInteractor interactor, IXRSelectInteractable target) => Allows(interactor);
    private bool Allows(IXRInteractor interactor) => remote != null && remote.CanPress(interactor);

    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        if (presser != null || !Allows(args.interactorObject)) return;
        presser = args.interactorObject;
        pressStart = Time.time;
        nextHoldHaptic = Time.time + 0.1f;
        fired = false;
        SendHaptic(presser, pressHaptic, 0.03f);
        if (holdSeconds <= 0f) Fire();
    }

    private void OnSelectExited(SelectExitEventArgs args)
    {
        if (args.interactorObject == presser) presser = null;
    }

    private void Update()
    {
        if (presser == null || fired) return;
        // The holding hand let go (or swapped) mid-press: the press is cancelled.
        if (!Allows(presser))
        {
            presser = null;
            return;
        }
        if (Time.time >= nextHoldHaptic)
        {
            SendHaptic(presser, Mathf.Lerp(pressHaptic, holdHapticMax, HoldProgress), 0.05f);
            nextHoldHaptic = Time.time + 0.1f;
        }
        if (Time.time - pressStart >= holdSeconds) Fire();
    }

    private void Fire()
    {
        fired = true;
        SendHaptic(presser, firedHaptic, 0.08f);
        if (clickSound != null) clickSound.Play();
        onPressed.Invoke();
    }

    private static void SendHaptic(IXRInteractor interactor, float amplitude, float duration)
    {
        if (interactor == null || amplitude <= 0f) return;
        HapticImpulsePlayer player = null;
        var feedback = interactor.transform.GetComponent<SimpleHapticFeedback>();
        if (feedback != null) player = feedback.hapticImpulsePlayer;
        if (player == null) player = interactor.transform.GetComponentInParent<HapticImpulsePlayer>();
        if (player != null) player.SendHapticImpulse(amplitude, duration);
    }
}
