using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;

// Assembly-CSharp is a predefined Unity assembly, so tests in an asmdef use reflection.
public class SnapLogicTests
{
    private readonly List<GameObject> objects = new List<GameObject>();
    private Type PartType => Type.GetType("MountablePart, Assembly-CSharp", true);
    private Type SlotType => Type.GetType("SnapPoint, Assembly-CSharp", true);

    private GameObject CreateObject(string name)
    {
        var obj = new GameObject(name);
        objects.Add(obj);
        return obj;
    }

    // Interactors register with the first active manager, and an earlier test can leave XRI's
    // auto-created default one behind; selecting through a second manager is then ignored.
    private XRInteractionManager GetInteractionManager()
    {
        var existing = UnityEngine.Object.FindAnyObjectByType<XRInteractionManager>();
        return existing != null ? existing : CreateObject("manager").AddComponent<XRInteractionManager>();
    }

    private static object Call(Component obj, string name, params object[] args) =>
        obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public).Invoke(obj, args);
    private static object Get(Component obj, string name) => obj.GetType().GetProperty(name).GetValue(obj);
    private static void Set(Component obj, string name, object value) => obj.GetType().GetField(name).SetValue(obj, value);

    // Mirrors XRI: selection is allowed only if every registered select filter passes.
    private static bool PassesSelectFilters(XRGrabInteractable grab, IXRSelectInteractor interactor)
    {
        var filters = new List<IXRSelectFilter>();
        grab.selectFilters.GetAll(filters);
        foreach (IXRSelectFilter filter in filters)
            if (filter.canProcess && !filter.Process(interactor, grab)) return false;
        return true;
    }

    private static void CountViolations(out Func<int> count, out Delegate handler)
    {
        int seen = 0;
        Action<object> capture = value => seen++;
        EventInfo rejected = Type.GetType("SnapPoint, Assembly-CSharp", true)
            .GetEvent("OrderRejected", BindingFlags.Public | BindingFlags.Static);
        handler = Delegate.CreateDelegate(rejected.EventHandlerType, capture.Target, capture.Method);
        rejected.AddEventHandler(null, handler);
        count = () => seen;
    }

    private static void StopCountingViolations(Delegate handler) =>
        Type.GetType("SnapPoint, Assembly-CSharp", true)
            .GetEvent("OrderRejected", BindingFlags.Public | BindingFlags.Static).RemoveEventHandler(null, handler);

    private Component CreateSlot(string name, Vector3 position, Quaternion rotation)
    {
        GameObject obj = CreateObject(name);
        obj.transform.SetPositionAndRotation(position, rotation);
        obj.AddComponent<SphereCollider>().isTrigger = true;
        return obj.AddComponent(SlotType);
    }

    [UnityTest]
    public IEnumerator SelectsNearestValidSocketAndAlignsOffsetPivot()
    {
        GameObject partObject = CreateObject("part");
        Component part = partObject.AddComponent(PartType);
        Transform pivot = CreateObject("pivot").transform;
        pivot.SetParent(partObject.transform, false);
        pivot.localPosition = new Vector3(0.005f, 0f, 0f);
        Set(part, "alignmentPivot", pivot);
        Component invalid = CreateSlot("near wrong rotation", pivot.position + Vector3.right * 0.003f, Quaternion.Euler(0, 90, 0));
        Component valid = CreateSlot("valid", pivot.position + Vector3.right * 0.01f, Quaternion.identity);

        Assert.That((bool)Call(part, "TryMountToNearbySlot"), Is.True);
        Assert.That(Get(part, "MountedSlot"), Is.SameAs(valid));
        Assert.That((bool)Get(invalid, "isOccupied"), Is.False);
        Assert.That(Vector3.Distance(pivot.position, valid.transform.position), Is.LessThan(0.0001f));
        Assert.That(Quaternion.Angle(pivot.rotation, valid.transform.rotation), Is.LessThan(0.01f));
        yield return null;
    }

    [UnityTest]
    public IEnumerator OccupancyAndPhysicsAreRestoredOnUnmount()
    {
        GameObject partObject = CreateObject("part");
        Component part = partObject.AddComponent(PartType);
        Rigidbody body = partObject.GetComponent<Rigidbody>();
        body.isKinematic = false;
        body.useGravity = true;
        Component slot = CreateSlot("socket", partObject.transform.position, Quaternion.identity);

        Assert.That((bool)Call(part, "TryMountToNearbySlot"), Is.True);
        Assert.That((bool)Get(slot, "isOccupied"), Is.True);
        Assert.That(body.isKinematic, Is.True);
        Component other = CreateObject("other").AddComponent(PartType);
        Assert.That((bool)Call(slot, "TrySeat", other), Is.False);

        Call(part, "Unmount");
        Assert.That((bool)Get(slot, "isOccupied"), Is.False);
        Assert.That((bool)Get(part, "IsMounted"), Is.False);
        Assert.That(body.isKinematic, Is.False);
        Assert.That(body.useGravity, Is.True);
        yield return null;
    }

    [UnityTest]
    public IEnumerator SeatedPartIgnoresHostCollisionsAndRestoresThemOnRemoval()
    {
        GameObject host = CreateObject("motherboard");
        host.AddComponent<Rigidbody>().isKinematic = true;
        Collider hostCollider = host.AddComponent<BoxCollider>();
        Component slot = CreateSlot("socket", Vector3.zero, Quaternion.identity);
        slot.transform.SetParent(host.transform, true);

        GameObject partObject = CreateObject("cpu");
        Collider partCollider = partObject.AddComponent<BoxCollider>();
        Component part = partObject.AddComponent(PartType);
        Collider otherCollider = CreateObject("other object").AddComponent<BoxCollider>();

        Assert.That((bool)Call(part, "TryMountToNearbySlot"), Is.True);
        Assert.That(Physics.GetIgnoreCollision(partCollider, hostCollider), Is.True);
        Assert.That(Physics.GetIgnoreCollision(partCollider, otherCollider), Is.False);

        Call(part, "Unmount");
        Assert.That(Physics.GetIgnoreCollision(partCollider, hostCollider), Is.False);
        Physics.IgnoreCollision(partCollider, hostCollider, true);
        Assert.That((bool)Call(part, "TryMountToNearbySlot"), Is.True);
        Call(part, "Unmount");
        Assert.That(Physics.GetIgnoreCollision(partCollider, hostCollider), Is.True);
        yield return null;
    }

    [UnityTest]
    public IEnumerator PcPartsRequireTheNearCasterAndKeepTheFilterAfterReparenting()
    {
        GameObject root = CreateObject("PC Components");
        GameObject part = CreateObject("part");
        part.transform.SetParent(root.transform, false);
        BoxCollider collider = part.AddComponent<BoxCollider>();
        collider.size = Vector3.one * 0.02f;
        XRGrabInteractable grab = part.AddComponent<XRGrabInteractable>();
        Type filterType = Type.GetType("NearOnlyGrabFilter, Assembly-CSharp", true);
        IXRSelectFilter filter = (IXRSelectFilter)root.AddComponent(filterType);

        GameObject hand = CreateObject("hand");
        NearFarInteractor nearFar = hand.AddComponent<NearFarInteractor>();
        SphereInteractionCaster sphere = nearFar.nearInteractionCaster as SphereInteractionCaster;
        Assert.That(sphere, Is.Not.Null);
        sphere.castRadius = 0.1f;
        sphere.castOrigin = hand.transform;
        hand.transform.position = new Vector3(0.5f, 0f, 0f);
        Assert.That(filter.Process(nearFar, grab), Is.False);

        hand.transform.position = new Vector3(0.05f, 0f, 0f);
        Assert.That(filter.Process(nearFar, grab), Is.True);
        part.transform.SetParent(null, true);
        var filters = new List<IXRSelectFilter>();
        grab.selectFilters.GetAll(filters);
        Assert.That(filters, Does.Contain(filter));
        hand.transform.position = new Vector3(0.5f, 0f, 0f);
        Assert.That(filter.Process(nearFar, grab), Is.False);
        XRRayInteractor ray = CreateObject("ray").AddComponent<XRRayInteractor>();
        Assert.That(filter.Process(ray, grab), Is.False);
        XRDirectInteractor direct = CreateObject("direct hand").AddComponent<XRDirectInteractor>();
        Assert.That(filter.Process(direct, grab), Is.True);
        yield return null;
    }

    [UnityTest]
    public IEnumerator MountedPartRequiresCloseFrontApproachButLoosePartKeepsNormalRange()
    {
        GameObject root = CreateObject("PC Components");
        GameObject cpu = CreateObject("cpu");
        cpu.transform.SetParent(root.transform, false);
        cpu.AddComponent<BoxCollider>().size = Vector3.one * 0.02f;
        Component part = cpu.AddComponent(PartType);
        XRGrabInteractable grab = cpu.GetComponent<XRGrabInteractable>();
        Type filterType = Type.GetType("NearOnlyGrabFilter, Assembly-CSharp", true);
        root.AddComponent(filterType);
        Component slot = CreateSlot("socket", Vector3.zero, Quaternion.identity);
        Set(slot, "limitGrabDistance", true);
        Set(slot, "mountedGrabDistance", 0.015f);
        Set(slot, "requireApproachDirection", true);
        Assert.That((bool)Call(slot, "TrySeat", part), Is.True);

        GameObject hand = CreateObject("hand");
        NearFarInteractor nearFar = hand.AddComponent<NearFarInteractor>();
        SphereInteractionCaster sphere = nearFar.nearInteractionCaster as SphereInteractionCaster;
        Assert.That(sphere, Is.Not.Null);
        sphere.castRadius = 0.1f;
        sphere.castOrigin = hand.transform;
        Physics.SyncTransforms();

        hand.transform.position = new Vector3(0f, 0.02f, 0f);
        Assert.That(PassesSelectFilters(grab, nearFar), Is.True, "Close approach from the front");
        hand.transform.position = new Vector3(0f, -0.02f, 0f);
        Assert.That(PassesSelectFilters(grab, nearFar), Is.False, "Behind the socket");
        hand.transform.position = new Vector3(0.04f, 0.005f, 0f);
        Assert.That(PassesSelectFilters(grab, nearFar), Is.False, "Too far to the side while seated");
        Call(part, "Unmount");
        Physics.SyncTransforms();
        Assert.That(PassesSelectFilters(grab, nearFar), Is.True, "Normal near range after removal");
        yield return null;
    }

    [UnityTest]
    public IEnumerator RemovalSettingsAreIndependentPerSocketAndCanLockPart()
    {
        GameObject root = CreateObject("PC Components");
        GameObject cpu = CreateObject("cpu");
        cpu.transform.SetParent(root.transform, false);
        cpu.AddComponent<BoxCollider>().size = Vector3.one * 0.02f;
        Component part = cpu.AddComponent(PartType);
        XRGrabInteractable grab = cpu.GetComponent<XRGrabInteractable>();
        Type filterType = Type.GetType("NearOnlyGrabFilter, Assembly-CSharp", true);
        root.AddComponent(filterType);
        Component cpuSlot = CreateSlot("cpu socket", Vector3.zero, Quaternion.identity);
        Component otherSlot = CreateSlot("other socket", new Vector3(1f, 0f, 0f), Quaternion.identity);
        Set(cpuSlot, "limitGrabDistance", true);
        Set(cpuSlot, "requireApproachDirection", true);
        Assert.That((bool)Call(cpuSlot, "TrySeat", part), Is.True);

        GameObject hand = CreateObject("hand");
        NearFarInteractor nearFar = hand.AddComponent<NearFarInteractor>();
        SphereInteractionCaster sphere = (SphereInteractionCaster)nearFar.nearInteractionCaster;
        sphere.castRadius = 0.1f;
        sphere.castOrigin = hand.transform;
        hand.transform.position = new Vector3(0f, -0.02f, 0f);
        Physics.SyncTransforms();
        Assert.That(PassesSelectFilters(grab, nearFar), Is.False, "CPU rejects behind");
        Assert.That((bool)Call(otherSlot, "CanRemoveFrom", hand.transform.position, grab), Is.True,
            "Other socket defaults to proximity-only");

        hand.transform.position = new Vector3(0f, 0.02f, 0f);
        Set(cpuSlot, "allowRemoval", false);
        Assert.That(PassesSelectFilters(grab, nearFar), Is.False);
        Assert.That(((IXRSelectFilter)part).Process(nearFar, grab), Is.False,
            "Non-removable policy also blocks selection without the group filter");

        Set(cpuSlot, "allowRemoval", true);
        Set(cpuSlot, "limitGrabDistance", false);
        Transform sideApproach = CreateObject("side approach").transform;
        sideApproach.rotation = Quaternion.Euler(0f, 0f, 90f);
        Set(cpuSlot, "removalApproach", sideApproach);
        GameObject regionObject = CreateObject("grip region");
        regionObject.transform.position = new Vector3(-0.005f, 0f, 0f);
        SphereCollider region = regionObject.AddComponent<SphereCollider>();
        region.radius = 0.006f;
        region.isTrigger = true;
        Set(cpuSlot, "removalGripRegion", region);
        Physics.SyncTransforms();
        Assert.That((bool)Call(cpuSlot, "CanRemoveFrom", new Vector3(-0.005f, 0f, 0f), grab), Is.True);
        Assert.That((bool)Call(cpuSlot, "CanRemoveFrom", new Vector3(0.005f, 0f, 0f), grab), Is.False,
            "Wrong side of approach plane");
        Assert.That((bool)Call(cpuSlot, "CanRemoveFrom", new Vector3(-0.03f, 0f, 0f), grab), Is.False,
            "Outside grip region");
        yield return null;
    }

    [UnityTest]
    public IEnumerator PullRequiresAnotherHandAndTracksTheMovingHost()
    {
        XRInteractionManager manager = GetInteractionManager();
        TestGrabInteractor boardHand = CreateObject("board hand").AddComponent<TestGrabInteractor>();
        TestGrabInteractor cpuHand = CreateObject("cpu hand").AddComponent<TestGrabInteractor>();
        GameObject board = CreateObject("board");
        board.AddComponent<BoxCollider>();
        XRGrabInteractable boardGrab = board.AddComponent<XRGrabInteractable>();
        Component slot = CreateSlot("socket", Vector3.zero, Quaternion.identity);
        slot.transform.SetParent(board.transform, true);
        Set(slot, "requirePullToRemove", true);
        Set(slot, "requireHostHeld", true);
        Set(slot, "pullDistance", 0.03f);
        Set(slot, "pullHoldSeconds", 0f);

        GameObject cpu = CreateObject("cpu");
        cpu.AddComponent<BoxCollider>();
        Component part = cpu.AddComponent(PartType);
        XRGrabInteractable cpuGrab = cpu.GetComponent<XRGrabInteractable>();
        cpuGrab.useDynamicAttach = true;
        // Matches the scene's parts; only velocity tracking keeps a held body non-kinematic.
        cpuGrab.movementType = XRBaseInteractable.MovementType.VelocityTracking;
        Assert.That((bool)Call(slot, "TrySeat", part), Is.True);
        manager.SelectEnter((IXRSelectInteractor)cpuHand, (IXRSelectInteractable)cpuGrab);
        Assert.That(cpuGrab.isSelected, Is.True);
        Assert.That((bool)Get(part, "IsMounted"), Is.True, "Gripping alone must not unmount");
        Assert.That(cpu.GetComponent<Rigidbody>().isKinematic, Is.True);

        cpuHand.transform.position = Vector3.up * 0.1f;
        part.GetType().GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(part, null);
        Assert.That((bool)Get(part, "IsMounted"), Is.True, "Host must be held by another hand");
        manager.SelectEnter((IXRSelectInteractor)boardHand, (IXRSelectInteractable)boardGrab);
        Assert.That(boardGrab.isSelected, Is.True);
        board.transform.position = Vector3.right;
        cpuHand.transform.position += Vector3.right;
        part.GetType().GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(part, null);
        Assert.That((bool)Get(part, "IsMounted"), Is.True, "Moving both together is not a pull");

        cpuHand.transform.position += Vector3.up * 0.04f;
        part.GetType().GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(part, null);
        Assert.That((bool)Get(part, "IsMounted"), Is.False);
        Assert.That(cpuGrab.isSelected, Is.True, "CPU remains in the pulling hand");
        Assert.That(cpu.GetComponent<Rigidbody>().isKinematic, Is.False);
        manager.SelectExit((IXRSelectInteractor)cpuHand, (IXRSelectInteractable)cpuGrab);
        manager.SelectExit((IXRSelectInteractor)boardHand, (IXRSelectInteractable)boardGrab);
        yield return null;
    }

    [UnityTest]
    public IEnumerator ReleasingAndRegrabbingWithoutPullLeavesPartSeated()
    {
        XRInteractionManager manager = GetInteractionManager();
        TestGrabInteractor hand = CreateObject("hand").AddComponent<TestGrabInteractor>();
        Component slot = CreateSlot("socket", Vector3.zero, Quaternion.identity);
        Set(slot, "requirePullToRemove", true);
        GameObject cpu = CreateObject("cpu");
        cpu.AddComponent<BoxCollider>();
        Component part = cpu.AddComponent(PartType);
        XRGrabInteractable grab = cpu.GetComponent<XRGrabInteractable>();
        grab.useDynamicAttach = true;
        Assert.That((bool)Call(slot, "TrySeat", part), Is.True);

        manager.SelectEnter((IXRSelectInteractor)hand, (IXRSelectInteractable)grab);
        manager.SelectExit((IXRSelectInteractor)hand, (IXRSelectInteractable)grab);
        manager.SelectEnter((IXRSelectInteractor)hand, (IXRSelectInteractable)grab);
        Assert.That((bool)Get(part, "IsMounted"), Is.True);
        Assert.That(cpu.transform.parent, Is.SameAs(slot.transform));
        Assert.That(cpu.GetComponent<Rigidbody>().isKinematic, Is.True);
        Assert.That(grab.trackPosition, Is.False);

        manager.SelectExit((IXRSelectInteractor)hand, (IXRSelectInteractable)grab);
        yield return new WaitForEndOfFrame();
        yield return null;
        Assert.That((bool)Get(part, "IsMounted"), Is.True);
        Assert.That((bool)Get(slot, "isOccupied"), Is.True);
        Assert.That(grab.trackPosition, Is.True);
        Assert.That(cpu.GetComponent<Rigidbody>().isKinematic, Is.True);
    }

    [UnityTest]
    public IEnumerator HeftUsesLighterFollowForTwoHandsAndRestoresSettings()
    {
        XRInteractionManager manager = GetInteractionManager();
        TestGrabInteractor first = CreateObject("first").AddComponent<TestGrabInteractor>();
        TestGrabInteractor second = CreateObject("second").AddComponent<TestGrabInteractor>();
        GameObject obj = CreateObject("heavy object");
        obj.AddComponent<BoxCollider>();
        XRGrabInteractable grab = obj.AddComponent<XRGrabInteractable>();
        grab.selectMode = InteractableSelectMode.Multiple;
        bool originalSmooth = grab.smoothPosition;
        float originalAmount = grab.smoothPositionAmount;
        Type heftType = Type.GetType("GrabHeft, Assembly-CSharp", true);
        obj.AddComponent(heftType);

        manager.SelectEnter((IXRSelectInteractor)first, (IXRSelectInteractable)grab);
        Assert.That(grab.smoothPosition, Is.True);
        Assert.That(grab.smoothPositionAmount, Is.EqualTo(5f));
        manager.SelectEnter((IXRSelectInteractor)second, (IXRSelectInteractable)grab);
        Assert.That(grab.interactorsSelecting.Count, Is.EqualTo(2));
        Assert.That(grab.smoothPositionAmount, Is.EqualTo(12f));
        manager.SelectExit((IXRSelectInteractor)first, (IXRSelectInteractable)grab);
        Assert.That(grab.smoothPositionAmount, Is.EqualTo(5f));
        manager.SelectExit((IXRSelectInteractor)second, (IXRSelectInteractable)grab);
        Assert.That(grab.smoothPosition, Is.EqualTo(originalSmooth));
        Assert.That(grab.smoothPositionAmount, Is.EqualTo(originalAmount));
        yield return null;
    }

    [UnityTest]
    public IEnumerator PrerequisitesBlockMountAndDependentBlocksRemovalWithOneEventEach()
    {
        Component cpuSlot = CreateSlot("CPU socket", Vector3.zero, Quaternion.identity);
        Component fanSlot = CreateSlot("fan socket", Vector3.zero, Quaternion.identity);
        var required = (IList)fanSlot.GetType().GetField("requiredOccupiedSlots").GetValue(fanSlot);
        required.Add(cpuSlot);
        var slotType = fanSlot.GetType().GetField("acceptedSlotType").FieldType;
        object coolerType = Enum.Parse(slotType, "CPUCooler");
        Set(fanSlot, "acceptedSlotType", coolerType);
        Component cpu = CreateObject("CPU").AddComponent(PartType);
        Component fan = CreateObject("fan").AddComponent(PartType);
        Set(fan, "compatibleSlotType", coolerType);
        Physics.SyncTransforms();

        EventInfo rejected = SlotType.GetEvent("OrderRejected", BindingFlags.Public | BindingFlags.Static);
        int count = 0;
        object lastViolation = null;
        Action<object> capture = value => { count++; lastViolation = value; };
        Delegate handler = Delegate.CreateDelegate(rejected.EventHandlerType, capture.Target, capture.Method);
        rejected.AddEventHandler(null, handler);
        try
        {
            Assert.That((bool)Call(fan, "TryMountToNearbySlot"), Is.False);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(lastViolation.GetType().GetField("kind").GetValue(lastViolation).ToString(), Is.EqualTo("MissingPrerequisite"));
            Assert.That(lastViolation.GetType().GetField("blockingSlot").GetValue(lastViolation), Is.SameAs(cpuSlot));

            Assert.That((bool)Call(cpuSlot, "TrySeat", cpu), Is.True);
            Assert.That((bool)Call(fan, "TryMountToNearbySlot"), Is.True);
            Call(cpu, "Unmount");
            Assert.That((bool)Get(cpu, "IsMounted"), Is.True);
            Assert.That(count, Is.EqualTo(2));
            Assert.That(lastViolation.GetType().GetField("kind").GetValue(lastViolation).ToString(), Is.EqualTo("DependentStillMounted"));
            Assert.That(lastViolation.GetType().GetField("blockingSlot").GetValue(lastViolation), Is.SameAs(fanSlot));

            Call(fan, "Unmount");
            Call(cpu, "Unmount");
            Assert.That((bool)Get(cpu, "IsMounted"), Is.False);
            Assert.That(count, Is.EqualTo(2));
        }
        finally
        {
            rejected.RemoveEventHandler(null, handler);
        }
        yield return null;
    }

    [UnityTest]
    public IEnumerator BlockedGrabKeepsPartMountedAndDoesNotSpamEvents()
    {
        XRInteractionManager manager = GetInteractionManager();
        TestGrabInteractor hand = CreateObject("hand").AddComponent<TestGrabInteractor>();
        Component cpuSlot = CreateSlot("CPU socket", Vector3.zero, Quaternion.identity);
        Component fanSlot = CreateSlot("fan socket", Vector3.zero, Quaternion.identity);
        ((IList)fanSlot.GetType().GetField("requiredOccupiedSlots").GetValue(fanSlot)).Add(cpuSlot);
        GameObject cpuObject = CreateObject("CPU");
        cpuObject.AddComponent<BoxCollider>();
        Component cpu = cpuObject.AddComponent(PartType);
        XRGrabInteractable grab = cpuObject.GetComponent<XRGrabInteractable>();
        Component fan = CreateObject("fan").AddComponent(PartType);
        Assert.That((bool)Call(cpuSlot, "TrySeat", cpu), Is.True);
        Assert.That((bool)Call(fanSlot, "TrySeat", fan), Is.True);

        EventInfo rejected = SlotType.GetEvent("OrderRejected", BindingFlags.Public | BindingFlags.Static);
        int count = 0;
        Action<object> capture = value => count++;
        Delegate handler = Delegate.CreateDelegate(rejected.EventHandlerType, capture.Target, capture.Method);
        rejected.AddEventHandler(null, handler);
        try
        {
            manager.SelectEnter((IXRSelectInteractor)hand, (IXRSelectInteractable)grab);
            Assert.That(grab.isSelected, Is.True);
            Assert.That((bool)Get(cpu, "IsMounted"), Is.True);
            Assert.That(count, Is.EqualTo(1));
            MethodInfo update = cpu.GetType().GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
            update.Invoke(cpu, null);
            update.Invoke(cpu, null);
            Assert.That((bool)Get(cpu, "IsMounted"), Is.True);
            Assert.That(count, Is.EqualTo(1));
            manager.SelectExit((IXRSelectInteractor)hand, (IXRSelectInteractable)grab);
        }
        finally
        {
            rejected.RemoveEventHandler(null, handler);
        }
        yield return null;
    }

    [UnityTest]
    public IEnumerator GpuSocketCanDependOnMotherboardBeingMountedInCase()
    {
        Component caseSlot = CreateSlot("case motherboard mount", Vector3.zero, Quaternion.identity);
        Component gpuSlot = CreateSlot("motherboard PCIe slot", Vector3.zero, Quaternion.identity);
        var slotType = caseSlot.GetType().GetField("acceptedSlotType").FieldType;
        object motherboardType = Enum.Parse(slotType, "Motherboard");
        object gpuType = Enum.Parse(slotType, "PCIe");
        Set(caseSlot, "acceptedSlotType", motherboardType);
        Set(gpuSlot, "acceptedSlotType", gpuType);
        ((IList)gpuSlot.GetType().GetField("requiredOccupiedSlots").GetValue(gpuSlot)).Add(caseSlot);
        Component motherboard = CreateObject("motherboard").AddComponent(PartType);
        Component gpu = CreateObject("GPU").AddComponent(PartType);
        Set(motherboard, "compatibleSlotType", motherboardType);
        Set(gpu, "compatibleSlotType", gpuType);

        Assert.That((bool)Call(gpuSlot, "TrySeat", gpu), Is.False);
        Assert.That((bool)Call(caseSlot, "TrySeat", motherboard), Is.True);
        Assert.That((bool)Call(gpuSlot, "TrySeat", gpu), Is.True);
        Call(motherboard, "Unmount");
        Assert.That((bool)Get(motherboard, "IsMounted"), Is.True);
        Call(gpu, "Unmount");
        Call(motherboard, "Unmount");
        Assert.That((bool)Get(motherboard, "IsMounted"), Is.False);
        yield return null;
    }

    [UnityTest]
    public IEnumerator DisablingPrerequisiteSlotAlsoVacatesItsDependents()
    {
        Component cpuSlot = CreateSlot("CPU socket", Vector3.zero, Quaternion.identity);
        Component fanSlot = CreateSlot("fan socket", Vector3.zero, Quaternion.identity);
        ((IList)fanSlot.GetType().GetField("requiredOccupiedSlots").GetValue(fanSlot)).Add(cpuSlot);
        Component cpu = CreateObject("CPU").AddComponent(PartType);
        Component fan = CreateObject("fan").AddComponent(PartType);
        Assert.That((bool)Call(cpuSlot, "TrySeat", cpu), Is.True);
        Assert.That((bool)Call(fanSlot, "TrySeat", fan), Is.True);

        ((Behaviour)cpuSlot).enabled = false;
        Assert.That((bool)Get(cpu, "IsMounted"), Is.False);
        Assert.That((bool)Get(fan, "IsMounted"), Is.False, "Dependent must not stay seated without its prerequisite");
        Assert.That((bool)Get(fanSlot, "isOccupied"), Is.False);
        yield return null;
    }

    [UnityTest]
    public IEnumerator DisablingMountedPartAlsoVacatesItsDependents()
    {
        Component cpuSlot = CreateSlot("CPU socket", Vector3.zero, Quaternion.identity);
        Component fanSlot = CreateSlot("fan socket", Vector3.zero, Quaternion.identity);
        ((IList)fanSlot.GetType().GetField("requiredOccupiedSlots").GetValue(fanSlot)).Add(cpuSlot);
        Component cpu = CreateObject("CPU").AddComponent(PartType);
        Component fan = CreateObject("fan").AddComponent(PartType);
        Assert.That((bool)Call(cpuSlot, "TrySeat", cpu), Is.True);
        Assert.That((bool)Call(fanSlot, "TrySeat", fan), Is.True);

        ((Behaviour)cpu).enabled = false;
        Assert.That((bool)Get(cpuSlot, "isOccupied"), Is.False);
        Assert.That((bool)Get(fan, "IsMounted"), Is.False);
        yield return null;
    }

    [UnityTest]
    public IEnumerator EmptyOrSelfPrerequisiteEntriesAreIgnored()
    {
        Component slot = CreateSlot("socket", Vector3.zero, Quaternion.identity);
        var required = (IList)slot.GetType().GetField("requiredOccupiedSlots").GetValue(slot);
        required.Add(null);
        required.Add(slot);
        Component part = CreateObject("part").AddComponent(PartType);
        Assert.That((bool)Call(slot, "TrySeat", part), Is.True);
        yield return null;
    }

    [UnityTest]
    public IEnumerator BadlyRotatedPartIsNotReportedAsAnOrderViolation()
    {
        Component cpuSlot = CreateSlot("CPU socket", new Vector3(1f, 0f, 0f), Quaternion.identity);
        Component fanSlot = CreateSlot("fan socket", Vector3.zero, Quaternion.identity);
        ((IList)fanSlot.GetType().GetField("requiredOccupiedSlots").GetValue(fanSlot)).Add(cpuSlot);
        GameObject fanObject = CreateObject("fan");
        fanObject.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
        Component fan = fanObject.AddComponent(PartType);
        Physics.SyncTransforms();

        CountViolations(out Func<int> count, out Delegate handler);
        try
        {
            Assert.That((bool)Call(fan, "TryMountToNearbySlot"), Is.False);
            Assert.That(count(), Is.EqualTo(0));
            fanObject.transform.rotation = Quaternion.identity;
            Assert.That((bool)Call(fan, "TryMountToNearbySlot"), Is.False);
            Assert.That(count(), Is.EqualTo(1), "A correctly aligned part is reported as blocked by order");
        }
        finally
        {
            StopCountingViolations(handler);
        }
        yield return null;
    }

    [UnityTest]
    public IEnumerator PartSeatedIntoMountedAssemblyIgnoresTheOuterHost()
    {
        GameObject pcCase = CreateObject("case");
        pcCase.AddComponent<Rigidbody>().isKinematic = true;
        Collider caseCollider = pcCase.AddComponent<BoxCollider>();
        Component caseSlot = CreateSlot("case motherboard mount", Vector3.zero, Quaternion.identity);
        caseSlot.transform.SetParent(pcCase.transform, true);
        var slotType = caseSlot.GetType().GetField("acceptedSlotType").FieldType;
        object motherboardType = Enum.Parse(slotType, "Motherboard");
        Set(caseSlot, "acceptedSlotType", motherboardType);

        GameObject board = CreateObject("motherboard");
        Collider boardCollider = board.AddComponent<BoxCollider>();
        Component motherboard = board.AddComponent(PartType);
        Set(motherboard, "compatibleSlotType", motherboardType);
        Component cpuSlot = CreateSlot("CPU socket", Vector3.zero, Quaternion.identity);
        cpuSlot.transform.SetParent(board.transform, true);
        Assert.That((bool)Call(caseSlot, "TrySeat", motherboard), Is.True);

        GameObject cpuObject = CreateObject("CPU");
        Collider cpuCollider = cpuObject.AddComponent<BoxCollider>();
        Component cpu = cpuObject.AddComponent(PartType);
        Assert.That((bool)Call(cpuSlot, "TrySeat", cpu), Is.True);
        Assert.That(Physics.GetIgnoreCollision(cpuCollider, boardCollider), Is.True);
        Assert.That(Physics.GetIgnoreCollision(cpuCollider, caseCollider), Is.True,
            "The motherboard assembly refreshes when the CPU joins it");

        Call(cpu, "Unmount");
        Assert.That(Physics.GetIgnoreCollision(cpuCollider, boardCollider), Is.False);
        Assert.That(Physics.GetIgnoreCollision(cpuCollider, caseCollider), Is.False,
            "Pairs are restored when the CPU leaves the assembly");
        Assert.That(Physics.GetIgnoreCollision(boardCollider, caseCollider), Is.True);
        yield return null;
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (GameObject obj in objects)
            if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
        objects.Clear();
    }
}

public class TestGrabInteractor : XRBaseInteractor { }
