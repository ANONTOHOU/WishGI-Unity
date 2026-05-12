using UnityEngine;
using static System.Net.Mime.MediaTypeNames;
using Application = UnityEngine.Application;

public class FrameRateSetter : MonoBehaviour
{
    void Awake()
    {
        // 关闭垂直同步（0 = Don't Sync）
        QualitySettings.vSyncCount = 0;

        // 设置目标帧率为 300
        Application.targetFrameRate = 300;
    }
}