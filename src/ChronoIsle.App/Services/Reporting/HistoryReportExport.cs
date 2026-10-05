using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace ChronoIsle.App.Services.Reporting;

public static class HistoryReportExport
{
    static IEnumerable<string[]> Rows(HistoryReport report)
    {
        yield return ["事项", "类型", "状态", "分类", "来源", "创建时间", "完成时间", "安排时间", "归档", "事项ID"];
        foreach (var r in report.Records)
            yield return [r.Title, r.KindLabel, r.StatusLabel, r.Category, r.Source, r.CreatedText, r.CompletedText, r.ScheduledText, r.Archived ? "是" : "否", r.Id];
    }

    public static string Csv(HistoryReport report) => string.Join("\r\n", Rows(report).Select(row => string.Join(",", row.Select(CsvCell)))) + "\r\n";
    static string CsvCell(string value)
    {
        // Spreadsheet applications execute formulas even inside quoted CSV cells.
        if (value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || value.FirstOrDefault() is '\t' or '\r' or '\n') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static void Xlsx(Stream output, HistoryReport report)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
        XNamespace docRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        void Put(string name, XElement xml) { using var stream = zip.CreateEntry(name).Open(); xml.Save(stream); }
        Put("[Content_Types].xml", new XElement(types + "Types",
            new XElement(types + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(types + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            new XElement(types + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
            new XElement(types + "Override", new XAttribute("PartName", "/xl/worksheets/sheet1.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))));
        Put("_rels/.rels", new XElement(rel + "Relationships", new XElement(rel + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", docRel.NamespaceName + "/officeDocument"), new XAttribute("Target", "xl/workbook.xml"))));
        Put("xl/workbook.xml", new XElement(s + "workbook", new XElement(s + "sheets", new XElement(s + "sheet", new XAttribute("name", "事项记录"), new XAttribute("sheetId", 1), new XAttribute(docRel + "id", "rId1")))));
        Put("xl/_rels/workbook.xml.rels", new XElement(rel + "Relationships", new XElement(rel + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", docRel.NamespaceName + "/worksheet"), new XAttribute("Target", "worksheets/sheet1.xml"))));
        Put("xl/worksheets/sheet1.xml", new XElement(s + "worksheet", new XElement(s + "sheetData",
            Rows(report).Select((row, i) => new XElement(s + "row", new XAttribute("r", i + 1), row.Select((v, j) => new XElement(s + "c",
                new XAttribute("r", $"{(char)('A' + j)}{i + 1}"), new XAttribute("t", "inlineStr"),
                new XElement(s + "is", new XElement(s + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), v)))))))));
    }
}
