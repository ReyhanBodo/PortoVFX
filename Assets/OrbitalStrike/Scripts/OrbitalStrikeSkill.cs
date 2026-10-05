using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Orbital Strike skill: missile launches, laps an accelerator ring around Venus,
/// gets slung back to Earth and lands as a kinetic rod. Press F to fire, R to reset.
/// All VFX are generated at runtime; scene objects are wired by OrbitalStrikeSceneBuilder.
/// </summary>
public class OrbitalStrikeSkill : MonoBehaviour
{
    [Header("Scene")]
    public Camera cam;
    public Light sun;
    public Volume volume;
    public Transform cityRoot;
    public Transform launchPad;
    public Transform impactPoint;
    public Transform groundCamAnchor;

    [Header("Space")]
    public Transform spaceRoot;
    public Transform venus;
    public Transform ringPivot;
    public Renderer ringRenderer;
    public Transform gate;

    [Header("Prefabs & Materials")]
    public GameObject missilePrefab;
    public GameObject rodPrefab;
    public Material particleAdditive;
    public Material particleAlpha;
    public Material shockwaveMat;

    [Header("HUD")]
    public Text statusText;
    public Text subText;
    public Text velocityText;
    public RectTransform velocityFill;
    public Image flash;
    public Image thermal;
    public Image reticle;
    public GameObject hudRoot;
    public Text hintText;

    [Header("Tuning")]
    public float ringRadius = 560f;
    public int laps = 7;
    public float lapDuration = 6.5f;
    public float missileSpaceScale = 6f;
    public float rodScale = 14f;
    public Color hudColor = new Color(1f, 0.48f, 0.1f);

    [Header("Audio (clips loaded from Resources/OSAudio)")]
    [Range(0f, 1f)] public float musicVolume = 0.55f;
    [Range(0f, 1f)] public float sfxVolume = 0.9f;

    // runtime
    AudioSource musicSrc, sfxSrc, engineSrc, railSrc;
    readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
    bool running;
    GameObject missile, rod;
    TrailRenderer trail;
    ParticleSystem exhaust, flame, padSmoke, speedLines, stars, dustRing, dustUp, fireBurst, smokeColumn, debrisSparks;
    Texture2D texDot, texLine, texSmoke;
    Material lineMat;
    Bloom bloom;
    ChromaticAberration chroma;
    LensDistortion lens;
    Vector3 camBasePos;
    Quaternion camBaseRot;
    float shake;
    float baseFov;
    Color sunColor;
    float sunIntensity, ambientIntensity;
    Quaternion sunRot;
    Coroutine statusTyping;
    readonly List<Material> ringGlowMats = new List<Material>();
    readonly List<Color> ringGlowBase = new List<Color>();

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        texDot = MakeDotTexture(64);
        texLine = MakeLineTexture(64);
        texSmoke = MakeSmokeTexture(128);

        if (volume != null)
        {
            var profile = volume.profile; // runtime copy, asset stays untouched
            profile.TryGet(out bloom);
            profile.TryGet(out chroma);
            profile.TryGet(out lens);
        }

        baseFov = cam.fieldOfView;
        sunColor = sun.color;
        sunIntensity = sun.intensity;
        sunRot = sun.transform.rotation;
        ambientIntensity = RenderSettings.ambientIntensity;
        camBasePos = cam.transform.position;
        camBaseRot = cam.transform.rotation;

        if (ringRenderer != null)
        {
            foreach (var m in ringRenderer.materials)
            {
                if (!m.name.Contains("Glow")) continue;
                ringGlowMats.Add(m);
                ringGlowBase.Add(m.GetColor("_EmissionColor"));
            }
            SetRingGlow(0.05f);
        }

        if (reticle != null) reticle.sprite = MakeSprite(MakeReticleTexture(256));
        if (thermal != null) thermal.sprite = MakeSprite(MakeThermalTexture(512));

        BuildMissile();
        BuildEffects();
        BuildAudio();
        ResetHud();
    }

    void Update()
    {
        if (!running && FirePressed()) StartCoroutine(Sequence());
        if (ResetPressed()) SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        if (venus != null) venus.Rotate(Vector3.up, 1.5f * Time.deltaTime, Space.Self);
    }

    void LateUpdate()
    {
        shake = Mathf.MoveTowards(shake, 0f, Time.deltaTime * Mathf.Max(1f, shake * 0.9f));
        var t = Time.time * 35f;
        var offset = new Vector3(Mathf.PerlinNoise(t, 0f) - 0.5f, Mathf.PerlinNoise(0f, t) - 0.5f, 0f) * shake;
        cam.transform.SetPositionAndRotation(camBasePos + camBaseRot * offset, camBaseRot);
    }

    void OnDestroy()
    {
        Physics.gravity = new Vector3(0f, -9.81f, 0f);
    }

    /// <summary>Fire the skill from UI or other scripts.</summary>
    public void Fire()
    {
        if (!running) StartCoroutine(Sequence());
    }

    static bool FirePressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.fKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.F);
#endif
    }

    static bool ResetPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.R);
#endif
    }

    // ------------------------------------------------------------------ sequence

    IEnumerator Sequence()
    {
        running = true;
        if (hintText != null) hintText.gameObject.SetActive(false);
        hudRoot.SetActive(true);
        musicSrc.clip = Clip("os_music");
        musicSrc.Play();

        // ---------------- 1. Launch ----------------
        EnterGround();
        var m = missile.transform;
        missile.SetActive(true);
        m.localScale = Vector3.one * 2.5f;
        m.SetPositionAndRotation(launchPad.position + Vector3.up * 6f, Quaternion.LookRotation(Vector3.up, Vector3.forward));
        ConfigureTrail(2.5f, 1.4f, new Color(1f, 0.85f, 0.7f));
        Status("[ LAUNCH SEQUENCE ]");
        Sub("SILO 01  ·  PAYLOAD: KINETIC ROD  ·  TARGET: EARTH");
        SetVelocity(0f, 0f);

        var padCam = launchPad.position + new Vector3(55f, 14f, -80f);
        padSmoke.transform.position = launchPad.position;
        padSmoke.Play();
        yield return Tween(1.2f, k =>
        {
            SetCam(padCam, m.position + Vector3.up * 8f);
            shake = 0.4f * k;
        });

        exhaust.Play();
        flame.Play();
        trail.emitting = true;
        Status("[ IGNITION ]");
        Sfx("os_ignition");
        engineSrc.pitch = 1f;
        StartCoroutine(FadeSource(engineSrc, 0.8f, 0.6f));
        const float launchAccel = 95f;
        const float launchTime = 3.4f;
        yield return Tween(launchTime, k =>
        {
            var t = k * launchTime;
            var h = 0.5f * launchAccel * t * t;
            m.position = launchPad.position + Vector3.up * (6f + h);
            if (t < 1.5f) SetCam(padCam, Vector3.Lerp(m.position, launchPad.position, 0.2f));
            else SetCam(m.position + new Vector3(14f, -38f, -16f), m.position + Vector3.up * 80f);
            if (t > 1.5f && t < 1.6f) Status("[ ASCENT ]");
            shake = Mathf.Lerp(1.5f, 0.3f, k);
            SetVelocity(Mathf.Lerp(0f, 0.0011f, k), k * 0.05f);
        });

        Sfx("os_whoosh", 0.8f);
        yield return FlashTo(1f, 0.35f);
        exhaust.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        padSmoke.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        // ---------------- 2. Approach Venus ----------------
        EnterSpace();
        var c = venus.position;
        var R = ringPivot.rotation;
        var n = R * Vector3.up;
        var gatePos = PathPos(0f);
        var gateFwd = PathTan(0f);
        var start = gatePos - gateFwd * 3000f + n * 220f;

        m.localScale = Vector3.one * missileSpaceScale;
        Teleport(start, Quaternion.LookRotation(gatePos - start, n));
        ConfigureTrail(9f, 1.6f, hudColor);
        trail.emitting = true;
        StartCoroutine(FlashTo(0f, 0.4f));

        StartCoroutine(FadeSource(engineSrc, 0.25f, 0.5f));
        railSrc.pitch = 0.7f;
        StartCoroutine(FadeSource(railSrc, 0.5f, 2.5f));
        Status("[ ACCELERATOR WAKING ]");
        Sub("VENUS ORBITAL RAIL  ·  GATE 00  ·  7 LAPS");
        var wideFar = c + (R * new Vector3(-0.25f, 0.45f, -1f)).normalized * 4200f;
        var wideNear = c + (R * new Vector3(-0.2f, 0.4f, -1f)).normalized * 2200f;
        const float approachTime = 3f;
        yield return Tween(approachTime, k =>
        {
            m.position = Vector3.Lerp(start, gatePos + n * 4f, k);
            SetCam(Vector3.Lerp(wideFar, wideNear, Smooth(k)), Vector3.Lerp(c, (c + gatePos) * 0.5f, k * 0.5f));
            SetRingGlow(Mathf.Lerp(0.05f, 1f, k));
            SetVelocity(Mathf.Lerp(0.0011f, 0.012f, k), Mathf.Lerp(0.05f, 0.1f, k));
        });

        // ---------------- 3. Laps ----------------
        var total = laps * Mathf.PI * 2f;
        var v0 = 3000f / approachTime / ringRadius;
        var T = lapDuration;
        var vEnd = v0 + 3f * (total - v0 * T) / T;
        var lastLap = 0;
        var beamTimer = 0f;
        Sub("SLINGSHOT CHARGE  ·  RAIL CURRENT 4.2 TA");
        yield return Tween(T, k =>
        {
            var t = k * T;
            var a = v0 * t + (total - v0 * T) * k * k * k;
            var omega = v0 + 3f * (total - v0 * T) * k * k / T;
            var speed01 = Mathf.InverseLerp(v0, vEnd, omega);
            var pos = PathPos(a) + n * 4f;
            var tan = PathTan(a);
            m.SetPositionAndRotation(pos, Quaternion.LookRotation(tan, n));

            var lap = Mathf.Min(laps, Mathf.FloorToInt(a / (Mathf.PI * 2f)) + 1);
            if (lap != lastLap)
            {
                lastLap = lap;
                Status($"[ LAP {lap} / {laps} ]", false);
                Sfx("os_lap_zap", Mathf.Lerp(0.6f, 1f, (float)lap / laps));
            }
            railSrc.pitch = Mathf.Lerp(0.7f, 2.4f, speed01);
            railSrc.volume = Mathf.Lerp(0.5f, 0.9f, speed01);

            trail.time = Mathf.Lerp(1.6f, 0.5f, speed01);
            trail.widthMultiplier = Mathf.Lerp(9f, 16f, speed01);
            SetVelocity(Mathf.Lerp(0.012f, 0.91f, speed01 * speed01), Mathf.Lerp(0.1f, 1f, speed01));
            if (bloom != null) bloom.intensity.value = Mathf.Lerp(1.2f, 3.5f, speed01);
            shake = speed01 * 0.6f;

            if (t < T * 0.38f)
            {
                var radial = (pos - c).normalized;
                SetCam(pos - tan * 120f + n * 38f + radial * 55f, pos + tan * 240f);
            }
            else if (t < T - 0.5f)
            {
                SetCam(c + (R * new Vector3(0.3f, 0.6f, -1f)).normalized * 1650f, c);
                beamTimer -= Time.deltaTime;
                if (lap >= 3 && beamTimer <= 0f)
                {
                    beamTimer = Mathf.Lerp(0.12f, 0.04f, speed01);
                    SpawnBeam(pos, tan, Mathf.Lerp(900f, 3200f, speed01));
                }
            }
            else
            {
                SetCam(gatePos + (R * new Vector3(1f, 0.25f, 0.6f)).normalized * 260f, gatePos);
            }
        });

        // ---------------- 4. Release + warp ----------------
        Status("[ RELEASE ]");
        Sub("EXIT VELOCITY 0.91 c  ·  VECTOR LOCKED: EARTH");
        SpawnShockwave(gatePos, Quaternion.LookRotation(n, gateFwd), 900f, 1.1f, hudColor * 3f);
        StartCoroutine(Pulse(0.75f, 0.35f));
        if (bloom != null) bloom.intensity.value = 7f;
        shake = 3f;
        SetRingGlow(0.3f);
        Sfx("os_release_boom");
        Sfx("os_warp", 0.8f);
        StartCoroutine(FadeSource(railSrc, 0f, 0.6f));

        speedLines.Play();
        var warpStart = m.position;
        const float warpTime = 1.7f;
        yield return Tween(warpTime, k =>
        {
            var p = warpStart + gateFwd * (3200f * k * warpTime);
            m.SetPositionAndRotation(p, Quaternion.LookRotation(gateFwd, n));
            SetCam(p - gateFwd * 70f + n * 12f, p + gateFwd * 200f);
            cam.fieldOfView = Mathf.Lerp(baseFov, 100f, Smooth(Mathf.Min(1f, k * 2f)));
            if (chroma != null) chroma.intensity.value = Mathf.Lerp(0.2f, 1f, k);
            if (lens != null) lens.intensity.value = Mathf.Lerp(0f, -0.45f, k);
            if (bloom != null) bloom.intensity.value = Mathf.Lerp(7f, 2f, k);
            shake = 1.2f;
        });
        Sfx("os_whoosh", 0.9f);
        yield return FlashTo(1f, 0.25f);
        speedLines.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        cam.fieldOfView = baseFov;
        if (chroma != null) chroma.intensity.value = 0f;
        if (lens != null) lens.intensity.value = 0f;

        // ---------------- 5. Re-entry ----------------
        EnterGround();
        var impact = impactPoint.position;
        var reStart = impact + new Vector3(220f, 6500f, 420f);
        var reEnd = impact + Vector3.up * 650f;
        m.localScale = Vector3.one * 4f;
        Teleport(reStart, Quaternion.LookRotation(reEnd - reStart));
        ConfigureTrail(14f, 1.4f, new Color(1f, 0.75f, 0.45f));
        trail.emitting = true;
        StartCoroutine(FlashTo(0f, 0.35f));
        Sfx("os_reentry");
        engineSrc.pitch = 1.3f;
        StartCoroutine(FadeSource(engineSrc, 0.6f, 1.5f));
        Status("[ RE-ENTRY ]");
        Sub("HULL TEMP 4100 K  ·  ATMOSPHERIC INTERFACE");

        var groundCam = groundCamAnchor.position;
        const float reentryTime = 2.3f;
        var thermalShown = false;
        yield return Tween(reentryTime, k =>
        {
            var e = k * k;
            m.position = Vector3.Lerp(reStart, reEnd, e);
            var look = Vector3.Lerp(impact + Vector3.up * 1400f, impact + Vector3.up * 120f, Smooth(Mathf.InverseLerp(0.55f, 1f, k)));
            SetCam(groundCam, look);
            SetVelocity(0.91f, 1f);
            shake = Mathf.Lerp(0.2f, 1.4f, k);

            var showThermal = k > 0.35f && k < 0.75f;
            if (showThermal != thermalShown)
            {
                thermalShown = showThermal;
                if (showThermal)
                {
                    Status("[ TARGET LOCK ]");
                    Sub("COORD 06°12'S 106°49'E  ·  YIELD: KINETIC  ·  NO WARHEAD");
                    Sfx("os_lock_on", 0.8f);
                }
            }
            thermal.gameObject.SetActive(showThermal && Mathf.Repeat(Time.time * 12f, 1f) > 0.12f);
            reticle.gameObject.SetActive(k > 0.35f);
            PlaceReticle(impact);
        });
        thermal.gameObject.SetActive(false);

        // ---------------- 6. Kinetic rod impact ----------------
        trail.emitting = false;
        flame.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        missile.SetActive(false);
        rod.SetActive(true);
        rod.transform.localScale = Vector3.one * rodScale;
        var rodRot = Quaternion.LookRotation(Vector3.down, Vector3.forward);
        var rodEnd = impact + Vector3.down * 60f;
        Sfx("os_rod_fall");
        yield return Tween(0.22f, k =>
        {
            rod.transform.SetPositionAndRotation(Vector3.Lerp(reEnd, rodEnd, k), rodRot);
            SetCam(groundCam, impact + Vector3.up * 120f);
        });

        Impact(impact);
        Sfx("os_impact");
        StartCoroutine(FadeSource(engineSrc, 0f, 0.3f));
        Status("[ IMPACT CONFIRMED ]");
        Sub("ZONE 0000 FLATTENED  ·  ORBITAL STRIKE  ·  0.91 c");
        reticle.gameObject.SetActive(false);

        // ---------------- 7. Aftermath ----------------
        var back = groundCam + new Vector3(-160f, 90f, -260f);
        yield return Tween(6f, k =>
        {
            SetCam(Vector3.Lerp(groundCam, back, Smooth(k)), impact + Vector3.up * Mathf.Lerp(120f, 90f, k));
            if (bloom != null) bloom.intensity.value = Mathf.Lerp(5f, 1.3f, Smooth(k));
            SetVelocity(Mathf.Lerp(0.91f, 0f, k), 1f - k);
        });

        if (hintText != null)
        {
            hintText.text = "Tekan [R] untuk reset";
            hintText.gameObject.SetActive(true);
        }
        running = false;
    }

    // ------------------------------------------------------------------ stages helpers

    void EnterGround()
    {
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.farClipPlane = 20000f;
        sun.transform.rotation = sunRot;
        sun.color = sunColor;
        sun.intensity = sunIntensity;
        RenderSettings.ambientIntensity = ambientIntensity;
        if (bloom != null) bloom.intensity.value = 1.2f;
    }

    void EnterSpace()
    {
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.farClipPlane = 20000f;
        sun.transform.rotation = Quaternion.LookRotation(ringPivot.rotation * new Vector3(1f, -0.25f, 0.55f));
        sun.color = new Color(1f, 0.96f, 0.9f);
        sun.intensity = 2.2f;
        RenderSettings.ambientIntensity = 0.12f;
        if (bloom != null) bloom.intensity.value = 1.2f;
    }

    Vector3 PathPos(float a) => ringPivot.position + ringPivot.rotation * new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * ringRadius;
    Vector3 PathTan(float a) => ringPivot.rotation * new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));

    void Teleport(Vector3 pos, Quaternion rot)
    {
        trail.emitting = false;
        missile.transform.SetPositionAndRotation(pos, rot);
        trail.Clear();
        exhaust.Clear();
    }

    void ConfigureTrail(float width, float time, Color color)
    {
        trail.widthMultiplier = width;
        trail.time = time;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(color, 0.25f), new GradientColorKey(color, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.6f, 0.4f), new GradientAlphaKey(0f, 1f) });
        trail.colorGradient = g;
    }

    void SetRingGlow(float k)
    {
        for (var i = 0; i < ringGlowMats.Count; i++)
            ringGlowMats[i].SetColor("_EmissionColor", ringGlowBase[i] * k);
    }

    void Impact(Vector3 p)
    {
        StartCoroutine(Pulse(1f, 0.6f));
        shake = 9f;
        if (bloom != null) bloom.intensity.value = 6f;

        SpawnShockwave(p + Vector3.up * 1.5f, Quaternion.Euler(90f, 0f, 0f), 2600f, 2.2f, new Color(1f, 0.6f, 0.3f) * 3f);
        SpawnShockwave(p + Vector3.up * 2f, Quaternion.Euler(90f, 0f, 0f), 1200f, 1.2f, Color.white * 2f);

        dustRing.transform.position = p + Vector3.up * 4f;
        dustRing.Emit(450);
        dustUp.transform.position = p;
        dustUp.Emit(220);
        fireBurst.transform.position = p + Vector3.up * 5f;
        fireBurst.Emit(160);
        debrisSparks.transform.position = p + Vector3.up * 5f;
        debrisSparks.Emit(300);
        smokeColumn.transform.position = p;
        smokeColumn.Play();

        // crater
        var crater = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(crater.GetComponent<Collider>());
        crater.name = "Crater";
        crater.transform.position = p + Vector3.up * 0.05f;
        crater.transform.localScale = new Vector3(150f, 0.1f, 150f);
        var cm = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        cm.SetColor("_BaseColor", new Color(0.07f, 0.05f, 0.04f));
        cm.SetFloat("_Smoothness", 0f);
        crater.GetComponent<Renderer>().sharedMaterial = cm;
        var glow = SpawnShockwave(p + Vector3.up * 0.4f, Quaternion.Euler(90f, 0f, 0f), 200f, 0f, new Color(1f, 0.35f, 0.05f) * 4f);
        glow.SetFloat("_Radius", 0.55f);
        glow.SetFloat("_Width", 0.25f);
        glow.SetFloat("_Inner", 0.6f);

        // buildings — stronger gravity so city-scale debris doesn't float
        Physics.gravity = new Vector3(0f, -35f, 0f);
        if (cityRoot == null) return;
        foreach (Transform b in cityRoot)
        {
            var d = Vector2.Distance(new Vector2(b.position.x, b.position.z), new Vector2(p.x, p.z));
            if (d < 90f) { b.gameObject.SetActive(false); continue; }
            if (d > 260f) continue;
            var rb = b.gameObject.AddComponent<Rigidbody>();
            rb.mass = Mathf.Max(1f, b.localScale.x * b.localScale.y * b.localScale.z * 0.01f);
            rb.AddExplosionForce(Mathf.Lerp(55f, 12f, d / 260f), p + Vector3.down * 8f, 300f, 0.35f, ForceMode.VelocityChange);
            rb.AddTorque(UnityEngine.Random.insideUnitSphere * 1.5f, ForceMode.VelocityChange);
        }
    }

    // ------------------------------------------------------------------ camera / HUD

    void SetCam(Vector3 pos, Vector3 lookAt)
    {
        camBasePos = pos;
        var dir = lookAt - pos;
        if (dir.sqrMagnitude > 1e-6f) camBaseRot = Quaternion.LookRotation(dir, Vector3.up);
    }

    void ResetHud()
    {
        hudRoot.SetActive(false);
        flash.color = new Color(1f, 1f, 1f, 0f);
        thermal.gameObject.SetActive(false);
        reticle.gameObject.SetActive(false);
        statusText.color = hudColor;
        if (hintText != null)
        {
            hintText.text = "Tekan [F] untuk Orbital Strike";
            hintText.gameObject.SetActive(true);
        }
    }

    void Status(string s, bool typed = true)
    {
        if (statusTyping != null) StopCoroutine(statusTyping);
        if (typed)
        {
            Sfx("os_ui_alert", 0.5f);
            statusTyping = StartCoroutine(TypeText(statusText, s));
        }
        else statusText.text = s;
    }

    void Sub(string s) => subText.text = s;

    void SetVelocity(float c, float fill01)
    {
        velocityText.text = $"VELOCITY  {c:0.0000} c";
        velocityFill.anchorMax = new Vector2(Mathf.Clamp01(fill01), 1f);
    }

    void PlaceReticle(Vector3 world)
    {
        var sp = cam.WorldToScreenPoint(world);
        if (sp.z < 0f) return;
        var canvasRect = (RectTransform)reticle.canvas.transform;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, sp, null, out var local);
        reticle.rectTransform.anchoredPosition = local;
        reticle.rectTransform.localRotation = Quaternion.Euler(0f, 0f, Time.time * 40f);
    }

    IEnumerator TypeText(Text t, string s)
    {
        for (var i = 0; i <= s.Length; i++)
        {
            t.text = s.Substring(0, i);
            if (i % 2 == 1 && s[i - 1] != ' ') Sfx("os_ui_type", 0.35f);
            yield return new WaitForSeconds(0.025f);
        }
    }

    // ------------------------------------------------------------------ audio

    void BuildAudio()
    {
        foreach (var c in Resources.LoadAll<AudioClip>("OSAudio")) clips[c.name] = c;
        musicSrc = NewSource("Music", false, musicVolume);
        sfxSrc = NewSource("SFX", false, sfxVolume);
        engineSrc = NewSource("EngineLoop", true, 0f);
        engineSrc.clip = Clip("os_engine_loop");
        railSrc = NewSource("RailLoop", true, 0f);
        railSrc.clip = Clip("os_rail_loop");
    }

    AudioSource NewSource(string name, bool loop, float volume)
    {
        var go = new GameObject("Audio_" + name);
        go.transform.SetParent(transform, false);
        var s = go.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = loop;
        s.volume = volume;
        s.spatialBlend = 0f; // 2D: the sequence cuts between ground and space cameras
        return s;
    }

    AudioClip Clip(string clipName) => clips.TryGetValue(clipName, out var c) ? c : null;

    void Sfx(string clipName, float volume = 1f)
    {
        var c = Clip(clipName);
        if (c != null) sfxSrc.PlayOneShot(c, volume);
    }

    IEnumerator FadeSource(AudioSource s, float target, float dur)
    {
        if (target > 0f && !s.isPlaying) s.Play();
        var from = s.volume;
        yield return Tween(dur, k => s.volume = Mathf.Lerp(from, target, k));
        if (target <= 0f) s.Stop();
    }

    IEnumerator FlashTo(float target, float dur)
    {
        var from = flash.color.a;
        yield return Tween(dur, k => flash.color = new Color(1f, 1f, 1f, Mathf.Lerp(from, target, k)));
    }

    IEnumerator Pulse(float peak, float dur)
    {
        yield return Tween(dur, k => flash.color = new Color(1f, 1f, 1f, peak * (1f - k) * (1f - k)));
    }

    static IEnumerator Tween(float dur, Action<float> step)
    {
        var t = 0f;
        while (t < dur)
        {
            step(t / dur);
            t += Time.deltaTime;
            yield return null;
        }
        step(1f);
    }

    static float Smooth(float k) => k * k * (3f - 2f * k);

    // ------------------------------------------------------------------ VFX builders

    void BuildMissile()
    {
        missile = Instantiate(missilePrefab);
        missile.name = "Missile";
        missile.SetActive(false);

        trail = missile.AddComponent<TrailRenderer>();
        trail.material = NewMat(particleAdditive, texLine, Color.white, 2.5f);
        trail.minVertexDistance = 0.5f;
        trail.emitting = false;
        trail.widthCurve = new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(0.15f, 1f), new Keyframe(1f, 0.1f));
        trail.alignment = LineAlignment.View;
        trail.numCapVertices = 4;

        // engine flame glow (local space, sits behind nozzle)
        flame = NewPS("Flame", missile.transform, NewMat(particleAdditive, texDot, new Color(1f, 0.65f, 0.3f), 4f));
        flame.transform.localPosition = new Vector3(0f, 0f, -2.3f);
        var fm = flame.main;
        fm.loop = true;
        fm.startLifetime = 0.12f;
        fm.startSpeed = 0f;
        fm.startSize = new ParticleSystem.MinMaxCurve(0.9f, 1.5f);
        fm.simulationSpace = ParticleSystemSimulationSpace.Local;
        fm.scalingMode = ParticleSystemScalingMode.Hierarchy;
        var fe = flame.emission;
        fe.rateOverTime = 60f;
        var fs = flame.shape;
        fs.enabled = false;

        // exhaust smoke (world space, emitted by distance)
        exhaust = NewPS("Exhaust", missile.transform, NewMat(particleAlpha, texSmoke, new Color(0.92f, 0.9f, 0.88f), 1f));
        exhaust.transform.localPosition = new Vector3(0f, 0f, -2.4f);
        var em = exhaust.main;
        em.loop = true;
        em.startLifetime = new ParticleSystem.MinMaxCurve(3f, 5f);
        em.startSpeed = new ParticleSystem.MinMaxCurve(2f, 8f);
        em.startSize = new ParticleSystem.MinMaxCurve(6f, 14f);
        em.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        em.simulationSpace = ParticleSystemSimulationSpace.World;
        em.maxParticles = 3000;
        var ee = exhaust.emission;
        ee.rateOverTime = 40f;
        ee.rateOverDistance = 1.2f;
        var es = exhaust.shape;
        es.shapeType = ParticleSystemShapeType.Cone;
        es.angle = 12f;
        es.radius = 0.5f;
        es.rotation = new Vector3(180f, 0f, 0f);
        FadeAndGrow(exhaust, 2.6f);

        rod = Instantiate(rodPrefab);
        rod.name = "KineticRod";
        rod.SetActive(false);
        // brighter steel + hotter bands so the rod reads against dust and sky
        foreach (var r in rod.GetComponentsInChildren<Renderer>())
        foreach (var mat in r.materials)
        {
            if (mat.name.Contains("Hull"))
            {
                mat.SetColor("_BaseColor", new Color(0.3f, 0.31f, 0.34f));
                mat.SetFloat("_Metallic", 0.45f);
                mat.SetFloat("_Smoothness", 0.35f);
            }
            else if (mat.name.Contains("Glow"))
            {
                mat.SetColor("_EmissionColor", mat.GetColor("_EmissionColor") * 2.5f);
            }
        }
    }

    void BuildEffects()
    {
        var fx = new GameObject("OrbitalStrikeFX").transform;

        padSmoke = NewPS("PadSmoke", fx, NewMat(particleAlpha, texSmoke, new Color(0.85f, 0.84f, 0.82f), 1f));
        var pm = padSmoke.main;
        pm.loop = true;
        pm.startLifetime = new ParticleSystem.MinMaxCurve(4f, 7f);
        pm.startSpeed = new ParticleSystem.MinMaxCurve(10f, 30f);
        pm.startSize = new ParticleSystem.MinMaxCurve(12f, 26f);
        pm.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        pm.maxParticles = 800;
        var pe = padSmoke.emission;
        pe.rateOverTime = 90f;
        var ps = padSmoke.shape;
        ps.shapeType = ParticleSystemShapeType.Circle;
        ps.radius = 6f;
        ps.rotation = new Vector3(-90f, 0f, 0f);
        Drag(padSmoke, 0.8f);
        FadeAndGrow(padSmoke, 2.2f);

        speedLines = NewPS("SpeedLines", cam.transform, NewMat(particleAdditive, texLine, new Color(0.8f, 0.9f, 1f), 2f));
        speedLines.transform.localPosition = new Vector3(0f, 0f, 420f);
        speedLines.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        var sm = speedLines.main;
        sm.loop = true;
        sm.startLifetime = 0.6f;
        sm.startSpeed = new ParticleSystem.MinMaxCurve(900f, 1400f);
        sm.startSize = new ParticleSystem.MinMaxCurve(0.6f, 1.6f);
        sm.simulationSpace = ParticleSystemSimulationSpace.Local;
        sm.maxParticles = 1500;
        var se = speedLines.emission;
        se.rateOverTime = 700f;
        var ss = speedLines.shape;
        ss.shapeType = ParticleSystemShapeType.Circle;
        ss.radius = 90f;
        ss.radiusThickness = 0.6f;
        var sr = speedLines.GetComponent<ParticleSystemRenderer>();
        sr.renderMode = ParticleSystemRenderMode.Stretch;
        sr.velocityScale = 0.06f;
        sr.lengthScale = 2f;

        stars = NewPS("Stars", spaceRoot, NewMat(particleAdditive, texDot, Color.white, 1.6f));
        stars.transform.localPosition = Vector3.zero;
        var stm = stars.main;
        stm.loop = false;
        stm.startLifetime = 1e6f;
        stm.startSpeed = 0f;
        stm.startSize = new ParticleSystem.MinMaxCurve(5f, 16f);
        stm.startColor = new ParticleSystem.MinMaxGradient(new Color(0.75f, 0.82f, 1f), new Color(1f, 0.92f, 0.8f));
        stm.simulationSpace = ParticleSystemSimulationSpace.Local;
        stm.maxParticles = 5000;
        var ste = stars.emission;
        ste.enabled = false;
        var sts = stars.shape;
        sts.shapeType = ParticleSystemShapeType.Sphere;
        sts.radius = 7000f;
        sts.radiusThickness = 0f;
        stars.Emit(4500);

        dustRing = NewPS("DustRing", fx, NewMat(particleAlpha, texSmoke, new Color(0.75f, 0.62f, 0.5f), 1f));
        var dm = dustRing.main;
        dm.loop = false;
        dm.startLifetime = new ParticleSystem.MinMaxCurve(5f, 9f);
        dm.startSpeed = new ParticleSystem.MinMaxCurve(120f, 420f);
        dm.startSize = new ParticleSystem.MinMaxCurve(40f, 110f);
        dm.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        dm.maxParticles = 1000;
        dustRing.emission.SetBursts(Array.Empty<ParticleSystem.Burst>());
        var de = dustRing.emission;
        de.rateOverTime = 0f;
        var ds = dustRing.shape;
        ds.shapeType = ParticleSystemShapeType.Circle;
        ds.radius = 30f;
        ds.rotation = new Vector3(-90f, 0f, 0f);
        Drag(dustRing, 1.4f);
        FadeAndGrow(dustRing, 2.5f);

        dustUp = NewPS("DustUp", fx, NewMat(particleAlpha, texSmoke, new Color(0.62f, 0.5f, 0.42f), 1f));
        var du = dustUp.main;
        du.loop = false;
        du.startLifetime = new ParticleSystem.MinMaxCurve(6f, 10f);
        du.startSpeed = new ParticleSystem.MinMaxCurve(60f, 260f);
        du.startSize = new ParticleSystem.MinMaxCurve(50f, 130f);
        du.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        du.maxParticles = 600;
        var due = dustUp.emission;
        due.rateOverTime = 0f;
        var dus = dustUp.shape;
        dus.shapeType = ParticleSystemShapeType.Hemisphere;
        dus.radius = 40f;
        dus.rotation = new Vector3(-90f, 0f, 0f);
        Drag(dustUp, 1.1f);
        FadeAndGrow(dustUp, 2.2f);

        fireBurst = NewPS("FireBurst", fx, NewMat(particleAdditive, texSmoke, new Color(1f, 0.55f, 0.2f), 3f));
        var fbm = fireBurst.main;
        fbm.loop = false;
        fbm.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.8f);
        fbm.startSpeed = new ParticleSystem.MinMaxCurve(80f, 300f);
        fbm.startSize = new ParticleSystem.MinMaxCurve(40f, 100f);
        fbm.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        fbm.maxParticles = 400;
        var fbe = fireBurst.emission;
        fbe.rateOverTime = 0f;
        var fbs = fireBurst.shape;
        fbs.shapeType = ParticleSystemShapeType.Hemisphere;
        fbs.radius = 20f;
        fbs.rotation = new Vector3(-90f, 0f, 0f);
        Drag(fireBurst, 2f);
        FadeAndGrow(fireBurst, 1.8f);

        debrisSparks = NewPS("Sparks", fx, NewMat(particleAdditive, texLine, new Color(1f, 0.7f, 0.35f), 4f));
        var dsm = debrisSparks.main;
        dsm.loop = false;
        dsm.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 3.5f);
        dsm.startSpeed = new ParticleSystem.MinMaxCurve(100f, 380f);
        dsm.startSize = new ParticleSystem.MinMaxCurve(1.5f, 3.5f);
        dsm.gravityModifier = 9f;
        dsm.maxParticles = 600;
        var dse = debrisSparks.emission;
        dse.rateOverTime = 0f;
        var dss = debrisSparks.shape;
        dss.shapeType = ParticleSystemShapeType.Hemisphere;
        dss.radius = 15f;
        dss.rotation = new Vector3(-90f, 0f, 0f);
        var dsr = debrisSparks.GetComponent<ParticleSystemRenderer>();
        dsr.renderMode = ParticleSystemRenderMode.Stretch;
        dsr.velocityScale = 0.05f;

        smokeColumn = NewPS("SmokeColumn", fx, NewMat(particleAlpha, texSmoke, new Color(0.38f, 0.33f, 0.3f, 0.45f), 1f));
        var scm = smokeColumn.main;
        scm.loop = true;
        scm.startLifetime = new ParticleSystem.MinMaxCurve(8f, 12f);
        scm.startSpeed = new ParticleSystem.MinMaxCurve(20f, 45f);
        scm.startSize = new ParticleSystem.MinMaxCurve(50f, 100f);
        scm.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        scm.maxParticles = 800;
        var sce = smokeColumn.emission;
        sce.rateOverTime = 18f;
        var scs = smokeColumn.shape;
        scs.shapeType = ParticleSystemShapeType.Cone;
        scs.angle = 22f;
        scs.radius = 90f;
        scs.radiusThickness = 0.3f;
        scs.rotation = new Vector3(-90f, 0f, 0f);
        FadeAndGrow(smokeColumn, 2.5f);

        lineMat = NewMat(particleAdditive, texLine, hudColor, 3f);
    }

    void SpawnBeam(Vector3 pos, Vector3 dir, float length)
    {
        var go = new GameObject("Beam");
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = lineMat;
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.SetPosition(0, pos - dir * length * 0.15f);
        lr.SetPosition(1, pos + dir * length);
        lr.widthMultiplier = 10f;
        lr.alignment = LineAlignment.View;
        StartCoroutine(FadeLine(lr, 0.7f));
    }

    IEnumerator FadeLine(LineRenderer lr, float dur)
    {
        yield return Tween(dur, k =>
        {
            var a = (1f - k) * (1f - k);
            lr.startColor = new Color(1f, 1f, 1f, a);
            lr.endColor = new Color(1f, 1f, 1f, 0f);
        });
        Destroy(lr.gameObject);
    }

    Material SpawnShockwave(Vector3 pos, Quaternion rot, float size, float dur, Color color)
    {
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(q.GetComponent<Collider>());
        q.name = "Shockwave";
        q.transform.SetPositionAndRotation(pos, rot);
        q.transform.localScale = Vector3.one * size;
        var m = new Material(shockwaveMat);
        m.SetColor("_Color", color);
        q.GetComponent<Renderer>().sharedMaterial = m;
        if (dur > 0f) StartCoroutine(AnimateShockwave(q, m, dur));
        return m;
    }

    IEnumerator AnimateShockwave(GameObject q, Material m, float dur)
    {
        yield return Tween(dur, k =>
        {
            var e = 1f - (1f - k) * (1f - k);
            m.SetFloat("_Radius", Mathf.Lerp(0.02f, 0.95f, e));
            m.SetFloat("_Width", Mathf.Lerp(0.04f, 0.12f, k));
            m.SetFloat("_Intensity", Mathf.Lerp(4f, 0f, k));
        });
        Destroy(q);
    }

    // ------------------------------------------------------------------ utilities

    ParticleSystem NewPS(string name, Transform parent, Material mat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Shape;
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = mat;
        r.sortMode = ParticleSystemSortMode.Distance;
        r.maxParticleSize = 4f;
        return ps;
    }

    static void FadeAndGrow(ParticleSystem ps, float grow)
    {
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.9f, 0.08f), new GradientAlphaKey(0.5f, 0.6f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(g);
        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, grow));
        var rot = ps.rotationOverLifetime;
        rot.enabled = true;
        rot.z = new ParticleSystem.MinMaxCurve(-0.3f, 0.3f);
    }

    static void Drag(ParticleSystem ps, float drag)
    {
        var lv = ps.limitVelocityOverLifetime;
        lv.enabled = true;
        lv.drag = drag;
        lv.multiplyDragByParticleSize = false;
        lv.multiplyDragByParticleVelocity = true;
    }

    static Material NewMat(Material template, Texture tex, Color color, float intensity)
    {
        var m = new Material(template);
        m.SetTexture("_BaseMap", tex);
        m.SetColor("_Color", color);
        m.SetFloat("_Intensity", intensity);
        return m;
    }

    static Sprite MakeSprite(Texture2D t) => Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f));

    static Texture2D NewTex(int w, int h)
    {
        return new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
    }

    static Texture2D MakeDotTexture(int s)
    {
        var t = NewTex(s, s);
        for (var y = 0; y < s; y++)
        for (var x = 0; x < s; x++)
        {
            var d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(s, s) * 0.5f) / (s * 0.5f);
            var a = Mathf.Clamp01(1f - d);
            t.SetPixel(x, y, new Color(1f, 1f, 1f, a * a * a));
        }
        t.Apply();
        return t;
    }

    static Texture2D MakeLineTexture(int s)
    {
        var t = NewTex(s, s);
        for (var y = 0; y < s; y++)
        {
            var d = Mathf.Abs((y + 0.5f) / s - 0.5f) * 2f;
            var a = Mathf.Pow(Mathf.Clamp01(1f - d), 2.2f);
            for (var x = 0; x < s; x++) t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        t.Apply();
        return t;
    }

    static Texture2D MakeSmokeTexture(int s)
    {
        var t = NewTex(s, s);
        for (var y = 0; y < s; y++)
        for (var x = 0; x < s; x++)
        {
            var u = (x + 0.5f) / s;
            var v = (y + 0.5f) / s;
            var d = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.5f)) * 2f;
            var n = Mathf.PerlinNoise(u * 4f + 3.1f, v * 4f + 7.7f) * 0.6f + Mathf.PerlinNoise(u * 9f, v * 9f) * 0.4f;
            var a = Mathf.Clamp01(1f - d) * Mathf.Clamp01(n * 1.6f - 0.25f);
            var shade = Mathf.Lerp(0.75f, 1f, n);
            t.SetPixel(x, y, new Color(shade, shade, shade, Mathf.SmoothStep(0f, 1f, a)));
        }
        t.Apply();
        return t;
    }

    Texture2D MakeReticleTexture(int s)
    {
        var t = NewTex(s, s);
        var c = new Vector2(s, s) * 0.5f;
        for (var y = 0; y < s; y++)
        for (var x = 0; x < s; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f) - c;
            var r = p.magnitude / (s * 0.5f);
            var ang = Mathf.Atan2(p.y, p.x) * Mathf.Rad2Deg;
            var ring = Mathf.Abs(r - 0.82f) < 0.018f && Mathf.Repeat(ang, 45f) > 8f;
            var inner = Mathf.Abs(r - 0.45f) < 0.012f;
            var tick = (Mathf.Abs(p.x) < 2f || Mathf.Abs(p.y) < 2f) && r > 0.55f && r < 0.98f;
            var dot = r < 0.04f;
            var a = ring || inner || tick || dot ? 1f : 0f;
            t.SetPixel(x, y, new Color(hudColor.r, hudColor.g, hudColor.b, a));
        }
        t.Apply();
        return t;
    }

    Texture2D MakeThermalTexture(int s)
    {
        var t = NewTex(s, s);
        t.wrapMode = TextureWrapMode.Repeat;
        for (var y = 0; y < s; y++)
        for (var x = 0; x < s; x++)
        {
            var u = (float)x / s;
            var v = (float)y / s;
            var n = Mathf.PerlinNoise(u * 7f, v * 7f) * 0.7f + Mathf.PerlinNoise(u * 23f, v * 23f) * 0.3f;
            var hot = n > 0.47f;
            var grid = (x % 32 == 0) || (y % 32 == 0);
            var col = hot ? new Color(1f, 0.42f, 0.12f, 0.92f) : new Color(0.02f, 0.01f, 0f, 0.92f);
            if (grid) col = new Color(0.05f, 0.02f, 0f, 0.95f);
            t.SetPixel(x, y, col);
        }
        t.Apply();
        return t;
    }
}
