using DocPivot.Core.Renaming;

namespace DocPivot.Core.Tests.Renaming;

public sealed class BatchRenamePlannerTests
{
    [Fact]
    public void Create_AppliesCaseInsensitivePlainReplaceToAllMatches()
    {
        var plan = CreatePlan(
            [Source("Quarter-QUARTER.pdf", 0)],
            BatchRenameRuleSet.Default with
            {
                SearchReplace = new RenameSearchReplaceRule(
                    "quarter",
                    "month",
                    MatchCase: false,
                    ReplaceAll: true),
            });

        var item = Assert.Single(plan.Items);
        Assert.Equal("month-month.pdf", item.ProposedFileName);
        Assert.Equal(RenamePlanItemStatus.Ready, item.Status);
    }

    [Fact]
    public void Create_AppliesFirstRegexCaptureReplacement()
    {
        var plan = CreatePlan(
            [Source("2026-08-report-2026.pdf", 0)],
            BatchRenameRuleSet.Default with
            {
                SearchReplace = new RenameSearchReplaceRule(
                    @"(\d{4})-(\d{2})",
                    "$2_$1",
                    MatchCase: true,
                    ReplaceAll: false,
                    UseRegularExpression: true),
            });

        Assert.Equal("08_2026-report-2026.pdf", Assert.Single(plan.Items).ProposedFileName);
    }

    [Fact]
    public void Create_InsertsAtVisibleCharacterIndexWithoutSplittingEmoji()
    {
        var plan = CreatePlan(
            [Source("A😀B.pdf", 0)],
            BatchRenameRuleSet.Default with
            {
                Insertion = new RenameInsertionRule(
                    "-",
                    RenameInsertPosition.SpecifiedIndex,
                    Index: 2),
            });

        Assert.Equal("A😀-B.pdf", Assert.Single(plan.Items).ProposedFileName);
    }

    [Fact]
    public void Create_ExpandsTokensAndInsertsFromEnd()
    {
        var source = Source("report.pdf", 0) with
        {
            FullPath = Path.Combine("C:\\archive\\north", "report.pdf"),
            CreationTimeUtc = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc),
        };
        var plan = CreatePlan(
            [source],
            BatchRenameRuleSet.Default with
            {
                Insertion = new RenameInsertionRule(
                    "_{parent}_{created:yyyyMMdd}_{index}",
                    RenameInsertPosition.End),
            });

        Assert.Equal("report_north_20260803_1.pdf", Assert.Single(plan.Items).ProposedFileName);
    }

    [Fact]
    public void Create_NumberingUsesFrozenFileNameSortStartStepAndPadding()
    {
        var plan = CreatePlan(
            [Source("z.pdf", 0), Source("a.pdf", 1), Source("m.pdf", 2)],
            BatchRenameRuleSet.Default with
            {
                SortMode = RenameSortMode.FileName,
                Numbering = new RenameNumberingRule(
                    IsEnabled: true,
                    Start: 5,
                    Step: 5,
                    PaddingWidth: 3,
                    Prefix: "[",
                    Suffix: "]-",
                    Position: RenameInsertPosition.Beginning),
            });

        Assert.Equal(
            ["[005]-a.pdf", "[010]-m.pdf", "[015]-z.pdf"],
            plan.Items.Select(static item => item.ProposedFileName));
    }

    [Fact]
    public void Create_ManualOverrideWinsWithoutChangingOtherCalculatedNames()
    {
        var first = Source("one.pdf", 0);
        var second = Source("two.pdf", 1);
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [first.FullPath] = "manual.pdf",
        };
        var plan = CreatePlan(
            [first, second],
            BatchRenameRuleSet.Default with
            {
                Insertion = new RenameInsertionRule("new-", RenameInsertPosition.Beginning),
            },
            overrides);

        Assert.Equal("manual.pdf", plan.Items[0].ProposedFileName);
        Assert.True(plan.Items[0].HasManualOverride);
        Assert.Equal("new-two.pdf", plan.Items[1].ProposedFileName);
        Assert.False(plan.Items[1].HasManualOverride);
    }

    [Theory]
    [InlineData("CON.pdf", "RENAME_NAME_RESERVED")]
    [InlineData("bad?.pdf", "RENAME_NAME_INVALID_CHARACTER")]
    [InlineData("trailing .pdf ", "RENAME_NAME_TRAILING_CHARACTER")]
    [InlineData("", "RENAME_NAME_EMPTY")]
    public void Create_RejectsUnsafeWindowsNames(string manualName, string expectedCode)
    {
        var source = Source("source.pdf", 0);
        var plan = CreatePlan(
            [source],
            BatchRenameRuleSet.Default,
            new Dictionary<string, string> { [source.FullPath] = manualName });

        var item = Assert.Single(plan.Items);
        Assert.Equal(RenamePlanItemStatus.Invalid, item.Status);
        Assert.Equal(expectedCode, item.ErrorCode);
        Assert.False(plan.CanExecute);
    }

    [Fact]
    public void Create_ProtectsExtensionUnlessExplicitlyEnabled()
    {
        var source = Source("source.pdf", 0);
        var manual = new Dictionary<string, string> { [source.FullPath] = "source.xlsx" };

        var protectedPlan = CreatePlan([source], BatchRenameRuleSet.Default, manual);
        var enabledPlan = CreatePlan(
            [source],
            BatchRenameRuleSet.Default with { IncludeExtension = true },
            manual);

        Assert.Equal("RENAME_EXTENSION_PROTECTED", Assert.Single(protectedPlan.Items).ErrorCode);
        Assert.Equal(RenamePlanItemStatus.Ready, Assert.Single(enabledPlan.Items).Status);
    }

    [Fact]
    public void Create_DetectsDuplicateAndExistingTargetsButAllowsSwapAndCaseOnlyRename()
    {
        var one = Source("one.pdf", 0);
        var two = Source("two.pdf", 1);
        var duplicateOverrides = new Dictionary<string, string>
        {
            [one.FullPath] = "same.pdf",
            [two.FullPath] = "SAME.pdf",
        };
        var duplicate = CreatePlan([one, two], BatchRenameRuleSet.Default, duplicateOverrides);
        Assert.All(duplicate.Items, static item =>
        {
            Assert.Equal(RenamePlanItemStatus.Conflict, item.Status);
            Assert.Equal("RENAME_TARGET_DUPLICATE", item.ErrorCode);
        });

        var existing = CreatePlan(
            [one],
            BatchRenameRuleSet.Default,
            new Dictionary<string, string> { [one.FullPath] = "taken.pdf" },
            path => path.EndsWith("taken.pdf", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("RENAME_TARGET_EXISTS", Assert.Single(existing.Items).ErrorCode);

        var swap = CreatePlan(
            [one, two],
            BatchRenameRuleSet.Default,
            new Dictionary<string, string>
            {
                [one.FullPath] = "two.pdf",
                [two.FullPath] = "one.pdf",
            },
            static _ => true);
        Assert.True(swap.CanExecute);

        var caseOnly = CreatePlan(
            [one],
            BatchRenameRuleSet.Default,
            new Dictionary<string, string> { [one.FullPath] = "ONE.pdf" },
            static _ => true);
        Assert.True(caseOnly.CanExecute);
    }

    [Fact]
    public void Create_RejectsUnknownTokenAndOutOfRangePosition()
    {
        var unknown = CreatePlan(
            [Source("source.pdf", 0)],
            BatchRenameRuleSet.Default with
            {
                Insertion = new RenameInsertionRule("{unknown}"),
            });
        Assert.Equal("RENAME_TOKEN_UNKNOWN", Assert.Single(unknown.Items).ErrorCode);

        var outOfRange = CreatePlan(
            [Source("source.pdf", 0)],
            BatchRenameRuleSet.Default with
            {
                Insertion = new RenameInsertionRule(
                    "x",
                    RenameInsertPosition.SpecifiedIndex,
                    Index: 999),
            });
        Assert.Equal("RENAME_POSITION_OUT_OF_RANGE", Assert.Single(outOfRange.Items).ErrorCode);
    }

    private static BatchRenamePlan CreatePlan(
        IReadOnlyList<RenameSourceFile> sources,
        BatchRenameRuleSet rules,
        IReadOnlyDictionary<string, string>? manualOverrides = null,
        Func<string, bool>? pathExists = null) =>
        BatchRenamePlanner.Create(sources, rules, manualOverrides, pathExists);

    private static RenameSourceFile Source(string fileName, int addedOrder)
    {
        var path = Path.Combine("C:\\rename-tests", fileName);
        return new RenameSourceFile(
            path,
            fileName,
            128,
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(addedOrder),
            new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc).AddDays(addedOrder),
            addedOrder);
    }
}
