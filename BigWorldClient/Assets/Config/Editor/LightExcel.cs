using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace BigWorldClient
{

    internal static class LightExcel
    {
        static readonly XNamespace MainNs =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        static readonly XNamespace RelNs =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        static readonly XNamespace PkgRelNs =
            "http://schemas.openxmlformats.org/package/2006/relationships";
        static readonly XNamespace StrictMainNs =
            "http://purl.oclc.org/ooxml/spreadsheetml/main";

        public static Dictionary<string, string> ReadSheetCells(string xlsxPath, string sheetName)
        {
            if (string.IsNullOrEmpty(xlsxPath)) throw new ArgumentNullException(nameof(xlsxPath));
            if (!File.Exists(xlsxPath)) throw new FileNotFoundException("找不到 Excel 文件", xlsxPath);

            using (var zip = ZipFile.OpenRead(xlsxPath))
            {

                var workbookDoc = LoadXml(zip, "xl/workbook.xml");
                if (workbookDoc.Root == null) throw new InvalidDataException("xl/workbook.xml 为空");
                if (workbookDoc.Root.Name.Namespace == StrictMainNs)
                    throw new InvalidDataException(
                        "不支持 Strict Open XML 格式的 .xlsx。请在 Excel 里“另存为”普通 .xlsx（默认格式）后重试");

                string relationId = null;
                var sheets = workbookDoc.Root
                    .Elements(MainNs + "sheets")
                    .Elements(MainNs + "sheet");
                foreach (var sheet in sheets)
                {
                    var name = (string)sheet.Attribute("name");
                    if (string.Equals(name, sheetName, StringComparison.OrdinalIgnoreCase))
                    {
                        relationId = (string)sheet.Attribute(RelNs + "id")
                                     ?? (string)sheet.Attribute("id");
                        break;
                    }
                }

                if (relationId == null)
                    throw new InvalidDataException($"工作簿里找不到名为 \"{sheetName}\" 的工作表");

                var relsDoc = LoadXml(zip, "xl/_rels/workbook.xml.rels");
                string target = null;
                var rels = relsDoc.Root.Elements(PkgRelNs + "Relationship");
                foreach (var rel in rels)
                {
                    if (string.Equals((string)rel.Attribute("Id"), relationId, StringComparison.Ordinal))
                    {
                        target = (string)rel.Attribute("Target");
                        break;
                    }
                }

                if (target == null)
                    throw new InvalidDataException($"找不到工作表 \"{sheetName}\" 的 Relationship 目标");

                target = target.Replace('\\', '/').TrimStart('/');
                if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                    target = "xl/" + target;

                var sharedStrings = ReadSharedStrings(zip);
                var sheetDoc = LoadXml(zip, target);
                return ReadCells(sheetDoc, sharedStrings);
            }
        }

        static XDocument LoadXml(ZipArchive zip, string entryName)
        {
            var entry = FindEntry(zip, entryName);
            if (entry == null)
                throw new InvalidDataException($"xlsx 包中缺少 {entryName}");

            using (var stream = entry.Open())
            {
                return XDocument.Load(stream);
            }
        }

        static ZipArchiveEntry FindEntry(ZipArchive zip, string entryName)
        {
            var normalized = entryName.Replace('\\', '/').TrimStart('/');
            var entry = zip.GetEntry(normalized);
            if (entry != null) return entry;
            entry = zip.GetEntry("/" + normalized);
            return entry;
        }

        static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var result = new List<string>();
            var entry = FindEntry(zip, "xl/sharedStrings.xml");
            if (entry == null) return result;

            using (var stream = entry.Open())
            {
                var doc = XDocument.Load(stream);
                var root = doc.Root;
                if (root == null) return result;

                foreach (var si in root.Elements(MainNs + "si"))
                {
                    string text = "";
                    foreach (var t in si.Descendants(MainNs + "t"))
                    {

                        if (t.Parent != null && t.Parent.Name == MainNs + "rPh") continue;
                        text += t.Value;
                    }
                    result.Add(text);
                }
            }

            return result;
        }

        static Dictionary<string, string> ReadCells(XDocument sheetDoc, List<string> sharedStrings)
        {
            var result = new Dictionary<string, string>();
            var root = sheetDoc.Root;
            if (root == null) return result;

            var sheetData = root.Elements(MainNs + "sheetData").FirstOrDefault();
            if (sheetData == null) return result;

            int fallbackRow = 0;
            foreach (var row in sheetData.Elements(MainNs + "row"))
            {
                fallbackRow++;
                int rowIndex = ParsePositiveInt((string)row.Attribute("r"), fallbackRow);
                int fallbackCol = 0;

                foreach (var cell in row.Elements(MainNs + "c"))
                {
                    fallbackCol++;
                    int colIndex = ParseColumnReference((string)cell.Attribute("r"), fallbackCol);
                    string value = CellToString(cell, sharedStrings);
                    result[$"{rowIndex}:{colIndex}"] = value;
                }
            }

            return result;
        }

        static string CellToString(XElement cell, List<string> sharedStrings)
        {
            string cellRef = (string)cell.Attribute("r");
            string type = (string)cell.Attribute("t");
            XElement v = cell.Element(MainNs + "v");

            if (type == "s")
            {
                if (v == null) return "";
                if (!int.TryParse(v.Value, out int index) || index < 0 || index >= sharedStrings.Count)
                    throw new InvalidDataException($"单元格 {cellRef} 的共享字符串索引无效: {v.Value}");
                return sharedStrings[index];
            }

            if (type == "inlineStr")
            {
                var inline = cell.Element(MainNs + "is");
                return inline == null ? "" : string.Concat(inline.Descendants(MainNs + "t").Select(t => t.Value));
            }

            if (type == "b")
            {
                return v != null && (v.Value == "1" || string.Equals(v.Value, "true", StringComparison.OrdinalIgnoreCase))
                    ? "TRUE"
                    : "FALSE";
            }

            if (type == "e")
                throw new InvalidDataException($"单元格 {cellRef} 是错误单元格");

            if (cell.Element(MainNs + "f") != null && v == null)
                throw new InvalidDataException($"单元格 {cellRef} 是公式但没有缓存值。请粘贴为值或重新保存 Excel 后重试");

            return v == null ? "" : v.Value;
        }

        static int ParseColumnReference(string cellRef, int fallback)
        {
            if (string.IsNullOrEmpty(cellRef)) return fallback;

            string letters = "";
            foreach (char c in cellRef)
            {
                if (c == '$') continue;
                if (char.IsLetter(c)) letters += c;
                else break;
            }

            if (letters.Length == 0) return fallback;

            int col = 0;
            foreach (char c in letters.ToUpperInvariant())
                col = col * 26 + (c - 'A' + 1);
            return col;
        }

        static int ParsePositiveInt(string value, int fallback)
        {
            return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
        }
    }
}
