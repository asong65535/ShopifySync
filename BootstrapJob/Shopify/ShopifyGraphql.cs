namespace BootstrapJob.Shopify;

internal static class ShopifyGraphql
{
    public const string ListLocations = """
        {
          locations(first: 10) {
            edges {
              node {
                id
              }
            }
          }
        }
        """;

    public const string StagedUploadsCreate = """
        mutation stagedUploadsCreate($input: [StagedUploadInput!]!) {
          stagedUploadsCreate(input: $input) {
            stagedTargets {
              url
              resourceUrl
              parameters {
                name
                value
              }
            }
            userErrors {
              field
              message
            }
          }
        }
        """;

    public const string BulkOperationRunMutation = """
        mutation bulkOperationRunMutation($mutation: String!, $stagedUploadPath: String!) {
          bulkOperationRunMutation(mutation: $mutation, stagedUploadPath: $stagedUploadPath) {
            bulkOperation {
              id
              status
            }
            userErrors {
              field
              message
            }
          }
        }
        """;

    // Per-line mutation template — passed as the $mutation variable string.
    // Shopify replaces $input with each JSONL line's "input" object.
    // Uses productSet (2025-10) which accepts variants + inventoryQuantities inline.
    public const string ProductSetTemplate =
        "mutation productImport($input: ProductSetInput!) { " +
        "productSet(input: $input, synchronous: false) { " +
        "product { id variants(first: 1) { edges { node { id sku inventoryItem { id } } } } } " +
        "userErrors { field message } } }";

    // Bulk query to fetch all products with their variant SKU and inventory item ID.
    // Result JSONL uses parent/child format: product lines, then variant lines with __parentId.
    public const string BulkQueryProducts = """
        {
          products {
            edges {
              node {
                id
                variants(first: 1) {
                  edges {
                    node {
                      id
                      sku
                      inventoryItem {
                        id
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    public const string BulkOperationRunQuery = """
        mutation bulkOperationRunQuery($query: String!) {
          bulkOperationRunQuery(query: $query) {
            bulkOperation {
              id
              status
            }
            userErrors {
              field
              message
            }
          }
        }
        """;

    public const string PollBulkOperationById = """
        query pollBulkOp($id: ID!) {
          node(id: $id) {
            ... on BulkOperation {
              id
              status
              errorCode
              url
              objectCount
            }
          }
        }
        """;
}
