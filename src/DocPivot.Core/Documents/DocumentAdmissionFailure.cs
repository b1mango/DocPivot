namespace DocPivot.Core.Documents;

public enum DocumentAdmissionFailure
{
    None,
    FileDoesNotExist,
    UnsupportedExtension,
    FileTooLarge,
    BatchLimitReached,
}

