using DocPivot.Core.Excel;

namespace DocPivot.Core.Tests.Excel;

public sealed class ExcelWorksheetNameAllocatorTests
{
    [Fact]
    public void Allocate_PreservesOrderAndResolvesCaseInsensitiveConflicts()
    {
        var result = ExcelWorksheetNameAllocator.Allocate(["总览", "总览", "TOTAL", "total"]);

        Assert.Equal(["总览", "总览 (2)", "TOTAL", "total (2)"], result);
    }

    [Fact]
    public void Allocate_ReplacesInvalidCharactersAndResolvesSanitizedConflicts()
    {
        var result = ExcelWorksheetNameAllocator.Allocate(["A/B:C*D?E[F]", "a\\b:c*d?e[f]"]);

        Assert.Equal(["A_B_C_D_E_F_", "a_b_c_d_e_f_ (2)"], result);
    }

    [Fact]
    public void Allocate_TruncatesBaseBeforeAppendingSuffix()
    {
        var longName = new string('甲', 40);

        var result = ExcelWorksheetNameAllocator.Allocate([longName, longName]);

        Assert.Equal(new string('甲', 31), result[0]);
        Assert.Equal(new string('甲', 27) + " (2)", result[1]);
        Assert.All(result, name => Assert.InRange(name.Length, 1, 31));
    }

    [Fact]
    public void Sanitize_HandlesEmptyControlAndExcelReservedEdges()
    {
        Assert.Equal("工作表", ExcelWorksheetNameAllocator.Sanitize(null));
        Assert.Equal("工作表", ExcelWorksheetNameAllocator.Sanitize("   "));
        Assert.Equal("A_B", ExcelWorksheetNameAllocator.Sanitize("A\u0001B"));
        Assert.Equal("_引用_", ExcelWorksheetNameAllocator.Sanitize("'引用'"));
        Assert.Equal("History_", ExcelWorksheetNameAllocator.Sanitize("History"));
    }

    [Fact]
    public void Allocate_DoesNotSplitSurrogatePairWhenTruncating()
    {
        var proposed = new string('甲', 30) + "😀";

        var result = Assert.Single(ExcelWorksheetNameAllocator.Allocate([proposed]));

        Assert.Equal(new string('甲', 30), result);
        Assert.False(char.IsSurrogate(result[^1]));
    }
}
