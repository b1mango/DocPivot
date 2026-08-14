namespace DocPivot.OfficeWorker;

internal static class WordPdfExporter
{
    public static void Export(
        string inputPath,
        string outputPath,
        Action<int> reportProcessId,
        Action<string> reportStage)
    {
        dynamic? application = null;
        dynamic? options = null;
        dynamic? documents = null;
        dynamic? identityDocument = null;
        dynamic? identityWindow = null;
        dynamic? document = null;

        try
        {
            var applicationType = Type.GetTypeFromProgID("Word.Application")
                ?? throw new OfficeWorkerException("WORD_NOT_INSTALLED", "Microsoft Word is not available.");
            application = Activator.CreateInstance(applicationType)
                ?? throw new OfficeWorkerException("WORD_START_FAILED", "Microsoft Word could not be started.", true);
            reportStage("word-created");

            // Hide the application before creating any document so the main
            // frame never flashes on the user's screen.
            ComObject.SetProperty((object)application, "Visible", false);
            reportStage("word-hidden");
            ComObject.SetProperty((object)application, "DisplayAlerts", 0);
            reportStage("word-alerts-configured");
            ComObject.SetProperty((object)application, "AutomationSecurity", 3);
            reportStage("word-security-configured");
            ComObject.SetProperty((object)application, "ScreenUpdating", false);
            reportStage("word-configured");

            documents = application.Documents;
            identityDocument = documents.Add(Visible: false);
            identityWindow = identityDocument.ActiveWindow;
            var identityProcessId = WordWindowVisibility.TryGetWindowProcessId(identityWindow);
            if (identityProcessId > 0)
            {
                WordWindowVisibility.HideWindows(identityProcessId);
            }

            reportProcessId(identityProcessId);
            identityDocument.Close(SaveChanges: 0);
            ComObject.FinalRelease(identityWindow);
            ComObject.FinalRelease(identityDocument);
            identityWindow = null;
            identityDocument = null;
            reportStage("word-identity");

            options = application.Options;
            options.UpdateLinksAtOpen = false;
            options.UpdateLinksAtPrint = false;

            document = documents.Open(
                FileName: inputPath,
                ConfirmConversions: false,
                ReadOnly: true,
                AddToRecentFiles: false,
                Revert: false,
                Visible: false,
                OpenAndRepair: false,
                NoEncodingDialog: true);
            reportStage("document-opened");
            document.ExportAsFixedFormat(
                OutputFileName: outputPath,
                ExportFormat: 17,
                OpenAfterExport: false,
                OptimizeFor: 0,
                Range: 0,
                Item: 0,
                IncludeDocProps: true,
                KeepIRM: true,
                CreateBookmarks: 2,
                DocStructureTags: true,
                BitmapMissingFonts: true,
                UseISO19005_1: false);
            reportStage("pdf-exported");
        }
        finally
        {
            try
            {
                identityDocument?.Close(SaveChanges: 0);
            }
            catch
            {
                // The worker is disposable; preserve the original automation failure.
            }

            try
            {
                document?.Close(SaveChanges: 0);
            }
            catch
            {
                // The worker process is disposable; preserve the original export failure.
            }

            try
            {
                application?.Quit(SaveChanges: 0);
            }
            catch
            {
                // The parent process owns timeout and process-tree cleanup.
            }

            ComObject.FinalRelease(document);
            ComObject.FinalRelease(identityWindow);
            ComObject.FinalRelease(identityDocument);
            ComObject.FinalRelease(documents);
            ComObject.FinalRelease(options);
            ComObject.FinalRelease(application);
        }
    }
}
