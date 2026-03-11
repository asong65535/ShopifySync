namespace SyncJob.Shopify;

public sealed class ShopifyApiException : Exception
{
    public int? StatusCode { get; }

    public ShopifyApiException(string message) : base(message) { }

    public ShopifyApiException(string message, int statusCode)
        : base(message) => StatusCode = statusCode;
}
