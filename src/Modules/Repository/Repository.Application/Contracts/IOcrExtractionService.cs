namespace SaaSApp.Repository.Application.Contracts;

public interface IOcrExtractionService
{
    Task<OcrExtractionResult> ExtractFromFileAsync(
        byte[] fileBytes,
        IReadOnlyList<string> parameters,
        IReadOnlyList<Dictionary<string, IReadOnlyList<string>>>? tableParameters = null,
        string? pageNo = null,
        string? ocrType = null,
        string? validateType = null,
        string? filename = null,
        Guid? repositoryId = null,
        CancellationToken cancellationToken = default);
}

public sealed record OcrExtractionResult(
    string RawJson,
    IReadOnlyList<UploadIndexFieldDto>? OcrFieldList,
    string? OcrText = null);
