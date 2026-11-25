@description('The Application Insights resource the alert watches.')
param appInsightsName string

@description('Who gets the alert email. Comes from an azd parameter, so it is not in the repo.')
param alertEmail string

@description('P95 latency of the Public API, in milliseconds, above which the alert fires.')
param p95ThresholdMs int = 1000

param location string = resourceGroup().location

resource insights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource email 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'carrierhub-email'
  location: 'global'            // action groups are always global
  properties: {
    groupShortName: 'carrierhub' // at most 12 characters, shown in SMS and app notifications
    enabled: true
    emailReceivers: [
      {
        name: 'owner'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

// The rule: every 5 minutes, compute the Public API's P95 latency over the last 5 minutes,
// and email when it's above the threshold. It resolves itself when latency is back to normal.
resource p95 'Microsoft.Insights/scheduledQueryRules@2022-06-15' = {
  name: 'publicapi-p95'
  location: location
  properties: {
    displayName: 'Public API P95 latency'
    severity: 2
    enabled: true
    scopes: [ insights.id ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    criteria: {
      allOf: [
        {
          query: 'requests | where cloud_RoleName == "publicapi" | summarize p95 = percentile(duration, 95)'
          metricMeasureColumn: 'p95'
          timeAggregation: 'Maximum'
          operator: 'GreaterThan'
          threshold: p95ThresholdMs
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [ email.id ]
    }
    autoMitigate: true
  }
}
