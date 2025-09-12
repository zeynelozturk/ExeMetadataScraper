using KeyboardSite.Entities.Entities;
using KeyboardSite.FileMetaDataProcessor.Models;
using KeyboardSite.Shared.Models;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static KeyboardSite.FileMetaDataProcessor.ExeFileMetaDataHelper;

namespace WinUIMetadataScraper;

// Add new DTOs here only.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DisplayNameDto))]
[JsonSerializable(typeof(LoginRequestDto))] // Register login request DTO
internal partial class AppJsonContext : JsonSerializerContext { }

internal sealed class DisplayNameDto
{
    public string? DisplayName { get; set; }
}

internal sealed class LoginRequestDto
{
    public required string Email { get; set; }
    public required string Password { get; set; }
}

internal static class JsonHelpers
{
    public static ValueTask<DisplayNameDto?> ReadDisplayNameAsync(Stream s) =>
        JsonSerializer.DeserializeAsync(s, AppJsonContext.Default.DisplayNameDto);
}


// Root DTO you actually POST (adjust if your API expects a different shape)
public sealed record UploadMetadataRequest(
    ProgramExeMetaData Metadata,
    CustomFileData CustomData
);

// If you already have a specific shape inside FileMetadataSerializer, you can drop this
// and instead annotate that type. Just ensure the root type you pass to Serialize has an entry below.
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
[JsonSerializable(typeof(UploadMetadataRequest))]
[JsonSerializable(typeof(ProgramExeMetaData))]
[JsonSerializable(typeof(CustomFileData))]
[JsonSerializable(typeof(ExeIconData))]
[JsonSerializable(typeof(ProgramExeSignatureInfo))]
// If ProgramExeMetaData.Icons is List<ExeIconInfo>, include it:
[JsonSerializable(typeof(List<ExeIconInfo>))]
[JsonSerializable(typeof(List<ExeIconData>))]
public partial class UploadJsonContext : JsonSerializerContext
{
}