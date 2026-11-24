using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace KompasTime;

internal static class ReportExcel
{
    private const string SheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static void Save(string path, AssemblyReport report)
    {
        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Выберите имя файла с расширением .xlsx.");

        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WritePart(archive, "[Content_Types].xml", """
                    <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                      <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                      <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                      <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                      <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                    </Types>
                    """);
                WritePart(archive, "_rels/.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                    </Relationships>
                    """);
                WritePart(archive, "xl/workbook.xml", """
                    <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                              xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                      <sheets><sheet name="Трудозатраты" sheetId="1" r:id="rId1"/></sheets>
                    </workbook>
                    """);
                WritePart(archive, "xl/_rels/workbook.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                      <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                    </Relationships>
                    """);
                WritePart(archive, "xl/styles.xml", """
                    <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                      <numFmts count="1"><numFmt numFmtId="164" formatCode="[hh]:mm:ss"/></numFmts>
                      <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
                      <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
                      <borders count="1"><border/></borders>
                      <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                      <cellXfs count="3">
                        <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
                        <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
                        <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
                      </cellXfs>
                      <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
                    </styleSheet>
                    """);
                WriteSheet(archive, report);
            }

            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void WriteSheet(ZipArchive archive, AssemblyReport report)
    {
        using var stream = archive.CreateEntry("xl/worksheets/sheet1.xml").Open();
        using var xml = XmlWriter.Create(stream);
        xml.WriteStartElement("worksheet", SheetNamespace);
        xml.WriteRaw($"""
            <sheetPr><outlinePr summaryBelow="0"/></sheetPr>
            <cols>
              <col min="1" max="1" width="60" customWidth="1"/>
              <col min="2" max="2" width="19" customWidth="1"/>
              <col min="3" max="3" width="{(report.IsFlat ? 70 : 12)}" customWidth="1"/>
            </cols>
            """);
        xml.WriteStartElement("sheetData", SheetNamespace);

        xml.WriteStartElement("row", SheetNamespace);
        Text(xml, "Файл", 2);
        Text(xml, "Трудозатраты", 2);
        Text(xml, report.IsFlat ? "Путь" : "Уровень", 2);
        xml.WriteEndElement();
        xml.WriteStartElement("row", SheetNamespace);
        Text(xml, report.Incomplete ? "Итого по доступным данным" : "Итого", 2);
        Duration(xml, report.TotalSeconds);
        xml.WriteEndElement();

        var pending = new Stack<(ReportNode Node, int Depth)>();
        if (report.IsFlat)
        {
            for (var index = report.Root.Children.Count - 1; index >= 0; index--)
                pending.Push((report.Root.Children[index], 0));
        }
        else
            pending.Push((report.Root, 0));
        var row = 2;
        while (pending.Count > 0)
        {
            if (++row > 1_048_576)
                throw new InvalidOperationException("Превышен предел строк одного листа Excel.");

            var (node, depth) = pending.Pop();
            xml.WriteStartElement("row", SheetNamespace);
            if (depth > 0)
                xml.WriteAttributeString("outlineLevel", Math.Min(depth, 7).ToString(CultureInfo.InvariantCulture));

            Text(xml, new string(' ', Math.Min(depth, 15) * 2) + Path.GetFileName(node.Path));
            if (node.Seconds is long seconds)
                Duration(xml, seconds);
            else
                Text(xml, node.Unavailable ? "недоступен" : "нет данных");
            if (report.IsFlat)
                Text(xml, node.Path);
            else
                Number(xml, depth.ToString(CultureInfo.InvariantCulture));
            xml.WriteEndElement();

            for (var index = node.Children.Count - 1; index >= 0; index--)
                pending.Push((node.Children[index], depth + 1));
        }
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void Text(XmlWriter xml, string value, int style = 0)
    {
        xml.WriteStartElement("c", SheetNamespace);
        if (style != 0)
            xml.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("t", "inlineStr");
        xml.WriteStartElement("is", SheetNamespace);
        xml.WriteStartElement("t", SheetNamespace);
        xml.WriteAttributeString("xml", "space", null, "preserve");
        xml.WriteString(value);
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void Duration(XmlWriter xml, long seconds) =>
        Number(xml, (seconds / 86400d).ToString("R", CultureInfo.InvariantCulture), 1);

    private static void Number(XmlWriter xml, string value, int style = 0)
    {
        xml.WriteStartElement("c", SheetNamespace);
        if (style != 0)
            xml.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        xml.WriteElementString("v", SheetNamespace, value);
        xml.WriteEndElement();
    }

    private static void WritePart(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
