using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;

// The title screen: any button on any device (controller, keyboard, mouse, gamepad) starts the build.
// Presses are ignored for a moment after the menu opens, so a button still being let go of after
// pressing Back To Menu does not skip straight past it.
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

    private void OnEnable()
    {
        openedAt = Time.unscaledTime;
        started = false;
        anyButton = InputSystem.onAnyButtonPress.Call(_ => TryStart());
        UpdatePrompt();
    }

    private void OnDisable()
    {
        anyButton?.Dispose();
        anyButton = null;
    }

    private void Update() => UpdatePrompt();

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
