import logging
import json
import os
import uuid
import time
import pandas as pd
import io
import azure.functions as func
from datetime import datetime, timedelta, timezone
from azure.identity import DefaultAzureCredential
from azure.mgmt.resourcegraph import ResourceGraphClient
from azure.mgmt.resourcegraph.models import QueryRequest, QueryRequestOptions
from azure.storage.queue import QueueServiceClient
from azure.storage.blob import BlobServiceClient
from azure.storage.fileshare import ShareServiceClient
from azure.data.tables import TableServiceClient
from azure.core.exceptions import ResourceNotFoundError


# Initialize the v2 Function App
app = func.FunctionApp()

# =====================================================================
# CONSTANTS & CONFIGURATION
# =====================================================================

# Azure infrastructure containers to exclude from scanning.
# These are internal Azure-managed containers — not business data.
EXCLUDED_CONTAINER_PREFIXES = (
    "azure-webjobs-",
    "app-package-",
    "scm-releases",
    "$logs",
    "$blobchangefeed",
    "$web",
    "$root",
)

# Table Storage config for persistent watermark
WATERMARK_TABLE_NAME = "InventoryWatermarks"
WATERMARK_PARTITION_KEY = "scanner"
WATERMARK_ROW_KEY = "last_sync_date"

# Default fallback for incremental window (days) if no watermark exists
DEFAULT_INCREMENTAL_DAYS = 30

# Retry configuration for storage data-plane calls
MAX_RETRIES = 3
INITIAL_BACKOFF_SECONDS = 1.0


# =====================================================================
# HELPER: Retry with Exponential Backoff
# =====================================================================
def retry_with_backoff(operation, description, max_retries=MAX_RETRIES):
    """Execute an operation with exponential backoff on transient failures.

    Args:
        operation: A callable that performs the desired operation.
        description: A human-readable description for logging.
        max_retries: Maximum number of retry attempts.

    Returns:
        The return value of the operation callable.

    Raises:
        The last exception if all retries are exhausted.
    """
    last_exception = None
    for attempt in range(max_retries):
        try:
            return operation()
        except Exception as e:
            last_exception = e
            error_code = getattr(e, 'error_code', '')
            status_code = getattr(e, 'status_code', 0)

            # Only retry on transient/throttling errors
            if status_code in (429, 500, 502, 503, 504) or 'Throttling' in str(error_code):
                backoff = INITIAL_BACKOFF_SECONDS * (2 ** attempt)
                logging.warning(
                    f"Retry {attempt + 1}/{max_retries} for '{description}' "
                    f"after {backoff}s (status={status_code}, error={error_code})"
                )
                time.sleep(backoff)
            else:
                # Non-transient error — don't retry
                raise
    # All retries exhausted
    raise last_exception


# =====================================================================
# HELPER: Persistent Watermark (Azure Table Storage)
# =====================================================================
def get_watermark(table_service_client):
    """Fetch the last sync date from Azure Table Storage.

    Returns the stored datetime, or a default fallback if no watermark exists.
    """
    try:
        table_client = table_service_client.get_table_client(WATERMARK_TABLE_NAME)
        entity = table_client.get_entity(
            partition_key=WATERMARK_PARTITION_KEY,
            row_key=WATERMARK_ROW_KEY,
        )
        watermark_str = entity.get("LastSyncDate")
        if watermark_str:
            return datetime.fromisoformat(watermark_str)
    except ResourceNotFoundError:
        logging.info("No existing watermark found. Using default incremental window.")
    except Exception as e:
        logging.warning(f"Failed to fetch watermark from Table Storage: {e}")

    return datetime.now(timezone.utc) - timedelta(days=DEFAULT_INCREMENTAL_DAYS)


def update_watermark(table_service_client, sync_date):
    """Update the last sync date in Azure Table Storage after a successful scan."""
    try:
        table_client = table_service_client.get_table_client(WATERMARK_TABLE_NAME)

        # Ensure the table exists
        try:
            table_service_client.create_table(WATERMARK_TABLE_NAME)
        except Exception:
            pass  # Table already exists

        entity = {
            "PartitionKey": WATERMARK_PARTITION_KEY,
            "RowKey": WATERMARK_ROW_KEY,
            "LastSyncDate": sync_date.isoformat(),
        }
        table_client.upsert_entity(entity)
        logging.info(f"Watermark updated to {sync_date.isoformat()}")
    except Exception as e:
        logging.warning(f"Failed to update watermark: {e}")


# =====================================================================
# THE ORCHESTRATOR (Triggered by Logic Apps)
# =====================================================================
@app.route(route="start-inventory", auth_level=func.AuthLevel.FUNCTION)
def storage_inventory_orchestrator(req: func.HttpRequest) -> func.HttpResponse:
    logging.info("Starting Enterprise Storage Inventory Discovery via HTTP Trigger...")

    try:
        # 1. Zero-Trust Authentication
        # Uses Azure CLI locally, Managed Identity in the cloud
        credential = DefaultAzureCredential()

        # 2. Fast Discovery via Azure Resource Graph (ARG)
        arg_client = ResourceGraphClient(credential)

        # KQL Query: Find all storage accounts across the enterprise
        query = """
        Resources 
        | where type =~ 'microsoft.storage/storageaccounts' 
        | project name, resourceGroup, subscriptionId
        """

        # Cross-subscription support: specify target subscriptions if configured
        # If INVENTORY_SUBSCRIPTION_IDS is set (comma-separated), scope to those.
        # Otherwise, ARG defaults to all subscriptions the identity has access to.
        target_subscriptions = os.environ.get("INVENTORY_SUBSCRIPTION_IDS", "")
        subscription_list = [s.strip() for s in target_subscriptions.split(",") if s.strip()] if target_subscriptions else None

        logging.info("Executing ARG Query to discover storage accounts...")
        query_request = QueryRequest(
            query=query,
            subscriptions=subscription_list,
            options=QueryRequestOptions(result_format="objectArray"),
        )
        response = arg_client.resources(query_request)

        # Convert ARG response to a list of dictionaries
        storage_accounts = [dict(row) for row in response.data]
        total_accounts = len(storage_accounts)
        logging.info(f"Discovery Complete: Found {total_accounts} storage accounts.")

        if total_accounts == 0:
            logging.info("No storage accounts found. Exiting.")
            return func.HttpResponse("No storage accounts found.", status_code=200)

        # 3. The Fan-Out Strategy (Chunking)
        batch_size = 15  # Send 15 accounts to each Worker to prevent timeouts
        batches = [storage_accounts[i:i + batch_size] for i in range(0, total_accounts, batch_size)]

        logging.info(f"Splitting workload into {len(batches)} parallel batches.")

        # 4. Dispatch to Azure Storage Queue to trigger the Workers
        queue_account = os.environ.get("WAREHOUSE_ACCOUNT", "stinvhost9922")
        queue_account_url = f"https://{queue_account}.queue.core.windows.net"
        queue_service = QueueServiceClient(
            account_url=queue_account_url,
            credential=credential,
            message_encode_policy=None,
            message_decode_policy=None,
        )
        queue_client = queue_service.get_queue_client("inventory-tasks")

        # Ensure queue exists
        try:
            queue_client.get_queue_properties()
        except Exception:
            queue_client.create_queue()

        # Generate a unique run ID so workers can create unique filenames
        run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S") + "_" + uuid.uuid4().hex[:8]

        for batch_index, batch in enumerate(batches):
            message_payload = {
                "run_id": run_id,
                "batch_index": batch_index,
                "total_batches": len(batches),
                "total_accounts_in_run": total_accounts,
                "accounts": batch,
            }
            message_content = json.dumps(message_payload)
            queue_client.send_message(message_content)
            logging.info(f"Dispatched batch {batch_index + 1}/{len(batches)} with {len(batch)} accounts.")

        logging.info(f"Successfully dispatched {len(batches)} batches to the Worker Queue (run_id={run_id}).")
        return func.HttpResponse(
            json.dumps({
                "status": "dispatched",
                "run_id": run_id,
                "total_accounts": total_accounts,
                "total_batches": len(batches),
            }),
            status_code=200,
            mimetype="application/json",
        )

    except Exception as e:
        logging.error(f"Critical error in Orchestrator: {e}")
        # TODO(security): In production, do not expose internal error details
        # to external callers. Return a generic error and log details internally.
        return func.HttpResponse(
            json.dumps({"status": "error", "message": "Internal server error. Check logs for details."}),
            status_code=500,
            mimetype="application/json",
        )


# =====================================================================
# THE WORKER (Triggered Instantly by the Queue)
# =====================================================================
@app.queue_trigger(arg_name="msg", queue_name="inventory-tasks", connection="AzureWebJobsStorage")
def storage_inventory_worker(msg: func.QueueMessage) -> None:
    logging.info("Worker Instance Started. Processing a batch of storage accounts...")
    scan_start_time = datetime.now(timezone.utc)

    try:
        # 1. Parse the enriched payload and setup authentication
        payload = json.loads(msg.get_body().decode('utf-8'))
        run_id = payload.get("run_id", uuid.uuid4().hex[:8])
        batch_index = payload.get("batch_index", 0)
        total_batches = payload.get("total_batches", 1)
        total_accounts_in_run = payload.get("total_accounts_in_run", 0)
        batch = payload.get("accounts", payload if isinstance(payload, list) else [])

        credential = DefaultAzureCredential()

        inventory_blobs = []
        inventory_shares = []
        scan_errors = []
        accounts_succeeded = []

        # 2. Persistent Watermark: Fetch last sync date from Table Storage
        warehouse_account = os.environ.get("WAREHOUSE_ACCOUNT", "stinvhost9922")
        table_url = f"https://{warehouse_account}.table.core.windows.net"
        table_service = TableServiceClient(endpoint=table_url, credential=credential)

        last_sync_date = get_watermark(table_service)
        logging.info(f"Incremental Scan Mode: Only scanning blobs modified AFTER {last_sync_date}")

        # 3. Iterate through assigned storage accounts
        for account in batch:
            account_name = account.get('name')
            account_sub = account.get('subscriptionId', 'unknown')
            account_rg = account.get('resourceGroup', 'unknown')
            logging.info(f"Scanning Storage Account: {account_name}")

            try:
                # ---------------------------------------------------------
                # 3A. Scan Blobs (Incremental) with Retry
                # ---------------------------------------------------------
                blob_url = f"https://{account_name}.blob.core.windows.net"
                blob_service_client = BlobServiceClient(account_url=blob_url, credential=credential)

                containers = retry_with_backoff(
                    lambda: list(blob_service_client.list_containers()),
                    f"list_containers({account_name})",
                )

                for container in containers:
                    # Skip Azure infrastructure containers
                    if container.name.startswith(EXCLUDED_CONTAINER_PREFIXES):
                        logging.info(f"  Skipping infrastructure container: {container.name}")
                        continue

                    container_client = blob_service_client.get_container_client(container.name)
                    blobs = retry_with_backoff(
                        lambda cc=container_client: list(cc.list_blobs(include=["deleted", "tags", "versions", "snapshots"])),
                        f"list_blobs({account_name}/{container.name})",
                    )

                    for blob in blobs:
                        # INCREMENTAL FILTER: Skip unchanged blobs
                        if blob.last_modified and blob.last_modified < last_sync_date:
                            continue

                        inventory_blobs.append({
                            "SubscriptionId": account_sub,
                            "ResourceGroup": account_rg,
                            "StorageAccount": account_name,
                            "ContainerName": container.name,
                            "BlobName": blob.name,
                            "BlobType": blob.blob_type,
                            "ContentType": blob.content_settings.content_type if getattr(blob, 'content_settings', None) else None,
                            "ContentMD5": blob.content_settings.content_md5.hex() if getattr(blob, 'content_settings', None) and getattr(blob.content_settings, 'content_md5', None) else None,
                            "IsDeleted": getattr(blob, 'deleted', False) or False,
                            "Size_Bytes": blob.size,
                            "CreationTime": blob.creation_time.strftime("%Y-%m-%d %H:%M:%S") if getattr(blob, 'creation_time', None) else None,
                            "LastModified": blob.last_modified.strftime("%Y-%m-%d %H:%M:%S") if getattr(blob, 'last_modified', None) else None,
                            "BlobTier": blob.blob_tier,
                            "VersionId": getattr(blob, 'version_id', None),
                            "IsCurrentVersion": getattr(blob, 'is_current_version', None),
                            "Snapshot": getattr(blob, 'snapshot', None),
                            "TagCount": getattr(blob, 'tag_count', None),
                        })

                # ---------------------------------------------------------
                # 3B. Scan File Shares with Retry
                # ---------------------------------------------------------
                try:
                    share_url = f"https://{account_name}.file.core.windows.net"
                    share_service_client = ShareServiceClient(
                        account_url=share_url,
                        credential=credential,
                        token_intent="backup",
                    )
                    shares = retry_with_backoff(
                        lambda: list(share_service_client.list_shares()),
                        f"list_shares({account_name})",
                    )
                    for share in shares:
                        inventory_shares.append({
                            "SubscriptionId": account_sub,
                            "ResourceGroup": account_rg,
                            "StorageAccount": account_name,
                            "FileShareName": share.name,
                            "Quota_GB": share.quota,
                            "AccessTier": share.access_tier,
                        })
                except Exception as fs_err:
                    logging.warning(f"Could not scan file shares for {account_name}: {fs_err}")

                accounts_succeeded.append(account_name)

            except Exception as inner_e:
                error_record = {
                    "StorageAccount": account_name,
                    "SubscriptionId": account_sub,
                    "ResourceGroup": account_rg,
                    "ErrorType": type(inner_e).__name__,
                    "ErrorMessage": str(inner_e)[:500],
                    "Timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S"),
                }
                scan_errors.append(error_record)
                logging.warning(f"Failed to scan account {account_name}: {inner_e}")
                continue

        # 4. Data Processing & Utilization Statistics (Pandas)
        df_blobs = pd.DataFrame(inventory_blobs)
        df_shares = pd.DataFrame(inventory_shares)
        df_errors = pd.DataFrame(scan_errors)

        logging.info(
            f"Batch {batch_index + 1}/{total_batches} complete. "
            f"Extracted {len(df_blobs)} newly modified blobs, "
            f"{len(df_shares)} file shares, "
            f"{len(accounts_succeeded)} accounts succeeded, "
            f"{len(scan_errors)} accounts failed."
        )

        df_stats = pd.DataFrame()
        if not df_blobs.empty:
            # Generate Storage Utilization Statistics per Container
            df_stats = df_blobs.groupby(['StorageAccount', 'ContainerName']).agg(
                TotalBlobs=('BlobName', 'count'),
                TotalSizeBytes=('Size_Bytes', 'sum'),
            ).reset_index()

            logging.info(f"Storage Utilization Statistics:\n{df_stats.to_json(orient='records', indent=2)}")
            logging.info(f"Blob Metadata Sample:\n{df_blobs.head(2).to_json(orient='records', indent=2)}")

        if not df_shares.empty:
            logging.info(f"File Share Metadata Sample:\n{df_shares.head(2).to_json(orient='records', indent=2)}")

        if not df_errors.empty:
            logging.info(f"Scan Errors:\n{df_errors.to_json(orient='records', indent=2)}")

        # 5. Warehouse Upload (Unique batch filenames to prevent overwrites)
        warehouse_container = "inventory-snapshots"

        try:
            warehouse_url = f"https://{warehouse_account}.blob.core.windows.net"
            warehouse_client = BlobServiceClient(account_url=warehouse_url, credential=credential)
            container_client = warehouse_client.get_container_client(warehouse_container)

            # Ensure container exists (create if not exists)
            try:
                container_client.get_container_properties()
            except Exception:
                container_client.create_container()

            # Partitioning by Date (YYYY/MM/DD) with run_id for uniqueness
            date_partition = datetime.now(timezone.utc).strftime("%Y/%m/%d")
            batch_suffix = f"{run_id}_batch{batch_index}"

            def upload_to_warehouse(df, prefix, format_type='json', allow_empty=False):
                """Upload a DataFrame to the warehouse.

                Args:
                    df: The DataFrame to upload.
                    prefix: The file name prefix.
                    format_type: 'json' or 'xlsx'.
                    allow_empty: If True, upload even when the DataFrame is empty.
                """
                if df is None:
                    return
                if df.empty and not allow_empty:
                    return

                if format_type == 'json':
                    blob_name = f"{date_partition}/{prefix}_{batch_suffix}.json"
                    blob_client = container_client.get_blob_client(blob_name)
                    data = df.to_json(orient='records') if not df.empty else "[]"
                    blob_client.upload_blob(data, overwrite=True)
                    logging.info(f"Uploaded JSON Snapshot: {blob_name} ({len(df)} records)")

                elif format_type == 'xlsx':
                    blob_name = f"{date_partition}/{prefix}_{batch_suffix}.xlsx"
                    blob_client = container_client.get_blob_client(blob_name)

                    # Convert to excel in memory (zero disk I/O)
                    output = io.BytesIO()
                    with pd.ExcelWriter(output, engine='openpyxl') as writer:
                        df.to_excel(writer, index=False)
                    output.seek(0)
                    blob_client.upload_blob(output.read(), overwrite=True)
                    logging.info(f"Uploaded Excel Report: {blob_name} ({len(df)} records)")

            # Upload JSON Snapshots — always upload, even empty, so downstream
            # systems know a scan happened vs. "did the scan fail?"
            upload_to_warehouse(df_blobs, "blobs_snapshot", format_type='json', allow_empty=True)
            upload_to_warehouse(df_shares, "shares_snapshot", format_type='json', allow_empty=True)

            # Upload error report if any accounts failed
            if not df_errors.empty:
                upload_to_warehouse(df_errors, "scan_errors", format_type='json')

            # Upload Excel Reports
            if not df_stats.empty:
                upload_to_warehouse(df_stats, "utilization_stats", format_type='xlsx')
                upload_to_warehouse(df_blobs, "blobs_report", format_type='xlsx')

            # 6. Scan Manifest — comprehensive metadata about this batch run
            scan_end_time = datetime.now(timezone.utc)
            scan_manifest = {
                "run_id": run_id,
                "batch_index": batch_index,
                "total_batches": total_batches,
                "total_accounts_in_run": total_accounts_in_run,
                "accounts_in_batch": len(batch),
                "accounts_succeeded": len(accounts_succeeded),
                "accounts_failed": len(scan_errors),
                "accounts_succeeded_list": accounts_succeeded,
                "accounts_failed_list": [e["StorageAccount"] for e in scan_errors],
                "total_blobs_extracted": len(df_blobs),
                "total_shares_extracted": len(df_shares),
                "total_size_bytes": int(df_blobs["Size_Bytes"].sum()) if not df_blobs.empty else 0,
                "incremental_watermark": last_sync_date.isoformat(),
                "scan_start_time": scan_start_time.isoformat(),
                "scan_end_time": scan_end_time.isoformat(),
                "scan_duration_seconds": round((scan_end_time - scan_start_time).total_seconds(), 2),
                "excluded_container_prefixes": list(EXCLUDED_CONTAINER_PREFIXES),
                "warehouse_path": f"{warehouse_account}/{warehouse_container}/{date_partition}/",
            }

            manifest_blob_name = f"{date_partition}/scan_manifest_{batch_suffix}.json"
            manifest_blob_client = container_client.get_blob_client(manifest_blob_name)
            manifest_blob_client.upload_blob(
                json.dumps(scan_manifest, indent=2),
                overwrite=True,
            )
            logging.info(f"Uploaded Scan Manifest: {manifest_blob_name}")

            logging.info(
                f"Worker successfully pushed all partitioned data to Warehouse "
                f"({warehouse_account}/{warehouse_container}). "
                f"Manifest: {json.dumps(scan_manifest, indent=2)}"
            )

        except Exception as wh_err:
            logging.error(f"Failed to push to Warehouse: {wh_err}")

        # 7. Update the persistent watermark on successful scan
        if accounts_succeeded:
            update_watermark(table_service, scan_start_time)

    except Exception as e:
        logging.error(f"Critical error in Worker: {e}")
        raise