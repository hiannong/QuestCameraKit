using UnityEngine;
using UnityEngine.UI;
using SimpleWebRTC;
using System.Collections.Generic;
using System;
using TMPro;

public class ObjectDetectionHandler : MonoBehaviour
{

    [Serializable]
    public class DetectedObject
    {
        public string label;
        public float confidence;
        public float[] box;
    }

    [Serializable]
    public class DetectionResult
    {
        public string type;
        public int frame_id;
        public List<DetectedObject> objects;

        // [추가됨] 이미지 처리를 위해 같은 JSON 포맷 안에 base64 필드 추가
        public string base64Data;
    }


    public WebRTCConnection webrtcConnection;
    public RectTransform overlayPanel; // Canvas 전체가 아니라 박스를 그릴 Panel 권장
    public GameObject boundingBoxPrefab;
    public RawImage displayRawImage;

    private Vector2 pythonResolution = new Vector2(640, 360);
    // [핵심] 스레드 간 데이터 전달을 위한 변수
    private string pendingJsonMessage = null;
    private readonly object lockObject = new object();

    private Texture2D receivedTexture;

    void OnEnable()
    {
        webrtcConnection.DataChannelMessageReceived.AddListener(OnMessageReceived);
    }

    void OnDisable()
    {
        webrtcConnection.DataChannelMessageReceived.RemoveListener(OnMessageReceived);
        if (receivedTexture != null) Destroy(receivedTexture); // 메모리 정리
    }

    // 1. 이 함수는 백그라운드 스레드에서 호출됩니다.
    public void OnMessageReceived(string jsonMessage)
    {
        // 락을 걸고 메시지만 저장한 뒤 빠르게 빠져나갑니다.
        lock (lockObject)
        {
            pendingJsonMessage = jsonMessage;
        }
    }
    //void Start()
    //{
        
    //}

    // 2. Update는 매 프레임 메인 스레드에서 호출됩니다.
    void Update()
    {
        string messageToProcess = null;

        lock (lockObject)
        {
            if (pendingJsonMessage != null)
            {
                Debug.Log($"[plz]: {pendingJsonMessage}");
                messageToProcess = pendingJsonMessage;
                pendingJsonMessage = null; // 처리할 거니까 비워줌
            }
        }

        if (messageToProcess != null)
        {
            ProcessData(messageToProcess);
        }
    }

    private void ProcessData(string json)
    {
        try
        {
            DetectionResult result = JsonUtility.FromJson<DetectionResult>(json);
            if (result != null)
            {
                if (result.type == "detection")
                {
                    DrawBoxes(result.objects);
                }
                // [추가됨] 이미지가 왔을 때 처리
                else if (result.type == "image")
                {
                    Debug.Log($"[plz]: here is ok");
                    ShowImage(result.base64Data);
                    Debug.Log($"[plz]: show image works");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[plz]:JSON passing error: " + e.Message);
        }
    }
    private void ShowImage(string base64String)
    {
        if (string.IsNullOrEmpty(base64String))
        {
            Debug.Log($"[plz]: no data");
            return;
        }

        try
        {
            if (base64String.Contains(","))
            {
                base64String = base64String.Split(',')[1];
            }

            byte[] imageBytes = Convert.FromBase64String(base64String);

            if (receivedTexture == null)
            {
                receivedTexture = new Texture2D(2, 2);
            }

            if (receivedTexture.LoadImage(imageBytes))
            {
                if (displayRawImage != null)
                {
                    displayRawImage.texture = receivedTexture;
                    Debug.Log($"[plz]: display raw image has info");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[plz]:we have moonjae: " + e.Message);
        }
    }
    private void DrawBoxes(List<DetectedObject> objects)
    {
        // 이제 메인 스레드이므로 Destroy/Instantiate가 안전합니다.
        foreach (Transform child in overlayPanel)
        {
            Destroy(child.gameObject);
        }

        ////// [강제 테스트 구간 시작] -----------------------------------------
        ////// 데이터와 상관없이 화면 중앙에 붉은색(혹은 기본색) 박스를 하나 만듭니다.
        //GameObject testBox = Instantiate(boundingBoxPrefab, overlayPanel);
        //RectTransform testRt = testBox.GetComponent<RectTransform>();

        //if (testRt != null)
        //{
        //    // 캔버스 크기 로그 찍기 (0으로 나오는지 확인용)
        //    Debug.Log($"[plz] Canvas Size: {overlayPanel.rect.width} x {overlayPanel.rect.height}");

        //    // 화면 정중앙에 300x300 크기로 고정
        //    testRt.anchorMin = new Vector2(0.5f, 0.5f);
        //    testRt.anchorMax = new Vector2(0.5f, 0.5f);
        //    testRt.pivot = new Vector2(0.5f, 0.5f);
        //    testRt.anchoredPosition = Vector2.zero;
        //    testRt.sizeDelta = new Vector2(300, 300);

        //    TMP_Text testLabel = testBox.GetComponentInChildren<TMP_Text>();
        //    if (testLabel != null) testLabel.text = "TEST BOX (CENTER)";
        //}
        //// [강제 테스트 구간 끝] -------------------------------------------
        Debug.Log("[plz]: it is dol a ga");
        if (objects == null) return; // 데이터가 비어있으면 중단
        var obj = objects[0];
        GameObject boxObj = Instantiate(boundingBoxPrefab, overlayPanel);
        RectTransform rt = boxObj.GetComponent<RectTransform>();

        float canvasWidth = overlayPanel.rect.width;
        float canvasHeight = overlayPanel.rect.height;

        // [핵심 수학 공식]: 세로 높이를 기준으로 '가상의 16:9 도화지 가로 크기'를 강제 계산합니다.
        float virtualWidth = canvasHeight * (640f / 360f); // 16:9 비율 강제 적용
        float virtualHeight = canvasHeight;

        // 정사각형 VR 화면과 16:9 파이썬 영상의 정중앙을 맞추기 위한 좌우 여백(오프셋) 계산
        float xOffset = (canvasWidth - virtualWidth) / 2f;

        // 파이썬 640x360 좌표를 가상 16:9 도화지 비율에 맞춰 오차 없이 매핑
        float x = (obj.box[0] / pythonResolution.x) * virtualWidth;
        float y = (obj.box[1] / pythonResolution.y) * virtualHeight;

        // 상자의 크기도 가로세로 왜곡 없이 똑같은 비율로 스케일링
        float width = ((obj.box[2] - obj.box[0]) / pythonResolution.x) * virtualWidth;
        float height = ((obj.box[3] - obj.box[1]) / pythonResolution.y) * virtualHeight;

        // UI 조준창 위치 및 크기 최종 반영
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);

        // 좌우 여백(xOffset)을 더해줌으로써 화면 정중앙 핏을 완벽하게 맞춥니다.
        rt.anchoredPosition = new Vector2(x + xOffset, -y);
        rt.sizeDelta = new Vector2(width, height);

        TMP_Text labelText = boxObj.GetComponentInChildren<TMP_Text>();

        if (labelText != null)
        {
            labelText.text = $"{obj.label} ({obj.confidence:P0})";
        }
        //foreach (var obj in objects)
        //{
        //    GameObject boxObj = Instantiate(boundingBoxPrefab, overlayPanel);
        //    RectTransform rt = boxObj.GetComponent<RectTransform>();

        //    float canvasWidth = overlayPanel.rect.width;
        //    float canvasHeight = overlayPanel.rect.height;

        //    // 좌표 계산 (파이썬 0,0 좌상단 -> 유니티 0,0 우하단/중앙 등 설정에 맞춤)
        //    float x = (obj.box[0] / pythonResolution.x) * canvasWidth;
        //    float y = (obj.box[1] / pythonResolution.y) * canvasHeight;
        //    float width = ((obj.box[2] - obj.box[0]) / pythonResolution.x) * canvasWidth;
        //    float height = ((obj.box[3] - obj.box[1]) / pythonResolution.y) * canvasHeight;

        //    // Anchor를 Top-Left (0, 1)로 잡았을 때의 기준
        //    rt.anchorMin = new Vector2(0, 1);
        //    rt.anchorMax = new Vector2(0, 1);
        //    rt.pivot = new Vector2(0, 1);
        //    rt.anchoredPosition = new Vector2(x, -y);
        //    rt.sizeDelta = new Vector2(width, height);

        //    TMP_Text labelText = boxObj.GetComponentInChildren<TMP_Text>();

        //    if (labelText != null)
        //    {
        //        labelText.text = $"{obj.label} ({obj.confidence:P0})";
        //    }
        //}
    }
}