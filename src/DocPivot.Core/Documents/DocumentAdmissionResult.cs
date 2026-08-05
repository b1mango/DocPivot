namespace DocPivot.Core.Documents;

public readonly record struct DocumentAdmissionResult(
    bool IsAccepted,
    DocumentAdmissionFailure Failure)
{
    public static DocumentAdmissionResult Accepted { get; } = new(true, DocumentAdmissionFailure.None);

    public static DocumentAdmissionResult Rejected(DocumentAdmissionFailure failure) => new(false, failure);
}

