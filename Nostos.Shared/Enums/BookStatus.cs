namespace Nostos.Shared.Enums;

public enum BookStatus
{
    Ready = 0,
    Downloading = 1,
    Transcoding = 2,
    Failed = 3,
    // A local-file book row exists, but its required file has not completed
    // upload yet. This is explicit lifecycle state: HasFile=false alone is
    // valid for metadata-only digital and physical books.
    UploadPending = 4,
}
