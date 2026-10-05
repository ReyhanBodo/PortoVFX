using System.Collections;
using System.IO;
using UnityEngine;

/// <summary>Debug helper: fires the skill and saves a Game view screenshot every interval.</summary>
public class OSCaptureDebug : MonoBehaviour
{
    public string folder = "Temp/OSCapture";
    public float interval = 1f;
    public float duration = 26f;

    IEnumerator Start()
    {
        Directory.CreateDirectory(folder);
        FindFirstObjectByType<OrbitalStrikeSkill>().Fire();
        var t0 = Time.time;
        var i = 0;
        while (Time.time - t0 < duration)
        {
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, $"cap_{i:00}_{Time.time - t0:00.0}s.png"));
            i++;
            yield return new WaitForSeconds(interval);
        }
        Destroy(this);
    }
}
