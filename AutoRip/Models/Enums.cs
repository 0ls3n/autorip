namespace AutoRip.Models;

public enum RipStatus
{
    Ripping,
    QueuedForProcessing,
    Transcoding,
    ExtractingSubtitles,
    Transferring,
    Completed,
    Failed
}

public enum TransferMode
{
    None,
    Sftp,
    LocalCopy,
    Both
}
