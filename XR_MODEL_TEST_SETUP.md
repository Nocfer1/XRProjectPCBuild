# XR model test scene

## Project setup found

- Unity Editor: **6000.3.7f1**
- Render pipeline: **URP 17.3.0**
- XR: OpenXR **1.17.1**, XR Plug-in Management **4.6.0**, XR Interaction Toolkit **3.5.1**, Input System **1.19.0**
- The project already has an OpenXR Standalone loader, the Oculus Touch Controller Profile enabled for Standalone, and the official XRI Starter Assets input actions and controller rig. Android settings were left as they were.

## Build and open the scene

The editor utility is available at **XR Project > Build Model Test Scene**. It creates `Assets/Scenes/XRModelTestScene.unity` additively so an open scene is not discarded, adds it to Build Settings, and leaves all imported model sources untouched. The new scene uses the XRI Starter Assets XR Origin and places the imported PC model variants on a simple workbench. The tracked XR camera starts 0.30 m behind the table, facing it; the two compact component rows sit within about 0.73–1.25 m of the starting headset position. Any cameras, lights, or audio listeners embedded in the imported model scene instances are disabled; only the XR Origin camera and scene key light remain active. The original `BasicScene` and `SampleScene` are preserved.

The editor Console reports each model's world-space bounds in metres, wrapper-local collider center, renderer/mesh counts, vertex and triangle counts where the mesh is readable, missing materials, and missing normals. It also warns about unusually high triangle counts. Each wrapper uses a generated `BoxCollider` aligned to the rendered bounds, plus a `Rigidbody` and `XRGrabInteractable`; no MeshCollider is added to the imported assets. Wrapper scales were set for this test scene after the raw imports measured in inconsistent units; these values are scene-only starting points, not edits to source geometry:

| Asset(s) | Test bounds (approx. metres) |
| --- | --- |
| `pc_case.glb`, `Case.glb`, `PC_Case.fbx` | 0.26 × 0.47 × 0.44 |
| `Motherboard.glb` | 0.26 × 0.05 × 0.32 |
| `GraphicCard.glb` | 0.26 × 0.13 × 0.05 |
| `PSU.fbx` | 0.37 × 0.18 × 0.21 |
| `CPUfan.fbx` | 0.12 × 0.10 × 0.11 |
| `CPU.glb` | 0.038 × 0.003 × 0.038 |
| `RAM.glb` | 0.14 × 0.038 × 0.003 |
| `ssd-kit.glb` | 0.11 × 0.004 × 0.17 |

Those are initial visual estimates; compare them with physical hardware before using them as final simulator dimensions. `PC_Case.fbx`, `PSU.fbx`, and `CPUfan.fbx` are imported as non-readable meshes, so their vertex, triangle, and normal data could not be audited by the utility. No missing material slots or readable meshes with missing normals were reported. Each wrapper BoxCollider uses the renderer bounds measured in wrapper-local coordinates; wrapper position is then set so collider and mesh bottoms touch the table surface, avoiding an initial drop or offset collider.

## Quest Link test steps

1. Install and open the Meta Horizon Link PC application, then connect and enable Quest Link on the headset (USB Link or Air Link).
2. In the Meta PC app, open **Settings > General** and choose **Set Meta Quest Link as active OpenXR runtime**. If that control says it is already active, leave it as is. Do not switch to SteamVR for this test.
3. In Unity, open `Assets/Scenes/XRModelTestScene.unity`. If `BasicScene` is still open alongside it, close `BasicScene` before Play Mode so only the isolated test scene runs; save it only if you want to keep its current edits.
4. Confirm the headset is in Quest Link mode, then press **Play** in Unity. Keep the Unity Game view active while entering Play Mode.
5. Move your head and controllers to check tracking. Reach or point at a part, hold either controller's **grip** to select/grab, move and rotate it, then release grip. Repeat to confirm it can be picked up again.

The Starter Assets bind XRI `Select` to each controller's grip button and provide controller pose tracking, near/far interaction, and the action manager. This scene does not add assembly sockets, snapping, tutorials, or hand tracking.

## Manual model review

No source mesh geometry or imported asset was edited. Inspect the Console bounds report before deciding that a part has the correct real-world size: the files include multiple case exports (`Case`, `PC_Case`, and `pc_case`) that may be alternative versions. A bounds/pivot offset does not automatically prove a bad asset, and non-readable meshes cannot have their normals or triangle counts checked by the utility. Correct any suspect wrapper scale or placement only after comparing it with the physical component reference.

## Validation limits

The scene was generated in the open Unity Editor. Hardware interaction requires a connected, working Quest Link session; headset/controller behavior is not considered verified until the steps above are completed on the device. Unity's Console also showed a warning about a missing `UniversalRenderPipelineGlobalSettings` component type in existing project settings; it predates this test scene and was left untouched. Check the rendered result in Play Mode and resolve that project warning if URP settings or materials fail to load.
