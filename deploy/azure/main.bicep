// ShiftReason on Azure Container Apps.
//
// Everything the live solver needs, in one resource group, sized to stay inside
// the Container Apps monthly free grant (180,000 vCPU-seconds, 360,000 GiB-seconds
// and 2 million requests per subscription):
//
//   - Log Analytics workspace, with a daily ingestion cap
//   - Container Apps environment of the *workload profiles* type, Consumption only
//   - the container app itself, scaling from zero to exactly one replica
//   - optionally, a budget that emails you as soon as anything costs money
//
// Deploy with deploy/azure/provision.sh, which wraps `az deployment group create`.

targetScope = 'resourceGroup'

@description('Region for every resource. Defaults to the resource group\'s.')
param location string = resourceGroup().location

@description('Prefix for resource names.')
@minLength(3)
@maxLength(20)
param namePrefix string = 'shiftreason'

@description('Container image to run, e.g. ghcr.io/<owner>/shiftreason:latest. The GHCR package must be public — no registry credentials are configured.')
param image string

// Why 4 vCPU and a workload-profiles environment:
//
// A *Consumption-only* environment caps a replica at 2 vCPU / 4 GiB. The
// workload-profiles environment's Consumption profile goes to 4 vCPU / 8 GiB, is
// billed identically, and carries no management fee as long as no Dedicated
// profile is added. The cores matter: CP-SAT's large-neighbourhood search needs
// several workers to keep finding improving rosters, which is what the live grid
// streams. Memory is fixed at 2 GiB per vCPU on this plan.
@description('vCPU per replica. Memory is set to 2 GiB per vCPU, as the Consumption plan requires.')
@allowed([ 1, 2, 3, 4 ])
param cpuCores int = 4

@description('Optional CP-SAT worker override (SHIFTREASON_SOLVER_WORKERS). Empty uses the app\'s own rule: cores - 1, never fewer than 2.')
param solverWorkers string = ''

@description('Global cap on solve requests per minute. Each solve can hold every core for up to a minute.')
@minValue(1)
param solvesPerMinute int = 30

@description('Email for budget alerts. Leave empty to skip creating the budget.')
param budgetContactEmail string = ''

@description('Monthly budget in your billing currency. Alerts fire at 50% and 100% of actual spend, and on forecast. With the free grant, expected spend is zero, so a small number means "tell me if anything costs money at all".')
@minValue(1)
param budgetAmount int = 1

@description('First day of the budget period. Must be the first of a month.')
param budgetStartDate string = utcNow('yyyy-MM-01')

var memory = '${cpuCores * 2}Gi'

var appEnv = concat(
  [
    { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
    { name: 'RateLimiting__SolvesPerMinute', value: string(solvesPerMinute) }
  ],
  empty(solverWorkers) ? [] : [ { name: 'SHIFTREASON_SOLVER_WORKERS', value: solverWorkers } ]
)

// ---------------------------------------------------------------------------

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    // Hard stop on ingestion. The first 5 GB a month are free and a demo app
    // writes a tiny fraction of that; the cap is there so a log storm from a bug
    // or a scripted client cannot turn into a bill.
    workspaceCapping: { dailyQuotaGb: json('0.1') }
  }
}

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${namePrefix}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    // Declaring the profile list is what makes this a workload-profiles
    // environment. Consumption only: adding any Dedicated profile would start a
    // management charge whether or not anything runs on it.
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: false
  }
}

resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: namePrefix
  location: location
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      // Session affinity requires single revision mode.
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        // 'auto' negotiates HTTP/1.1 upgrades, which is what SignalR's WebSocket
        // transport needs.
        transport: 'auto'
        allowInsecure: false
        // Belt and braces: with maxReplicas 1 there is only one replica to land
        // on, but if the cap is ever raised, a SignalR client must stay on the
        // replica that holds its connection. That, and not this setting, is
        // where scaling out would actually break: the hub has no backplane.
        stickySessions: { affinity: 'sticky' }
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
    }
    template: {
      containers: [
        {
          name: 'shiftreason'
          image: image
          resources: {
            cpu: cpuCores
            memory: memory
          }
          env: appEnv
          probes: [
            {
              // Generous: .NET startup plus loading OR-Tools' native libraries
              // on a cold replica. 30 x 2s before the replica is declared dead.
              type: 'Startup'
              httpGet: { path: '/health', port: 8080 }
              initialDelaySeconds: 1
              periodSeconds: 2
              failureThreshold: 30
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health', port: 8080 }
              periodSeconds: 10
              failureThreshold: 3
            }
            {
              // A 60-second solve saturates every core. The liveness probe must
              // tolerate a slow /health during one, or the platform would kill
              // the replica mid-solve for being "unresponsive".
              type: 'Liveness'
              httpGet: { path: '/health', port: 8080 }
              periodSeconds: 30
              timeoutSeconds: 10
              failureThreshold: 4
            }
          ]
        }
      ]
      scale: {
        // Zero replicas costs nothing. The price is a cold start of a few seconds
        // on the first request, which the static demo absorbs: it never waits
        // for this app at all.
        minReplicas: 0
        // One replica, deliberately. SignalR has no backplane here, and a second
        // replica would split hub groups across processes.
        maxReplicas: 1
        // Scale back to zero five minutes after the last request (the platform
        // default, stated explicitly). Every minute awake at 4 vCPU spends 240
        // vCPU-seconds of the free grant.
        cooldownPeriod: 300
        rules: [
          {
            name: 'http'
            http: {
              metadata: { concurrentRequests: '50' }
            }
          }
        ]
      }
    }
  }
}

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = if (!empty(budgetContactEmail)) {
  name: '${namePrefix}-monthly'
  properties: {
    category: 'Cost'
    amount: budgetAmount
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStartDate }
    // Budgets alert; they do not stop anything. Azure cost data also lags by up
    // to a day, so treat this as an early warning rather than a hard cap.
    notifications: {
      actualHalf: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 50
        thresholdType: 'Actual'
        contactEmails: [ budgetContactEmail ]
      }
      actualFull: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: [ budgetContactEmail ]
      }
      forecastFull: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: [ budgetContactEmail ]
      }
    }
  }
}

@description('Public URL of the live solver.')
output url string = 'https://${app.properties.configuration.ingress.fqdn}'

@description('Container app name, for the GitHub Actions deploy step.')
output containerAppName string = app.name
