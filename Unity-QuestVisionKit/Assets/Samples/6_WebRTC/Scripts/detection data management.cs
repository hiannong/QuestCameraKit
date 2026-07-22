using System;
using System.Collections.Generic;

[Serializable]
public class DetectionResult
{
    public string type;
    public int frame_id;
    public List<DetectedObject> objects;
}

[Serializable]
public class DetectedObject
{
    public string label;
    public float confidence;
    public int[] box; // [x1, y1, x2, y2]
}