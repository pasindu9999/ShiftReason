# Deploying ShiftReason

The end state is two public URLs, both free to run:

| | Where | What it is | When it's down |
|---|---|---|---|
| **Static demo** | Cloudflare Pages | The React app playing recorded real solves. No backend. | Never — it's static files |
| **Live solver** | Azure Container Apps | The full app: API, SignalR, CP-SAT, 4 vCPU | Scaled to zero when idle. The first request wakes it, which can take up to ~30 s if the platform has to pull the image onto a fresh node |

Put the **static demo** link on your CV. It's the one that can't let you down, and it links to the live solver for anyone who wants to try a real solve.

Every push to `main` then runs the whole pipeline: test → build image → smoke-test it → publish → deploy both sites → smoke-test the live one.

```text
dotnet ─┐
        ├─> image (build, smoke test, publish to GHCR) ──> deploy-app  (Azure)
web ────┤
        └────────────────────────────────────────────────> deploy-demo (Cloudflare)
```

The deploy jobs **skip themselves** until their settings exist, so the pipeline is green from the first push and each step below simply switches another part on.

---

## 0. Before you leave this machine

One file from an earlier commit is now build output, so untrack it:

```bash
git rm --cached src/ShiftReason.Api/wwwroot/index.html
git add -A
# Git on Windows ignores the executable bit, so record it explicitly. (CI and
# these docs call the scripts through `bash`, so they work either way.)
git update-index --chmod=+x deploy/smoke-test.sh deploy/azure/provision.sh deploy/azure/github-oidc.sh
git status          # review — nothing under wwwroot/, dist/, bin/ or obj/ should appear
git commit -m "Phase 6: container image, CI/CD, Azure and Cloudflare deployment"
git push
```

Watch the first run at `https://github.com/pasindu9999/ShiftReason/actions`. On a push to `main` the **Container image** job builds the image, smoke-tests it and publishes it to GitHub Container Registry. The two deploy jobs show as *skipped*. That's expected.

## 1. On the new machine

Install:

- **Git**
- **Docker** — Docker Desktop on Windows or macOS, Docker Engine on Linux
- **Azure CLI** — https://aka.ms/azure-cli. *Or skip it and use Azure Cloud Shell in the browser, which has the CLI and Bicep preinstalled.*
- Optional: **GitHub CLI** (`gh`). If it's installed and logged in, step 5 sets the GitHub secrets for you.
- Optional, only to develop without Docker: **.NET 10 SDK** and **Node 24**

```bash
git clone https://github.com/pasindu9999/ShiftReason.git
cd ShiftReason
```

> **Windows:** run the `.sh` scripts from **Git Bash** or **WSL**. `.gitattributes` keeps them LF on checkout, so they run as-is.

## 2. Run it locally in Docker

```bash
docker compose up --build
```

Open http://localhost:8080. Pick **Night certification crunch** and press **Solve**. You should see the ward declared impossible, then the two conflicting rules listed in plain English.

Check the container the same way CI does, from a second terminal:

```bash
bash deploy/smoke-test.sh http://localhost:8080
```

It ends with `==> PASS`. (It needs `jq`: `winget install jqlang.jq`, `brew install jq` or `apt install jq`.)

The first log line reports the solver's worker count, e.g. `CP-SAT workers=15 (ProcessorCount=16)`.

## 3. Make the container image public

GitHub creates every new package as **private**, and Azure has no credentials to pull a private one.

1. Open https://github.com/users/pasindu9999/packages/container/package/shiftreason
2. **Package settings** → **Danger Zone** → **Change visibility** → **Public**

This is a one-off. Later pushes keep it public.

## 4. Create the Azure resources

**Account.** Sign up at https://azure.microsoft.com/free. A card is required to verify identity; you aren't charged. The Container Apps free grant (180,000 vCPU-seconds, 360,000 GiB-seconds and 2 million requests per month) is a permanent monthly allowance, separate from the 30-day trial credit. When the trial ends, Azure asks you to move to pay-as-you-go to keep resources running. The app keeps costing nothing inside the grant, and the budget below emails you if that ever changes.

Then run:

```bash
az login
BUDGET_EMAIL=you@example.com bash deploy/azure/provision.sh
```

To use **Azure Cloud Shell** instead: open https://shell.azure.com, choose **Bash** and **No storage account required** (an ephemeral session), then:

```bash
git clone https://github.com/pasindu9999/ShiftReason.git && cd ShiftReason
BUDGET_EMAIL=you@example.com bash deploy/azure/provision.sh
```

The script:

- checks the image can be pulled anonymously, and stops with the fix if it can't (see step 3)
- registers the `Microsoft.App` and `Microsoft.OperationalInsights` providers on a fresh subscription
- creates resource group `rg-shiftreason` and deploys [`deploy/azure/main.bicep`](../deploy/azure/main.bicep):
  - a Log Analytics workspace with a **0.1 GB/day ingestion cap**
  - a **workload-profiles** Container Apps environment, Consumption profile only. That's what allows 4 vCPU per replica; a Consumption-only environment caps at 2. There's no management fee unless a Dedicated profile is added.
  - the container app: 4 vCPU / 8 GiB, **0 to 1 replicas**, sticky sessions, startup/readiness/liveness probes on `/health`
  - a **budget of 1 (your billing currency) per month** that emails you at 50%, at 100% and on forecast. It's there to alert you if anything costs money at all.
- prints the live URL.

Override any of these through the environment: `RESOURCE_GROUP`, `LOCATION` (default `eastus`; for example `australiaeast` or `southeastasia` if you're closer), `CPU_CORES` (1–4), `IMAGE`.

Then smoke-test the live app. The first request cold-starts it:

```bash
bash deploy/smoke-test.sh https://shiftreason.<something>.azurecontainerapps.io
```

## 5. Deploy every push to Azure automatically

```bash
bash deploy/azure/github-oidc.sh
```

This creates an Entra app registration that trusts **only** GitHub's short-lived token for pushes to `main` of this repository. That's OpenID Connect federation: **no password or client secret is ever created**, so there's nothing to store, leak or rotate. It grants that identity **Contributor on `rg-shiftreason` only**.

If `gh` is installed and logged in (`gh auth login`), the script sets these for you. Otherwise it prints them; add each one at **Settings → Secrets and variables → Actions**:

| Kind | Name | Value |
|---|---|---|
| Secret | `AZURE_CLIENT_ID` | printed by the script |
| Secret | `AZURE_TENANT_ID` | printed by the script |
| Secret | `AZURE_SUBSCRIPTION_ID` | printed by the script |
| Variable | `AZURE_RESOURCE_GROUP` | `rg-shiftreason` |
| Variable | `AZURE_CONTAINERAPP_NAME` | `shiftreason` |
| Variable | `LIVE_URL` | the live URL from step 4 |

`LIVE_URL` also turns on the "Open the live solver" link in the static demo.

The next push to `main` runs **Deploy to Azure Container Apps**. It rolls out a new revision tagged with the commit (`sha-…`, never `latest`), then runs the smoke test through Azure's ingress.

## 6. The static demo on Cloudflare Pages

1. Sign up at https://dash.cloudflare.com. It's free and needs no card.
2. **Create the project once.** The simplest way is the CLI:

   ```bash
   npx wrangler login
   npx wrangler pages project create shiftreason --production-branch main
   ```

   Or in the dashboard: **Workers & Pages** → **Create** → **Pages** → **Upload assets** (direct upload), then upload any folder to finish; CI replaces it on the next push. The name becomes `shiftreason.pages.dev`; pick another if it's taken.
3. **API token.** Go to **My Profile → API Tokens → Create Token → Create Custom Token**. Set the permission to **Account → Cloudflare Pages → Edit**, then create it and copy the token.
4. **Account ID.** Go to **Workers & Pages** and copy the *Account ID* from the right-hand panel.
5. Add them to GitHub:

| Kind | Name | Value |
|---|---|---|
| Secret | `CLOUDFLARE_API_TOKEN` | the token from 3 |
| Secret | `CLOUDFLARE_ACCOUNT_ID` | the ID from 4 |
| Variable | `CLOUDFLARE_PAGES_PROJECT` | the project name from 2 |

Push, or re-run the workflow from the Actions tab. **Deploy static demo to Cloudflare Pages** publishes the demo build.

> **Alternative:** Cloudflare's own Git integration also works, with **Workers & Pages → Create → Pages → Connect to Git**. Set the root directory to `src/ShiftReason.Web`, the build command to `npm run build:demo`, the output directory to `dist`, and the environment variables `VITE_LIVE_URL` and `VITE_REPO_URL`. If you use it, **leave `CLOUDFLARE_PAGES_PROJECT` unset** so the site isn't deployed twice. The trade-off is that Cloudflare builds every push, including ones whose tests failed.

## 7. Check the finished thing

- **Static demo:** it autoplays the large ward improving live. Press **Stop**, choose **Night certification crunch**, open **Cheapest fixes**, and press **Give this up & re-solve** on option 1. The ward solves, and the page states it's proven optimal. Open the browser dev tools: the Network tab shows no calls to `/api` or `/hubs`, because none exist here.
- **Live solver:** choose **Large ward**, press **Solve**, and watch the grid improve over about 30 seconds. Then try **Reproducible** and compare the objective.
- **README:** replace the two link placeholders at the top of `README.md` with your URLs.

---

## Refreshing the demo recordings

The static demo plays `src/ShiftReason.Web/public/demo/traces/index.json`. Record it again whenever the model or the presets change, or the demo drifts from what the solver actually does. The frontend tests check each recording's internal consistency, but not whether it matches today's solver.

```bash
docker compose up --build -d
SHIFTREASON_API=http://localhost:8080 node scripts/record-demos.mjs
git add src/ShiftReason.Web/public/demo/traces/index.json
```

## Costs and guardrails

What keeps this at zero:

| Guardrail | Where |
|---|---|
| Scale to **zero** replicas when idle — no charge at all | `main.bicep` `minReplicas: 0` |
| **One** replica maximum | `main.bicep` `maxReplicas: 1` |
| Solves capped at **60 s** each, **30/min** globally (HTTP 429 beyond that) | `Program.cs` |
| Log ingestion capped at **0.1 GB/day** | `main.bicep` `workspaceCapping` |
| No Azure Container Registry (it bills even when idle); images live on free GHCR | this setup |
| No Dedicated workload profile (it starts a management fee) | `main.bicep` |
| **Budget email** as soon as spend appears | `main.bicep` `budget` |

What to know honestly: **any** request wakes the replica, and it stays awake five minutes after the last one. An awake replica at 4 vCPU spends 240 vCPU-seconds of the grant per minute, so the grant covers about **12 hours awake per month** — plenty for a portfolio, but not unlimited. Someone scripting requests around the clock could run past it. The budget email is the early warning, but budgets alert rather than stop anything, and Azure cost data lags by up to a day. If you ever need to stop the spend at once:

```bash
az containerapp ingress disable --name shiftreason --resource-group rg-shiftreason
```

For a smaller footprint, re-run provisioning with `CPU_CORES=2`. That halves the burn rate, and the solver still gets two workers.

## Tearing it all down

```bash
az group delete --name rg-shiftreason --yes
az ad app delete --id "$(az ad app list --display-name shiftreason-github-deploy --query '[0].appId' -o tsv)"
```

Then delete the Cloudflare Pages project and the GHCR package from their dashboards.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `provision.sh`: *Cannot pull … anonymously* | CI hasn't pushed the image yet, or the package is private. See step 3. |
| **Container image** job: `denied: permission_denied: write_package` | The package exists but isn't linked to this repo. Go to Package settings → **Manage Actions access** → add `ShiftReason` with **Write**. |
| `azure/login`: `AADSTS70021` (no matching federated identity) | The workflow ran from something other than a push to `main` (a branch or a pull request), or the repo was renamed. Re-run `github-oidc.sh`. |
| **Deploy to Azure** shows *skipped* | `AZURE_CONTAINERAPP_NAME` isn't set as a repository **variable** (not a secret). |
| Live smoke test times out on health | Cold start, or a revision failed to start. Run `az containerapp logs show -n shiftreason -g rg-shiftreason --follow` and `az containerapp revision list -n shiftreason -g rg-shiftreason -o table`. |
| Live grid barely improves | Check the startup log for `CP-SAT workers=`. One worker means no large-neighbourhood search; the app never picks fewer than two, but `SHIFTREASON_SOLVER_WORKERS` can override that. |
| Cloudflare: *Project not found* | Create the project once, as in step 6.2. The name must match `CLOUDFLARE_PAGES_PROJECT` exactly. |
| `$'\r': command not found` when running a script | It was checked out with CRLF line endings. Run `git add --renormalize . && git checkout -- .`, or run it from WSL. |
| Solver tests fail on CI after making the repo **private** | Private repos get 2-core runners. The worker floor of two keeps the tests passing there; if they're still tight, run the test projects one at a time with `dotnet test -m:1`. |
