using KeyboardSite.Entities.Entities;
using KeyboardSite.FileMetaDataProcessor.Models;

namespace WinUIMetadataScraper;

public sealed class PendingItem
{
    public ProgramExeMetaData Metadata { get; set; } = default!;
    public CustomFileData CustomData { get; set; } = default!;
    public string FilePath { get; set; } = string.Empty;
    public string FileName => System.IO.Path.GetFileName(FilePath);
}