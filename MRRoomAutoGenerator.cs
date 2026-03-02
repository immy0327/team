using UnityEngine;
using UnityEngine.XR;
using System.Collections.Generic;

public class MRRoomAutoFurniture : MonoBehaviour
{
    [Header("Materials")]
    public Material wallMaterial;
    public Material floorMaterial;
    public Material furnitureMaterial;

    [Header("Room Size (掃描後可更新)")]
    public Vector3 roomCenter = Vector3.zero;
    public Vector3 roomExtents = new Vector3(2f, 1.25f, 2.5f);

    [Header("Furniture Auto Settings")]
    public int furnitureCount = 5; // 掃描出來的家具數量
    public Vector3 minFurnitureSize = new Vector3(0.3f, 0.5f, 0.3f);
    public Vector3 maxFurnitureSize = new Vector3(1f, 1f, 1f);

    private bool roomGenerated = false;
    private List<Vector3> placedPositions = new List<Vector3>();

    void Update()
    {
        if (!roomGenerated && IsMRReady())
        {
            GenerateRoom();
            GenerateFurnitureAuto();
            roomGenerated = true;
        }
    }

    bool IsMRReady()
    {
        List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        SubsystemManager.GetSubsystems(displays);
        foreach (var d in displays)
            if (d.running) return true;
        return false;
    }

    void GenerateRoom()
    {
        Vector3 c = roomCenter;
        Vector3 e = roomExtents;
        float floorY = c.y - e.y;

        CreateCube(c + Vector3.down * e.y, new Vector3(e.x * 2, 0.1f, e.z * 2), floorMaterial);
        CreateCube(c + Vector3.left * e.x, new Vector3(0.1f, e.y * 2, e.z * 2), wallMaterial);
        CreateCube(c + Vector3.right * e.x, new Vector3(0.1f, e.y * 2, e.z * 2), wallMaterial);
        CreateCube(c + Vector3.forward * e.z, new Vector3(e.x * 2, e.y * 2, 0.1f), wallMaterial);
        CreateCube(c + Vector3.back * e.z, new Vector3(e.x * 2, e.y * 2, 0.1f), wallMaterial);

        Debug.Log("房間生成完成！");
    }

    void GenerateFurnitureAuto()
    {
        Vector3 c = roomCenter;
        Vector3 e = roomExtents;
        float floorY = c.y - e.y;

        placedPositions.Clear();

        for (int i = 0; i < furnitureCount; i++)
        {
            Vector3 size = new Vector3(
                Random.Range(minFurnitureSize.x, maxFurnitureSize.x),
                Random.Range(minFurnitureSize.y, maxFurnitureSize.y),
                Random.Range(minFurnitureSize.z, maxFurnitureSize.z)
            );

            Vector3 pos = Vector3.zero;
            bool validPos = false;
            int attempts = 0;

            while (!validPos && attempts < 50)
            {
                pos = new Vector3(
                    Random.Range(-e.x + size.x / 2, e.x - size.x / 2),
                    floorY + size.y / 2,
                    Random.Range(-e.z + size.z / 2, e.z - size.z / 2)
                );

                validPos = true;
                foreach (var other in placedPositions)
                {
                    if (Vector3.Distance(pos, other) < 0.5f) // 避免重疊
                    {
                        validPos = false;
                        break;
                    }
                }
                attempts++;
            }

            placedPositions.Add(pos);
            CreateCube(pos, size, furnitureMaterial);
        }

        Debug.Log("家具自動生成完成！");
    }

    void CreateCube(Vector3 pos, Vector3 scale, Material mat)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.position = pos;
        cube.transform.localScale = scale;

        var mr = cube.GetComponent<MeshRenderer>();
        if (mat != null)
            mr.material = mat;
        else
        {
            mr.material = new Material(Shader.Find("Unlit/Color"));
            mr.material.color = Color.red;
        }
    }
}