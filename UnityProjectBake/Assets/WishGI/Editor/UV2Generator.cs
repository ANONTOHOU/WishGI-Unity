using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// 显式消除歧义
using Debug = UnityEngine.Debug;
using UEObject = UnityEngine.Object;

namespace WishGI.Baking.Editor
{
    /// <summary>
    /// 为场景中缺少 UV2 的 Mesh 自动生成 Lightmap UV（UV2）。
    /// 菜单路径：Tools/WishGI/Generate UV2 For Scene Meshes
    /// 
    /// 原理：
    ///   Unity 编辑器提供 Unwrapping.GenerateSecondaryUVSet(mesh)，
    ///   可在编辑期为任意可读 Mesh 生成 UV2，功能等同于
    ///   模型导入设置中的 "Generate Lightmap UVs"。
    ///   本脚本批量扫描场景，自动补齐缺失的 UV2。
    /// </summary>
    public static class UV2Generator
    {
        // ────────────────────────────────────────────
        // 菜单入口
        // ────────────────────────────────────────────
        [MenuItem("WishGI/Step 0: Generate UV2 For Scene Meshes", false, 10)]
        public static void GenerateUV2ForScene()
        {
            // 1. 收集场景中所有启用的 MeshFilter
            var filters = UEObject.FindObjectsByType<MeshFilter>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None
            );

            if (filters.Length == 0)
            {
                Debug.LogWarning("[WishGI] 当前场景没有找到任何启用的 MeshFilter。");
                return;
            }

            int generated = 0;
            int skipped = 0;
            int alreadyHas = 0;

            // 用 HashSet 避免对同一个 Mesh 资产重复处理
            var processed = new HashSet<int>();

            foreach (var mf in filters)
            {
                Mesh mesh = mf.sharedMesh;

                // ── 基本过滤 ──
                if (mesh == null)
                {
                    skipped++;
                    continue;
                }

                // 同一个 Mesh 资产只处理一次
                int meshId = mesh.GetInstanceID();
                if (processed.Contains(meshId))
                    continue;
                processed.Add(meshId);

                // ── 已有 UV2，跳过 ──
                if (mesh.uv2 != null && mesh.uv2.Length > 0)
                {
                    alreadyHas++;
                    continue;
                }

                // ── 可读性检查 ──
                if (!mesh.isReadable)
                {
                    Debug.LogWarning(
                        $"[WishGI] Mesh 不可读，无法生成 UV2，请启用 Read/Write Enabled: " +
                        $"{mf.gameObject.name} → {mesh.name}",
                        mf.gameObject
                    );
                    skipped++;
                    continue;
                }

                // ── 顶点数检查 ──
                if (mesh.vertexCount == 0)
                {
                    Debug.LogWarning(
                        $"[WishGI] Mesh 无顶点，跳过: {mf.gameObject.name} → {mesh.name}",
                        mf.gameObject
                    );
                    skipped++;
                    continue;
                }

                // ────────────────────────────────────
                // 核心：为 Mesh 生成 UV2
                // ────────────────────────────────────
                //
                // 对于导入模型的 Mesh（如 FBX/GLTF），sharedMesh
                // 是资产引用，直接修改会改变原始资产。
                // 这里提供两种策略：
                //   A) 直接修改原始 Mesh（简单，但会改动资产）
                //   B) 复制一份再生成（安全，不动原始资产）
                //
                // 下面默认使用 A，如需 B 请参考注释。

                // ── 策略 A：直接修改 ──
                GenerateUV2(mesh, mf.gameObject.name);
                generated++;

                // ── 策略 B（可选）：复制 Mesh 后生成 ──
                // Mesh cloned = UEObject.Instantiate(mesh);
                // cloned.name = mesh.name + "_UV2";
                // GenerateUV2(cloned, mf.gameObject.name);
                // mf.sharedMesh = cloned;
                // // 可选：保存为资产
                // // AssetDatabase.CreateAsset(cloned, $"Assets/WishGI/GeneratedUV2/{cloned.name}.asset");
                // generated++;
            }

            // ── 结果汇总 ──
            Debug.Log(
                $"[WishGI] 完成！\n" +
                $"  已生成 UV2: {generated} 个 Mesh\n" +
                $"  已有 UV2 (跳过): {alreadyHas} 个\n" +
                $"  无法处理 (跳过): {skipped} 个"
            );

            EditorUtility.DisplayDialog(
                "UV2 Generator",
                $"处理完成！\n\n" +
                $"已生成 UV2: {generated}\n" +
                $"已有 UV2 (跳过): {alreadyHas}\n" +
                $"无法处理 (跳过): {skipped}",
                "OK"
            );
        }

        /// <summary>
        /// 使用 Unity 内置 Unwrapping API 为 Mesh 生成 UV2。
        /// 可自定义展 UV 参数（硬边角度、包边距等）。
        /// </summary>
        private static void GenerateUV2(Mesh mesh, string objectName)
        {
            // 展 UV 参数，可根据项目需求调整
            var settings = new UnwrapParam();
            UnwrapParam.SetDefaults(out settings);
            // settings.hardAngle  = 88f;   // 硬边角度阈值（度）
            // settings.packMargin = 0.004f; // UV 岛间距
            // settings.angleError = 0.08f;  // 角度误差容忍
            // settings.areaError  = 0.15f;  // 面积误差容忍

            Unwrapping.GenerateSecondaryUVSet(mesh, settings);

            // 验证结果
            if (mesh.uv2 != null && mesh.uv2.Length > 0)
            {
                Debug.Log(
                    $"[WishGI] ✓ 已为 \"{objectName}\" 的 Mesh \"{mesh.name}\" " +
                    $"生成 UV2 ({mesh.uv2.Length} 个 UV 坐标)",
                    mesh
                );
            }
            else
            {
                Debug.LogError(
                    $"[WishGI] ✗ 为 \"{objectName}\" 的 Mesh \"{mesh.name}\" " +
                    $"生成 UV2 失败，请检查 Mesh 数据是否完整。",
                    mesh
                );
            }
        }
    }
}
