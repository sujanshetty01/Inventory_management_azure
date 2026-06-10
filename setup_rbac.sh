#!/usr/bin/env bash
# =============================================================================
# setup_rbac.sh — Assign subscription-level RBAC for the Inventory Scanner
#
# WHY SUBSCRIPTION SCOPE?
#   Assigning roles at subscription scope means any NEW storage account created
#   in the future automatically inherits access. No manual updates needed.
#
# USAGE:
#   bash setup_rbac.sh
#
# PREREQUISITES:
#   - az CLI logged in with Owner or User Access Administrator rights
#   - The User-Assigned Managed Identity must already exist
#
# For multi-subscription enterprises, run this script once per subscription
# and add each subscription ID to INVENTORY_SUBSCRIPTION_IDS in your
# Function App settings (comma-separated).
# =============================================================================

set -euo pipefail

# ── Configuration ─────────────────────────────────────────────────────────────
SUBSCRIPTION_ID="ca452141-812a-45da-a79b-a6beb43200e8"
IDENTITY_NAME="id-lumen-storage-inventory"
IDENTITY_RG="Inventory_management"
# ──────────────────────────────────────────────────────────────────────────────

echo "=============================================="
echo " Inventory Scanner — RBAC Setup"
echo "=============================================="
echo ""

# Validate subscription
CURRENT_SUB=$(az account show --query id -o tsv 2>/dev/null)
if [ "$CURRENT_SUB" != "$SUBSCRIPTION_ID" ]; then
  echo "Switching to subscription: $SUBSCRIPTION_ID"
  az account set --subscription "$SUBSCRIPTION_ID"
fi

SCOPE="/subscriptions/$SUBSCRIPTION_ID"

# Get the Managed Identity's principal ID
echo "Fetching Managed Identity: $IDENTITY_NAME"
PRINCIPAL_ID=$(az identity show \
  --name "$IDENTITY_NAME" \
  --resource-group "$IDENTITY_RG" \
  --query principalId -o tsv 2>/dev/null)

if [ -z "$PRINCIPAL_ID" ]; then
  echo "❌ ERROR: Managed Identity '$IDENTITY_NAME' not found in RG '$IDENTITY_RG'"
  exit 1
fi

echo "Principal ID: $PRINCIPAL_ID"
echo "Scope:        $SCOPE"
echo ""

# Helper: assign a role, skip gracefully if already assigned
assign_role() {
  local ROLE="$1"
  local DESC="$2"

  echo -n "  → $ROLE ... "

  RESULT=$(az role assignment create \
    --assignee "$PRINCIPAL_ID" \
    --role "$ROLE" \
    --scope "$SCOPE" \
    --description "$DESC" \
    -o json 2>&1)

  if echo "$RESULT" | grep -q "roleDefinitionName"; then
    CREATED_ON=$(echo "$RESULT" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('createdOn','?')[:10])" 2>/dev/null || echo "?")
    echo "✅ Assigned (created: $CREATED_ON)"
  elif echo "$RESULT" | grep -qi "already exists\|Conflict"; then
    echo "✅ Already assigned"
  else
    echo "❌ Failed: $RESULT"
  fi
}

echo "Assigning roles at subscription scope..."
echo ""

# 1. Read blobs across ALL storage accounts (current + future)
assign_role "Storage Blob Data Reader" \
  "Inventory scanner: list containers and read blob metadata across subscription"

# 2. Read file shares across ALL storage accounts (current + future)
assign_role "Storage File Data SMB Share Reader" \
  "Inventory scanner: list file shares across subscription"

# 3. Send/receive queue messages for Orchestrator → Worker dispatch
assign_role "Storage Queue Data Contributor" \
  "Inventory scanner: dispatch and consume inventory task queue messages"

# 4. Read subscription/resource metadata for Azure Resource Graph queries
assign_role "Reader" \
  "Inventory scanner: read resource metadata for Azure Resource Graph discovery"

# 5. Read/write Table Storage for persistent incremental scan watermark
assign_role "Storage Table Data Contributor" \
  "Inventory scanner: persist incremental scan watermark in Table Storage"

echo ""
echo "=============================================="
echo " Verification — Current Role Assignments"
echo "=============================================="
az role assignment list \
  --assignee "$PRINCIPAL_ID" \
  --scope "$SCOPE" \
  --query "sort_by([].{Role:roleDefinitionName, CreatedOn:createdOn[:10]}, &Role)" \
  -o table 2>/dev/null || echo "(Verification query failed — assignments were still applied above)"

echo ""
echo "✅ Done! The Managed Identity '$IDENTITY_NAME' now has subscription-scoped"
echo "   access. Any NEW storage account created in subscription $SUBSCRIPTION_ID"
echo "   will be automatically accessible to the inventory scanner."
echo ""
echo "Next: Re-run the inventory scan to verify all accounts succeed:"
echo "  curl -s https://func-lumen-inventory.azurewebsites.net/api/start-inventory \\"
echo "    -H 'x-functions-key: <YOUR_HOST_KEY>'"
