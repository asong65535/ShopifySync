namespace BootstrapJob.Shopify.Models;

internal sealed record StagedUploadTarget(
    string Url,
    string ResourceUrl,
    IReadOnlyList<StagedUploadParameter> Parameters);

internal sealed record StagedUploadParameter(string Name, string Value);
