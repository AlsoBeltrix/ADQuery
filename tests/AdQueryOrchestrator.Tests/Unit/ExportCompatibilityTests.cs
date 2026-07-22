using System.Text;
using AdQuery.Orchestrator.Controllers;
using ClosedXML.Excel;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class ExportCompatibilityTests
{
    private static readonly IReadOnlyList<string> Headers =
    [
        "Name",
        "Enabled",
        "Count"
    ];

    private static readonly IReadOnlyList<Dictionary<string, object?>> Rows =
    [
        new()
        {
            ["Name"] = "Ada, \"Countess\"",
            ["Enabled"] = true,
            ["Count"] = 42
        },
        new()
        {
            ["Name"] = "Grace Hopper",
            ["Enabled"] = false,
            ["Count"] = 7
        }
    ];

    [Fact]
    public void CsvExport_ContainsExpectedHeadersAndRows()
    {
        var bytes = QueryController.GenerateFileContent(Rows, Headers, "csv");

        using var reader = new StringReader(Encoding.UTF8.GetString(bytes));
        Assert.Equal("Name,Enabled,Count", reader.ReadLine());
        Assert.Equal("\"Ada, \"\"Countess\"\"\",True,42", reader.ReadLine());
        Assert.Equal("Grace Hopper,False,7", reader.ReadLine());
        Assert.Null(reader.ReadLine());
    }

    [Fact]
    public void ExcelExport_OpensWithExpectedWorksheetHeadersAndRows()
    {
        var bytes = QueryController.GenerateFileContent(Rows, Headers, "excel");

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheet("Data");

        Assert.Equal("Name", worksheet.Cell(1, 1).GetString());
        Assert.Equal("Enabled", worksheet.Cell(1, 2).GetString());
        Assert.Equal("Count", worksheet.Cell(1, 3).GetString());
        Assert.Equal("Ada, \"Countess\"", worksheet.Cell(2, 1).GetString());
        Assert.Equal("True", worksheet.Cell(2, 2).GetString());
        Assert.Equal(42, worksheet.Cell(2, 3).GetDouble());
        Assert.Equal("Grace Hopper", worksheet.Cell(3, 1).GetString());
        Assert.Equal("False", worksheet.Cell(3, 2).GetString());
        Assert.Equal(7, worksheet.Cell(3, 3).GetDouble());
        Assert.True(worksheet.Cell(4, 1).IsEmpty());
    }
}
