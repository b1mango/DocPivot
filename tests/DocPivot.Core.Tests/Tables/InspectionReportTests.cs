using DocPivot.Core.Tables;

namespace DocPivot.Core.Tests.Tables;

public sealed class InspectionReportTests
{
    [Theory]
    [InlineData(DocumentTableSourceKind.DigitalText)]
    [InlineData(DocumentTableSourceKind.OpticalCharacterRecognition)]
    public void AnalyzePage_ExtractsReportFieldsFromPositionedWords(DocumentTableSourceKind source)
    {
        var page = TableLayoutAnalyzer.AnalyzePage(1, 600, 800, source,
        [
            Word("报 告", 20, 20), Word("编 号：", 75, 20), Word("LAB-007", 180, 20),
            Word("申请方名称:", 20, 40), Word("纸月试验厂", 180, 40),
            Word("样品测试周期：2031年8月 4日 ～ 2031年8月 9日", 20, 60),
            Word("授权签字人", 20, 100), Word("2031年8月 9日", 20, 116),
        ]);

        Assert.Equal("LAB-007", page.ReportFields["报告编号"]);
        Assert.Equal("纸月试验厂", page.ReportFields["申请方名称"]);
        Assert.Equal("2031年8月4日~2031年8月9日", page.ReportFields["样品测试周期"]);
        Assert.Equal("2031年8月9日", page.ReportFields["授权签字人"]);
        Assert.Null(InspectionReport.Detect([page]));
    }

    private static DocumentWord Word(string text, double left, double top) =>
        new(text, new DocumentBounds(left, top, left + 42, top + 10), 1);
}
