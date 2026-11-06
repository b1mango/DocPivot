using DocPivot.Core.Tables;

namespace DocPivot.Core.Tests.Tables;

public sealed class DocumentCellValueClassifierTests
{
    [Theory]
    [InlineData("1,234,567", DocumentCellValueKind.WholeNumber)]
    [InlineData("-12,345.60", DocumentCellValueKind.FractionalNumber)]
    [InlineData("0.125", DocumentCellValueKind.FractionalNumber)]
    [InlineData("1,250.5%", DocumentCellValueKind.Percentage)]
    [InlineData("2026.1.5", DocumentCellValueKind.Date)]
    [InlineData("2026/11/6", DocumentCellValueKind.Date)]
    [InlineData("2026-02-30", DocumentCellValueKind.Text)]
    [InlineData("1899-12-31", DocumentCellValueKind.Text)]
    [InlineData("20261106-1234", DocumentCellValueKind.Text)]
    [InlineData("000123", DocumentCellValueKind.Text)]
    [InlineData("01.50", DocumentCellValueKind.Text)]
    [InlineData("001%", DocumentCellValueKind.Text)]
    [InlineData("12,34", DocumentCellValueKind.Text)]
    [InlineData("1234567890123456", DocumentCellValueKind.Text)]
    [InlineData("1,234,567,890,123,456", DocumentCellValueKind.Text)]
    [InlineData("123456789012345.6", DocumentCellValueKind.Text)]
    [InlineData("=SUM(A1:A2)", DocumentCellValueKind.Text)]
    [InlineData("１２３", DocumentCellValueKind.Text)]
    public void Classify_PreservesNumbersAndLosslessIdentifiers(string text, DocumentCellValueKind expected)
    {
        Assert.Equal(expected, DocumentCellValueClassifier.Classify(text));
    }

    [Theory]
    [InlineData("合同编号")]
    [InlineData("合同号")]
    [InlineData("产品代码")]
    [InlineData("Account Number")]
    [InlineData("Contract No.")]
    [InlineData("Item ID")]
    [InlineData("Code")]
    [InlineData("Reference Number")]
    public void Classify_IdentifierColumnKeepsDigitsAsText(string header)
    {
        Assert.Equal(DocumentCellValueKind.Text, DocumentCellValueClassifier.Classify("12345678", header));
        Assert.Equal(DocumentCellValueKind.Text, DocumentCellValueClassifier.Classify("2026-11-06", header));
    }

    [Theory]
    [InlineData("Amount")]
    [InlineData("数量")]
    [InlineData("Number of items")]
    public void Classify_OrdinaryNumericColumnRemainsNumeric(string header)
    {
        Assert.Equal(DocumentCellValueKind.WholeNumber, DocumentCellValueClassifier.Classify("12345678", header));
    }
}
