namespace SyncJob.Shopify;

internal static class ShopifyGraphql
{
    /// <summary>
    /// inventorySetQuantities with compareQuantity per item (compare-and-set pass).
    /// Variables: { input: InventorySetQuantitiesInput }
    /// </summary>
    public const string InventorySetQuantitiesWithCompare = """
        mutation inventorySetQuantities($input: InventorySetQuantitiesInput!) {
          inventorySetQuantities(input: $input) {
            inventoryAdjustmentGroup {
              id
            }
            userErrors {
              code
              field
              message
            }
          }
        }
        """;

}
