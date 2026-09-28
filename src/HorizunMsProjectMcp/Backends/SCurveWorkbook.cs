using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using Horizun.ProjectMcp.Tools;

namespace Horizun.ProjectMcp.Backends;

/// <summary>
/// Writes the S-curve as an Excel workbook: the table of cumulative PV, EV, AC and forecast by
/// period, and a native Excel line chart over it — the chart a report is built from, which had to be
/// made by hand afterwards.
/// </summary>
/// <remarks>
/// Written as plain Office Open XML, so there is no spreadsheet library to ship. Excel draws the chart
/// from the cells, so a figure corrected in the sheet corrects the chart too.
/// </remarks>
public static class SCurveWorkbook
{
    public static int Write(TimephasedResult curve, string path, string title)
    {
        var curves = curve.Curves ?? new Dictionary<string, IReadOnlyList<TimephasedBucket>>();
        var order = new[] { "pv", "ev", "ac", "forecast" }.Where(curves.ContainsKey).ToList();
        var headers = new Dictionary<string, string>
        {
            ["pv"] = "PV (planned)", ["ev"] = "EV (earned)", ["ac"] = "AC (actual cost)", ["forecast"] = "Forecast (to date + remaining)",
        };
        var periods = order.SelectMany(k => curves[k].Select(b => b.Period)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        var values = order.ToDictionary(k => k, k => curves[k].ToDictionary(b => b.Period, b => b.Value));

        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
            .Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ")
            .Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">")
            .Append("<cols><col min=\"1\" max=\"1\" width=\"12\" customWidth=\"1\"/>")
            .Append($"<col min=\"2\" max=\"{order.Count + 1}\" width=\"22\" customWidth=\"1\"/></cols><sheetData>");
        sheet.Append("<row r=\"1\">").Append(Text("A1", "Period"));
        for (var c = 0; c < order.Count; c++)
        {
            sheet.Append(Text($"{Column(c + 1)}1", headers[order[c]]));
        }

        sheet.Append("</row>");
        for (var r = 0; r < periods.Count; r++)
        {
            var row = r + 2;
            sheet.Append($"<row r=\"{row}\">").Append(Text($"A{row}", periods[r]));
            for (var c = 0; c < order.Count; c++)
            {
                // A curve that stops at the status date leaves its later cells empty, so the line
                // stops there rather than dropping to zero.
                if (values[order[c]].TryGetValue(periods[r], out var v))
                {
                    sheet.Append($"<c r=\"{Column(c + 1)}{row}\"><v>{v.ToString(CultureInfo.InvariantCulture)}</v></c>");
                }
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData><drawing r:id=\"rId1\"/></worksheet>");

        var last = periods.Count + 1;
        var series = new StringBuilder();
        for (var c = 0; c < order.Count; c++)
        {
            var col = Column(c + 1);
            series.Append($"<c:ser><c:idx val=\"{c}\"/><c:order val=\"{c}\"/>")
                .Append($"<c:tx><c:strRef><c:f>'S-curve'!${col}$1</c:f></c:strRef></c:tx>")
                .Append("<c:marker><c:symbol val=\"none\"/></c:marker>")
                .Append($"<c:cat><c:strRef><c:f>'S-curve'!$A$2:$A${last}</c:f></c:strRef></c:cat>")
                .Append($"<c:val><c:numRef><c:f>'S-curve'!${col}$2:${col}${last}</c:f></c:numRef></c:val>")
                .Append("<c:smooth val=\"0\"/></c:ser>");
        }

        var chart = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                    + "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" "
                    + "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" "
                    + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                    + "<c:chart><c:title><c:tx><c:rich><a:bodyPr/><a:p><a:r><a:t>" + SecurityElement.Escape(title)
                    + "</a:t></a:r></a:p></c:rich></c:tx><c:overlay val=\"0\"/></c:title><c:autoTitleDeleted val=\"0\"/>"
                    + "<c:plotArea><c:layout/><c:lineChart><c:grouping val=\"standard\"/><c:varyColors val=\"0\"/>"
                    + series + "<c:marker val=\"1\"/><c:axId val=\"1001\"/><c:axId val=\"1002\"/></c:lineChart>"
                    + "<c:catAx><c:axId val=\"1001\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling>"
                    + "<c:delete val=\"0\"/><c:axPos val=\"b\"/><c:tickLblPos val=\"nextTo\"/><c:crossAx val=\"1002\"/>"
                    + "<c:crosses val=\"autoZero\"/><c:auto val=\"1\"/><c:lblAlgn val=\"ctr\"/><c:lblOffset val=\"100\"/></c:catAx>"
                    + "<c:valAx><c:axId val=\"1002\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling>"
                    + "<c:delete val=\"0\"/><c:axPos val=\"l\"/><c:majorGridlines/><c:numFmt formatCode=\"#,##0\" sourceLinked=\"0\"/>"
                    + "<c:tickLblPos val=\"nextTo\"/><c:crossAx val=\"1001\"/><c:crosses val=\"autoZero\"/>"
                    + "<c:crossBetween val=\"between\"/></c:valAx></c:plotArea>"
                    + "<c:legend><c:legendPos val=\"b\"/><c:overlay val=\"0\"/></c:legend><c:plotVisOnly val=\"1\"/>"
                    + "<c:dispBlanksAs val=\"gap\"/></c:chart></c:chartSpace>";

        var drawing = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                      + "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" "
                      + "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">"
                      + $"<xdr:twoCellAnchor><xdr:from><xdr:col>{order.Count + 2}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>"
                      + $"<xdr:to><xdr:col>{order.Count + 14}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>26</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>"
                      + "<xdr:graphicFrame macro=\"\"><xdr:nvGraphicFramePr><xdr:cNvPr id=\"2\" name=\"S-curve\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr>"
                      + "<xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm><a:graphic>"
                      + "<a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\">"
                      + "<c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" "
                      + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rId1\"/>"
                      + "</a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:twoCellAnchor></xdr:wsDr>";

        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
                + "<Override PartName=\"/xl/drawings/drawing1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>"
                + "<Override PartName=\"/xl/charts/chart1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawingml.chart+xml\"/>"
                + "</Types>",
            ["_rels/.rels"] = Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml")),
            ["xl/workbook.xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + "<sheets><sheet name=\"S-curve\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet1.xml")),
            ["xl/worksheets/sheet1.xml"] = sheet.ToString(),
            ["xl/worksheets/_rels/sheet1.xml.rels"] = Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing", "../drawings/drawing1.xml")),
            ["xl/drawings/drawing1.xml"] = drawing,
            ["xl/drawings/_rels/drawing1.xml.rels"] = Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart", "../charts/chart1.xml")),
            ["xl/charts/chart1.xml"] = chart,
        };

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        return periods.Count;
    }

    private static string Rels(params (string Id, string Type, string Target)[] relations) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
        + string.Concat(relations.Select(r => $"<Relationship Id=\"{r.Id}\" Type=\"{r.Type}\" Target=\"{r.Target}\"/>"))
        + "</Relationships>";

    private static string Text(string cell, string value) =>
        $"<c r=\"{cell}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(value)}</t></is></c>";

    private static string Column(int index) => ((char)('A' + index)).ToString();
}
