using DocPivot.Core.Documents;
using Microsoft.Win32;
using System.IO;

namespace DocPivot.App.Services;

public sealed class WindowsFilePickerService : IFilePickerService
{
    public IReadOnlyList<string> PickFiles(DocumentOperation operation)
    {
        var extensions = DocumentAdmissionPolicy.GetSupportedExtensions(operation);
        var patterns = string.Join(';', extensions.Select(static extension => $"*{extension}"));
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = true,
            Filter = $"支持的文档 ({patterns})|{patterns}|所有文件 (*.*)|*.*",
            Title = "添加文档",
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public string? PickFolder(string initialDirectory)
        => PickFolder(initialDirectory, "选择输出目录");

    public string? PickRenameFolder(string initialDirectory)
        => PickFolder(initialDirectory, "添加待重命名文件夹");

    private static string? PickFolder(string initialDirectory, string title)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null,
            Multiselect = false,
            Title = title,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
