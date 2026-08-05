using DocPivot.Core.Documents;

namespace DocPivot.App.Services;

public interface IFilePickerService
{
    IReadOnlyList<string> PickFiles(DocumentOperation operation);

    string? PickFolder(string initialDirectory);

    string? PickRenameFolder(string initialDirectory) => PickFolder(initialDirectory);
}
