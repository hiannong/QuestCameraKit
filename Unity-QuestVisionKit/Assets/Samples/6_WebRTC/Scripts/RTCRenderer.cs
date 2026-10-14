using System;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;

public class RTCRenderer : MonoBehaviour
{
    [Header("Camera & Raycast Settings")]
    [SerializeField] private float mergeThreshold = 0.2f;

    [Header("Marker Settings & Orientation")]
    [SerializeField] private GameObject markerPrefab;
    [SerializeField] private Texture2D customMarkerTexture;
    [SerializeField] private bool faceCamera = true; // 울퉁불퉁한 메쉬 각도 무시하고 사용자 정면 향함 (뒤틀림 100% 방지)
    [SerializeField] private bool useSurfaceNormal = false; // 3D 표면 법선 벡터 회전 적용 여부
    [SerializeField, Range(0.5f, 3.0f)] private float markerScaleMultiplier = 1.0f; // 박스 크기 확장 배율
    [SerializeField, Range(0.01f, 2.0f)] private float markerZThickness = 0.2f;     // 박스 Z축 입체 두께

    [Header("Label Filtering")]
    [SerializeField] private YOLOv9Labels[] labelFilters;
    [SerializeField, Range(0f, 1f)] private float minConfidence = 0.15f;

    [Header("640x360 WebRTC Stream & Aspect Ratio Calibration")]
    [SerializeField] private float modelInputWidth = 640f;   // 전송받는 YOLO 이미지 너비 (640)
    [SerializeField] private float modelInputHeight = 480f;  // 전송받는 YOLO 이미지 높이 (480)

    [Header("WebRTC 가상 카메라 연동")]
    [SerializeField] private Camera webrtcStreamingCamera;

    private Camera _mainCamera;
    private EnvironmentRaycastManager _envRaycastManager;
    private readonly Dictionary<string, MarkerController> _activeMarkers = new();
    private float _lastBoxRenderTime = -1f;
    private int _boxRenderCount = 0;

    // 실제 기기 공장 출하 캘리브레이션(Intrinsics & Extrinsics) 캐시 변수
    // 이미지 로그에서 추출한 실제 물리 측정값으로 기본값 설정
    private Vector2 _cachedFocalLength = new Vector2(867.191f, 867.191f);
    private Vector2 _cachedPrincipalPoint = new Vector2(637.762f, 639.611f);
    private Vector2Int _cachedResolution = new Vector2Int(1280, 960);

    // X: -3.21cm -> -0.0321m / Y: -1.84cm -> -0.0184m / Z: 6.24cm -> 0.0624m
    private Vector3 _cachedLensLocalPos = new Vector3(-0.0321f, -0.0184f, 0.0624f);

    // Pitch: 11.35° / Yaw: 0.83° / Roll: 0.78°
    private Quaternion _cachedLensLocalRot = Quaternion.Euler(11.35f, 0.83f, 0.78f);
    private bool _hasRealCameraIntrinsics = false;

    private System.Collections.IEnumerator Start()
    {
        // 앱 구동 시 기기 공장 직출 파스스루 캘리브레이션 수치를 1회 자동 캐싱 (스트림 점유 없음)
        var pca = GetComponent<PassthroughCameraAccess>() ?? FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
        if (pca != null)
        {
            float timer = 0f;
            while (timer < 2.0f && (pca.Intrinsics.FocalLength.x < 0.001f))
            {
                timer += 0.1f;
                yield return new WaitForSeconds(0.1f);
            }

            if (pca.Intrinsics.FocalLength.x > 0.001f)
            {
                _cachedFocalLength = pca.Intrinsics.FocalLength;
                _cachedPrincipalPoint = pca.Intrinsics.PrincipalPoint;
                _cachedResolution = pca.Intrinsics.SensorResolution;
                _cachedLensLocalPos = pca.transform.localPosition;
                _cachedLensLocalRot = pca.transform.localRotation;
                _hasRealCameraIntrinsics = true;

                Debug.Log($"[RTCRenderer] 실제 Quest 3 카메라 캘리브레이션 수치 캐싱 성공! FocalLength: {_cachedFocalLength}, PrincipalPoint: {_cachedPrincipalPoint}");
            }
        }
    }

    /// <summary>
    /// 실제 기기에서 캐싱된 공장 직출 Intrinsics 수치를 우선 사용하여 100% 피트되는 3D Ray를 생성합니다.
    /// </summary>
    private Ray GetRay(float u, float v)
    {
        Camera headCam = webrtcStreamingCamera != null ? webrtcStreamingCamera : Camera.main;
        Transform headTransform = headCam != null ? headCam.transform : transform;

        // 실제 캐싱된 물리 렌즈 포즈(LocalPos/Rot) 사용
        Vector3 lensWorldPos = headTransform.TransformPoint(_cachedLensLocalPos);
        Quaternion lensWorldRot = headTransform.rotation * _cachedLensLocalRot;

        // 1. 실제 기기 캘리브레이션 정보가 캐싱된 경우 100% 픽셀 퍼펙트 역투영
        if (_hasRealCameraIntrinsics && _cachedResolution.x > 0 && _cachedFocalLength.x > 0.001f)
        {
            float pixelX = u * _cachedResolution.x;
            float pixelY = v * _cachedResolution.y;

            float dirX = (pixelX - _cachedPrincipalPoint.x) / _cachedFocalLength.x;
            float dirY = (_cachedPrincipalPoint.y - pixelY) / _cachedFocalLength.y;
            Vector3 localDir = new Vector3(dirX, dirY, 1.0f).normalized;

            Vector3 worldDir = (lensWorldRot * localDir).normalized;
            return new Ray(lensWorldPos, worldDir);
        }

        // 2. Fallback: Quest 3 순정 파스스루 센서 원본 비율(4:3) 기준 핀홀 역투영 (X축 늘어남 100% 방지)
        float fovYDeg = 70f;
        float aspect = (_hasRealCameraIntrinsics && _cachedResolution.y > 0) 
            ? ((float)_cachedResolution.x / _cachedResolution.y) 
            : (4f / 3f); // Quest 3 파스스루 광학 센서 원본 비율은 4:3 (1280x960)입니다!

        float fovYRad = fovYDeg * Mathf.Deg2Rad;
        float tanHalfFovY = Mathf.Tan(fovYRad * 0.5f);
        float tanHalfFovX = tanHalfFovY * aspect;

        float dX = (u - 0.5f) * 2f * tanHalfFovX;
        float dY = (0.5f - v) * 2f * tanHalfFovY;

        Vector3 fallbackLocalDir = new Vector3(dX, dY, 1.0f).normalized;
        Vector3 fallbackWorldDir = (lensWorldRot * fallbackLocalDir).normalized;
        return new Ray(lensWorldPos, fallbackWorldDir);
    }

    private void Awake()
    {
        _envRaycastManager = GetComponent<EnvironmentRaycastManager>() ?? FindAnyObjectByType<EnvironmentRaycastManager>(FindObjectsInactive.Include);
        _mainCamera = Camera.main;

        if (webrtcStreamingCamera == null)
        {
            webrtcStreamingCamera = Camera.main;
            Debug.LogWarning("[plz] WebRTC camera is not assigned. replace to MainCamera.");
        }
    }

    public void RenderDetections(Unity.InferenceEngine.Tensor<float> coords, Unity.InferenceEngine.Tensor<int> labelIDs, Unity.InferenceEngine.Tensor<float> confidences = null)
    {
        if (coords == null || labelIDs == null || _envRaycastManager == null) return;

        var numDetections = coords.shape[0];
        if (numDetections == 0) return; // 검출된 박스가 없으면 시간 측정 제외

        //  [시간 측정 로직] 이전 박스가 그려진 시점부터 현재 박스가 그려지기까지 걸린 시간(ms) 계산
        float currentTime = Time.realtimeSinceStartup;
        if (_lastBoxRenderTime > 0f)
        {
            float intervalMs = (currentTime - _lastBoxRenderTime) * 1000f; // 초 -> ms 변환
            float currentFps = 1000f / intervalMs;
            _boxRenderCount++;

            Debug.Log($" [plz #{_boxRenderCount}] {intervalMs:F2} ms (fps: {currentFps:F1} FPS)");
        }
        _lastBoxRenderTime = currentTime;
        ClearPreviousMarkers();

        // 640x360 (16:9) 전송 영상과 유니티 가상 카메라의 순수 1:1 Viewport 변환 (인공적 오프셋 제거)
        for (int i = 0; i < numDetections; i++)
        {
            var detectedCenterX = coords[i, 0];
            var detectedCenterY = coords[i, 1];
            var detectedWidth = coords[i, 2];
            var detectedHeight = coords[i, 3];

            // 1. 640x360 전송 영상 기준 정규화 비율 (0.0 ~ 1.0)
            var perX = detectedCenterX / modelInputWidth;
            var perY = detectedCenterY / modelInputHeight;

            // 2. 순수 1:1 좌표계 (GetRay 내부에서 0.5f - v 연산으로 Y축 수직 각도 변환)
            float unityViewportX = perX;
            float unityViewportY = perY;

            Ray centerRay = GetRay(unityViewportX, unityViewportY);

            if (!_envRaycastManager.Raycast(centerRay, out var centerHit))
            {
                continue; // 메쉬 레이캐스트 실패 시 스킵
            }

            var markerWorldPos = centerHit.point;

            // Bounding Box 네 모퉁이 3D 바운딩 순수 좌표 변환
            var u1 = (detectedCenterX - detectedWidth * 0.5f) / modelInputWidth;
            var u2 = (detectedCenterX + detectedWidth * 0.5f) / modelInputWidth;

            var v1 = (detectedCenterY - detectedHeight * 0.5f) / modelInputHeight;
            var v2 = (detectedCenterY + detectedHeight * 0.5f) / modelInputHeight;

            Ray tlRay = GetRay(u1, v1);
            Ray trRay = GetRay(u2, v1);
            Ray blRay = GetRay(u1, v2);

            var depth = Vector3.Distance(webrtcStreamingCamera.transform.position, markerWorldPos);
            var worldTL = tlRay.GetPoint(depth);
            var worldTR = trRay.GetPoint(depth);
            var worldBL = blRay.GetPoint(depth);

            var markerWidth = Vector3.Distance(worldTR, worldTL);
            var markerHeight = Vector3.Distance(worldBL, worldTL);
            var markerScale = new Vector3(markerWidth, markerHeight, 1f);

            var detectedLabel = (YOLOv9Labels)labelIDs[i];
            if (labelFilters is { Length: > 0 } && !Array.Exists(labelFilters, label => label == detectedLabel))
            {
                continue;
            }

            Quaternion markerRotation;
            if (faceCamera && webrtcStreamingCamera != null)
            {
                Vector3 dirToCam = (webrtcStreamingCamera.transform.position - markerWorldPos).normalized;
                markerRotation = Quaternion.LookRotation(-dirToCam, Vector3.up);
            }
            else if (useSurfaceNormal)
            {
                var surfaceNormal = SampleSurfaceNormal(markerWorldPos, centerHit.normal);
                markerRotation = Quaternion.LookRotation(-surfaceNormal, Vector3.up);
            }
            else
            {
                markerRotation = Quaternion.identity;
            }

            var dictionaryKey = detectedLabel.ToString();
            var confidence = GetConfidence(coords, confidences, i);
            if (confidence >= 0f && confidence < minConfidence)
            {
                continue;
            }
            var labelWithConfidence = confidence >= 0f ? $"{dictionaryKey} ({confidence * 100f:F0}%)" : dictionaryKey;

            var lookupKey = dictionaryKey;
            if (_activeMarkers.TryGetValue(lookupKey, out MarkerController existingMarker))
            {
                if (Vector3.Distance(existingMarker.transform.position, markerWorldPos) < mergeThreshold)
                {
                    existingMarker.UpdateMarker(markerWorldPos, markerRotation, markerScale, labelWithConfidence);
                    continue;
                }
                lookupKey = $"{dictionaryKey}_{i}";
            }

            var markerGo = Instantiate(markerPrefab);
            if (customMarkerTexture != null)
            {
                var meshRenderer = markerGo.GetComponentInChildren<MeshRenderer>();
                if (meshRenderer != null && meshRenderer.material != null)
                {
                    meshRenderer.material.mainTexture = customMarkerTexture;
                }
            }

            var marker = markerGo.GetComponent<MarkerController>();
            if (!marker) continue;

            marker.UpdateMarker(markerWorldPos, markerRotation, markerScale, labelWithConfidence);
            _activeMarkers[lookupKey] = marker;
        }
    }

    //  [AI 컴퓨터 비전 수식] VR 카메라 고유의 비대칭 렌즈 행렬을 완벽하게 디코드하는 역투영 함수
    private Ray GetVRUnprojectedRay(Camera cam, float u, float v)
    {
        // 1. 뷰포트 비율(0~1)을 NDC 클립 공간(-1~1) 좌표로 치환
        float ndcX = u * 2f - 1f;
        float ndcY = v * 2f - 1f;

        // 2. 카메라의 순정 투영 행렬을 반전(Inverse)시킵니다.
        Matrix4x4 invProj = cam.projectionMatrix.inverse;
        Vector4 clipSpacePos = new Vector4(ndcX, ndcY, -1f, 1f); // Near Plane 투사

        // 3. 행렬 곱을 연산하여 카메라 기준의 로컬 3D 공간 좌표로 복원합니다.
        Vector4 viewSpacePos = invProj * clipSpacePos;
        Vector3 localDir = new Vector3(viewSpacePos.x / viewSpacePos.w, viewSpacePos.y / viewSpacePos.w, viewSpacePos.z / viewSpacePos.w);

        // 유니티 카메라 스페이스 가이드라인 정렬 (-Z축 전진 제어)
        localDir.z = -Mathf.Abs(localDir.z);
        Vector3 correctLocalForward = new Vector3(localDir.x, localDir.y, -localDir.z).normalized;

        // 4. 로컬 방향 벡터를 가상 카메라의 실제 세계 회전축값과 직결합니다.
        Vector3 worldDir = cam.transform.TransformDirection(correctLocalForward).normalized;

        return new Ray(cam.transform.position, worldDir);
    }

    private void ClearPreviousMarkers()
    {
        foreach (var marker in _activeMarkers.Values)
        {
            if (marker && marker.gameObject) Destroy(marker.gameObject);
        }
        _activeMarkers.Clear();
    }

    private static float GetConfidence(Unity.InferenceEngine.Tensor<float> coords, Unity.InferenceEngine.Tensor<float> confidenceTensor, int index)
    {
        var sampled = SampleConfidence(confidenceTensor, index);
        if (sampled >= 0f) return Mathf.Clamp01(sampled);
        if (coords == null || coords.shape.rank < 2) return -1f;
        var channels = coords.shape[coords.shape.rank - 1];
        if (channels <= 4) return -1f;
        try { return Mathf.Clamp01(coords[index, 4]); } catch { return -1f; }
    }

    private static float SampleConfidence(Unity.InferenceEngine.Tensor<float> tensor, int index)
    {
        if (tensor == null) return -1f;
        var length = tensor.shape.length;
        if (index < 0 || index >= length) return -1f;
        try { return tensor[index]; } catch { return -1f; }
    }

    private Vector3 SampleSurfaceNormal(Vector3 position, Vector3 fallbackNormal)
    {
        if (_envRaycastManager == null) return fallbackNormal;
        var origin = _mainCamera ? _mainCamera.transform.position : position - fallbackNormal * 0.1f;
        var direction = position - origin;
        if (direction.sqrMagnitude > 0.0001f)
        {
            if (_envRaycastManager.Raycast(new Ray(origin, direction.normalized), out var hit, direction.magnitude + 0.05f)) return hit.normal;
        }
        return fallbackNormal;
    }
}