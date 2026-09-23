#!/usr/bin/env bash
# Lets GitHub Actions deploy to Azure without storing any password.
#
#   RESOURCE_GROUP=rg-shiftreason bash deploy/azure/github-oidc.sh
#
# Creates an Entra app registration whose only way in is a federated credential:
# Azure accepts a short-lived token that GitHub mints for a workflow run on this
# repository's main branch, and nothing else. No client secret is ever created,
# so there is nothing to leak, rotate, or expire.
#
# Run after deploy/azure/provision.sh. Safe to re-run. If the GitHub CLI (gh) is
# installed and logged in, the secrets and variables are set for you; otherwise
# the exact values are printed.

set -euo pipefail

# Git Bash on Windows rewrites arguments that look like paths, so Azure scopes such
# as /subscriptions/... would reach az as C:/Program Files/Git/subscriptions/...
export MSYS_NO_PATHCONV=1

cd "$(dirname "$0")/../.."

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-shiftreason}"
CONTAINER_APP="${CONTAINER_APP:-shiftreason}"
IDENTITY_NAME="${IDENTITY_NAME:-shiftreason-github-deploy}"
# owner/repo, from the git remote unless given.
REPO="${REPO:-$(git remote get-url origin | sed -E 's#.*github\.com[:/]([^/]+/[^/]+)$#\1#; s#\.git$##')}"

az account show >/dev/null 2>&1 || { echo "Not logged in. Run: az login" >&2; exit 1; }

# Entra compares the federated subject case-sensitively, but git remotes are not
# case-canonical: a remote typed as owner/shiftreason would produce a credential
# GitHub's token never matches, and login fails with an opaque AADSTS70021.
# GitHub's API returns the canonical casing for any spelling of the name.
canonical=$(curl -fsS "https://api.github.com/repos/${REPO}" 2>/dev/null \
              | sed -nE 's/^  "full_name": *"([^"]+)".*/\1/p' | head -1 || true)
if [[ -n "$canonical" ]]; then REPO="$canonical"; fi

subscription_id=$(az account show --query id -o tsv)
tenant_id=$(az account show --query tenantId -o tsv)
scope=$(az group show --name "$RESOURCE_GROUP" --query id -o tsv) \
  || { echo "Resource group $RESOURCE_GROUP not found; run deploy/azure/provision.sh first." >&2; exit 1; }
fqdn=$(az containerapp show --name "$CONTAINER_APP" --resource-group "$RESOURCE_GROUP" \
         --query properties.configuration.ingress.fqdn -o tsv)
live_url="https://${fqdn}"

echo "Repository : $REPO (branch main)"
echo "Scope      : $scope"
echo

# 1. App registration + service principal, reused if they already exist.
client_id=$(az ad app list --display-name "$IDENTITY_NAME" --query "[0].appId" -o tsv)
if [[ -z "$client_id" ]]; then
  client_id=$(az ad app create --display-name "$IDENTITY_NAME" --query appId -o tsv)
  echo "Created app registration $IDENTITY_NAME"
fi
if ! az ad sp show --id "$client_id" >/dev/null 2>&1; then
  az ad sp create --id "$client_id" -o none
fi
principal_id=$(az ad sp show --id "$client_id" --query id -o tsv)

# 2. Trust GitHub's token for pushes to main, and only that. A pull request,
#    another branch or a fork presents a different subject and is refused.
subject="repo:${REPO}:ref:refs/heads/main"
existing=$(az ad app federated-credential list --id "$client_id" \
             --query "[?subject=='${subject}'].name" -o tsv)
if [[ -z "$existing" ]]; then
  credential=$(printf '{"name":"github-main","issuer":"https://token.actions.githubusercontent.com","subject":"%s","audiences":["api://AzureADTokenExchange"]}' "$subject")
  az ad app federated-credential create --id "$client_id" --parameters "$credential" -o none
  echo "Trusted $subject"
fi

# 3. Permission to roll out new revisions, on this one resource group only.
#
#    Contributor rather than the narrower built-in "Container Apps Contributor":
#    that role's wildcards (Microsoft.App/containerApps/*/write) may not cover the
#    top-level update this pipeline performs, and a deploy that fails on its first
#    run is a worse outcome than a role scoped to a group that holds nothing but
#    this app. Tighten it once a deploy has succeeded, if you like.
assigned=$(az role assignment list --assignee "$principal_id" --scope "$scope" \
             --role Contributor --query "[0].id" -o tsv)
if [[ -z "$assigned" ]]; then
  # Entra replication can lag a freshly created principal by a few seconds.
  for attempt in 1 2 3 4 5 6; do
    if az role assignment create \
         --assignee-object-id "$principal_id" \
         --assignee-principal-type ServicePrincipal \
         --role Contributor \
         --scope "$scope" -o none 2>/dev/null; then
      echo "Granted Contributor on $RESOURCE_GROUP"
      break
    fi
    if [[ $attempt == 6 ]]; then
      echo "Role assignment failed; re-run this script in a minute." >&2
      exit 1
    fi
    sleep 10
  done
fi

# 4. Hand the values to GitHub. Plain arrays rather than associative ones:
#    macOS still ships bash 3.2, which has no `declare -A`.
secrets=(
  "AZURE_CLIENT_ID=$client_id"
  "AZURE_TENANT_ID=$tenant_id"
  "AZURE_SUBSCRIPTION_ID=$subscription_id"
)
variables=(
  "AZURE_RESOURCE_GROUP=$RESOURCE_GROUP"
  "AZURE_CONTAINERAPP_NAME=$CONTAINER_APP"
  "LIVE_URL=$live_url"
)

if command -v gh >/dev/null && gh auth status >/dev/null 2>&1; then
  for pair in "${secrets[@]}"; do
    gh secret set "${pair%%=*}" --repo "$REPO" --body "${pair#*=}"
  done
  for pair in "${variables[@]}"; do
    gh variable set "${pair%%=*}" --repo "$REPO" --body "${pair#*=}"
  done
  echo
  echo "Set on $REPO. The next push to main deploys automatically."
else
  echo
  echo "Add these at https://github.com/${REPO}/settings/secrets/actions"
  echo
  echo "  Secrets tab -> New repository secret:"
  for pair in "${secrets[@]}"; do
    printf '    %-26s %s\n' "${pair%%=*}" "${pair#*=}"
  done
  echo
  echo "  Variables tab -> New repository variable:"
  for pair in "${variables[@]}"; do
    printf '    %-26s %s\n' "${pair%%=*}" "${pair#*=}"
  done
  echo
  echo "The next push to main after that deploys automatically."
fi
