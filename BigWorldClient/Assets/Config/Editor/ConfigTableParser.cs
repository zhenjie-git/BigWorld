using System;
using System.Collections.Generic;
using System.Globalization;

namespace BigWorldClient
{

    public class ConfigColumn
    {
        public string Name;
        public string Type;
        public string Endpoint;
        public int ColIndex;
        public int RowNo;
    }

    public class ConfigRowData
    {
        public int RowNo;
        public Dictionary<string, object> Values = new();
    }

    public class GenericTable
    {
        public List<ConfigColumn> Columns = new();
        public List<ConfigRowData> Rows = new();

        public object Get(ConfigRowData row, string field)
        {
            return row.Values.TryGetValue(field, out object value) ? value : null;
        }

        public double GetFloat(ConfigRowData row, string field)
        {
            return Convert.ToDouble(Get(row, field), CultureInfo.InvariantCulture);
        }

        public string GetString(ConfigRowData row, string field)
        {
            return Get(row, field) as string ?? "";
        }
    }

    public static class ConfigTypeParsers
    {
        public static readonly string[] KnownTypes = { "float", "int", "string", "bool", "string_array", "recentering" };
        public static readonly string[] KnownEndpoints = { "server", "client", "both", "tool" };

        public static object Parse(string type, string raw, string where)
        {
            switch (type)
            {
                case "float": return ParseFloat(raw, where);
                case "int": return ParseInt(raw, where);
                case "string": return raw ?? "";
                case "bool": return ParseBool(raw, where);
                case "string_array": return ParseStringArray(raw, where);
                case "recentering": return ParseRecentering(raw, where);
                default:
                    throw new InvalidOperationException($"{where} 的数据类型 \"{type}\" 未注册");
            }
        }

        static double ParseFloat(string raw, string where)
        {
            if (!TryParse(raw, out double value))
                throw new FormatException($"{where} 的值 \"{raw}\" 不是有效数字");
            return value;
        }

        static long ParseInt(string raw, string where)
        {
            if (!TryParse(raw, out double value) || value != Math.Floor(value))
                throw new FormatException($"{where} 的值 \"{raw}\" 不是有效整数");
            return (long)value;
        }

        static bool ParseBool(string raw, string where)
        {
            string v = (raw ?? "").Trim();
            if (v.Length == 0) return false;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)
                || v == "-" || v == "×" || v.Equals("no", StringComparison.OrdinalIgnoreCase) || v == "否")
                return false;
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || v.Equals("x", StringComparison.OrdinalIgnoreCase) || v.Equals("y", StringComparison.OrdinalIgnoreCase)
                || v == "√" || v == "是")
                return true;
            throw new FormatException($"{where} 的值 \"{raw}\" 无法识别（允许：1、x、y、√、是、true、yes；禁止：留空、0、-、×、否、false、no）");
        }

        static List<object> ParseStringArray(string raw, string where)
        {
            var result = new List<object>();
            string value = (raw ?? "").Trim();
            if (value.Length == 0) return result;
            foreach (string part in value.Split(','))
            {
                string item = part.Trim();
                if (item.Length == 0)
                    throw new FormatException($"{where} 的数组值 \"{raw}\" 含空元素（逗号分隔）");
                result.Add(item);
            }
            return result;
        }

        static List<object> ParseRecentering(string raw, string where)
        {
            string value = (raw ?? "").Trim();
            string[] groups = value.Split(';');
            if (groups.Length != 3)
                throw new FormatException($"{where} 的值 \"{value}\" 必须是 3 组，用分号分隔");

            var result = new List<object>();
            for (int i = 0; i < groups.Length; i++)
            {
                string[] fields = groups[i].Split(',');
                if (fields.Length != 4)
                    throw new FormatException($"{where} 第 {i + 1} 组 \"{groups[i].Trim()}\" 必须是 4 个数（最小角,最大角,等待,时长）");

                double mn = ParseFloat(fields[0], $"{where}[{i}].minimum_angle");
                double mx = ParseFloat(fields[1], $"{where}[{i}].maximum_angle");
                double wt = ParseFloat(fields[2], $"{where}[{i}].wait_time");
                double rt = ParseFloat(fields[3], $"{where}[{i}].recentering_time");

                if (mn < 0 || mn > 360 || mx < 0 || mx > 360)
                    throw new FormatException($"{where}[{i}] 的角度必须在 [0, 360] 内");
                if (mn > mx)
                    throw new FormatException($"{where}[{i}] 的 minimum_angle 不能大于 maximum_angle");
                if (wt < -1 || wt > 20 || rt < -1 || rt > 20)
                    throw new FormatException($"{where}[{i}] 的 wait_time/recentering_time 必须在 [-1, 20] 内");

                result.Add(new JsonObject()
                    .Set("minimum_angle", mn)
                    .Set("maximum_angle", mx)
                    .Set("wait_time", wt)
                    .Set("recentering_time", rt));
            }
            return result;
        }

        static bool TryParse(string raw, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                   && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    public static class ConfigTableParser
    {
        public static GenericTable Parse(string sheetName, List<string[]> rawRows)
        {
            if (rawRows.Count < 3)
                throw new InvalidOperationException($"{sheetName} 至少需要 3 行表头（字段名/数据类型/端）");

            var table = new GenericTable();
            string[] header = PadRow(rawRows[0]);
            string[] typeRow = PadRow(rawRows[1]);
            string[] endpointRow = PadRow(rawRows[2]);

            int colCount = 0;
            for (int c = 0; c < header.Length; c++)
                if ((header[c] ?? "").Trim().Length > 0) colCount = c + 1;

            for (int c = 0; c < colCount; c++)
            {
                string name = (header[c] ?? "").Trim();
                string type = (typeRow[c] ?? "").Trim();
                string endpoint = (endpointRow[c] ?? "").Trim();
                if (name.Length == 0)
                    throw new FormatException($"{sheetName} 第 1 行第 {c + 1} 列缺少字段名");
                if (Array.IndexOf(ConfigTypeParsers.KnownTypes, type) < 0)
                    throw new FormatException($"{sheetName} 列 {name} 的数据类型 \"{type}\" 无效（支持: {string.Join("/", ConfigTypeParsers.KnownTypes)}）");
                if (Array.IndexOf(ConfigTypeParsers.KnownEndpoints, endpoint) < 0)
                    throw new FormatException($"{sheetName} 列 {name} 的端 \"{endpoint}\" 无效（支持: {string.Join("/", ConfigTypeParsers.KnownEndpoints)}）");

                table.Columns.Add(new ConfigColumn { Name = name, Type = type, Endpoint = endpoint, ColIndex = c, RowNo = c + 1 });
            }

            for (int r = 3; r < rawRows.Count; r++)
            {
                string[] raw = PadRow(rawRows[r], colCount);
                bool anyValue = false;
                for (int c = 0; c < colCount; c++)
                    if ((raw[c] ?? "").Trim().Length > 0) anyValue = true;
                if (!anyValue) continue;

                var row = new ConfigRowData { RowNo = r + 1 };
                foreach (ConfigColumn column in table.Columns)
                {
                    string cell = (raw[column.ColIndex] ?? "").Trim();
                    if (cell.Length == 0 && column.Type != "string" && column.Type != "string_array" && column.Type != "bool")
                        throw new FormatException($"{sheetName} 第 {r + 1} 行列 {column.Name} 缺少数值");
                    row.Values[column.Name] = ConfigTypeParsers.Parse(column.Type, cell, $"{sheetName} 第 {r + 1} 行列 {column.Name}");
                }
                table.Rows.Add(row);
            }

            if (table.Rows.Count == 0)
                throw new InvalidOperationException($"{sheetName} 没有数据行");

            return table;
        }

        static string[] PadRow(string[] row, int minCols = 64)
        {
            int len = Math.Max(row?.Length ?? 0, minCols);
            var result = new string[len];
            for (int i = 0; i < len; i++)
                result[i] = row != null && i < row.Length ? row[i] : "";
            return result;
        }
    }
}
