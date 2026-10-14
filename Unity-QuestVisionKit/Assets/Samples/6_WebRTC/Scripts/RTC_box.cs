using UnityEngine;
using SimpleWebRTC;
using System.Collections.Generic;
using System;

public class RTC_box : MonoBehaviour
{
    [Serializable]
    public class PythonPayload
    {
        public string type;
        public int frame_id;

        //  기존 코드 (IL2CPP 컴파일러 크래시 원인)
        // public List<float[]> coords; 

        //  변경된 코드 (float[] 대신 List<float> 구조를 사용해 안전하게 우회)
        public List<List<float>> coords;

        public List<int> labelIDs;       // 파이썬의 [0, 1, ...] 수신
        public List<float> confidences;  // 파이썬의 [0.95, ...] su신
    }

    [Header("WebRTC 세팅")]
    public WebRTCConnection webrtcConnection;

    [Header("순정 렌더러 연동")]
    [SerializeField] private RTCRenderer pureObjectRenderer; //  순정 렌더러 연결 칸

    // 멀티스레드(WebRTC 수신) 환경에서 안전하게 데이터를 주고받기 위한 버퍼 및 락 장치
    private string pendingJsonMessage = null;
    private readonly object lockObject = new object();

    //  1. 스크립트가 켜질 때 WebRTC 데이터 채널 수신 대기 시작
    void OnEnable()
    {
        if (webrtcConnection != null)
            webrtcConnection.DataChannelMessageReceived.AddListener(OnMessageReceived);
    }

    //  2. 스크립트가 꺼질 때 리스너 제거
    void OnDisable()
    {
        if (webrtcConnection != null)
            webrtcConnection.DataChannelMessageReceived.RemoveListener(OnMessageReceived);
    }

    //  3. PC 파이썬으로부터 WebRTC 데이터 패킷이 도착하면 실행되는 백그라운드 함수
    public void OnMessageReceived(string jsonMessage)
    {
        lock (lockObject)
        {
            pendingJsonMessage = jsonMessage; // 도착한 JSON 문자열을 보관함에 킵(Keep)
        }
    }

    //  4. 유니티 메인 루프 (매 프레임마다 보관함을 감시)
    void Update()
    {
        string messageToProcess = null;
        lock (lockObject)
        {
            if (pendingJsonMessage != null)
            {
                messageToProcess = pendingJsonMessage;
                pendingJsonMessage = null; // 보관함 비우기
            }
        }

        //  킵해둔 메세지가 있다면 드디어 유니티 메인 스레드에서 ProcessData를 실행!
        if (messageToProcess != null)
        {
            Debug.Log($"[plz] data: {messageToProcess}");
            ProcessData(messageToProcess);
        }
    }

    //  5. 파이썬 JSON 데이터를 순정 유니티 텐서(Tensor)로 가공하여 순정 렌더러에 공급하는 함수
    private void ProcessData(string json)
    {
        try
        {
            //  [핵심 변경] JsonUtility를 버리고 중첩 배열을 완벽히 파싱하는 Newtonsoft.Json을 사용합니다.
            PythonPayload data = Newtonsoft.Json.JsonConvert.DeserializeObject<PythonPayload>(json);

            //  [안전장치 추가] 혹시 모를 Null 에러를 원천 차단하기 위해 data.coords가 null인지 먼저 검사합니다.
            if (data != null && data.type == "detection" && data.coords != null && data.coords.Count > 0)
            {
                int count = data.coords.Count;
                Debug.Log($"[plz] count: {count})");

                // 1) 파이썬의 coords 데이터를 유니티 Sentis Tensor<float>로 구워내기
                var coordsShape = new Unity.InferenceEngine.TensorShape(count, 4);
                var coordsTensor = new Unity.InferenceEngine.Tensor<float>(coordsShape);
                for (int i = 0; i < count; i++)
                {
                    //  구조가 List<List<float>>로 바뀌어도 대괄호 문법은 [i][j]로 완전히 동일하게 작동합니다!
                    coordsTensor[i, 0] = data.coords[i][0]; // cx
                    coordsTensor[i, 1] = data.coords[i][1]; // cy
                    coordsTensor[i, 2] = data.coords[i][2]; // w
                    coordsTensor[i, 3] = data.coords[i][3]; // h
                }

                // 2) 파이썬의 labelIDs 데이터를 유니티 Sentis Tensor<int>로 구워내기
                var labelShape = new Unity.InferenceEngine.TensorShape(count);
                var labelTensor = new Unity.InferenceEngine.Tensor<int>(labelShape);
                for (int i = 0; i < count; i++)
                {
                    labelTensor[i] = data.labelIDs[i];
                }

                // 3) 파이썬의 confidences 데이터를 유니티 Sentis Tensor<float>로 구워내기
                var confShape = new Unity.InferenceEngine.TensorShape(count);
                var confTensor = new Unity.InferenceEngine.Tensor<float>(confShape);
                for (int i = 0; i < count; i++)
                {
                    confTensor[i] = data.confidences[i];
                }

                // 4) 완벽하게 조립된 3대 순정 텐서를 오리지널 ObjectRenderer에 토스!
                if (pureObjectRenderer != null)
                {
                    pureObjectRenderer.RenderDetections(coordsTensor, labelTensor, confTensor);
                }

                // 메모리 누수 방지를 위한 수동 해제
                coordsTensor.Dispose();
                labelTensor.Dispose();
                confTensor.Dispose();
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[plz] Tensor transition fail: " + e.Message);
        }
    }
}