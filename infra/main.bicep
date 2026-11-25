targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the environment that can be used as part of naming resource convention, the name of the resource group for your application will use this name, prefixed with rg-')
param environmentName string

@minLength(1)
@description('The location used for all deployed resources')
param location string

@description('Id of the user or app to assign application roles')
param principalId string = ''

param alertEmail string
param entraMerchantUserObjectId string
param entraPublicApiClientId string
param entraTenantId string
@secure()
param parcelgo_webhook_secret string
@metadata({azd: {
  type: 'generate'
  config: {length:22,noSpecial:true}
  }
})
@secure()
param redis_password string

var tags = {
  'azd-env-name': environmentName
}

resource rg 'Microsoft.Resources/resourceGroups@2022-09-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module alerts 'alerts/alerts.bicep' = {
  name: 'alerts'
  scope: rg
  params: {
    alertEmail: alertEmail
    appInsightsName: insights.outputs.name
    location: location
  }
}
module contentsafety 'contentsafety/contentsafety.module.bicep' = {
  name: 'contentsafety'
  scope: resourceGroup('rg-docchat-dev')
  params: {
    location: location
  }
}
module env 'env/env.module.bicep' = {
  name: 'env'
  scope: rg
  params: {
    env_acr_outputs_name: env_acr.outputs.name
    location: location
    userPrincipalId: principalId
  }
}
module env_acr 'env-acr/env-acr.module.bicep' = {
  name: 'env-acr'
  scope: rg
  params: {
    location: location
  }
}
module foundry 'foundry/foundry.module.bicep' = {
  name: 'foundry'
  scope: resourceGroup('rg-docchat-dev')
  params: {
    location: location
  }
}
module gateway_identity 'gateway-identity/gateway-identity.module.bicep' = {
  name: 'gateway-identity'
  scope: rg
  params: {
    location: location
  }
}
module gateway_roles_messaging 'gateway-roles-messaging/gateway-roles-messaging.module.bicep' = {
  name: 'gateway-roles-messaging'
  scope: rg
  params: {
    location: location
    messaging_outputs_name: messaging.outputs.name
    principalId: gateway_identity.outputs.principalId
  }
}
module insights 'insights/insights.module.bicep' = {
  name: 'insights'
  scope: rg
  params: {
    location: location
  }
}
module messaging 'messaging/messaging.module.bicep' = {
  name: 'messaging'
  scope: rg
  params: {
    location: location
  }
}
module notification_identity 'notification-identity/notification-identity.module.bicep' = {
  name: 'notification-identity'
  scope: rg
  params: {
    location: location
  }
}
module notification_roles_messaging 'notification-roles-messaging/notification-roles-messaging.module.bicep' = {
  name: 'notification-roles-messaging'
  scope: rg
  params: {
    location: location
    messaging_outputs_name: messaging.outputs.name
    principalId: notification_identity.outputs.principalId
  }
}
module notification_roles_postgres 'notification-roles-postgres/notification-roles-postgres.module.bicep' = {
  name: 'notification-roles-postgres'
  scope: rg
  params: {
    location: location
    postgres_outputs_name: postgres.outputs.name
    principalId: notification_identity.outputs.principalId
    principalName: notification_identity.outputs.principalName
  }
}
module opsassistant_identity 'opsassistant-identity/opsassistant-identity.module.bicep' = {
  name: 'opsassistant-identity'
  scope: rg
  params: {
    location: location
  }
}
module opsassistant_roles_contentsafety 'opsassistant-roles-contentsafety/opsassistant-roles-contentsafety.module.bicep' = {
  name: 'opsassistant-roles-contentsafety'
  scope: resourceGroup('rg-docchat-dev')
  params: {
    location: location
    principalId: opsassistant_identity.outputs.principalId
  }
}
module opsassistant_roles_foundry 'opsassistant-roles-foundry/opsassistant-roles-foundry.module.bicep' = {
  name: 'opsassistant-roles-foundry'
  scope: resourceGroup('rg-docchat-dev')
  params: {
    location: location
    principalId: opsassistant_identity.outputs.principalId
  }
}
module opsassistant_roles_postgres 'opsassistant-roles-postgres/opsassistant-roles-postgres.module.bicep' = {
  name: 'opsassistant-roles-postgres'
  scope: rg
  params: {
    location: location
    postgres_outputs_name: postgres.outputs.name
    principalId: opsassistant_identity.outputs.principalId
    principalName: opsassistant_identity.outputs.principalName
  }
}
module opsassistant_roles_search 'opsassistant-roles-search/opsassistant-roles-search.module.bicep' = {
  name: 'opsassistant-roles-search'
  scope: resourceGroup('rg-docchat-dev')
  params: {
    location: location
    principalId: opsassistant_identity.outputs.principalId
  }
}
module postgres 'postgres/postgres.module.bicep' = {
  name: 'postgres'
  scope: rg
  params: {
    location: location
  }
}
module search 'search/search.module.bicep' = {
  name: 'search'
  scope: resourceGroup('rg-docchat-dev')
  params: {
    location: location
  }
}
module sweep_identity 'sweep-identity/sweep-identity.module.bicep' = {
  name: 'sweep-identity'
  scope: rg
  params: {
    location: location
  }
}
module sweep_roles_contentsafety 'sweep-roles-contentsafety/sweep-roles-contentsafety.module.bicep' = {
  name: 'sweep-roles-contentsafety'
  scope: resourceGroup('rg-docchat-dev')
  params: {
    location: location
    principalId: sweep_identity.outputs.principalId
  }
}
module sweep_roles_foundry 'sweep-roles-foundry/sweep-roles-foundry.module.bicep' = {
  name: 'sweep-roles-foundry'
  scope: resourceGroup('rg-docchat-dev')
  params: {
    location: location
    principalId: sweep_identity.outputs.principalId
  }
}
module sweep_roles_postgres 'sweep-roles-postgres/sweep-roles-postgres.module.bicep' = {
  name: 'sweep-roles-postgres'
  scope: rg
  params: {
    location: location
    postgres_outputs_name: postgres.outputs.name
    principalId: sweep_identity.outputs.principalId
    principalName: sweep_identity.outputs.principalName
  }
}
module sweep_roles_search 'sweep-roles-search/sweep-roles-search.module.bicep' = {
  name: 'sweep-roles-search'
  scope: resourceGroup('rg-docchat-dev')
  params: {
    location: location
    principalId: sweep_identity.outputs.principalId
  }
}
module tracking_identity 'tracking-identity/tracking-identity.module.bicep' = {
  name: 'tracking-identity'
  scope: rg
  params: {
    location: location
  }
}
module tracking_roles_messaging 'tracking-roles-messaging/tracking-roles-messaging.module.bicep' = {
  name: 'tracking-roles-messaging'
  scope: rg
  params: {
    location: location
    messaging_outputs_name: messaging.outputs.name
    principalId: tracking_identity.outputs.principalId
  }
}
module tracking_roles_postgres 'tracking-roles-postgres/tracking-roles-postgres.module.bicep' = {
  name: 'tracking-roles-postgres'
  scope: rg
  params: {
    location: location
    postgres_outputs_name: postgres.outputs.name
    principalId: tracking_identity.outputs.principalId
    principalName: tracking_identity.outputs.principalName
  }
}
output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = env.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT
output CONTENTSAFETY_CONNECTIONSTRING string = contentsafety.outputs.connectionString
output ENV_AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN
output ENV_AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID
output ENV_AZURE_CONTAINER_REGISTRY_ENDPOINT string = env.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT
output ENV_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = env.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID
output FOUNDRY_CONNECTIONSTRING string = foundry.outputs.connectionString
output GATEWAY_IDENTITY_CLIENTID string = gateway_identity.outputs.clientId
output GATEWAY_IDENTITY_ID string = gateway_identity.outputs.id
output INSIGHTS_APPINSIGHTSCONNECTIONSTRING string = insights.outputs.appInsightsConnectionString
output MESSAGING_SERVICEBUSENDPOINT string = messaging.outputs.serviceBusEndpoint
output MESSAGING_SERVICEBUSHOSTNAME string = messaging.outputs.serviceBusHostName
output NOTIFICATION_IDENTITY_CLIENTID string = notification_identity.outputs.clientId
output NOTIFICATION_IDENTITY_ID string = notification_identity.outputs.id
output OPSASSISTANT_IDENTITY_CLIENTID string = opsassistant_identity.outputs.clientId
output OPSASSISTANT_IDENTITY_ID string = opsassistant_identity.outputs.id
output POSTGRES_CONNECTIONSTRING string = postgres.outputs.connectionString
output POSTGRES_HOSTNAME string = postgres.outputs.hostName
output SEARCH_CONNECTIONSTRING string = search.outputs.connectionString
output SWEEP_IDENTITY_CLIENTID string = sweep_identity.outputs.clientId
output SWEEP_IDENTITY_ID string = sweep_identity.outputs.id
output SWEEP_IDENTITY_PRINCIPALID string = sweep_identity.outputs.principalId
output TRACKING_IDENTITY_CLIENTID string = tracking_identity.outputs.clientId
output TRACKING_IDENTITY_ID string = tracking_identity.outputs.id
