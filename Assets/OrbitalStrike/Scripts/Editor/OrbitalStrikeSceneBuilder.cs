using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

/// <summary>
/// Tools > Orbital Strike > Build Demo Scene.
/// Generates materials, prefabs and the OrbitalStrike scene (low-poly city + Venus accelerator + HUD).
/// </summary>
public static class OrbitalStrikeSceneBuilder
{
    const string Root = "Assets/OrbitalStrike";
    const string ModelDir = Root + "/Models";
    const string MatDir = Root + "/Materials";
    const string PrefabDir = Root + "/Prefabs";
    const string ScenePath = Root + "/Scenes/OrbitalStrike.unity";

    static readonly Vector3 SpaceOrigin = new Vector3(0f, 30000f, 0f);
    const float VenusRadius = 400f;
    const float RingRadius = 560f;
    static readonly Color HudColor = new Color(1f, 0.48f, 0.1f);

    enum Align { LongAxisForward, FlatAxisUp, FlatAxisForward }

    [MenuItem("Tools/Orbital Strike/Build Demo Scene")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        EnsureFolders();

        // ---------- materials ----------
        var hull = LitMat("OS_Hull", new Color(0.07f, 0.07f, 0.08f), 0.85f, 0.65f);
        var panel = LitMat("OS_Panel", new Color(0.78f, 0.78f, 0.8f), 0.3f, 0.5f);
        var glow = LitMat("OS_Glow", new Color(1f, 0.45f, 0.1f), 0f, 0.4f, new Color(1f, 0.42f, 0.08f) * 12f);
        var ground = LitMat("OS_Ground", new Color(0.62f, 0.58f, 0.52f), 0f, 0.1f);
        var road = LitMat("OS_Road", new Color(0.32f, 0.31f, 0.3f), 0f, 0.15f);
        var buildingMats = new[]
        {
            LitMat("OS_Bld_A", new Color(0.86f, 0.84f, 0.8f), 0f, 0.2f),
            LitMat("OS_Bld_B", new Color(0.72f, 0.74f, 0.78f), 0f, 0.2f),
            LitMat("OS_Bld_C", new Color(0.82f, 0.72f, 0.6f), 0f, 0.2f),
            LitMat("OS_Bld_D", new Color(0.55f, 0.6f, 0.66f), 0.2f, 0.45f),
        };
        foreach (var m in buildingMats) m.enableInstancing = true;

        var venusMat = LitMat("OS_Venus", Color.white, 0f, 0.12f);
        venusMat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Textures/Venus_Albedo.png"));
        EditorUtility.SetDirty(venusMat);

        var atmo = ShaderMat("OS_VenusAtmosphere", "OrbitalStrike/Atmosphere");
        atmo.SetColor("_Color", new Color(1f, 0.78f, 0.42f));
        atmo.SetFloat("_Power", 3.2f);
        atmo.SetFloat("_Intensity", 2.2f);

        var pAdd = ShaderMat("OS_ParticleAdditive", "OrbitalStrike/Particle");
        pAdd.SetFloat("_SrcBlend", (float)BlendMode.One);
        pAdd.SetFloat("_DstBlend", (float)BlendMode.One);
        var pAlpha = ShaderMat("OS_ParticleAlpha", "OrbitalStrike/Particle");
        pAlpha.SetFloat("_SrcBlend", (float)BlendMode.One);
        pAlpha.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        pAlpha.renderQueue = 3000;
        var shock = ShaderMat("OS_Shockwave", "OrbitalStrike/Shockwave");

        var remap = new Dictionary<string, Material> { { "Glow", glow }, { "Hull", hull }, { "Panel", panel } };

        // ---------- prefabs ----------
        var missilePrefab = SavePrefab(BuildAligned(ModelDir + "/OS_Missile.fbx", "OS_Missile", Align.LongAxisForward, 4f, false, remap));
        var rodPrefab = SavePrefab(BuildAligned(ModelDir + "/OS_KineticRod.fbx", "OS_KineticRod", Align.LongAxisForward, 20f, true, remap));

        // ---------- scene ----------
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var cam = Camera.main;
        cam.transform.SetPositionAndRotation(new Vector3(0f, 25f, -520f), Quaternion.Euler(-8f, 0f, 0f));
        cam.farClipPlane = 20000f;
        cam.nearClipPlane = 0.5f;
        cam.fieldOfView = 60f;
        cam.allowHDR = true;
        var camData = cam.GetUniversalAdditionalCameraData();
        camData.renderPostProcessing = true;

        var sun = Object.FindFirstObjectByType<Light>();
        sun.transform.rotation = Quaternion.Euler(38f, -35f, 0f);
        sun.intensity = 1.6f;
        sun.color = new Color(1f, 0.95f, 0.88f);
        sun.shadows = LightShadows.Soft;

        // ground + city
        var groundGo = GameObject.CreatePrimitive(PrimitiveType.Plane);
        groundGo.name = "Ground";
        groundGo.transform.localScale = new Vector3(400f, 1f, 400f);
        groundGo.GetComponent<Renderer>().sharedMaterial = ground;

        var impact = new GameObject("ImpactPoint").transform;
        impact.position = Vector3.zero;
        var launchPad = BuildLaunchPad(new Vector3(-650f, 0f, -380f), hull, glow);
        var anchor = new GameObject("GroundCamAnchor").transform;
        anchor.position = new Vector3(0f, 60f, -560f);
        var player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        player.name = "Player";
        player.transform.position = new Vector3(4f, 1f, -555f);
        player.transform.localScale = new Vector3(1f, 1f, 1f);
        player.GetComponent<Renderer>().sharedMaterial = glow;

        var city = BuildCity(buildingMats, road, new[] { (launchPad.position, 150f), (anchor.position, 110f) });

        // space
        var space = new GameObject("SpaceRoot").transform;
        space.position = SpaceOrigin;
        var venus = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        venus.name = "Venus";
        Object.DestroyImmediate(venus.GetComponent<Collider>());
        venus.transform.SetParent(space, false);
        venus.transform.localScale = Vector3.one * VenusRadius * 2f;
        venus.transform.localRotation = Quaternion.Euler(-2.6f, 0f, 0f);
        venus.GetComponent<Renderer>().sharedMaterial = venusMat;
        SmoothSphere(venus);
        var atmoGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        atmoGo.name = "Atmosphere";
        Object.DestroyImmediate(atmoGo.GetComponent<Collider>());
        atmoGo.transform.SetParent(space, false);
        atmoGo.transform.localScale = Vector3.one * VenusRadius * 2.08f;
        var atmoR = atmoGo.GetComponent<Renderer>();
        atmoR.sharedMaterial = atmo;
        atmoR.shadowCastingMode = ShadowCastingMode.Off;

        var ringPivot = new GameObject("RingPivot").transform;
        ringPivot.SetParent(space, false);
        ringPivot.localRotation = Quaternion.Euler(16f, 0f, 9f);
        // ring model: rail radius 1.0 plus gate rings reaching 1.058 in Blender units
        var ring = BuildAligned(ModelDir + "/OS_AcceleratorRing.fbx", "AcceleratorRing", Align.FlatAxisUp, 2f * 1.058f, false, remap);
        ring.transform.SetParent(ringPivot, false);
        ring.transform.localScale = Vector3.one * RingRadius;

        var gate = BuildAligned(ModelDir + "/OS_LaunchGate.fbx", "LaunchGate", Align.FlatAxisForward, 2.4f, false, remap);
        gate.transform.position = ringPivot.position + ringPivot.rotation * new Vector3(RingRadius, 0f, 0f);
        gate.transform.rotation = Quaternion.LookRotation(ringPivot.rotation * Vector3.forward, ringPivot.rotation * Vector3.up);
        gate.transform.localScale = Vector3.one * 55f;
        gate.transform.SetParent(space, true);

        // post processing
        var volGo = new GameObject("PostFX");
        var vol = volGo.AddComponent<Volume>();
        vol.isGlobal = true;
        vol.sharedProfile = BuildProfile();

        // HUD
        var hud = BuildHud(out var refs);

        // skill
        var skillGo = new GameObject("OrbitalStrike");
        var skill = skillGo.AddComponent<OrbitalStrikeSkill>();
        skill.cam = cam;
        skill.sun = sun;
        skill.volume = vol;
        skill.cityRoot = city;
        skill.launchPad = launchPad;
        skill.impactPoint = impact;
        skill.groundCamAnchor = anchor;
        skill.spaceRoot = space;
        skill.venus = venus.transform;
        skill.ringPivot = ringPivot;
        skill.ringRenderer = ring.GetComponentInChildren<Renderer>();
        skill.gate = gate.transform;
        skill.missilePrefab = missilePrefab;
        skill.rodPrefab = rodPrefab;
        skill.particleAdditive = pAdd;
        skill.particleAlpha = pAlpha;
        skill.shockwaveMat = shock;
        skill.statusText = refs.status;
        skill.subText = refs.sub;
        skill.velocityText = refs.velocity;
        skill.velocityFill = refs.fill;
        skill.flash = refs.flash;
        skill.thermal = refs.thermal;
        skill.reticle = refs.reticle;
        skill.hudRoot = refs.hudRoot;
        skill.hintText = refs.hint;
        skill.ringRadius = RingRadius;
        skill.hudColor = HudColor;

        EditorSceneManager.SaveScene(scene, ScenePath);
        AddToBuildSettings(ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("[OrbitalStrike] Scene built: " + ScenePath + "  — tekan Play lalu F.");
    }

    // ------------------------------------------------------------------ builders

    static Transform BuildLaunchPad(Vector3 pos, Material hull, Material glow)
    {
        var pad = new GameObject("LaunchPad").transform;
        pad.position = pos;
        var baseGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        baseGo.transform.SetParent(pad, false);
        baseGo.transform.localScale = new Vector3(40f, 1.5f, 40f);
        baseGo.transform.localPosition = new Vector3(0f, 1.5f, 0f);
        baseGo.GetComponent<Renderer>().sharedMaterial = hull;
        var ringGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ringGo.transform.SetParent(pad, false);
        ringGo.transform.localScale = new Vector3(30f, 0.2f, 30f);
        ringGo.transform.localPosition = new Vector3(0f, 3.05f, 0f);
        ringGo.GetComponent<Renderer>().sharedMaterial = glow;
        var tower = GameObject.CreatePrimitive(PrimitiveType.Cube);
        tower.transform.SetParent(pad, false);
        tower.transform.localScale = new Vector3(4f, 34f, 4f);
        tower.transform.localPosition = new Vector3(9f, 17f, 0f);
        tower.GetComponent<Renderer>().sharedMaterial = hull;
        return pad;
    }

    static Transform BuildCity(Material[] mats, Material road, (Vector3 pos, float radius)[] clear)
    {
        var city = new GameObject("City").transform;
        var rnd = new System.Random(1337);
        const float lot = 26f, gap = 4f, roadW = 22f;
        const int lotsPerBlock = 3;
        var blockSize = lotsPerBlock * lot + (lotsPerBlock - 1) * gap;
        var pitch = blockSize + roadW;
        const float cityRadius = 950f;
        var n = Mathf.CeilToInt(cityRadius / pitch);

        for (var bx = -n; bx <= n; bx++)
        for (var bz = -n; bz <= n; bz++)
        {
            var blockOrigin = new Vector3(bx * pitch, 0f, bz * pitch) - new Vector3(blockSize, 0f, blockSize) * 0.5f;
            for (var lx = 0; lx < lotsPerBlock; lx++)
            for (var lz = 0; lz < lotsPerBlock; lz++)
            {
                var c = blockOrigin + new Vector3(lx * (lot + gap) + lot * 0.5f, 0f, lz * (lot + gap) + lot * 0.5f);
                var r = new Vector2(c.x, c.z).magnitude;
                if (r > cityRadius) continue;
                if (clear.Any(k => Vector2.Distance(new Vector2(c.x, c.z), new Vector2(k.pos.x, k.pos.z)) < k.radius)) continue;
                if (rnd.NextDouble() < 0.08) continue; // occasional empty lot

                var centre = 1f - r / cityRadius;
                var h = Mathf.Lerp(10f, 30f, (float)rnd.NextDouble()) + centre * centre * (float)rnd.NextDouble() * 110f;
                var w = lot * Mathf.Lerp(0.7f, 1f, (float)rnd.NextDouble());
                var d = lot * Mathf.Lerp(0.7f, 1f, (float)rnd.NextDouble());
                var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
                b.name = "Bld";
                b.transform.SetParent(city, false);
                b.transform.localScale = new Vector3(w, h, d);
                b.transform.position = c + Vector3.up * h * 0.5f;
                b.GetComponent<Renderer>().sharedMaterial = mats[rnd.Next(mats.Length)];
            }
        }

        // roads as long thin strips
        var roads = new GameObject("Roads").transform;
        for (var i = -n; i <= n + 1; i++)
        {
            var off = i * pitch - pitch * 0.5f;
            foreach (var horizontal in new[] { true, false })
            {
                var s = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.DestroyImmediate(s.GetComponent<Collider>());
                s.transform.SetParent(roads, false);
                s.transform.localScale = horizontal ? new Vector3(cityRadius * 2f, 0.1f, roadW) : new Vector3(roadW, 0.1f, cityRadius * 2f);
                s.transform.position = horizontal ? new Vector3(0f, 0.05f, off) : new Vector3(off, 0.05f, 0f);
                s.GetComponent<Renderer>().sharedMaterial = road;
            }
        }
        return city;
    }

    /// <summary>
    /// Instantiates an FBX under a pivot and rotates/scales it so that the requested axis is predictable,
    /// independent of the exporter's axis conventions.
    /// </summary>
    static GameObject BuildAligned(string fbxPath, string name, Align mode, float targetSize, bool pivotAtNose, Dictionary<string, Material> remap)
    {
        var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (fbx == null) throw new System.Exception("Model not found: " + fbxPath);
        var root = new GameObject(name);
        var inst = (GameObject)Object.Instantiate(fbx);
        inst.name = "Model";
        inst.transform.SetParent(root.transform, false);

        foreach (var r in inst.GetComponentsInChildren<Renderer>())
        {
            r.sharedMaterials = r.sharedMaterials
                .Select(m => remap.FirstOrDefault(kv => m != null && m.name.Contains(kv.Key)).Value ?? m)
                .ToArray();
        }

        var pts = CollectPoints(root.transform, inst);
        var b = BoundsOf(pts);
        var size = b.size;
        int axis;
        if (mode == Align.LongAxisForward) axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
        else axis = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
        var dir = Vector3.zero;
        dir[axis] = 1f;
        if (mode == Align.LongAxisForward && -b.min[axis] > b.max[axis]) dir = -dir; // nose = farther end
        var target = mode == Align.FlatAxisUp ? Vector3.up : Vector3.forward;
        var q = Quaternion.FromToRotation(dir, target);
        inst.transform.localRotation = q * inst.transform.localRotation;
        inst.transform.localPosition = q * inst.transform.localPosition;

        pts = CollectPoints(root.transform, inst);
        b = BoundsOf(pts);
        var measure = mode == Align.LongAxisForward ? b.size.z : Mathf.Max(b.size.x, b.size.y, b.size.z);
        var s = targetSize / Mathf.Max(measure, 1e-5f);
        inst.transform.localScale *= s;
        inst.transform.localPosition *= s;

        pts = CollectPoints(root.transform, inst);
        b = BoundsOf(pts);
        var shift = new Vector3(-b.center.x, -b.center.y, pivotAtNose ? -b.max.z : -b.center.z);
        if (mode == Align.FlatAxisUp) shift.y = -b.center.y;
        inst.transform.localPosition += shift;
        return root;
    }

    static List<Vector3> CollectPoints(Transform root, GameObject inst)
    {
        var pts = new List<Vector3>();
        foreach (var mf in inst.GetComponentsInChildren<MeshFilter>())
        {
            var m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            pts.AddRange(mf.sharedMesh.vertices.Select(v => m.MultiplyPoint3x4(v)));
        }
        return pts;
    }

    static Bounds BoundsOf(List<Vector3> pts)
    {
        var b = new Bounds(pts[0], Vector3.zero);
        foreach (var p in pts) b.Encapsulate(p);
        return b;
    }

    static GameObject SavePrefab(GameObject go)
    {
        var path = PrefabDir + "/" + go.name + ".prefab";
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
        return prefab;
    }

    static void SmoothSphere(GameObject sphere)
    {
        // Built-in sphere is fine at this distance; keep hook for a higher-res mesh later.
        sphere.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
    }

    static VolumeProfile BuildProfile()
    {
        var path = Root + "/Settings/OS_PostFX.asset";
        AssetDatabase.DeleteAsset(path);
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(profile, path);

        var bloom = profile.Add<Bloom>(true);
        bloom.intensity.Override(1.2f);
        bloom.threshold.Override(0.95f);
        bloom.scatter.Override(0.75f);
        var tone = profile.Add<Tonemapping>(true);
        tone.mode.Override(TonemappingMode.ACES);
        var vig = profile.Add<Vignette>(true);
        vig.intensity.Override(0.28f);
        var ca = profile.Add<ChromaticAberration>(true);
        ca.intensity.Override(0f);
        var lens = profile.Add<LensDistortion>(true);
        lens.intensity.Override(0f);
        var color = profile.Add<ColorAdjustments>(true);
        color.contrast.Override(12f);
        color.saturation.Override(-6f);

        foreach (var c in profile.components) AssetDatabase.AddObjectToAsset(c, profile);
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();
        return profile;
    }

    struct HudRefs
    {
        public Text status, sub, velocity, hint;
        public RectTransform fill;
        public Image flash, thermal, reticle;
        public GameObject hudRoot;
    }

    static GameObject BuildHud(out HudRefs refs)
    {
        refs = new HudRefs();
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var canvasGo = new GameObject("HUD", typeof(Canvas), typeof(CanvasScaler));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        refs.thermal = FullImage(canvasGo.transform, "Thermal", new Color(1f, 1f, 1f, 0.6f));
        refs.thermal.preserveAspect = false;

        var hudRoot = new GameObject("HudRoot", typeof(RectTransform));
        hudRoot.transform.SetParent(canvasGo.transform, false);
        Stretch((RectTransform)hudRoot.transform);
        refs.hudRoot = hudRoot;

        refs.status = MakeText(hudRoot.transform, "Status", font, 40, HudColor, TextAnchor.UpperCenter, new Vector2(0.5f, 1f), new Vector2(0f, -70f), new Vector2(1400f, 60f));
        refs.sub = MakeText(hudRoot.transform, "Sub", font, 20, new Color(1f, 0.85f, 0.7f, 0.85f), TextAnchor.LowerCenter, new Vector2(0.5f, 0f), new Vector2(0f, 60f), new Vector2(1600f, 30f));
        refs.velocity = MakeText(hudRoot.transform, "Velocity", font, 22, new Color(1f, 0.9f, 0.8f), TextAnchor.LowerCenter, new Vector2(0.5f, 0f), new Vector2(0f, 130f), new Vector2(600f, 30f));

        var barBg = new GameObject("VelocityBar", typeof(RectTransform), typeof(Image));
        barBg.transform.SetParent(hudRoot.transform, false);
        var bgRt = (RectTransform)barBg.transform;
        bgRt.anchorMin = bgRt.anchorMax = new Vector2(0.5f, 0f);
        bgRt.anchoredPosition = new Vector2(0f, 108f);
        bgRt.sizeDelta = new Vector2(420f, 6f);
        barBg.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.18f);
        var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(barBg.transform, false);
        var fillRt = (RectTransform)fill.transform;
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = new Vector2(0f, 1f);
        fillRt.offsetMin = fillRt.offsetMax = Vector2.zero;
        fill.GetComponent<Image>().color = HudColor;
        refs.fill = fillRt;

        var ret = new GameObject("Reticle", typeof(RectTransform), typeof(Image));
        ret.transform.SetParent(canvasGo.transform, false);
        var retRt = (RectTransform)ret.transform;
        retRt.anchorMin = retRt.anchorMax = new Vector2(0.5f, 0.5f);
        retRt.sizeDelta = new Vector2(220f, 220f);
        refs.reticle = ret.GetComponent<Image>();

        refs.hint = MakeText(canvasGo.transform, "Hint", font, 24, new Color(1f, 1f, 1f, 0.9f), TextAnchor.LowerLeft, new Vector2(0f, 0f), new Vector2(40f, 40f), new Vector2(700f, 40f));
        ((RectTransform)refs.hint.transform).pivot = Vector2.zero;

        refs.flash = FullImage(canvasGo.transform, "Flash", new Color(1f, 1f, 1f, 0f));
        refs.flash.raycastTarget = false;
        return canvasGo;
    }

    static Image FullImage(Transform parent, string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        Stretch((RectTransform)go.transform);
        var img = go.GetComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    static Text MakeText(Transform parent, string name, Font font, int size, Color color, TextAnchor align, Vector2 anchor, Vector2 pos, Vector2 box)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, anchor.y);
        rt.anchoredPosition = pos;
        rt.sizeDelta = box;
        var t = go.GetComponent<Text>();
        t.font = font;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    // ------------------------------------------------------------------ assets

    static Material LitMat(string name, Color color, float metallic, float smoothness, Color? emission = null)
    {
        var m = LoadOrCreate(name, "Universal Render Pipeline/Lit");
        m.SetColor("_BaseColor", color);
        m.SetFloat("_Metallic", metallic);
        m.SetFloat("_Smoothness", smoothness);
        if (emission.HasValue)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emission.Value);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }
        EditorUtility.SetDirty(m);
        return m;
    }

    static Material ShaderMat(string name, string shader)
    {
        var m = LoadOrCreate(name, shader);
        EditorUtility.SetDirty(m);
        return m;
    }

    static Material LoadOrCreate(string name, string shaderName)
    {
        var path = MatDir + "/" + name + ".mat";
        var shader = Shader.Find(shaderName);
        if (shader == null) throw new System.Exception("Shader not found: " + shaderName);
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(shader);
            AssetDatabase.CreateAsset(m, path);
        }
        else m.shader = shader;
        return m;
    }

    static void EnsureFolders()
    {
        foreach (var f in new[] { "Materials", "Prefabs", "Scenes", "Settings" })
            if (!AssetDatabase.IsValidFolder(Root + "/" + f)) AssetDatabase.CreateFolder(Root, f);
    }

    static void AddToBuildSettings(string path)
    {
        var scenes = EditorBuildSettings.scenes.ToList();
        if (scenes.Any(s => s.path == path)) return;
        scenes.Insert(0, new EditorBuildSettingsScene(path, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }
}
