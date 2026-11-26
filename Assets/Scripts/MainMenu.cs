using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;

// The title screen: any button on any device (controller, keyboard, mouse, gamepad) starts the build.
// Presses are ignored for a moment after the menu opens, so a button still being let go of after
// pressing Back To Menu does not skip straight past it. That moment is counted from the first smooth
// frame, and XR tracking/presence flags (which the Input System reports as buttons) are ignored, so
// a headset or controller coming online on a cold start does not count as a press.
public class MainMenu : MonoBehaviour
{
    [Tooltip("Seconds after the menu opens before a press starts the game.")]
    [Min(0f)] public float armDelay = 0.75f;
    [Tooltip("Scene loaded by a press.")]
    public string gameScene = SceneFlow.GameScene;

    [Tooltip("Shown once presses are accepted. Optional.")]
    public TMP_Text prompt;
    [Tooltip("Prompt opacity pulses between this and 1 while waiting for a press.")]
    [Range(0f, 1f)] public float promptMinAlpha = 0.35f;
    [Min(0f)] public float promptPulseSpeed = 2f;

    public UnityEvent onStart = new UnityEvent();

    public bool IsArmed => !started && Time.unscaledTime - openedAt >= armDelay;
    public bool HasStarted => started;

    private float openedAt;
    private bool started;
    private IDisposable anyButton;

    // A frame longer than this counts as a loading stall, so the arm timer does not start during it.
    private const float MaxSmoothFrameTime = 0.1f;

    private void OnEnable()
    {
        // Not armed until frames run smoothly, so a load or XR start-up stall cannot use up armDelay.
        openedAt = float.PositiveInfinity;
        started = false;
        anyButton = InputSystem.onAnyButtonPress.Call(OnAnyButton);
        UpdatePrompt();
    }

    private void OnDisable()
    {
        anyButton?.Dispose();
        anyButton = null;
    }

    private void Update()
    {
        if (float.IsPositiveInfinity(openedAt) && Time.unscaledDeltaTime < MaxSmoothFrameTime)
            openedAt = Time.unscaledTime;
        UpdatePrompt();
    }

    private void OnAnyButton(InputControl control)
    {
        if (IsStateFlag(control)) return;
        TryStart();
    }

    // XR devices expose tracking and presence state as buttons. These flip to "pressed" when a headset
    // or controller comes online, which is not a user press.
    private static bool IsStateFlag(InputControl control)
    {
        if (control.noisy || control.synthetic) return true;
        string name = control.name;
        return name == "isTracked" || name == "userPresence" || name.EndsWith("Touched");
    }

    // Starts the game if the menu is ready. Returns false if it was pressed too early or already started.
    public bool TryStart()
    {
        if (!isActiveAndEnabled || !IsArmed) return false;
        started = true;
        onStart.Invoke();
        if (!string.IsNullOrEmpty(gameScene)) SceneFlow.Load(gameScene);
        return true;
    }

    private void UpdatePrompt()
    {
        if (prompt == null) return;
        bool visible = IsArmed;
        if (prompt.enabled != visible) prompt.enabled = visible;
        if (!visible) return;
        float pulse = 0.5f + 0.5f * Mathf.Cos((Time.unscaledTime - openedAt - armDelay) * promptPulseSpeed * Mathf.PI);
        prompt.alpha = Mathf.Lerp(promptMinAlpha, 1f, pulse);
    }
}
