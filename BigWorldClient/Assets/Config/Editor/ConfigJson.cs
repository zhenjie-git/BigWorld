using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BigWorldClient
{

    public class JsonObject
    {
        readonly List<(string Key, object Value)> _fields = new();

        public JsonObject Set(string key, object value)
        {
            _fields.Add((key, value));
            return this;
        }

        public IReadOnlyList<(string Key, object Value)> Fields => _fields;
    }

    public static class ConfigJsonWriter
    {
        public static string Write(object node)
        {
            var sb = new StringBuilder();
            WriteNode(sb, node, 0);
            return sb.ToString();
        }

        static void WriteNode(StringBuilder sb, object node, int indent)
        {
            switch (node)
            {
                case JsonObject obj:
                    WriteObject(sb, obj, indent);
                    break;
                case List<object> list:
                    WriteArray(sb, list, indent);
                    break;
                case null:
                    sb.Append("null");
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case double d:
                    sb.Append(Num(d));
                    break;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new InvalidCastException($"不支持的 JSON 节点类型: {node.GetType().Name}");
            }
        }

        static void WriteObject(StringBuilder sb, JsonObject obj, int indent)
        {
            if (obj.Fields.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append("{\n");
            for (int i = 0; i < obj.Fields.Count; i++)
            {
                var (key, value) = obj.Fields[i];
                sb.Append(Indent(indent + 1));
                WriteString(sb, key);
                sb.Append(": ");
                WriteNode(sb, value, indent + 1);
                if (i < obj.Fields.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append(Indent(indent)).Append("}");
        }

        static void WriteArray(StringBuilder sb, List<object> list, int indent)
        {
            if (list.Count == 0)
            {
                sb.Append("[]");
                return;
            }

            sb.Append("[\n");
            for (int i = 0; i < list.Count; i++)
            {
                sb.Append(Indent(indent + 1));
                WriteNode(sb, list[i], indent + 1);
                if (i < list.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append(Indent(indent)).Append("]");
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            if (v == Math.Floor(v) && Math.Abs(v) < 1e15)
                return v.ToString("0", CultureInfo.InvariantCulture);
            return v.ToString("0.########", CultureInfo.InvariantCulture);
        }

        static string Indent(int level) => new(' ', level * 4);
    }
}
