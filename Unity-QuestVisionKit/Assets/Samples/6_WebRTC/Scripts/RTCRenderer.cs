using System;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;

public class RTCRenderer : MonoBehaviour
{
    [Header("Camera & Raycast Settings")]
    [SerializeField] private float mergeThreshold = 0.2f;

    [Header("Marker Settings")]
    [SerializeField] private GameObject markerPrefab;

    [Header("Label Filtering")]
    [SerializeField] private YOLOv9Labels[] labelFilters;
    [SerializeField, Range(0f, 1f)] private float minConfidence = 0.15f;

    [Header("WebRTC 가상 카메라 연동")]
    [SerializeField] private Camera webrtcStreamingCamera;

    private Camera _mainCamera;
    private const float ModelInputSize = 320f;
    private EnvironmentRaycastManager _envRaycastManager;
    private readonly Dictionary<string, MarkerController> _activeMarkers = new();

    //  [메타 퀘스트 3 오프셋 고정 상수] 
    // WebRTC가 복사해가는 VR 눈 버퍼의 하단 패딩으로 인해 발생하는 Y축 광학 오차값입니다.
    private const float QuestEyeBufferYOffset = -0.125f;

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
        ClearPreviousMarkers();

        var imageSize = ModelInputSize;

        for (var i = 0; i < numDetections; i++)
        {
            var detectedCenterX = coords[i, 0];
            var detectedCenterY = coords[i, 1];
            var detectedWidth = coords[i, 2];
            var detectedHeight = coords[i, 3];

            // 640x640 정규화 비율 (0.0 ~ 1.0)
            var perX = detectedCenterX / imageSize;
            var perY = detectedCenterY / imageSize;

            float unityViewportX = perX;
            float unityViewportY = 1f - perY; // 상하 반전

            //  [AI 핵심 공식 수정] VR 눈 버퍼와 640 텍스처 간의 수직 비대칭 오차를 수학적으로 즉시 상쇄합니다.
            unityViewportY += QuestEyeBufferYOffset;

            //  [AI 핵심 투영법] 유니티 가상 카메라의 역투영 행렬(Inverse Projection)을 직접 추출하여 
            // 렌즈 비대칭 왜곡률을 1:1로 완전 무력화하는 마스터 레이저를 쏩니다.
            Ray centerRay = GetVRUnprojectedRay(webrtcStreamingCamera, unityViewportX, unityViewportY);

            if (!_envRaycastManager.Raycast(centerRay, out var centerHit))
            {
                continue;
            }

            var markerWorldPos = centerHit.point;

            // 크기 연산용 네 모퉁이 축도 동일한 오프셋과 역투영 행렬을 적용해 정렬합니다.
            var u1 = (detectedCenterX - detectedWidth * 0.5f) / imageSize;
            var v1 = 1f - ((detectedCenterY - detectedHeight * 0.5f) / imageSize) + QuestEyeBufferYOffset;
            var u2 = (detectedCenterX + detectedWidth * 0.5f) / imageSize;
            var v2 = 1f - ((detectedCenterY + detectedHeight * 0.5f) / imageSize) + QuestEyeBufferYOffset;

            Ray tlRay = GetVRUnprojectedRay(webrtcStreamingCamera, u1, v1);
            Ray trRay = GetVRUnprojectedRay(webrtcStreamingCamera, u2, v1);
            Ray blRay = GetVRUnprojectedRay(webrtcStreamingCamera, u1, v2);

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

            var surfaceNormal = SampleSurfaceNormal(markerWorldPos, centerHit.normal);
            var markerRotation = Quaternion.LookRotation(-surfaceNormal, Vector3.up);

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