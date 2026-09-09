using System;
using System.IO;
using UnityEngine;

namespace BigWorldClient
{

    public class ExportResult
    {
        public bool Success;
        public string Message;
        public string[] Outputs;

        public static ExportResult Ok(string[] outputs)
        {
            return new ExportResult { Success = true, Outputs = outputs ?? Array.Empty<string>(), Message = string.Join("\n", outputs ?? Array.Empty<string>()) };
        }

        public static ExportResult Fail(string message)
        {
            return new ExportResult { Success = false, Message = message ?? "" };
        }
    }

    public class ConfigTableEntry
    {
        public string Name;
        public string Description;
        public string ExcelRelativePath;
        public string GameConfigFolder;
        public string ToggleLabel;
        public Func<bool, ExportResult> Export;
    }

    public static class ConfigTableRegistry
    {
        public static string GameConfigDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "GameConfig"));

        public static readonly ConfigTableEntry[] Entries =
        {
            new ConfigTableEntry
            {
                Name = ConfigTableDefinitions.PlayerConfig.Name,
                Description = ConfigTableDefinitions.PlayerConfig.Description,
                ExcelRelativePath = ConfigTableDefinitions.PlayerConfig.ExcelRelativePath,
                GameConfigFolder = ConfigTableDefinitions.PlayerConfig.GameConfigFolder,
                Export = withCurves => ConfigTableExporter.Export(ConfigTableDefinitions.PlayerConfig, withCurves),
            },
            new ConfigTableEntry
            {
                Name = ConfigTableDefinitions.StateConfig.Name,
                Description = ConfigTableDefinitions.StateConfig.Description,
                ExcelRelativePath = ConfigTableDefinitions.StateConfig.ExcelRelativePath,
                GameConfigFolder = ConfigTableDefinitions.StateConfig.GameConfigFolder,
                ToggleLabel = ConfigTableDefinitions.StateConfig.ToggleLabel,
                Export = withCurves =>
                {
                    ExportResult result = ConfigTableExporter.Export(ConfigTableDefinitions.StateConfig, withCurves);
                    if (result.Success) MoveStateMappingGenerator.Generate();
                    return result;
                },
            },
            new ConfigTableEntry
            {
                Name = ConfigTableDefinitions.StateTransitionTable.Name,
                Description = ConfigTableDefinitions.StateTransitionTable.Description,
                ExcelRelativePath = ConfigTableDefinitions.StateTransitionTable.ExcelRelativePath,
                GameConfigFolder = ConfigTableDefinitions.StateTransitionTable.GameConfigFolder,
                Export = withCurves => ConfigTableExporter.Export(ConfigTableDefinitions.StateTransitionTable, withCurves),
            },
            new ConfigTableEntry
            {
                Name = ConfigTableDefinitions.PanelConfig.Name,
                Description = ConfigTableDefinitions.PanelConfig.Description,
                ExcelRelativePath = ConfigTableDefinitions.PanelConfig.ExcelRelativePath,
                GameConfigFolder = ConfigTableDefinitions.PanelConfig.GameConfigFolder,
                Export = withCurves =>
                {
                    ExportResult result = ConfigTableExporter.Export(ConfigTableDefinitions.PanelConfig, withCurves);
                    if (result.Success) PanelIdsGenerator.Generate();
                    return result;
                },
            },
            new ConfigTableEntry
            {
                Name = ConfigTableDefinitions.SceneConfig.Name,
                Description = ConfigTableDefinitions.SceneConfig.Description,
                ExcelRelativePath = ConfigTableDefinitions.SceneConfig.ExcelRelativePath,
                GameConfigFolder = ConfigTableDefinitions.SceneConfig.GameConfigFolder,
                Export = withCurves => ConfigTableExporter.Export(ConfigTableDefinitions.SceneConfig, withCurves),
            },
        };
    }
}
