# ShopifySync

Bidirectional inventory sync between a **PCAmerica (CRE/RPE)** point-of-sale system and a **Shopify** storefront. PCA is always the source of truth in conflicts.

## What it does

- Detects inventory quantity changes on both PCA and Shopify using a three-way delta algorithm
- Pushes PCA changes to Shopify automatically on a configurable schedule
- Optionally pulls Shopify changes back to PCA (`BidirectionalSync:ShopifyToPcaEnabled`)
- Resolves conflicts by always deferring to PCA (PCA wins)
- Desktop UI with scheduler, manual "Sync Now", run history, and settings

## Requirements

- Windows 10 with PCAmerica CRE/RPE installed (SQL Server `.\PCAMERICA`)
- .NET 8 SDK
- Shopify store with a private app access token (Admin API 2025-10)

## Solution Structure

```
ShopifySync.sln
  ├── PcaData/               EF Core read-only access to CRELiquorStore + narrow write context
  ├── SyncData/              EF Core read/write for ShopifySync DB (owns migrations)
  ├── PcaData.TestDriver/    22-test smoke-test runner; applies migrations on startup
  ├── BootstrapJob/          One-time bulk import of PCA catalog into Shopify
  ├── SyncJob/               Bidirectional delta sync engine (class library)
  ├── SyncHistory/           JSON history file I/O (class library, no SyncJob reference)
  ├── SyncHistory.Tests/     xUnit tests for SyncHistory (4 tests)
  └── ShopifySyncApp/        Avalonia 11 MVVM desktop app
```

## Documentation

| File | Purpose |
|------|---------|
| [docs/ARCHITECTURE-GUIDE.md](docs/ARCHITECTURE-GUIDE.md) | System design and architecture document. Provides comprehensive background knowledge of project and quick lookup of functionality. |

## Quick Start

### 1. Configure secrets

Copy the template below into `appsettings.local.json` in each runnable project (`PcaData.TestDriver/`, `BootstrapJob/`, `ShopifySyncApp/`) and fill in real values:

```json
{
  "ConnectionStrings": {
    "PcAmerica": "Server=.\\PCAMERICA;Database=YOUR-PCA-DATABASE;Integrated Security=True;TrustServerCertificate=True;",
    "ShopifySync": "Server=.\\PCAMERICA;Database=ShopifySync;Integrated Security=True;TrustServerCertificate=True;"
  },
  "Shopify": {
    "StoreUrl": "your-store.myshopify.com",
    "AccessToken": "shpat_..."
  },
  "BidirectionalSync": {
    "ShopifyToPcaEnabled": false
  },
  "App": {
    "PollIntervalMinutes": 5,
    "AutoStartOnLaunch": false
  }
}
```

> **Do not** add `ApplicationIntent=ReadOnly` to the PcAmerica connection string — `AddPcAmericaDb` appends it automatically. Duplicating it breaks the connection.

Windows Authentication only — no SA password required. The app must run as a Windows admin who is a SQL Server sysadmin on `.\PCAMERICA`.

### 2. Apply migrations and run smoke tests

```bash
dotnet build
cd PcaData.TestDriver && dotnet run
```

This applies all `SyncData` migrations on first run, then runs 22 smoke tests against the live database.

### 3. Bootstrap (one-time only)

The Bootstrap job imports the full PCA catalog into Shopify and populates `ProductSyncMap`. Run it once:

```bash
cd BootstrapJob
dotnet run -- --list-locations   # find your Shopify location ID
dotnet run -- --dry-run          # preview JSONL, no API calls
dotnet run                       # full import
```

If products already exist in Shopify, use `--map-only` to populate `ProductSyncMap` by matching SKUs without creating products:

```bash
dotnet run -- --force --map-only
```

### 4. Run the desktop app

Run the executable ShopifySyncApp/ShopifySyncApp.exe
```bash
```

Use the Settings tab to configure the poll interval and Shopify credentials, then start the scheduler or click Sync Now.

## Sync Algorithm

Each sync run is an 8-step bidirectional delta:

| Step | What happens |
|------|-------------|
| 1 | Read `SyncState.LastPolledAt` (informational) |
| 2 | Load all active PCA items (`IsDeleted = false`) |
| 3 | Load `ProductSyncMap`; log items missing from map as warnings |
| 4 | Query Shopify inventory for all mapped items (batches of 250, 1 s rate-limit delay) |
| 5 | Three-way delta comparison per item (see below) |
| 6 | Push PCA→Shopify with `compareQuantity`; retry stale conflicts unconditionally |
| 7 | Pull Shopify→PCA — one `SaveChangesAsync` per item to isolate failures |
| 8 | Persist `LastKnownQty`, `LastSyncedAt` for all successes; upsert `SyncState` |

### Three-way delta

`ProductSyncMap.LastKnownQty` is the last value both systems agreed on.

```
pcaDelta    = (int)Truncate(InStock)   − (int)Truncate(LastKnownQty)
shopifyDelta = shopifyAvailable        − (int)Truncate(LastKnownQty)

pcaDelta ≠ 0, shopifyDelta = 0  → push to Shopify
pcaDelta = 0, shopifyDelta ≠ 0  → pull to PCA  (if ShopifyToPcaEnabled)
both ≠ 0                         → conflict: PCA wins, push to Shopify
both = 0                         → no change
```

## Configuration Reference

| Key | Default | Description |
|-----|---------|-------------|
| `ConnectionStrings:PcAmerica` | — | SQL Server connection to `CRELiquorStore` |
| `ConnectionStrings:ShopifySync` | — | SQL Server connection to `ShopifySync` |
| `Shopify:StoreUrl` | — | e.g. `your-store.myshopify.com` |
| `Shopify:AccessToken` | — | Admin API token (`shpat_...`) |
| `BidirectionalSync:ShopifyToPcaEnabled` | `false` | Enable Shopify→PCA writes |
| `App:PollIntervalMinutes` | `5` | Scheduler interval |
| `App:AutoStartOnLaunch` | `false` | Start scheduler automatically on app launch |

## Tests

```bash
# Integration smoke tests (requires live .\PCAMERICA SQL Server)
cd PcaData.TestDriver && dotnet run

# SyncHistory unit tests (no DB required)
dotnet test SyncHistory.Tests/
```

**22 smoke tests** in `PcaData.TestDriver` cover:
- PcaData connectivity, row/column mapping, save-guard, negative-stock warning
- SyncData migration apply, CRUD for all tables
- SyncJob: happy path, no-change, not-in-map, conflict retry, cancellation, error paths
- Bidirectional: Shopify→PCA write, config gate, conflict PCA-wins, query failure, PCA write failure, no-false-delta

All SyncJob tests (11–21) use fake `HttpMessageHandler` — no live Shopify connection needed.

Test 6 produces a `WARN` for 2 negative-stock items in the dev DB — expected, not a failure.
Test 13 is skipped on the VM when the production sync map is populated.

