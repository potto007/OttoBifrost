using System;
using System.Collections.Generic;
using OttoBifrost.Patches;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace OttoBifrost.Components;

/// Shows the far end of the portal on a disc in its opening.
///
/// StaticView takes a first picture once the objects close to the far portal exist, and a second
/// once the whole arrival area does, then renders nothing until the next approach. LiveView
/// renders only the window nearest the player, to bound the render cost. The others keep their
/// last frame.
public sealed class PortalWindow : MonoBehaviour
{
    private const float MaxViewDistance = 12f;
    private const int TextureSize = 512;
    // Fitted to the wood portal arch. The stone portal shows the same disc.
    private static readonly Vector3 DiscOffset = new(0f, 1.31f, 0.01f);
    private const float DiscDiameter = 2.85f;
    private const int DiscSegments = 48;
    // Close up, the view must follow head movement every frame. Further away it changes
    // little per frame.
    private const float EveryFrameDistance = 4f;
    private const float FastRateDistance = 8f;
    private const float FastRateInterval = 0.1f;
    private const float SlowRateInterval = 1f / 3f;
    // Detail beyond this is not visible in a small disc.
    private const float FarClip = 300f;
    private const float NearClip = 0.5f;
    private const float NearClipBlocked = 0.1f;
    private const float CameraRadius = 0.35f;
    private const float WaterClearance = 0.3f;
    // A static picture is taken from head height just behind the far portal, facing the way the
    // player faces on arrival, tilted down a little so the ground shows.
    private const float StaticEyeHeight = 1.8f;
    private const float StaticPullBack = 1.5f;
    private const float StaticPitch = 8f;
    private const float StaticCheckInterval = 0.2f;
    // Without the mod on the server, "built" means the arrival object count held still this long.
    private const float StaticSettleTime = 1f;
    // Lets terrain edits rebuild their heightmaps before the picture is taken.
    private const float StaticCaptureDelay = 0.2f;
    // The first picture waits only for objects within the radius the destination pass creates
    // first. Objects further than this behind the far portal are out of shot.
    private const float StaticBehindAllowance = 2f;
    // A static picture shimmers and ripples like the surface of the portal, fades out at the rim,
    // and lets the portal behind it show through.
    private const int RippleRings = 12;
    private const float StaticAlpha = 0.7f;
    private const float RimFadeWidth = 0.08f;
    private const float RippleAmplitude = 0.012f;
    private const float RippleFrequency = 40f;
    private const float RippleSpeed = 3f;
    private const float WaveAmplitude = 0.006f;
    private const float ShimmerFrequency = 9f;
    private const float ShimmerSpeed = 2.2f;
    // Vertex colours only darken, so the bright band sits on a dimmed base.
    private const float ShimmerBase = 0.82f;
    private static readonly Color ShimmerTint = new(0.92f, 0.97f, 1f);
    private static readonly Vector2 UvCentre = new(0.5f, 0.5f);

    private static readonly List<PortalWindow> Windows = new();
    private static readonly Quaternion HalfTurn = Quaternion.Euler(0f, 180f, 0f);
    private static PortalWindow? _live;
    private static int _liveFrame = -1;
    // One static picture per frame, so walking into a hub room does not render every portal at once.
    private static int _captureFrame = -1;
    private static readonly List<ZDO> NearObjects = new();
    private static readonly HashSet<ZoneSystem.SectorIndex> NearSectors = new();
    private static Mesh? _discMesh;
    // Every static window shares one ripple mesh, animated once per frame.
    private static Mesh? _rippleMesh;
    private static Vector2[] _rippleBase = Array.Empty<Vector2>();
    private static Vector2[] _rippleUv = Array.Empty<Vector2>();
    private static Color[] _rippleColors = Array.Empty<Color>();
    private static int _rippleFrame = -1;
    private static int _blockMask;

    private TeleportWorld _portal = null!;
    private Camera? _camera;
    private RenderTexture? _texture;
    private Material? _material;
    // Sprites/Default draws both faces, so one disc seen from behind shows the image
    // mirrored. Each face of the portal gets its own disc.
    private MeshRenderer? _frontDisc;
    private MeshRenderer? _backDisc;
    private bool _viewerInFront = true;
    private bool _discsRipple;
    private bool _hasFrame;
    private float _nextRenderTime;
    private bool _staticView;
    private StaticStage _captured;
    private ZDOID _frameFarEnd = ZDOID.None;
    private float _nextStaticCheck;
    private int _settleCount = -1;
    private float _settleChangedAt;
    private float _readySince = -1f;
    private float _approachedAt = -1f;

    private enum StaticStage
    {
        None,
        // The objects close to the far portal exist.
        Near,
        // Every object around the arrival point exists.
        Full
    }
    private TeleportWorld? _hiddenPortal;
    private Renderer[] _hiddenRenderers = Array.Empty<Renderer>();
    private bool[] _hiddenWasEnabled = Array.Empty<bool>();

    private void Awake()
    {
        _portal = GetComponent<TeleportWorld>();
    }

    private void OnEnable()
    {
        Windows.Add(this);
    }

    private void OnDisable()
    {
        Windows.Remove(this);
        if (_live == this)
            _live = null;
    }

    private void OnDestroy()
    {
        if (_camera != null)
            Object.Destroy(_camera.gameObject);
        if (_texture != null)
        {
            _texture.Release();
            Object.Destroy(_texture);
        }
        if (_material != null)
            Object.Destroy(_material);
    }

    private void LateUpdate()
    {
        Camera main = Camera.main;
        Player player = Player.m_localPlayer;
        if (main == null || player == null)
            return;

        Vector3 eye = player.GetEyePoint();
        bool staticView = OttoBifrostPlugin.PreviewMode.Value == OttoBifrostPlugin.PreviewModes.StaticView;
        if (staticView != _staticView)
        {
            // A frame from the other mode stays up until the new mode replaces it.
            _staticView = staticView;
            _captured = StaticStage.None;
        }
        if (!staticView)
            SelectLiveWindow(eye);

        bool linked = IsLinked();
        float distance = Vector3.Distance(eye, transform.position);
        if (!linked || distance > MaxViewDistance)
        {
            SetDiscVisible(false);
            if (!linked)
                _hasFrame = false;
            // Each approach takes a new picture, so it follows changes at the far end and the
            // time of day.
            ResetStaticCapture();
            return;
        }

        EnsureDiscs();
        if (_discsRipple != staticView)
        {
            _discsRipple = staticView;
            SetDiscMesh(staticView ? RippleMesh() : DiscMesh());
        }

        Transform model = ModelOf(_portal);
        bool frontSide = Vector3.Dot(model.forward, main.transform.position - model.position) >= 0f;
        // The stone portal model faces the other way from the wood portal model.
        ZDO? zdo = Portals.ZdoOf(_portal);
        _viewerInFront = zdo != null && Portals.IsStonePortal(zdo) ? frontSide : !frontSide;

        if (staticView)
        {
            UpdateStaticView(main, zdo);
            return;
        }

        if (_live != this)
        {
            // The last frame shows the arrival side, which is the same from either face.
            SetDiscVisible(_hasFrame);
            return;
        }

        TeleportWorld? farPortal = FindFarPortal(zdo);
        if (farPortal == null)
        {
            SetDiscVisible(_hasFrame);
            return;
        }

        SetDiscVisible(true);
        if (_hasFrame && Time.time < _nextRenderTime)
            return;

        _nextRenderTime = Time.time + (distance <= EveryFrameDistance ? 0f
            : distance <= FastRateDistance ? FastRateInterval
            : SlowRateInterval);
        Render(main, eye, model, farPortal);
    }

    private static void SelectLiveWindow(Vector3 eye)
    {
        if (_liveFrame == Time.frameCount)
            return;

        _liveFrame = Time.frameCount;
        _live = null;
        float best = MaxViewDistance;
        foreach (PortalWindow window in Windows)
        {
            float distance = Vector3.Distance(eye, window.transform.position);
            if (distance <= best && window.IsLinked())
            {
                best = distance;
                _live = window;
            }
        }
    }

    private bool IsLinked()
    {
        return _portal.HaveTarget() && _portal.TargetFound();
    }

    private static TeleportWorld? FindFarPortal(ZDO? zdo)
    {
        return InstanceOf(zdo != null ? Portals.FarEnd(zdo) : null);
    }

    private static TeleportWorld? InstanceOf(ZDO? farEnd)
    {
        ZNetView? view = farEnd != null && ZNetScene.instance != null ? ZNetScene.instance.FindInstance(farEnd) : null;
        return view != null ? view.GetComponent<TeleportWorld>() : null;
    }

    private void UpdateStaticView(Camera main, ZDO? zdo)
    {
        ZDO? farEnd = zdo != null ? Portals.FarEnd(zdo) : null;
        // A tag change that connects the portal somewhere else makes the picture wrong.
        if (farEnd != null && farEnd.m_uid != _frameFarEnd)
        {
            _hasFrame = false;
            _frameFarEnd = farEnd.m_uid;
            ResetStaticCapture();
        }

        SetDiscVisible(_hasFrame);
        if (_hasFrame)
            AnimateRipple();

        if (_approachedAt < 0f)
            _approachedAt = Time.time;

        // Mid-teleport, the far end of the arrival portal is the area the player is leaving,
        // and it is being unloaded.
        if (_captured == StaticStage.Full || farEnd == null || Time.time < _nextStaticCheck || Player.m_localPlayer.IsTeleporting())
            return;
        _nextStaticCheck = Time.time + StaticCheckInterval;

        TeleportWorld? farPortal = InstanceOf(farEnd);
        StaticStage ready = farPortal != null ? BuiltStage(farEnd, farPortal) : StaticStage.None;
        if (ready <= _captured)
        {
            _readySince = -1f;
            return;
        }

        if (_readySince < 0f)
            _readySince = Time.time;
        if (Time.time - _readySince < StaticCaptureDelay || _captureFrame == Time.frameCount)
            return;

        _captureFrame = Time.frameCount;
        CaptureStatic(main, farPortal!);
        _captured = ready;
        _readySince = -1f;
        SetDiscVisible(true);

        if (PerfStats.Enabled)
            OttoBifrostPlugin.Log.LogInfo(
                $"Static picture of {farEnd.m_uid} ({(ready == StaticStage.Full ? "whole arrival area" : "near objects")}) " +
                $"after {Time.time - _approachedAt:F2} s in range: server reports complete {DestinationSync.IsDestinationComplete(farEnd.m_uid)}, " +
                $"{_settleCount} objects known around the arrival point");
    }

    /// Both stages need the server to have nothing left to send, or the arrival object count to
    /// hold still. Full is the test a fast teleport makes before it lands the player.
    private StaticStage BuiltStage(ZDO farEnd, TeleportWorld farPortal)
    {
        if (ZoneSystem.instance == null || ZNetScene.instance == null || ZDOMan.instance == null)
            return StaticStage.None;

        Transform far = farPortal.transform;
        // Vanilla TeleportWorld.Teleport lands the player here.
        Vector3 arrival = far.position + far.forward * farPortal.m_exitDistance + Vector3.up;
        int known = DestinationSync.CountKnownObjects(arrival);
        if (known != _settleCount)
        {
            _settleCount = known;
            _settleChangedAt = Time.time;
        }

        bool settled = DestinationSync.IsDestinationComplete(farEnd.m_uid) || Time.time - _settleChangedAt >= StaticSettleTime;
        if (!settled)
            return StaticStage.None;
        if (ZNetScene.instance.IsAreaReady(arrival))
            return StaticStage.Full;
        return _captured < StaticStage.Near && AreNearObjectsBuilt(far) ? StaticStage.Near : StaticStage.None;
    }

    /// Every object within the destination pass's radius of the far portal exists, apart from
    /// those behind it. At a big base this passes well before the 3x3 zones IsAreaReady checks.
    private static bool AreNearObjectsBuilt(Transform far)
    {
        ZoneSystem zoneSystem = ZoneSystem.instance;
        ZNetScene scene = ZNetScene.instance;
        Vector3 origin = far.position;
        Vector3 forward = far.forward;
        float halfZone = zoneSystem.m_zoneSize * 0.5f;
        float radiusSqr = ZoneLoadPatches.PrimeRadius * ZoneLoadPatches.PrimeRadius;
        Vector2s centre = ZoneSystem.GetZone(origin);

        NearObjects.Clear();
        NearSectors.Clear();
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                Vector2s zone = new(centre.x + dx, centre.y + dy);
                Vector3 zonePos = ZoneSystem.GetZonePos(zone);
                float gapX = Mathf.Max(Mathf.Abs(origin.x - zonePos.x) - halfZone, 0f);
                float gapZ = Mathf.Max(Mathf.Abs(origin.z - zonePos.z) - halfZone, 0f);
                if (gapX * gapX + gapZ * gapZ > radiusSqr)
                    continue;
                // An unloaded zone has no ground to show yet.
                if (!zoneSystem.m_zones.ContainsKey(zone))
                    return false;
                ZDOMan.instance.FindObjects(zone, NearObjects, NearSectors);
            }
        }

        bool built = true;
        foreach (ZDO zdo in NearObjects)
        {
            if (!zdo.IsValid() || !scene.IsPrefabZDOValid(zdo) || scene.HaveInstance(zdo))
                continue;

            // A terrain edit shapes its whole zone, so its position does not matter.
            Vector3 offset = zdo.GetPosition() - origin;
            offset.y = 0f;
            if (zdo.Type != ZDO.ObjectType.Terrain &&
                (offset.sqrMagnitude > radiusSqr || Vector3.Dot(offset, forward) < -StaticBehindAllowance))
                continue;

            built = false;
            break;
        }

        NearObjects.Clear();
        return built;
    }

    private void ResetStaticCapture()
    {
        _captured = StaticStage.None;
        _settleCount = -1;
        _readySince = -1f;
        _approachedAt = -1f;
    }

    private void CaptureStatic(Camera main, TeleportWorld farPortal)
    {
        Transform far = farPortal.transform;
        Vector3 eye = far.position + Vector3.up * StaticEyeHeight;
        Vector3 position = eye - far.forward * StaticPullBack;
        Quaternion rotation = Quaternion.LookRotation(far.forward, Vector3.up) * Quaternion.Euler(StaticPitch, 0f, 0f);
        // The far portal may have gained its own discs since the renderers were last collected.
        _hiddenPortal = null;
        RenderFrom(main, eye, position, rotation, farPortal);
    }

    private void Render(Camera main, Vector3 eye, Transform here, TeleportWorld farPortal)
    {
        // Seen from the front, the viewer comes out behind the far portal and looks through its
        // front, so the mapping turns half a circle. Seen from the back, the viewer is already
        // on the arrival side.
        Matrix4x4 turn = Matrix4x4.Rotate(_viewerInFront ? HalfTurn : Quaternion.identity);
        Matrix4x4 map = ModelOf(farPortal).localToWorldMatrix * turn * here.worldToLocalMatrix;
        Vector3 farEye = map.MultiplyPoint3x4(eye);
        Vector3 position = map.MultiplyPoint3x4(main.transform.position);
        Vector3 forward = map.MultiplyVector(main.transform.forward);
        Vector3 up = map.MultiplyVector(main.transform.up);
        RenderFrom(main, farEye, position, Quaternion.LookRotation(forward, up), farPortal);
    }

    /// Pulls the camera in front of anything between farEye and position, then renders.
    private void RenderFrom(Camera main, Vector3 farEye, Vector3 position, Quaternion rotation, TeleportWorld farPortal)
    {
        Camera camera = EnsureCamera(main);

        float nearClip = NearClip;
        Vector3 offset = position - farEye;
        float length = offset.magnitude;
        if (length > 0.01f && Physics.SphereCast(farEye, CameraRadius, offset / length, out RaycastHit hit, length, _blockMask))
        {
            position = farEye + offset / length * Mathf.Max(hit.distance - NearClipBlocked, 0f);
            nearClip = NearClipBlocked;
        }

        float waterLine = Floating.GetLiquidLevel(position, 1f, LiquidType.All) + WaterClearance;
        if (position.y < waterLine)
            position.y = waterLine;

        camera.transform.SetPositionAndRotation(position, rotation);
        camera.fieldOfView = main.fieldOfView;
        camera.nearClipPlane = nearClip;
        camera.farClipPlane = Mathf.Min(main.farClipPlane, FarClip);

        HideFarPortal(farPortal);
        long start = PerfStats.Begin();
        // Camera.Render is synchronous, so the global shadow distance is back before the main
        // camera draws.
        float shadowDistance = QualitySettings.shadowDistance;
        QualitySettings.shadowDistance = 0f;
        try
        {
            camera.Render();
        }
        finally
        {
            QualitySettings.shadowDistance = shadowDistance;
            RestoreFarPortal();
        }

        PerfStats.AddPreviewRender(start);
        _hasFrame = true;
    }

    // The far portal's own frame and discs would block the view. Vanilla effects and other
    // windows switch their renderers off themselves, so each renderer returns to its own state.
    private void HideFarPortal(TeleportWorld farPortal)
    {
        if (_hiddenPortal != farPortal)
        {
            _hiddenPortal = farPortal;
            _hiddenRenderers = farPortal.GetComponentsInChildren<Renderer>();
            _hiddenWasEnabled = new bool[_hiddenRenderers.Length];
        }

        for (int i = 0; i < _hiddenRenderers.Length; i++)
        {
            Renderer renderer = _hiddenRenderers[i];
            _hiddenWasEnabled[i] = renderer != null && renderer.enabled;
            if (_hiddenWasEnabled[i])
                renderer!.enabled = false;
        }
    }

    private void RestoreFarPortal()
    {
        for (int i = 0; i < _hiddenRenderers.Length; i++)
        {
            if (_hiddenWasEnabled[i])
                _hiddenRenderers[i].enabled = true;
        }
    }

    private void SetDiscVisible(bool visible)
    {
        if (_frontDisc == null || _backDisc == null)
            return;
        _frontDisc.enabled = visible && _viewerInFront;
        _backDisc.enabled = visible && !_viewerInFront;
    }

    private void SetDiscMesh(Mesh mesh)
    {
        if (_frontDisc == null || _backDisc == null)
            return;
        _frontDisc.GetComponent<MeshFilter>().sharedMesh = mesh;
        _backDisc.GetComponent<MeshFilter>().sharedMesh = mesh;
    }

    // Created on first approach, so portals the player never walks up to cost nothing.
    private void EnsureDiscs()
    {
        if (_frontDisc != null)
            return;

        if (_blockMask == 0)
            _blockMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "piece_nonsolid");

        _texture = new RenderTexture(TextureSize, TextureSize, 24, RenderTextureFormat.ARGB32)
        {
            antiAliasing = 1,
            filterMode = FilterMode.Bilinear
        };
        _material = new Material(Shader.Find("Sprites/Default")) { mainTexture = _texture };

        Transform model = ModelOf(_portal);
        _frontDisc = AddDisc(model, DiscOffset, Quaternion.identity);
        _backDisc = AddDisc(model, new Vector3(DiscOffset.x, DiscOffset.y, -DiscOffset.z), HalfTurn);
    }

    private MeshRenderer AddDisc(Transform parent, Vector3 localPosition, Quaternion localRotation)
    {
        GameObject disc = new($"{OttoBifrostPlugin.ModName}Window");
        disc.transform.SetParent(parent, false);
        disc.transform.localPosition = localPosition;
        disc.transform.localRotation = localRotation;
        disc.transform.localScale = Vector3.one * DiscDiameter;
        disc.AddComponent<MeshFilter>().sharedMesh = DiscMesh();

        MeshRenderer renderer = disc.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = _material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.enabled = false;
        return renderer;
    }

    private Camera EnsureCamera(Camera main)
    {
        if (_camera != null)
            return _camera;

        _camera = new GameObject($"{OttoBifrostPlugin.ModName}Camera").AddComponent<Camera>();
        _camera.CopyFrom(main);
        _camera.enabled = false;
        _camera.targetTexture = _texture;
        _camera.useOcclusionCulling = false;
        _camera.allowMSAA = false;
        _camera.clearFlags = CameraClearFlags.Skybox;
        _camera.depthTextureMode = DepthTextureMode.Depth;
        return _camera;
    }

    private static Transform ModelOf(TeleportWorld portal)
    {
        return portal.m_model != null ? portal.m_model.transform : portal.transform;
    }

    private static Mesh DiscMesh()
    {
        if (_discMesh != null)
            return _discMesh;

        Vector3[] vertices = new Vector3[DiscSegments + 1];
        Vector2[] uv = new Vector2[DiscSegments + 1];
        int[] triangles = new int[DiscSegments * 3];
        Vector2 centre = new(0.5f, 0.5f);
        uv[0] = centre;
        for (int i = 0; i < DiscSegments; i++)
        {
            float angle = 2f * Mathf.PI * i / DiscSegments;
            Vector2 rim = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 0.5f;
            vertices[i + 1] = rim;
            uv[i + 1] = rim + centre;
            triangles[3 * i] = 0;
            triangles[3 * i + 1] = i + 1;
            triangles[3 * i + 2] = (i + 1) % DiscSegments + 1;
        }

        _discMesh = new Mesh { vertices = vertices, uv = uv, triangles = triangles };
        _discMesh.RecalculateBounds();
        return _discMesh;
    }

    /// The same disc as DiscMesh, split into rings, so the ripple has vertices to move.
    private static Mesh RippleMesh()
    {
        if (_rippleMesh != null)
            return _rippleMesh;

        int count = 1 + RippleRings * DiscSegments;
        Vector3[] vertices = new Vector3[count];
        _rippleBase = new Vector2[count];
        _rippleUv = new Vector2[count];
        _rippleColors = new Color[count];
        int[] triangles = new int[DiscSegments * 3 + (RippleRings - 1) * DiscSegments * 6];

        for (int ring = 1; ring <= RippleRings; ring++)
        {
            float radius = 0.5f * ring / RippleRings;
            for (int i = 0; i < DiscSegments; i++)
            {
                float angle = 2f * Mathf.PI * i / DiscSegments;
                Vector2 point = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                int index = RingVertex(ring, i);
                vertices[index] = point;
                _rippleBase[index] = point;
            }
        }

        int t = 0;
        for (int i = 0; i < DiscSegments; i++)
        {
            int next = (i + 1) % DiscSegments;
            triangles[t++] = 0;
            triangles[t++] = RingVertex(1, i);
            triangles[t++] = RingVertex(1, next);
            for (int ring = 1; ring < RippleRings; ring++)
            {
                triangles[t++] = RingVertex(ring, i);
                triangles[t++] = RingVertex(ring + 1, i);
                triangles[t++] = RingVertex(ring + 1, next);
                triangles[t++] = RingVertex(ring, i);
                triangles[t++] = RingVertex(ring + 1, next);
                triangles[t++] = RingVertex(ring, next);
            }
        }

        _rippleMesh = new Mesh { vertices = vertices, triangles = triangles };
        _rippleMesh.MarkDynamic();
        _rippleMesh.RecalculateBounds();
        _rippleFrame = -1;
        AnimateRipple();
        return _rippleMesh;
    }

    private static int RingVertex(int ring, int segment)
    {
        return 1 + (ring - 1) * DiscSegments + segment;
    }

    private static void AnimateRipple()
    {
        if (_rippleMesh == null || _rippleFrame == Time.frameCount)
            return;
        _rippleFrame = Time.frameCount;

        float time = Time.time;
        float pulse = 0.04f * Mathf.Sin(time * 1.1f);
        for (int i = 0; i < _rippleBase.Length; i++)
        {
            Vector2 point = _rippleBase[i];
            float r = point.magnitude;
            Vector2 direction = r > 0.0001f ? point / r : Vector2.zero;

            // Rings travel out from the centre, strongest halfway to the rim, over a slow wave.
            float ripple = Mathf.Sin(r * RippleFrequency - time * RippleSpeed) * RippleAmplitude * Mathf.Sin(r * 2f * Mathf.PI);
            Vector2 wave = new(Mathf.Sin(point.y * 9f + time * 1.7f), Mathf.Sin(point.x * 7f - time * 1.3f));
            _rippleUv[i] = point + UvCentre + direction * ripple + wave * WaveAmplitude;

            // A narrow bright band sweeps diagonally across the picture.
            float band = Mathf.Max(0f, Mathf.Sin((point.x + point.y) * ShimmerFrequency - time * ShimmerSpeed));
            band *= band;
            band *= band;
            float brightness = Mathf.Clamp01(ShimmerBase + (1f - ShimmerBase) * band + pulse);
            float rim = Mathf.SmoothStep(0f, 1f, (0.5f - r) / RimFadeWidth);
            float alpha = Mathf.Clamp01(StaticAlpha + 0.1f * band) * rim;
            _rippleColors[i] = new Color(ShimmerTint.r * brightness, ShimmerTint.g * brightness, ShimmerTint.b * brightness, alpha);
        }

        _rippleMesh.uv = _rippleUv;
        _rippleMesh.colors = _rippleColors;
    }
}
