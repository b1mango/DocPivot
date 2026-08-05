using DocPivot.Core.Excel;

namespace DocPivot.Core.Tests.Excel;

public sealed class WindowsOutputNamePolicyTests
{
    [Fact]
    public void Allocate_ReplacesInvalidAndControlCharactersAndTrimsTrailingDotsAndSpaces()
    {
        var result = WindowsOutputNamePolicy.Allocate(["报告<>:\"/\\|?*\u0001. "]);

        Assert.Equal("报告__________.xlsx", Assert.Single(result));
    }

    [Theory]
    [InlineData("CON", "CON_.xlsx")]
    [InlineData("prn", "prn_.xlsx")]
    [InlineData("AUX.report", "AUX.report_.xlsx")]
    [InlineData("com1", "com1_.xlsx")]
    [InlineData("LPT9", "LPT9_.xlsx")]
    public void Allocate_AvoidsReservedDeviceNames(string input, string expected)
    {
        Assert.Equal(expected, Assert.Single(WindowsOutputNamePolicy.Allocate([input])));
    }

    [Fact]
    public void Allocate_UsesFallbackForEmptyNames()
    {
        var result = WindowsOutputNamePolicy.Allocate([null, " ", "."]);

        Assert.Equal(["工作表.xlsx", "工作表 (2).xlsx", "工作表 (3).xlsx"], result);
    }

    [Fact]
    public void Allocate_ResolvesCaseInsensitiveAndSanitizedConflictsInOrder()
    {
        var result = WindowsOutputNamePolicy.Allocate(["A/B", "a\\b", "A_B"]);

        Assert.Equal(["A_B.xlsx", "a_b (2).xlsx", "A_B (3).xlsx"], result);
    }

    [Fact]
    public void Allocate_NormalizesExtension()
    {
        Assert.Equal("总览.xls", Assert.Single(WindowsOutputNamePolicy.Allocate(["总览"], "xls")));
        Assert.Throws<ArgumentException>(() => WindowsOutputNamePolicy.Allocate(["总览"], "bad/ext"));
    }
}
