using System;
using System.Collections.Generic;
using OttoBifrost.Patches;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace OttoBifrost.Components;

/// Shows the far end of the portal on a disc in its opening. Only the window nearest the
/// player renders live, to bound the render cost. The others keep their last frame.
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

    private static readonly List<PortalWindow> Windows = new();
    private static readonly Quaternion HalfTurn = Quaternion.Euler(0f, 180f, 0f);
    private static PortalWindow? _live;
    private static int _liveFrame = -1;
    private static Mesh? _discMesh;
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
    private bool _hasFrame;
    private float _nextRenderTime;
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
        SelectLiveWindow(eye);

        bool linked = IsLinked();
        float distance = Vector3.Distance(eye, transform.position);
        if (!linked || distance > MaxViewDistance)
        {
            SetDiscVisible(false);
            if (!linked)
                _hasFrame = false;
            return;
        }

        EnsureDiscs();
        Transform model = ModelOf(_portal);
        bool frontSide = Vector3.Dot(model.forward, main.transform.position - model.position) >= 0f;
        // The stone portal model faces the other way from the wood portal model.
        ZDO? zdo = Portals.ZdoOf(_portal);
        _viewerInFront = zdo != null && Portals.IsStonePortal(zdo) ? frontSide : !frontSide;

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
        ZDO? farEnd = zdo != null ? Portals.FarEnd(zdo) : null;
        ZNetView? view = farEnd != null && ZNetScene.instance != null ? ZNetScene.instance.FindInstance(farEnd) : null;
        return view != null ? view.GetComponent<TeleportWorld>() : null;
    }

    private void Render(Camera main, Vector3 eye, Transform here, TeleportWorld farPortal)
    {
        Camera camera = EnsureCamera(main);

        // Seen from the front, the viewer comes out behind the far portal and looks through its
        // front, so the mapping turns half a circle. Seen from the back, the viewer is already
        // on the arrival side.
        Matrix4x4 turn = Matrix4x4.Rotate(_viewerInFront ? HalfTurn : Quaternion.identity);
        Matrix4x4 map = ModelOf(farPortal).localToWorldMatrix * turn * here.worldToLocalMatrix;
        Vector3 farEye = map.MultiplyPoint3x4(eye);
        Vector3 position = map.MultiplyPoint3x4(main.transform.position);
        Vector3 forward = map.MultiplyVector(main.transform.forward);
        Vector3 up = map.MultiplyVector(main.transform.up);

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

        camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward, up));
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
}
