using UnityEngine;
using SimpleWebRTC;
using System.Collections.Generic;
using System;
using TMPro;

public class makingbox : MonoBehaviour
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
    }

    [Header("WebRTC 세팅")]
    public WebRTCConnection webrtcConnection;

    [Header("UI 배치 세팅")]
    public RectTransform overlayPanel;
    public GameObject boundingBoxPrefab;

    private Vector2 pythonResolution = new Vector2(640, 360);

    private string pendingJsonMessage = null;
    private readonly object lockObject = new object();

    //  [핵심 수정] 단일 박스 변수를 지우고, 여러 박스를 담아둘 '바구니 리스트'를 만듭니다.
    private List<GameObject> liveBoxInstances = new List<GameObject>();

    void OnEnable()
    {
        webrtcConnection.DataChannelMessageReceived.AddListener(OnMessageReceived);
    }

    void OnDisable()
    {
        webrtcConnection.DataChannelMessageReceived.RemoveListener(OnMessageReceived);
    }

    //void Start()
    //{
    //    // 시작할 때 기본적으로 상자 2개를 풀(Pool)에 미리 만들어 둡니다.
    //    if (boundingBoxPrefab != null && overlayPanel != null)
    //    {
    //        for (int i = 0; i < 2; i++)
    //        {
    //            CreateNewBox();
    //        }
    //        Debug.Log("[plz] complete box preclone. waiting space bar");
    //    }
    //    else
    //    {
    //        Debug.LogError("[plz] prefab or panel is not connected!");
    //    }
    //}

    //  [추가] 상자가 더 필요할 때 새 상자를 복사해서 리스트에 추가하는 함수
    private GameObject CreateNewBox()
    {
        GameObject newBox = Instantiate(boundingBoxPrefab, overlayPanel);
        newBox.name = $"LIVE_DETECTION_BOX_{liveBoxInstances.Count}";
        newBox.SetActive(false); // 일단 숨김
        liveBoxInstances.Add(newBox);
        return newBox;
    }

    public void OnMessageReceived(string jsonMessage)
    {
        lock (lockObject)
        {
            pendingJsonMessage = jsonMessage;
        }
    }

    void Update()
    {
        string messageToProcess = null;
        lock (lockObject)
        {
            if (pendingJsonMessage != null)
            {
                Debug.Log($"[plz]: {pendingJsonMessage}");
                messageToProcess = pendingJsonMessage;
                pendingJsonMessage = null;
            }
        }

        if (messageToProcess != null)
        {
            //ProcessData(messageToProcess);
            Debug.Log($"[plz]: hi");
        }
    }

    private void ProcessData(string json)
    {
        try
        {
            DetectionResult result = JsonUtility.FromJson<DetectionResult>(json);
            if (result != null && result.type == "detection")
            {
                Debug.Log($"[plz] PC signal accpet success (sender ID: {result.frame_id})");
                UpdateBoxPosition(result.objects);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[plz] JSON passing error: " + e.Message);
        }
    }

    private void UpdateBoxPosition(List<DetectedObject> objects)
    {
        // 1. 데이터가 없으면 모든 상자를 숨기고 종료
        if (objects == null || objects.Count == 0)
        {
            foreach (var box in liveBoxInstances) box.SetActive(false);
            return;
        }

        // 2. 만약 PC가 보낸 데이터 개수(예: 3개)가 현재 만들어진 상자 개수(2개)보다 많으면 실시간으로 더 생성합니다.
        while (liveBoxInstances.Count < objects.Count)
        {
            CreateNewBox();
        }

        // 3. 일단 화면의 박스를 전부 숨겨두고, 필요한 만큼만 켜서 재배치합니다.
        for (int i = 0; i < liveBoxInstances.Count; i++)
        {
            if (i < objects.Count)
            {
                var obj = objects[i];

                //  [핵심] i번째 상자 오브젝트와 그 컴포넌트들을 각각 가져옵니다!
                GameObject currentBox = liveBoxInstances[i];
                currentBox.SetActive(true);

                RectTransform boxRect = currentBox.GetComponent<RectTransform>();
                TMP_Text boxLabel = currentBox.GetComponentInChildren<TMP_Text>();

                float canvasWidth = overlayPanel.rect.width;
                float canvasHeight = overlayPanel.rect.height;

                float x1 = (obj.box[0] / pythonResolution.x) * canvasWidth;
                float y1 = (obj.box[1] / pythonResolution.y) * canvasHeight;
                float x2 = (obj.box[2] / pythonResolution.x) * canvasWidth;
                float y2 = (obj.box[3] / pythonResolution.y) * canvasHeight;

                float width = x2 - x1;
                float height = y2 - y1;

                boxRect.anchorMin = new Vector2(0, 1);
                boxRect.anchorMax = new Vector2(0, 1);
                boxRect.pivot = new Vector2(0, 1);

                boxRect.anchoredPosition = new Vector2(x1, -y1);
                boxRect.sizeDelta = new Vector2(width, height);

                boxRect.localPosition = new Vector3(boxRect.localPosition.x, boxRect.localPosition.y, 0);
                boxRect.localScale = Vector3.one;

                if (boxLabel != null)
                {
                    boxLabel.text = obj.label;
                }
            }
            else
            {
                // 이번 데이터 개수보다 뒤에 있는 남는 상자들은 숨깁니다.
                liveBoxInstances[i].SetActive(false);
            }
        }
    }
}