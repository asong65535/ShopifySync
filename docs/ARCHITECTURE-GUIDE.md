# ShopifySync Architecture Guide

This document explains how ShopifySync works — its structure, data flow, sync algorithm, and the design decisions behind them.

---

## Overview

ShopifySync keeps a Shopify storefront's inventory in sync with a PCAmerica (CRE/RPE) point-of-sale system. Sync is **bidirectional**: changes made in PCA push to Shopify, changes made in Shopify pull back to PCA, and when both sides change the same item, PCA always wins.

The system operates in three phases:

1. **Bootstrap** — A one-time bulk import that creates all PCA products in Shopify and records the mapping between the two systems.
2. **Delta Sync** — A periodic poll that detects quantity changes and pushes only what changed.
3. **Bidirectional Sync** — An extension of delta sync that also pulls Shopify changes back to PCA using a three-way comparison.

---

## Solution Layout

```
ShopifySync.sln
  PcaData/               Read-only database access to PCAmerica
  SyncData/              Read/write database for ShopifySync's own tables
  PcaData.TestDriver/    22-test smoke-test runner
  BootstrapJob/          One-time product import console app
  SyncJob/               Bidirectional delta sync engine
  SyncHistory/           JSON history file I/O
  SyncHistory.Tests/     Unit tests for SyncHistory
  ShopifySyncApp/        Avalonia 11 desktop app
```

### How they connect

```
PcaData (reads PCAmerica DB)
    │
    ├──► BootstrapJob ──► Shopify API (bulk product import)
    │         │
    │         ▼
    │    SyncData (writes ProductSyncMap linking table)
    │         │
    │         ▼
    └──► SyncJob ──► Shopify API (inventory updates)
              │
              ▼
         SyncHistory ──► JSON log files
              │
              ▼
         ShopifySyncApp (desktop UI)
```

---

## The Two Databases

ShopifySync talks to two SQL Server databases on the same instance (`.\PCAMERICA`):

### CRELiquorStore (PCAmerica's database)

This is the POS system's own database. ShopifySync treats it as **read-only** by default — the `PcAmericaDbContext` will throw an exception if you try to save changes. The only exception is `PcaWriteDbContext`, a deliberately narrow context that can update a single field (`In_Stock`) on a single table (`Inventory`) for bidirectional sync.

Key tables:
- **Inventory** — One row per product. `ItemNum` (string) is the primary key. `InStock` (decimal) is the quantity. `IsDeleted` is a soft-delete flag that must always be filtered.
- **Inventory_SKUS** — UPC barcodes. Composite key `(ItemNum, AltSku)` — there is no surrogate ID column.

### ShopifySync (our database)

Created and managed by EF Core migrations. Contains:

- **ProductSyncMap** — The heart of the system. Links each PCA item to its Shopify product, variant, and inventory item IDs. Also stores `LastKnownQty` — the last quantity both systems agreed on, which drives the delta algorithm.
- **SyncState** — A single-row table storing `LastPolledAt`. The ID is always 1 and must be set manually (EF won't auto-generate it).
- **SyncUnmatched** — Records PCA items that couldn't be matched during bootstrap. Not used during normal sync runs.

---

## Bootstrap: Getting Products Into Shopify

The bootstrap job is a one-time operation that imports the full PCA catalog into Shopify. It runs as a console app with several modes:

| Command | What it does |
|---------|-------------|
| `dotnet run` | Full import — create products in Shopify, populate ProductSyncMap |
| `dotnet run -- --dry-run` | Build the JSONL file but don't upload anything |
| `dotnet run -- --force` | Clear ProductSyncMap and re-import from scratch |
| `dotnet run -- --force --map-only` | Don't create products — just match existing Shopify products to PCA items by SKU |
| `dotnet run -- --list-locations` | Print Shopify location IDs and exit |

### How it works

1. Load all active PCA items (where `IsDeleted = false`)
2. Build a JSONL file with one line per product, formatted as Shopify `productSet` mutations
3. Upload the JSONL to Shopify via a staged upload (presigned URL)
4. Start a bulk operation and poll until it completes
5. Parse the result and insert `ProductSyncMap` rows linking PCA items to their new Shopify IDs

The `--map-only` mode skips steps 2–4 and instead runs a bulk **query** to read existing Shopify products, matching them to PCA items by SKU.

---

## Delta Sync: Keeping Inventory in Sync

Once bootstrap is complete, the sync engine runs periodically (or on demand) to detect and propagate inventory changes.

### The core idea: three-way delta

Simple two-way comparison ("is PCA different from what we last sent?") can't tell you *which side* changed. ShopifySync solves this with a three-way comparison using `LastKnownQty` as the common ancestor — the last value both systems agreed on.

On each sync run, the engine queries both PCA and Shopify for current quantities, then computes two deltas per item:

```
pcaDelta     = current PCA quantity  − LastKnownQty
shopifyDelta = current Shopify quantity − LastKnownQty
```

The combination determines what to do:

| PCA changed? | Shopify changed? | Action |
|:---:|:---:|---|
| Yes | No | Push PCA value to Shopify |
| No | Yes | Pull Shopify value to PCA (if enabled) |
| Yes | Yes | **Conflict** — PCA wins, push to Shopify |
| No | No | Nothing to do |

### The 8-step sync cycle

Each run of `SyncOrchestrator.RunAsync()` follows this sequence:

**Step 1** — Read `SyncState.LastPolledAt` (logged for diagnostics, not used for logic).

**Step 2** — Load all active PCA items from `CRELiquorStore`.

**Step 3** — Load all `ProductSyncMap` rows into a dictionary. PCA items with no mapping are logged as warnings and counted, but not synced — they need a re-bootstrap or manual match.

**Step 3c** — Query Shopify for current inventory levels of all mapped items. This uses `nodes` queries in batches of 250 with a 1-second delay between batches to respect rate limits. If this step fails, the entire sync cycle aborts.

**Step 4** — Run the three-way delta comparison for every mapped item.

**Step 5** — Push PCA changes to Shopify. Items are batched (250 per call) and sent with `compareQuantity` set to `LastKnownQty`. This tells Shopify: "only accept this if you agree with what we last wrote." If Shopify reports `COMPARE_QUANTITY_STALE` (meaning someone changed it on the Shopify side since we last synced), the item is retried unconditionally — PCA always wins.

**Step 6** — Pull Shopify changes to PCA (only if `BidirectionalSync:ShopifyToPcaEnabled` is `true`). Each item is written individually with its own `SaveChangesAsync` call so a failure on one item doesn't block the others.

**Step 7** — Update `ProductSyncMap` for all successfully synced items: new `LastKnownQty`, audit columns, and timestamp.

**Step 8** — Update `SyncState.LastPolledAt`.

### Concurrency

`SyncService` wraps the orchestrator with a semaphore. If a sync is already running, a second request returns immediately instead of queuing. The UI's "Sync Now" button and the background scheduler both go through this gate.

---

## Quantity Handling

PCA stores inventory as `decimal` (e.g., `12.5`). Shopify expects `int`. Every quantity conversion in the codebase follows the same formula:

```csharp
(int)Math.Max(0, Math.Truncate(inStock))
```

This truncates fractional quantities and clamps negatives to zero. The same formula is used for both the value sent to Shopify and the delta comparison, so they can never drift apart.

---

## Error Handling

Errors are categorized for display in the UI:

| Category | What happened | Retried next run? |
|---|---|:---:|
| NotInSyncMap | PCA item has no ProductSyncMap entry | Yes (until mapped) |
| NotFoundInShopify | Shopify rejected the push (non-conflict error) | Yes |
| ConflictOverwritten | Both sides changed; PCA won | No (resolved) |
| RetryFailed | Even the unconditional retry failed | Yes |
| PcaWriteFailed | Couldn't write Shopify's value to PCA | Yes |
| ShopifyQueryFailed | Item missing from Shopify inventory query | Yes |

`LastKnownQty` is only updated on success. Failed items keep their old baseline, so they'll be picked up again on the next sync run.

Fatal errors (like a Shopify API outage) abort the entire cycle and are surfaced in the UI.

---

## The Desktop App

ShopifySyncApp is an Avalonia 11 MVVM application with two tabs:

- **Sync Status** — Shows whether the scheduler is running, the last sync result, and any errors. Has a "Sync Now" button for manual triggers.
- **History** — Lists past sync runs loaded from JSON files in `%APPDATA%\ShopifySync\logs\`.

A Settings dialog (accessible from the main window) lets you configure the poll interval, Shopify credentials, and whether to auto-start the scheduler on launch.

### Crash handling

Unhandled exceptions from any source (UI thread, background tasks, app domain) are caught by a global handler that shows a crash dialog and exits. This is intentional — an unhandled exception in an inventory sync tool means something needs human attention.

---

## Configuration

All runnable projects use the same two-file pattern:

| File | Committed? | Purpose |
|------|:---:|---|
| `appsettings.json` | Yes | Structure and defaults — no secrets |
| `appsettings.local.json` | No | Real values and secrets (gitignored) |

Environment variables override both files (standard .NET config precedence).

### Required settings

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

**Important:** Do not add `ApplicationIntent=ReadOnly` to the PcAmerica connection string. The code appends it automatically — duplicating it produces a malformed connection string.

---

## Shopify API Notes

ShopifySync uses the **GraphQL Admin API (version 2025-10)** with raw `HttpClient` — no SDK.

A few things that aren't obvious from the Shopify docs:

- `currentBulkOperation` returns `null` in this store's context. We poll by node ID instead.
- `objectCount` in bulk operation polling responses is a JSON **string**, not a number.
- JSONL uploads must be UTF-8 **without BOM** — Shopify's parser silently fails otherwise.
- The `productSet` mutation requires `productOptions` when you include variants. Omitting it gives a confusing error about "options input required."
- The `$synchronous` parameter in bulk mutations cannot be a GraphQL variable — it must be hardcoded.
- `HttpContent` streams are consumed on first send. Retry loops must create a new `StringContent` on each attempt.

Both the bootstrap and sync `ShopifyClient` implementations use the same retry strategy: 3 attempts with linear backoff (0s, 5s, 10s), retrying on HTTP 429 and 5xx responses.

---

## Testing

### Smoke tests (PcaData.TestDriver)

22 integration tests that run against the live SQL Server. They cover database connectivity, EF Core mapping, migration application, and the full sync algorithm with fake HTTP handlers (no live Shopify calls).

```bash
cd PcaData.TestDriver && dotnet run
```

Tests 11–21 use fake `HttpMessageHandler` subclasses that return predetermined JSON responses, so they don't need a Shopify connection. They "borrow" existing `ProductSyncMap` rows (snapshot before, restore after) to avoid needing a separate test database.

### Unit tests (SyncHistory.Tests)

4 xUnit tests covering JSON round-trip, filename formatting, null handling, and ordering.

```bash
dotnet test SyncHistory.Tests/
```
