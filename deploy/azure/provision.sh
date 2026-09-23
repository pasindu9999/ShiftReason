#!/usr/bin/env bash
# Creates (or updates) everything ShiftReason needs in Azure. Safe to re-run.
#
#   bash deploy/azure/provision.sh
#
# Runs anywhere the Azure CLI does: Azure Cloud Shell (zero setup — pick an
# ephemeral session, no storage account needed), WSL, macOS, Linux, or Git Bash
# on Windows. Log in first with `az login`.
#
# Everything is overridable through the environment:
#
#   RESOURCE_GROUP   default rg-shiftreason
#   LOCATION         default eastus
#   IMAGE            default ghcr.io/<owner-from-git-remote>/shiftreason:latest
#   BUDGET_EMAIL     where cost alerts go; empty skips the budget
#   CPU_CORES        default 4 (1-4; memory is 2 GiB per core)

set -euo pipefail

cd "$(dirname "$0")/../.."

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-shiftreason}"
LOCATION="${LOCATION:-eastus}"
CPU_CORES="${CPU_CORES:-4}"
BUDGET_EMAIL="${BUDGET_EMAIL:-}"

if [[ -z "${IMAGE:-}" ]]; then
  # github.com/<owner>/<repo>(.git) -> <owner>, lower-cased as GHCR requires.
  owner=$(git remote get-url origin | sed -E 's#.*github\.com[:/]([^/]+)/.*#\1#' | tr '[:upper:]' '[:lower:]')
  IMAGE="ghcr.io/${owner}/shiftreason:latest"
fi

command -v az >/dev/null || { echo "Azure CLI not found: https://aka.ms/azure-cli" >&2; exit 1; }
az account show >/dev/null 2>&1 || { echo "Not logged in. Run: az login" >&2; exit 1; }

echo "Subscription : $(az account show --query name -o tsv) ($(az account show --query id -o tsv))"
echo "Group        : $RESOURCE_GROUP in $LOCATION"
echo "Image        : $IMAGE"
echo "Size         : ${CPU_CORES} vCPU / $((CPU_CORES * 2)) GiB"
echo "Budget alerts: ${BUDGET_EMAIL:-none}"
echo

# An image the platform cannot pull makes the first revision fail to start, with
# the reason buried in the portal. Check anonymously now, and say how to fix it.
image_repo="${IMAGE%:*}"
image_tag="${IMAGE##*:}"
registry_path="${image_repo#ghcr.io/}"
token=$(curl -fsS "https://ghcr.io/token?scope=repository:${registry_path}:pull" \
          | sed -E 's/.*"token":"([^"]+)".*/\1/' || true)
manifest_types="application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.docker.distribution.manifest.v2+json"

if ! curl -fsS -o /dev/null \
       -H "Authorization: Bearer ${token}" \
       -H "Accept: ${manifest_types}" \
       "https://ghcr.io/v2/${registry_path}/manifests/${image_tag}"; then
  cat >&2 <<MSG

Cannot pull $IMAGE anonymously.

Either CI has not pushed it yet (push to main and wait for the "Container image"
job to go green), or the GHCR package is still private. New packages always
start private:

  https://github.com/users/${registry_path%%/*}/packages/container/package/shiftreason
  -> Package settings -> Danger Zone -> Change visibility -> Public
MSG
  exit 1
fi
echo "Image is publicly pullable."

# Fresh subscriptions have neither provider registered, and the deployment then
# fails with an unhelpful error.
for provider in Microsoft.App Microsoft.OperationalInsights; do
  state=$(az provider show --namespace "$provider" --query registrationState -o tsv 2>/dev/null || echo NotRegistered)
  if [[ "$state" != Registered ]]; then
    echo "Registering $provider (one-off, takes a minute or two)..."
    az provider register --namespace "$provider" --wait --only-show-errors
  fi
done

az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --only-show-errors -o none

echo "Deploying deploy/azure/main.bicep ..."
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --name shiftreason \
  --template-file deploy/azure/main.bicep \
  --parameters image="$IMAGE" cpuCores="$CPU_CORES" budgetContactEmail="$BUDGET_EMAIL" \
  --only-show-errors -o none

output() {
  az deployment group show \
    --resource-group "$RESOURCE_GROUP" \
    --name shiftreason \
    --query "properties.outputs.$1.value" -o tsv
}

url=$(output url)
app_name=$(output containerAppName)

cat <<DONE

Live solver:   $url
Container app: $app_name

Smoke test it now (the first request cold-starts the replica):
  bash deploy/smoke-test.sh $url

Then let GitHub Actions deploy every push to main:
  RESOURCE_GROUP=$RESOURCE_GROUP bash deploy/azure/github-oidc.sh
DONE
