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

    /// <summary>
    /// Batch query for current inventory levels via the nodes query.
    /// Variables: { ids: [ID!]!, locationId: ID! }
    /// Returns up to 250 InventoryItems per call with their available quantity.
    /// </summary>
    public const string QueryInventoryLevels = """
        query queryInventoryLevels($ids: [ID!]!, $locationId: ID!) {
          nodes(ids: $ids) {
            ... on InventoryItem {
              id
              inventoryLevel(locationId: $locationId) {
                quantities(names: ["available"]) {
                  name
                  quantity
                }
              }
            }
          }
        }
        """;
}
