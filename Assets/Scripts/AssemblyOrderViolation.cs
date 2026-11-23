using System;
using UnityEngine.Events;

public enum AssemblyOrderViolationKind { MissingPrerequisite, DependentStillMounted }

// Carries the exact objects involved so a UI can choose its own wording.
[Serializable]
public class AssemblyOrderViolation
{
    public AssemblyOrderViolationKind kind;
    public SnapPoint attemptedSlot;
    public MountablePart attemptedPart;
    public SnapPoint blockingSlot;
}

[Serializable]
public class AssemblyOrderViolationEvent : UnityEvent<AssemblyOrderViolation> { }
