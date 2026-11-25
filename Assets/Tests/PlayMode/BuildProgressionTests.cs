using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// Assembly-CSharp is a predefined Unity assembly, so tests in an asmdef use reflection.
public class BuildProgressionTests
{
    private const int CpuSlot = 0;
    private const int RamSlot = 1;

    private readonly List<GameObject> objects = new List<GameObject>();
    private static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    private Type PartType => T("MountablePart");
    private Type SlotType => T("SnapPoint");

    [TearDown]
    public void TearDown()
    {
        foreach (GameObject obj in objects) if (obj != null) UnityEngine.Object.Destroy(obj);
        objects.Clear();
    }

    private GameObject CreateObject(string name, Vector3 position = default, bool active = true)
    {
        var obj = new GameObject(name);
        obj.SetActive(active);
        obj.transform.position = position;
        objects.Add(obj);
        return obj;
    }

    private static object Call(object obj, string name, params object[] args) =>
        obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null,
            Array.ConvertAll(args, a => a.GetType()), null).Invoke(obj, args);
    private static object Get(object obj, string name) => obj.GetType().GetProperty(name).GetValue(obj);
    private static void Set(object obj, string name, object value) => obj.GetType().GetField(name).SetValue(obj, value);
    private static IList List(object obj, string field) => (IList)obj.GetType().GetField(field).GetValue(obj);
    private static object SlotEnum(int value) => Enum.ToObject(T("SlotType"), value);

    private Component CreatePart(string name, Vector3 position, int slotType)
    {
        GameObject obj = CreateObject(name, position);
        obj.AddComponent<BoxCollider>().size = Vector3.one * 0.02f;
        Component part = obj.AddComponent(PartType);
        Set(part, "compatibleSlotType", SlotEnum(slotType));
        obj.GetComponent<Rigidbody>().useGravity = false;
        return part;
    }

    private Component CreateSlot(string name, Vector3 position, int slotType)
    {
        GameObject obj = CreateObject(name, position);
        obj.AddComponent<SphereCollider>().isTrigger = true;
        Component slot = obj.AddComponent(SlotType);
        Set(slot, "acceptedSlotType", SlotEnum(slotType));
        return slot;
    }

    private Rigidbody CreateLoot(string name, Vector3 position)
    {
        GameObject obj = CreateObject(name, position);
        obj.AddComponent<BoxCollider>().size = Vector3.one * 0.05f;
        return obj.AddComponent<Rigidbody>();
    }

    private Component CreateChute(Vector3 position)
    {
        Component chute = CreateObject("chute", position).AddComponent(T("DropChute"));
        Set(chute, "warningSeconds", 0f);
        Set(chute, "releaseInterval", 0f);
        Set(chute, "holdOpenSeconds", 0f);
        Set(chute, "spawnJitter", 0f);
        return chute;
    }

    // Created inactive so Awake sees the configured stages.
    private Component CreateProgression(Component chute, IEnumerable<Rigidbody> starting, params (Component[] required, Rigidbody[] drops)[] stages)
    {
        GameObject obj = CreateObject("progression", default, false);
        Component progression = obj.AddComponent(T("BuildProgression"));
        Set(progression, "chute", chute);
        foreach (Rigidbody body in starting) List(progression, "startingParts").Add(body);
        foreach (var (required, drops) in stages)
        {
            object stage = Activator.CreateInstance(T("BuildStage"));
            foreach (Component part in required) List(stage, "requiredMounted").Add(part);
            foreach (Rigidbody body in drops) List(stage, "partsToDrop").Add(body);
            List(progression, "stages").Add(stage);
        }
        obj.SetActive(true);
        return progression;
    }

    private static Func<int> CountReleases(Component chute)
    {
        int released = 0;
        Action<Rigidbody> handler = body => released++;
        chute.GetType().GetEvent("PartReleased").AddEventHandler(chute, handler);
        return () => released;
    }

    private static IEnumerator Frames(int count)
    {
        for (int i = 0; i < count; i++) yield return null;
    }

    [UnityTest]
    public IEnumerator SeatAndVacateEventsReportSlotAndPart()
    {
        Component part = CreatePart("cpu", Vector3.zero, CpuSlot);
        Component slot = CreateSlot("socket", Vector3.zero, CpuSlot);
        var seated = new List<(object, object)>();
        var vacated = new List<(object, object)>();
        EventInfo seatEvent = SlotType.GetEvent("PartSeated");
        EventInfo vacateEvent = SlotType.GetEvent("PartVacated");
        Delegate onSeat = MakeHandler(seatEvent, (s, p) => seated.Add((s, p)));
        Delegate onVacate = MakeHandler(vacateEvent, (s, p) => vacated.Add((s, p)));
        seatEvent.AddEventHandler(null, onSeat);
        vacateEvent.AddEventHandler(null, onVacate);
        try
        {
            Assert.That((bool)Call(slot, "TrySeat", part), Is.True);
            Assert.That(seated, Has.Count.EqualTo(1));
            Assert.That(seated[0].Item1, Is.SameAs(slot));
            Assert.That(seated[0].Item2, Is.SameAs(part));
            Call(part, "Unmount");
            Assert.That(vacated, Has.Count.EqualTo(1));
            Assert.That(vacated[0].Item2, Is.SameAs(part));
        }
        finally
        {
            seatEvent.RemoveEventHandler(null, onSeat);
            vacateEvent.RemoveEventHandler(null, onVacate);
        }
        yield return null;
    }

    // Builds an Action<SnapPoint, MountablePart> without naming the types at compile time.
    private Delegate MakeHandler(EventInfo info, Action<object, object> callback)
    {
        var relay = new Relay { callback = callback };
        MethodInfo invoke = typeof(Relay).GetMethod(nameof(Relay.Invoke)).MakeGenericMethod(SlotType, PartType);
        return Delegate.CreateDelegate(info.EventHandlerType, relay, invoke);
    }

    private class Relay
    {
        public Action<object, object> callback;
        public void Invoke<TSlot, TPart>(TSlot slot, TPart part) => callback(slot, part);
    }

    [UnityTest]
    public IEnumerator StageDropsOnceEvenWhenTriggerPartIsRemounted()
    {
        Component cpu = CreatePart("cpu", Vector3.zero, CpuSlot);
        Component slot = CreateSlot("socket", Vector3.zero, CpuSlot);
        Rigidbody ram = CreateLoot("ram", new Vector3(2f, 0f, 0f));
        Component chute = CreateChute(new Vector3(0f, 5f, 0f));
        Func<int> releases = CountReleases(chute);
        Component progression = CreateProgression(chute, new[] { cpu.GetComponent<Rigidbody>() },
            (new[] { cpu }, new[] { ram }));

        Assert.That(ram.gameObject.activeSelf, Is.False, "Locked parts start hidden.");
        Assert.That((bool)Call(slot, "TrySeat", cpu), Is.True);
        Assert.That((int)Get(progression, "UnlockedStageCount"), Is.EqualTo(1));
        Assert.That(ram.gameObject.activeSelf, Is.True);
        Assert.That((bool)Call(chute, "IsPending", ram), Is.True, "The part waits in the chute first.");
        yield return Frames(5);
        Assert.That(releases(), Is.EqualTo(1));
        Assert.That(ram.isKinematic, Is.False);

        for (int i = 0; i < 3; i++)
        {
            Call(cpu, "Unmount");
            Assert.That((bool)Call(slot, "TrySeat", cpu), Is.True);
        }
        yield return Frames(5);
        Assert.That((int)Get(progression, "UnlockedStageCount"), Is.EqualTo(1));
        Assert.That(releases(), Is.EqualTo(1), "Remounting must not drop the part again.");
        Assert.That((bool)Get(progression, "IsComplete"), Is.True);
    }

    [UnityTest]
    public IEnumerator StageWaitsUntilAllRequiredPartsAreMountedTogether()
    {
        Component ramA = CreatePart("ram A", new Vector3(0f, 0f, 0f), RamSlot);
        Component ramB = CreatePart("ram B", new Vector3(1f, 0f, 0f), RamSlot);
        Component slotA = CreateSlot("slot A", new Vector3(0f, 0f, 0f), RamSlot);
        Component slotB = CreateSlot("slot B", new Vector3(1f, 0f, 0f), RamSlot);
        Rigidbody ssd = CreateLoot("ssd", new Vector3(3f, 0f, 0f));
        Component progression = CreateProgression(CreateChute(new Vector3(0f, 5f, 0f)), new Rigidbody[0],
            (new[] { ramA, ramB }, new[] { ssd }));

        Assert.That((bool)Call(slotA, "TrySeat", ramA), Is.True);
        Assert.That((int)Get(progression, "UnlockedStageCount"), Is.EqualTo(0));
        Call(ramA, "Unmount");
        Assert.That((bool)Call(slotB, "TrySeat", ramB), Is.True);
        Assert.That((int)Get(progression, "UnlockedStageCount"), Is.EqualTo(0));
        Assert.That(ssd.gameObject.activeSelf, Is.False);
        Assert.That((bool)Call(slotA, "TrySeat", ramA), Is.True);
        Assert.That((int)Get(progression, "UnlockedStageCount"), Is.EqualTo(1));
        Assert.That(ssd.gameObject.activeSelf, Is.True);
        yield return null;
    }

    [UnityTest]
    public IEnumerator StagesUnlockInOrder()
    {
        Component cpu = CreatePart("cpu", Vector3.zero, CpuSlot);
        Component cpuSlot = CreateSlot("cpu socket", Vector3.zero, CpuSlot);
        Component ram = CreatePart("ram", new Vector3(1f, 0f, 0f), RamSlot);
        Component ramSlot = CreateSlot("ram slot", new Vector3(1f, 0f, 0f), RamSlot);
        Rigidbody ssd = CreateLoot("ssd", new Vector3(3f, 0f, 0f));
        Component progression = CreateProgression(CreateChute(new Vector3(0f, 5f, 0f)), new Rigidbody[0],
            (new[] { cpu }, new[] { ram.GetComponent<Rigidbody>() }),
            (new[] { ram }, new[] { ssd }));

        Assert.That(ram.gameObject.activeSelf, Is.False);
        // The second stage's part is mounted out of order (by code); its stage still waits for the first.
        ram.gameObject.SetActive(true);
        Assert.That((bool)Call(ramSlot, "TrySeat", ram), Is.True);
        Assert.That((int)Get(progression, "UnlockedStageCount"), Is.EqualTo(0));
        Assert.That((bool)Call(cpuSlot, "TrySeat", cpu), Is.True);
        yield return Frames(5);
        // The RAM was dropped (unmounted) by stage one, so stage two now waits for it to be mounted again.
        Assert.That((int)Get(progression, "UnlockedStageCount"), Is.EqualTo(1));
        Assert.That(ssd.gameObject.activeSelf, Is.False);
    }

    [UnityTest]
    public IEnumerator FullRestartReturnsToTheFirstStage()
    {
        Vector3 home = new Vector3(0.5f, 0f, 0f);
        Component cpu = CreatePart("cpu", home, CpuSlot);
        Component slot = CreateSlot("socket", Vector3.zero, CpuSlot);
        Rigidbody ram = CreateLoot("ram", new Vector3(2f, 0f, 0f));
        Component chute = CreateChute(new Vector3(0f, 5f, 0f));
        Component progression = CreateProgression(chute, new[] { cpu.GetComponent<Rigidbody>() },
            (new[] { cpu }, new[] { ram }));

        cpu.transform.position = Vector3.zero;
        Assert.That((bool)Call(slot, "TrySeat", cpu), Is.True);
        yield return Frames(5);
        Assert.That(ram.gameObject.activeSelf, Is.True);

        Call(progression, "FullRestart");
        Assert.That((int)Get(progression, "UnlockedStageCount"), Is.EqualTo(0));
        Assert.That((bool)Get(cpu, "IsMounted"), Is.False);
        Assert.That(Vector3.Distance(cpu.transform.position, home), Is.LessThan(0.0001f));
        Assert.That(ram.gameObject.activeSelf, Is.False);

        // And the first stage can be earned again.
        cpu.transform.position = Vector3.zero;
        Assert.That((bool)Call(slot, "TrySeat", cpu), Is.True);
        Assert.That((int)Get(progression, "UnlockedStageCount"), Is.EqualTo(1));
    }

    [UnityTest]
    public IEnumerator ResetCurrentStageRemountsProgressAndDropsLatestPartsAgain()
    {
        Vector3 home = new Vector3(0.5f, 0f, 0f);
        Component cpu = CreatePart("cpu", home, CpuSlot);
        Component slot = CreateSlot("socket", Vector3.zero, CpuSlot);
        Set(cpu, "buildSlot", slot);
        Rigidbody ram = CreateLoot("ram", new Vector3(2f, 0f, 0f));
        Component chute = CreateChute(new Vector3(0f, 5f, 0f));
        Func<int> releases = CountReleases(chute);
        Component progression = CreateProgression(chute, new[] { cpu.GetComponent<Rigidbody>() },
            (new[] { cpu }, new[] { ram }));

        cpu.transform.position = Vector3.zero;
        Assert.That((bool)Call(slot, "TrySeat", cpu), Is.True);
        yield return Frames(5);
        // The player takes the CPU back out and leaves the RAM lying somewhere.
        Call(cpu, "Unmount");
        cpu.transform.position = new Vector3(3f, 0f, 3f);
        ram.position = new Vector3(4f, 0f, 4f);

        Call(progression, "ResetCurrentStage");
        Assert.That((bool)Get(cpu, "IsMounted"), Is.True, "Finished stages are rebuilt in their build slots.");
        Assert.That(Get(cpu, "MountedSlot"), Is.SameAs(slot));
        Assert.That((bool)Call(chute, "IsPending", ram), Is.True);
        yield return Frames(5);
        Assert.That(releases(), Is.EqualTo(2));
        Assert.That((int)Get(progression, "UnlockedStageCount"), Is.EqualTo(1), "Resetting never undoes progress.");
    }

    [UnityTest]
    public IEnumerator ResetCurrentStageRebuildsEveryFinishedStageInOrder()
    {
        Component cpu = CreatePart("cpu", new Vector3(0.5f, 0f, 0f), CpuSlot);
        Component cpuSlot = CreateSlot("cpu socket", Vector3.zero, CpuSlot);
        Component ram = CreatePart("ram", new Vector3(1.5f, 0f, 0f), RamSlot);
        Component ramSlot = CreateSlot("ram slot", new Vector3(1f, 0f, 0f), RamSlot);
        List(ramSlot, "requiredOccupiedSlots").Add(cpuSlot);
        Set(cpu, "buildSlot", cpuSlot);
        Set(ram, "buildSlot", ramSlot);
        Rigidbody ssd = CreateLoot("ssd", new Vector3(3f, 0f, 0f));
        Component chute = CreateChute(new Vector3(0f, 5f, 0f));
        Component progression = CreateProgression(chute, new[] { cpu.GetComponent<Rigidbody>() },
            (new[] { cpu }, new[] { ram.GetComponent<Rigidbody>() }),
            (new[] { ram }, new[] { ssd }));

        cpu.transform.position = Vector3.zero;
        Assert.That((bool)Call(cpuSlot, "TrySeat", cpu), Is.True);
        yield return Frames(5);
        // The chute tumbles parts as they fall; line the RAM up with its slot by hand.
        ram.transform.SetPositionAndRotation(new Vector3(1f, 0f, 0f), Quaternion.identity);
        Assert.That((bool)Call(ramSlot, "TrySeat", ram), Is.True);
        Assert.That((int)Get(progression, "UnlockedStageCount"), Is.EqualTo(2));
        yield return Frames(5);

        // Tear the build apart; the RAM has to come out before the CPU it depends on.
        Call(ram, "Unmount");
        Call(cpu, "Unmount");
        Call(progression, "ResetCurrentStage");
        Assert.That(Get(cpu, "MountedSlot"), Is.SameAs(cpuSlot));
        Assert.That(Get(ram, "MountedSlot"), Is.SameAs(ramSlot));
        Assert.That((bool)Call(chute, "IsPending", ssd), Is.True, "Only the latest stage's parts are dropped again.");
        Assert.That((bool)Call(chute, "IsPending", ram.GetComponent<Rigidbody>()), Is.False);
        Assert.That((int)Get(progression, "UnlockedStageCount"), Is.EqualTo(2));
        yield return null;
    }

    [UnityTest]
    public IEnumerator LostPartIsSentBackThroughTheChute()
    {
        GameObject root = CreateObject("parts");
        Rigidbody lost = CreateLoot("lost", new Vector3(0f, -50f, 0f));
        lost.transform.SetParent(root.transform, true);
        Rigidbody safe = CreateLoot("safe", new Vector3(1f, 0f, 0f));
        safe.transform.SetParent(root.transform, true);
        Component chute = CreateChute(new Vector3(0f, 5f, 0f));
        Func<int> releases = CountReleases(chute);
        Component catcher = CreateObject("catcher").AddComponent(T("LostPartCatcher"));
        Set(catcher, "chute", chute);
        List(catcher, "watchedRoots").Add(root.transform);
        Set(catcher, "playArea", new Bounds(Vector3.zero, Vector3.one * 20f));

        Assert.That((int)Call(catcher, "CheckNow"), Is.EqualTo(1));
        Assert.That((bool)Call(chute, "IsPending", lost), Is.True);
        Assert.That((bool)Call(chute, "IsPending", safe), Is.False);
        Assert.That((int)Call(catcher, "CheckNow"), Is.EqualTo(0), "A part already in the chute is not sent twice.");
        yield return Frames(5);
        Assert.That(releases(), Is.EqualTo(1));
        Assert.That(lost.position.y, Is.GreaterThan(4f));
    }

    [UnityTest]
    public IEnumerator FallingPartUsesContinuousDetectionThenGetsItsOwnModeBack()
    {
        GameObject floor = CreateObject("floor", new Vector3(0f, -0.5f, 0f));
        floor.AddComponent<BoxCollider>().size = new Vector3(10f, 1f, 10f);
        Rigidbody part = CreateLoot("part", new Vector3(5f, 0.5f, 5f));
        part.collisionDetectionMode = CollisionDetectionMode.Discrete;
        Component chute = CreateChute(new Vector3(0f, 1.5f, 0f));
        Set(chute, "maxFallSeconds", 3f);

        Call(chute, "Drop", part);
        yield return Frames(3);
        Assert.That(part.collisionDetectionMode, Is.EqualTo(CollisionDetectionMode.ContinuousSpeculative));
        float timeout = Time.time + 5f;
        while (part.collisionDetectionMode != CollisionDetectionMode.Discrete && Time.time < timeout) yield return null;
        Assert.That(part.collisionDetectionMode, Is.EqualTo(CollisionDetectionMode.Discrete));
        Assert.That(part.position.y, Is.GreaterThan(-0.1f), "The part lands on the floor.");
    }
}
