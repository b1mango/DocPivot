namespace DocPivot.OfficeWorker;

internal static class ExcelCompressionRiskInspector
{
    public static IReadOnlyList<string> Inspect(object workbookValue)
    {
        dynamic workbook = workbookValue;
        var risks = new List<string>();
        dynamic? names = null;
        dynamic? charts = null;
        dynamic? connections = null;
        dynamic? worksheets = null;
        try
        {
            names = workbook.Names;
            if ((int)names.Count > 0)
            {
                risks.Add("工作簿级名称");
            }

            charts = workbook.Charts;
            if ((int)charts.Count > 0)
            {
                risks.Add("图表工作表");
            }

            connections = workbook.Connections;
            if ((int)connections.Count > 0)
            {
                risks.Add("外部数据连接");
            }

            worksheets = workbook.Worksheets;
            for (var index = 1; index <= (int)worksheets.Count; index++)
            {
                dynamic? worksheet = null;
                dynamic? pivotTables = null;
                dynamic? chartObjects = null;
                dynamic? queryTables = null;
                try
                {
                    worksheet = worksheets[index];
                    pivotTables = worksheet.PivotTables();
                    if ((int)pivotTables.Count > 0)
                    {
                        risks.Add("数据透视表");
                    }

                    chartObjects = worksheet.ChartObjects();
                    if ((int)chartObjects.Count > 0)
                    {
                        risks.Add("嵌入式图表");
                    }

                    queryTables = worksheet.QueryTables;
                    if ((int)queryTables.Count > 0)
                    {
                        risks.Add("查询表");
                    }
                }
                finally
                {
                    ComObject.FinalRelease(queryTables);
                    ComObject.FinalRelease(chartObjects);
                    ComObject.FinalRelease(pivotTables);
                    ComObject.FinalRelease(worksheet);
                }
            }

            return risks.Distinct(StringComparer.Ordinal).ToArray();
        }
        finally
        {
            ComObject.FinalRelease(worksheets);
            ComObject.FinalRelease(connections);
            ComObject.FinalRelease(charts);
            ComObject.FinalRelease(names);
        }
    }
}
