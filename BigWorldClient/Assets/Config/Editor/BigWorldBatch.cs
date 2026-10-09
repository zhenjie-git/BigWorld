using System;
using UnityEditor;
using UnityEngine;

namespace BigWorldClient
{
    public static class BigWorldBatch
    {
        public static void CompileCheck()
        {
            Debug.Log("[BigWorldBatch] compile check ok");
            EditorApplication.Exit(0);
        }

        public static void RunZonePipeline()
        {
            try
            {
                ConfigSchemaGenerator.Generate();
                ConfigTableDefinitions.ExportSceneConfig();
                string splitError = ZoneSceneSplitter.SplitAll();
                if (splitError != null)
                    throw new InvalidOperationException($"世界地图拆分失败: {splitError}");
                var voxelResult = VoxelSceneExporter.GenerateAll();
                if (!voxelResult.Success)
                    throw new InvalidOperationException($"体素生成失败: {voxelResult.Message}");
                Debug.Log($"[BigWorldBatch] zone 管线完成:\n{voxelResult.Message}");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[BigWorldBatch] 管线失败: {e.Message}\n{e.StackTrace}");
                EditorApplication.Exit(1);
            }
        }
    }
}
