using DocPivot.Infrastructure.Pdf;

namespace DocPivot.Infrastructure.Tests.Pdf;

public sealed class QpdfJsonInspectorTests
{
    [Fact]
    public void Parse_ReadsPagesEncryptionAndSignatureFields()
    {
        const string json = """
            {
              "pages": [{"pageposfrom1": 1}, {"pageposfrom1": 2}],
              "encrypt": {"encrypted": false},
              "acroform": {
                "fields": [
                  {"fieldtype": "/Tx"},
                  {"fieldtype": "/Sig"}
                ]
              }
            }
            """;

        var result = QpdfJsonInspector.Parse(json);

        Assert.Equal(2, result.PageCount);
        Assert.False(result.IsEncrypted);
        Assert.True(result.HasSignatureFields);
    }

    [Fact]
    public void Parse_AllowsMissingAcroFormAndReportsEncryption()
    {
        const string json = """
            {
              "pages": [{"pageposfrom1": 1}],
              "encrypt": {"encrypted": true}
            }
            """;

        var result = QpdfJsonInspector.Parse(json);

        Assert.Single(Enumerable.Range(1, result.PageCount));
        Assert.True(result.IsEncrypted);
        Assert.False(result.HasSignatureFields);
    }

    [Theory]
    [InlineData("{\"encrypt\":{\"encrypted\":false}}")]
    [InlineData("{\"pages\":[]}")]
    public void Parse_RejectsMissingRequiredStructuredFields(string json)
    {
        Assert.Throws<FormatException>(() => QpdfJsonInspector.Parse(json));
    }
}
