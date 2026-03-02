using UnityEngine;

[System.Serializable]
public class FurnitureData
{
    public string name;
    public Vector3 relativePosition; // 相對房間中心
    public Vector3 size;             // 家具尺寸 (x=寬, y=高, z=深)
}