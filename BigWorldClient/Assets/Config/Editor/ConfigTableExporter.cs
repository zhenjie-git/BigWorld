using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Google.FlatBuffers;
using UnityEditor;
using UnityEngine;

namespace BigWorldClient
{

    public class TableDef
    {
        public string Name;
        public string Description;
        public string ExcelRelativePath;
        public string SheetName;
        public string ServerBytesRelativePath;
        public string ClientBytesAssetPath;
        public string GameConfigFolder;
        public bool RowsAsEntries;
        public string ToggleLabel;
        public Type TableObjectType;
        public Type EntryObjectType;
        public Action<GenericTable> Validate;
        public Func<GenericTable, GenericTable> PostParse;
        public Func<TableDef, List<string[]>, List<string>> PreProcess;
    }

    public static class ConfigTableExporter
    {
        public static ExportResult Export(TableDef def, bool withCurves = false)
        {
            try
            {
                return ExportResult.Ok(ExportCore(def, withCurves));
            }
            catch (Exception e)
            {
                return ExportResult.Fail($"{e.Message}\n{e.StackTrace}");
            }
        }

        static string[] ExportCore(TableDef def, bool withCurves)
        {
            string xlsxPath = Path.Combine(ConfigTableRegistry.GameConfigDir, def.ExcelRelativePath);
            if (!File.Exists(xlsxPath))
                throw new FileNotFoundException("Excel 源文件不存在，请确认路径", xlsxPath);

            var rawRows = ReadSheetRows(xlsxPath, def.SheetName);

            var outputs = new List<string>();
            if (withCurves && def.PreProcess != null)
            {
                List<string> generated = def.PreProcess(def, rawRows);
                if (generated != null)
                    outputs.AddRange(generated);
            }

            GenericTable table = ConfigTableParser.Parse(def.SheetName, rawRows);
            if (def.PostParse != null)
                table = def.PostParse(table);
            def.Validate?.Invoke(table);

            if (def.ServerBytesRelativePath != null)
            {
                string path = Path.Combine(ConfigTableRegistry.GameConfigDir, def.ServerBytesRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ConfigTableRegistry.GameConfigDir);
                File.WriteAllBytes(path, BuildBytes(table, def, "server"));
                outputs.Add(path);
            }

            if (def.ClientBytesAssetPath != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(def.ClientBytesAssetPath) ?? "Assets");
                File.WriteAllBytes(def.ClientBytesAssetPath, BuildBytes(table, def, "client"));
                outputs.Add(def.ClientBytesAssetPath);
            }

            AssetDatabase.Refresh();
            if (def.ClientBytesAssetPath != null)
                AssetDatabase.ImportAsset(def.ClientBytesAssetPath, ImportAssetOptions.ForceUpdate);
            return outputs.ToArray();
        }

        public static byte[] BuildBytes(GenericTable table, TableDef def, string endpoint)
        {
            object root = Activator.CreateInstance(def.TableObjectType);
            if (def.RowsAsEntries)
            {
                if (def.EntryObjectType == null)
                    throw new InvalidOperationException($"{def.Name} 是行式表，缺少 EntryObjectType");
                PropertyInfo entriesProp = def.TableObjectType.GetProperty("Entries")
                    ?? throw new InvalidOperationException($"{def.TableObjectType.Name} 缺少 Entries 属性（需重新生成协议代码）");

                var listType = typeof(List<>).MakeGenericType(def.EntryObjectType);
                IList list = (IList)Activator.CreateInstance(listType);
                foreach (ConfigRowData row in table.Rows)
                {
                    object entry = Activator.CreateInstance(def.EntryObjectType);
                    FillObject(entry, def.EntryObjectType, table, row, endpoint);
                    list.Add(entry);
                }
                entriesProp.SetValue(root, list);
            }
            else
            {
                FillObject(root, def.TableObjectType, table, table.Rows[0], endpoint);
            }

            var builder = new FlatBufferBuilder(1024);
            MethodInfo pack = def.TableObjectType.GetMethod("Pack")
                ?? throw new InvalidOperationException($"{def.TableObjectType.Name} 缺少 Pack（需 --gen-object-api 生成）");
            object offset = pack.Invoke(null, new object[] { builder, root });
            int rootOffset = (int)offset.GetType().GetField("Value").GetValue(offset);
            builder.Finish(rootOffset);
            return builder.SizedByteArray();
        }

        static void FillObject(object obj, Type type, GenericTable table, ConfigRowData row, string endpoint)
        {
            foreach (ConfigColumn column in table.Columns)
            {
                if (column.Endpoint != endpoint && column.Endpoint != "both") continue;
                PropertyInfo prop = type.GetProperty(ToPascal(column.Name));
                if (prop == null)
                    throw new InvalidOperationException(
                        $"字段 {column.Name} 在 {type.Name} 中不存在——表头变了，请先执行 BigWorld/Config/生成配置协议代码");
                row.Values.TryGetValue(column.Name, out object parsed);
                prop.SetValue(obj, ConvertValue(parsed, prop.PropertyType, column.Name));
            }
        }

        static object ConvertValue(object parsed, Type targetType, string fieldName)
        {
            if (targetType == typeof(float)) return Convert.ToSingle((double)parsed);
            if (targetType == typeof(long)) return parsed is long l ? l : Convert.ToInt64(parsed);
            if (targetType == typeof(string)) return parsed as string ?? "";
            if (targetType == typeof(bool)) return parsed is bool b && b;

            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type elemType = targetType.GetGenericArguments()[0];
                IList list = (IList)Activator.CreateInstance(targetType);
                if (parsed is List<object> items)
                {
                    foreach (object item in items)
                        list.Add(ConvertItem(item, elemType, fieldName));
                }
                return list;
            }

            throw new InvalidOperationException($"字段 {fieldName}：无法把 {parsed?.GetType().Name ?? "null"} 转成 {targetType.Name}");
        }

        static object ConvertItem(object item, Type elemType, string fieldName)
        {
            if (elemType == typeof(string)) return item as string ?? "";
            if (item is JsonObject obj)
            {
                object entry = Activator.CreateInstance(elemType);
                foreach (var (key, value) in obj.Fields)
                {
                    PropertyInfo prop = elemType.GetProperty(ToPascal(key))
                        ?? throw new InvalidOperationException($"字段 {fieldName} 的子字段 {key} 在 {elemType.Name} 中不存在");
                    prop.SetValue(entry, Convert.ToSingle((double)value));
                }
                return entry;
            }
            throw new InvalidOperationException($"字段 {fieldName}：数组元素类型不匹配");
        }

        public static string ToPascal(string snake)
        {
            string[] parts = snake.Split('_');
            var sb = new System.Text.StringBuilder();
            foreach (string part in parts)
            {
                if (part.Length == 0) continue;
                sb.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
            }
            return sb.ToString();
        }

        public static List<string[]> ReadSheetRows(string xlsxPath, string sheetName)
        {
            var cells = LightExcel.ReadSheetCells(xlsxPath, sheetName);
            int maxRow = 0, maxCol = 0;
            foreach (string key in cells.Keys)
            {
                int sep = key.IndexOf(':');
                int r = int.Parse(key.Substring(0, sep));
                int c = int.Parse(key.Substring(sep + 1));
                maxRow = Math.Max(maxRow, r);
                maxCol = Math.Max(maxCol, c);
            }

            var rows = new List<string[]>(maxRow);
            for (int r = 1; r <= maxRow; r++)
            {
                string[] row = new string[Math.Max(maxCol, 1)];
                for (int c = 1; c <= maxCol; c++)
                    row[c - 1] = cells.TryGetValue($"{r}:{c}", out string v) ? v.Trim() : "";
                rows.Add(row);
            }
            return rows;
        }

        public static void BatchExport(TableDef def, bool withCurves = false)
        {
            ExportResult result = Export(def, withCurves);
            if (result.Success)
                Debug.Log($"[Config] {def.Name} 已导出:\n{result.Message}");
            else
                Debug.LogError($"[Config] {def.Name} 导出失败:\n{result.Message}");
            if (!result.Success && Application.isBatchMode)
                EditorApplication.Exit(1);
        }
    }
}
