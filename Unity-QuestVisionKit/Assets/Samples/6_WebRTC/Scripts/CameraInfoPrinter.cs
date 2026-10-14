using System.Collections;
using UnityEngine;
using Meta.XR;
using TMPro;

public class CameraInfoPrinter : MonoBehaviour
{
    [Header("VR UI 출력 (선택 사항)")]
    [SerializeField] private TextMeshProUGUI debugText;

    private PassthroughCameraAccess _pca;

    private IEnumerator Start()
    {
        Debug.Log("[CameraInfoPrinter]  파스스루 카메라 캘리브레이션 진단 시작...");
        if (debugText) debugText.text = "카메라 캘리브레이션 데이터 대기 중...";

        // 1. PassthroughCameraAccess 컴포넌트 탐색
        _pca = GetComponent<PassthroughCameraAccess>() ?? FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
        if (_pca == null)
        {
            Debug.LogError("[CameraInfoPrinter]  씬 내에서 PassthroughCameraAccess 컴포넌트를 찾지 못했습니다.");
            if (debugText) debugText.text = "Error: PassthroughCameraAccess를 찾을 수 없습니다.";
            yield break;
        }

        // 2. 파스스루 카메라 재생(IsPlaying = true) 상태가 될 때까지 대기 (권한 승인 및 HW 구동)
        Debug.Log("[CameraInfoPrinter]  카메라 파스스루 센서 구동(IsPlaying) 대기 중...");
        yield return new WaitUntil(() => _pca != null && _pca.IsPlaying);

        // 3. Intrinsics 데이터 유효성 검사 대기
        float timeout = 5.0f;
        float elapsed = 0f;
        while (elapsed < timeout && _pca.Intrinsics.FocalLength.x < 0.001f)
        {
            elapsed += 0.2f;
            yield return new WaitForSeconds(0.2f);
        }

        PrintCameraDetails();
    }

    private void Update()
    {
        // 키보드 스페이스바 누르면 즉시 재출력 (에디터/PC용)
        if (Input.GetKeyDown(KeyCode.Space))
        {
            PrintCameraDetails();
        }
    }

    public void PrintCameraDetails()
    {
        if (_pca == null)
        {
            _pca = GetComponent<PassthroughCameraAccess>() ?? FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
        }

        if (_pca == null)
        {
            Debug.LogError("[CameraInfoPrinter] no PassthroughCameraAccess ");
            return;
        }

        var intrinsics = _pca.Intrinsics;
        var res = _pca.CurrentResolution != Vector2Int.zero ? _pca.CurrentResolution : intrinsics.SensorResolution;
        
        // GetCameraPose() 전용 API를 사용하여 물리 렌즈의 실물 포즈 가져오기
        Pose lensPose = _pca.GetCameraPose();
        Camera mainCam = Camera.main;
        Transform eyeTransform = mainCam != null ? mainCam.transform : transform;

        // 눈(CenterEyeAnchor) 대비 물리 렌즈의 상대 위치 및 회전각 변환
        Vector3 localPos = eyeTransform.InverseTransformPoint(lensPose.position);
        Quaternion localRotQuat = Quaternion.Inverse(eyeTransform.rotation) * lensPose.rotation;
        Vector3 localRot = localRotQuat.eulerAngles;

        string infoLog = $"===========================================================\n" +
                         $"-----------------------------------------------------------\n" +
                         $"[CameraInfoPrinter](IsPlaying): {_pca.IsPlaying}\n" +
                         $"[CameraInfoPrinter]1. (Sensor Resolution): {res.x} x {res.y} px\n" +
                         $"[CameraInfoPrinter]2. (Focal Length): fx = {intrinsics.FocalLength.x:F3}, fy = {intrinsics.FocalLength.y:F3}\n" +
                         $"[CameraInfoPrinter]3. (Principal Point): cx = {intrinsics.PrincipalPoint.x:F3}, cy = {intrinsics.PrincipalPoint.y:F3}\n" +
                         $"[CameraInfoPrinter]4. (Real Physical Lens Position relative to Eyes):\n" +
                         $"[CameraInfoPrinter]   - X (): {localPos.x * 100f:F2} cm\n" +
                         $"[CameraInfoPrinter]   - Y (): {localPos.y * 100f:F2} cm\n" +
                         $"[CameraInfoPrinter]   - Z (): {localPos.z * 100f:F2} cm\n" +
                         $"[CameraInfoPrinter]5. (Real Physical Lens Rotation Euler):\n" +
                         $"[CameraInfoPrinter]   - Pitch (X): {localRot.x:F2} deg\n" +
                         $"[CameraInfoPrinter]   - Yaw   (Y): {localRot.y:F2} deg\n" +
                         $"[CameraInfoPrinter]   - Roll  (Z): {localRot.z:F2} deg\n" +
                         $"===========================================================";

        // 1. 유니티 콘솔 및 Logcat 필수 출력 (Debug.LogError로 눈에 띄게 강조)
        Debug.Log(infoLog);

        // 2. VR 화면 UI 텍스트 출력
        if (debugText != null)
        {
            debugText.text = infoLog;
        }
    }
}
