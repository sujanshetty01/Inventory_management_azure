# Azure Storage Inventory Scanner

> Enterprise-grade, serverless Azure Function that discovers all storage accounts across your organization and extracts comprehensive metadata — secured with Zero-Trust Managed Identity.

## Architecture

```
Trigger (Logic Apps / Timer)
    → Orchestrator (Azure Resource Graph discovery)
        → Fan-Out via Azure Queue
            → Worker (Blob + File Share scanning)
                → Warehouse (JSON snapshots + Excel reports → Azure Blob Storage)
```

## Project Structure

```
Inventory-management/
├── function_app.py       # Core application (Orchestrator + Worker)
├── host.json             # Azure Functions host configuration
├── requirements.txt      # Python dependencies
├── local.settings.json   # Local development settings (NOT deployed)
├── .gitignore            # Git ignore rules
└── .vscode/              # VS Code settings
```

## Functions

| Function | Trigger | Purpose |
|---|---|---|
| `storage_inventory_orchestrator` | Timer (1st of every month) | Discovers storage accounts via ARG, fans out to queue |
| `storage_inventory_worker` | Queue (`inventory-tasks`) | Scans blobs/file shares, uploads JSON + Excel to warehouse |

## Prerequisites

- Azure Function App (`func-lumen-inventory`) with **Flex Consumption** plan
- Storage Account (`stinvhost9922`) with **User-Assigned Managed Identity** (`id-lumen-storage-inventory`)
- RBAC Roles assigned at **Subscription scope** (not per-storage-account):

| Role | Why Subscription Scope? |
|---|---|
| `Storage Blob Data Reader` | Covers all current **and future** storage accounts automatically |
| `Storage File Data SMB Share Reader` | Covers all current **and future** file shares automatically |
| `Storage Queue Data Contributor` | Allows Orchestrator to dispatch + Worker to consume queue messages |
| `Reader` | Required for Azure Resource Graph discovery queries |
| `Storage Table Data Contributor` | Required for persistent incremental scan watermark |

> **Important:** Assigning at subscription scope means any new storage account a customer creates is **automatically discoverable and scannable** — no manual RBAC updates needed.

### Setup RBAC (one-time)

```bash
bash setup_rbac.sh
```

This idempotent script assigns all required roles at subscription scope. Safe to re-run.

---

## Deployment to Azure

### Step 1: Configure Azure Function App Settings

In the Azure Portal → your Function App → **Configuration** → **Application Settings**, add:

| Setting | Value |
|---|---|
| `AzureWebJobsStorage__accountName` | `stinvhost9922` |
| `WAREHOUSE_ACCOUNT` | `stinvhost9922` |
| `FUNCTIONS_WORKER_RUNTIME` | `python` |
| `AzureWebJobsFeatureFlags` | `EnableWorkerIndexing` |

> **Important**: Do NOT add a connection string for `AzureWebJobsStorage`. The `__accountName` suffix tells the runtime to use Managed Identity (Zero-Trust).

### Step 2: Deploy via Azure Functions Core Tools

```bash
# From the project root directory
func azure functionapp publish func-lumen-inventory --python
```

This command:
- Packages your code + `requirements.txt`
- Installs dependencies remotely on the Function App
- Deploys and activates your functions

### Step 3: Verify Deployment

```bash
# List deployed functions
func azure functionapp list-functions func-lumen-inventory
```

You should see:
```
storage_inventory_orchestrator - timerTrigger
storage_inventory_worker       - queueTrigger
```

---

## Triggering via Azure Logic Apps (Monthly)

Since your architecture uses **Azure Logic Apps** as the external trigger:

### Step 1: Get the Function's Master Key

```bash
# In Azure Portal → Function App → App Keys → Copy the "default" host key
```

### Step 2: Create the Logic App

1. In Azure Portal → Create **Logic App (Consumption)**.
2. Open the **Logic App Designer**.
3. Add trigger: **Recurrence**
   - Interval: `1`
   - Frequency: `Month`
   - On these days: `1` (1st of every month)
   - At these hours: `0` (midnight)
4. Add action: **HTTP**
   - Method: `POST`
   - URI: `https://func-lumen-inventory.azurewebsites.net/api/start-inventory`
   - Headers:
     ```
     Content-Type: application/json
     x-functions-key: <YOUR_HOST_KEY>
     ```
   - Body: `{}`
5. **Save** the Logic App.

### Step 3: Test the Logic App

Click **Run Trigger → Run** in the Logic App Designer to execute it immediately.

---

## Local Development

### Setup

```bash
python -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
az login
```

### Run Locally

```bash
# Start Azurite (for local queue trigger emulation)
azurite --silent --location .

# In a separate terminal
func start --port 7072
```

### Test Locally

```bash
# Trigger the Orchestrator (full pipeline)
curl --request POST -H "Content-Type:application/json" \
     --data '{}' \
     http://localhost:7072/admin/functions/storage_inventory_orchestrator

# Or trigger the Worker directly
curl --request POST -H "Content-Type:application/json" \
     --data '{"input": "[{\"name\": \"stinvhost9922\", \"resourceGroup\": \"my-rg\", \"subscriptionId\": \"YOUR_SUB_ID\"}]"}' \
     http://localhost:7072/admin/functions/storage_inventory_worker
```

---

## Output (Warehouse)

The Worker uploads reports to `stinvhost9922/inventory-snapshots`, partitioned by date:

```
inventory-snapshots/
└── 2026/
    └── 06/
        └── 08/
            ├── blobs_snapshot.json       # Raw blob metadata (JSON)
            ├── shares_snapshot.json      # Raw file share metadata (JSON)
            ├── utilization_stats.xlsx    # Aggregated container statistics
            └── blobs_report.xlsx        # Full blob inventory (Excel)
```
