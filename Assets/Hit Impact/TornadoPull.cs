using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class TornadoPull : MonoBehaviour
{
    private enum Phase
    {
        Pulling,
        Holding
    }

    private class CapturedInfo
    {
        public Phase phase;
        public float holdTimer;
    }

    [Header("Deteksi Object")]
    [Tooltip("Radius area di sekitar tornado yang akan menarik object")]
    [SerializeField] private float pullRadius = 10f;
    [Tooltip("Layer object yang boleh ditarik oleh tornado")]
    [SerializeField] private LayerMask affectedLayers = ~0;
    [Tooltip("Jeda waktu antar pengecekan object baru di sekitar tornado (detik)")]
    [SerializeField] private float detectionInterval = 0.25f;

    [Header("Tarikan (Pull)")]
    [Tooltip("Kekuatan tarikan menuju sumbu tengah tornado")]
    [SerializeField] private float pullForce = 15f;
    [Tooltip("Kekuatan putaran object mengelilingi sumbu tornado (efek spiral)")]
    [SerializeField] private float spinForce = 10f;
    [Tooltip("Radius area di mana object akan ikut berputar (spin) mengelilingi tornado. Di luar radius ini object tetap tertarik (pull) tapi tidak diputar")]
    [SerializeField] private float spinRadius = 6f;
    [Tooltip("Radius cincin orbit target. Object ditarik menuju jarak ini dari pusat (bukan ke titik tengah), supaya mereka muter-muter mengelilingi sumbu tornado, bukan menumpuk di tengah. Set kecil (mis. 1-2) kalau ingin mereka berputar rapat di inti")]
    [SerializeField] private float orbitRadius = 2f;
    [Tooltip("Kekuatan koreksi menuju radius orbit (mendorong keluar kalau terlalu dekat pusat, menarik masuk kalau di dalam spinRadius tapi di luar orbit)")]
    [SerializeField] private float orbitCorrectionForce = 12f;
    [Tooltip("Kekuatan dorongan object naik mengikuti tornado")]
    [SerializeField] private float liftForce = 6f;
    [Tooltip("Tinggi maksimum relatif terhadap tornado sebelum object mulai menahan (hold)")]
    [SerializeField] private float maxLiftHeight = 8f;
    [Tooltip("Jarak minimum ke sumbu tengah sebelum object mulai menahan (hold)")]
    [SerializeField] private float minCoreDistance = 1f;

    [Header("Containment (agar object tidak kabur dari radius)")]
    [Tooltip("Kelipatan pull force tambahan per unit jarak melebihi pullRadius (spring-like correction)")]
    [SerializeField] private float overshootCorrectionForce = 20f;
    [Tooltip("Seberapa besar kecepatan outward (menjauhi pusat) diredam saat object berada di luar radius, per detik (0 = tidak diredam, 1 = langsung dihentikan)")]
    [SerializeField, Range(0f, 1f)] private float outwardVelocityDamping = 0.6f;

    [Header("Tahan (Hold)")]
    [Tooltip("Berapa lama object berputar-putar di dalam tornado sebelum dihempaskan")]
    [SerializeField] private float holdDuration = 2f;

    [Header("Hempasan (Fling)")]
    [Tooltip("Kekuatan hempasan object keluar dari tornado")]
    [SerializeField] private float flingForce = 25f;
    [Tooltip("Kekuatan hempasan ke atas saat object dilempar")]
    [SerializeField] private float flingUpwardForce = 8f;
    [Tooltip("Torsi acak agar object berputar saat terhempas")]
    [SerializeField] private float flingTorque = 5f;
    [Tooltip("Jeda sebelum object yang baru dihempaskan bisa tertarik lagi, supaya siklus pull-hold-blow terlihat looping")]
    [SerializeField] private float recaptureCooldown = 1.5f;
    [Tooltip("Jeda minimum antar hempasan (fling) satu object ke object berikutnya, supaya tidak semua object dihempaskan bersamaan")]
    [SerializeField] private float flingStaggerInterval = 0.4f;

    [Header("Debug")]
    [SerializeField] private bool drawGizmos = true;
    [Tooltip("Offset ketinggian (sumbu Y) untuk posisi gizmo saja, tidak mempengaruhi perhitungan fisika/pull")]
    [SerializeField] private float gizmoYOffset = 0f;

    private readonly Dictionary<Rigidbody, CapturedInfo> _captured = new Dictionary<Rigidbody, CapturedInfo>();
    private readonly Dictionary<Rigidbody, float> _cooldowns = new Dictionary<Rigidbody, float>();
    private readonly List<Rigidbody> _buffer = new List<Rigidbody>();
    private readonly List<Rigidbody> _flingQueue = new List<Rigidbody>();
    private float _detectionTimer;
    private float _flingStaggerTimer;

    private void FixedUpdate()
    {
        _detectionTimer -= Time.fixedDeltaTime;
        if (_detectionTimer <= 0f)
        {
            _detectionTimer = detectionInterval;
            DetectNearbyObjects();
        }

        PullCapturedObjects();
        ProcessFlingQueue();
        TickCooldowns();
    }

    private void DetectNearbyObjects()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, pullRadius, affectedLayers, QueryTriggerInteraction.Ignore);
        foreach (var hit in hits)
        {
            Rigidbody rb = hit.attachedRigidbody;
            if (rb == null || rb.isKinematic || rb.gameObject == gameObject) continue;
            if (_captured.ContainsKey(rb) || _cooldowns.ContainsKey(rb)) continue;
            _captured.Add(rb, new CapturedInfo { phase = Phase.Pulling, holdTimer = 0f });
        }
    }

    private void PullCapturedObjects()
    {
        if (_captured.Count == 0) return;

        _buffer.Clear();
        _buffer.AddRange(_captured.Keys);

        foreach (var rb in _buffer)
        {
            if (rb == null)
            {
                _captured.Remove(rb);
                continue;
            }

            CapturedInfo info = _captured[rb];

            Vector3 toCenter = transform.position - rb.position;

            Vector3 flatToCenter = new Vector3(toCenter.x, 0f, toCenter.z);
            float coreDistance = flatToCenter.magnitude;
            Vector3 pullDir = coreDistance > 0.01f ? flatToCenter / coreDistance : Vector3.zero;

            if (coreDistance > spinRadius)
            {
                // DI LUAR spinRadius: tarik lurus menuju pusat seperti biasa,
                // supaya object yang jauh mendekat dulu ke tornado.
                rb.AddForce(pullDir * pullForce, ForceMode.Acceleration);
            }
            else
            {
                // DI DALAM spinRadius: jangan tarik ke titik pusat, tapi arahkan ke
                // CINCIN orbit (orbitRadius). Kalau terlalu jauh dari cincin -> ditarik
                // masuk; kalau terlalu dekat pusat -> didorong keluar. Ini yang bikin
                // object muter-muter mengelilingi sumbu, bukan menumpuk di tengah.
                float radialError = coreDistance - orbitRadius; // + = di luar cincin, - = di dalam cincin
                // pullDir mengarah ke pusat, jadi:
                //   radialError > 0 (di luar cincin)  -> dorong ke arah pusat (+pullDir)
                //   radialError < 0 (di dalam cincin) -> dorong menjauh pusat (-pullDir)
                rb.AddForce(pullDir * radialError * orbitCorrectionForce, ForceMode.Acceleration);
            }

            // Kalau object sudah keluar radius, tambahkan koreksi ekstra (spring-like)
            // sebanding dengan seberapa jauh dia overshoot, supaya selalu tertarik balik
            // dan tidak pernah benar-benar kabur dari radius gizmo.
            float overshoot = coreDistance - pullRadius;
            if (overshoot > 0f)
            {
                rb.AddForce(pullDir * overshoot * overshootCorrectionForce, ForceMode.Acceleration);

                // Redam komponen kecepatan yang mengarah keluar (outward) supaya momentum
                // tidak terus mendorongnya makin jauh sebelum spring force sempat menarik balik.
                Vector3 flatVelocity = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
                float outwardSpeed = Vector3.Dot(flatVelocity, -pullDir);
                if (outwardSpeed > 0f)
                {
                    Vector3 outwardComponent = -pullDir * outwardSpeed;
                    rb.linearVelocity -= outwardComponent * (outwardVelocityDamping * Time.fixedDeltaTime * 10f);
                }
            }

            if (coreDistance <= spinRadius)
            {
                // Arah tangensial untuk memutar object mengelilingi sumbu tornado.
                // Kalau object hampir tepat di pusat (pullDir ~ nol), pakai arah
                // fallback dari kecepatan horizontalnya supaya spin tidak berhenti.
                Vector3 tangent = Vector3.Cross(Vector3.up, pullDir);
                if (tangent.sqrMagnitude < 0.001f)
                {
                    Vector3 flatVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
                    tangent = flatVel.sqrMagnitude > 0.001f
                        ? Vector3.Cross(Vector3.up, flatVel.normalized)
                        : Vector3.forward;
                }
                rb.AddForce(tangent.normalized * spinForce, ForceMode.Acceleration);
            }

            float height = rb.position.y - transform.position.y;
            if (height < maxLiftHeight)
            {
                rb.AddForce(Vector3.up * liftForce, ForceMode.Acceleration);
            }

            if (info.phase == Phase.Pulling)
            {
                if (height >= maxLiftHeight || coreDistance <= minCoreDistance)
                {
                    info.phase = Phase.Holding;
                    info.holdTimer = holdDuration;
                }
            }
            else
            {
                info.holdTimer -= Time.fixedDeltaTime;
                if (info.holdTimer <= 0f && !_flingQueue.Contains(rb))
                {
                    // Jangan langsung dihempaskan di sini. Object yang sudah siap
                    // dihempaskan dimasukkan ke antrian dulu, dan tetap dipull/spin
                    // (masih ada di _captured) sampai gilirannya diproses satu per satu
                    // oleh ProcessFlingQueue, supaya tidak semua object terhempas bersamaan.
                    _flingQueue.Add(rb);
                }
            }
        }
    }

    private void ProcessFlingQueue()
    {
        if (_flingQueue.Count == 0) return;

        _flingStaggerTimer -= Time.fixedDeltaTime;
        if (_flingStaggerTimer > 0f) return;

        // Ambil satu object paling depan antrian (yang paling lama menunggu) dan hempaskan.
        Rigidbody rb = _flingQueue[0];
        _flingQueue.RemoveAt(0);

        if (rb != null)
        {
            Fling(rb);
            _captured.Remove(rb);
            _cooldowns[rb] = recaptureCooldown;
        }

        _flingStaggerTimer = flingStaggerInterval;
    }

    private void TickCooldowns()
    {
        if (_cooldowns.Count == 0) return;

        _buffer.Clear();
        _buffer.AddRange(_cooldowns.Keys);

        foreach (var rb in _buffer)
        {
            if (rb == null)
            {
                _cooldowns.Remove(rb);
                continue;
            }

            float remaining = _cooldowns[rb] - Time.fixedDeltaTime;
            if (remaining <= 0f)
                _cooldowns.Remove(rb);
            else
                _cooldowns[rb] = remaining;
        }
    }

    private void Fling(Rigidbody rb)
    {
        Vector3 outward = rb.position - transform.position;
        outward.y = 0f;
        outward = outward.sqrMagnitude > 0.01f ? outward.normalized : Random.insideUnitSphere;

        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.AddForce(outward * flingForce + Vector3.up * flingUpwardForce, ForceMode.VelocityChange);
        rb.AddTorque(Random.insideUnitSphere * flingTorque, ForceMode.VelocityChange);
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;
        Vector3 gizmoCenter = transform.position + Vector3.up * gizmoYOffset;

        // Pull radius (area deteksi & tarikan menuju pusat)
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.35f);
        Gizmos.DrawWireSphere(gizmoCenter, pullRadius);

        // Spin radius (area di mana object ikut diputar mengelilingi tornado)
        Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.5f);
        Gizmos.DrawWireSphere(gizmoCenter, spinRadius);

        // Orbit radius (cincin target tempat object muter-muter)
        Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.7f);
        Gizmos.DrawWireSphere(gizmoCenter, orbitRadius);

        // Core / minimum distance
        Gizmos.color = new Color(1f, 0.2f, 0f, 0.6f);
        Gizmos.DrawWireSphere(gizmoCenter, minCoreDistance);
    }
}