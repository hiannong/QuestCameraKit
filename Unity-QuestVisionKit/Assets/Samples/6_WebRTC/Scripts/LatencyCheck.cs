using System;
using System.Collections;
using UnityEngine;
using SimpleWebRTC;

public class LatencyCheck : MonoBehaviour
{
    [Header("WebRTC Reference")]
    [SerializeField] private WebRTCConnection webRTCConnection;

    [Header("Timestamp Settings")]
    [SerializeField] private float timestampInterval = 0.033f; // 약 30 FPS 간격

    private float _timer = 0f;
    private bool _isSynced = false;

    private void Awake()
    {
        EnsureWebRTCConnection();
    }

    private void EnsureWebRTCConnection()
    {
        if (webRTCConnection == null)
        {
            webRTCConnection = GetComponent<WebRTCConnection>();
            if (webRTCConnection == null)
            {
                webRTCConnection = FindObjectOfType<WebRTCConnection>();
            }
        }
    }

    private void OnEnable()
    {
        EnsureWebRTCConnection();
        if (webRTCConnection != null)
        {
            webRTCConnection.DataChannelMessageReceived.AddListener(OnMessageReceived);
        }
    }

    private void OnDisable()
    {
        if (webRTCConnection != null)
        {
            webRTCConnection.DataChannelMessageReceived.RemoveListener(OnMessageReceived);
        }
    }

    private void Update()
    {
        EnsureWebRTCConnection();

        if (webRTCConnection != null && webRTCConnection.IsWebRTCActive)
        {
            // 연결 시 1회 시계 동기화 수행
            if (!_isSynced)
            {
                _isSynced = true;
                StartCoroutine(RoutineClockSync());
            }

            // 프레임 타임스탬프 지속 전송
            _timer += Time.deltaTime;
            if (_timer >= timestampInterval)
            {
                _timer = 0f;
                SendFrameTimestamp();
            }
        }
        else
        {
            _isSynced = false;
        }
    }

    private IEnumerator RoutineClockSync()
    {
        // 시계 동기화를 위해 5개의 핑을 보냄
        for (int i = 0; i < 5; i++)
        {
            SendClockSyncPing();
            yield return new WaitForSeconds(0.1f);
        }
    }

    private void SendClockSyncPing()
    {
        if (webRTCConnection == null || !webRTCConnection.IsWebRTCActive) return;
        try
        {
            long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string json = $"{{\"type\":\"clock_sync\",\"t0\":{t0}}}";
            webRTCConnection.SendDataChannelMessage(json);
        }
        catch { }
    }

    public void SendFrameTimestamp()
    {
        if (webRTCConnection == null || !webRTCConnection.IsWebRTCActive) return;
        try
        {
            long questTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string json = $"{{\"type\":\"timestamp\",\"timestamp\":{questTs}}}";
            webRTCConnection.SendDataChannelMessage(json);
        }
        catch { }
    }

    private void OnMessageReceived(string message)
    {
        try
        {
            if (message.Contains("\"type\":\"clock_sync_ack\""))
            {
                long t2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                ClockSyncAck ack = JsonUtility.FromJson<ClockSyncAck>(message);
                if (ack != null && ack.t0 > 0)
                {
                    long rtt = t2 - ack.t0;
                    long offset = ack.t1 - (ack.t0 + rtt / 2);
                    string setOffsetJson = $"{{\"type\":\"set_offset\",\"offset\":{offset}}}";
                    webRTCConnection.SendDataChannelMessage(setOffsetJson);
                }
            }
        }
        catch { }
    }

    [Serializable]
    private class ClockSyncAck
    {
        public string type;
        public long t0;
        public long t1;
    }
}
