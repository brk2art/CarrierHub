@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param sweep_containerimage string

param sweep_identity_outputs_id string

param postgres_outputs_connectionstring string

param postgres_outputs_hostname string

param entrapublicapiclientid_value string

param insights_outputs_appinsightsconnectionstring string

param sweep_identity_outputs_clientid string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

resource sweep 'Microsoft.App/jobs@2025-07-01' = {
  name: 'sweep'
  location: location
  properties: {
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 1800
      scheduleTriggerConfig: {
        cronExpression: '0 6 * * *'
      }
      registries: [
        {
          server: env_outputs_azure_container_registry_endpoint
          identity: env_outputs_azure_container_registry_managed_identity_id
        }
      ]
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: sweep_containerimage
          name: 'sweep'
          args: [
            '--sweep-once'
          ]
          env: [
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
              value: 'in_memory'
            }
            {
              name: 'PUBLICAPI_HTTP'
              value: 'https://publicapi.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__publicapi__http__0'
              value: 'https://publicapi.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'PUBLICAPI_HTTPS'
              value: 'https://publicapi.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__publicapi__https__0'
              value: 'https://publicapi.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'ConnectionStrings__assistantdb'
              value: '${postgres_outputs_connectionstring};Database=assistantdb'
            }
            {
              name: 'ASSISTANTDB_HOST'
              value: postgres_outputs_hostname
            }
            {
              name: 'ASSISTANTDB_PORT'
              value: '5432'
            }
            {
              name: 'ASSISTANTDB_URI'
              value: 'postgresql://${postgres_outputs_hostname}/assistantdb'
            }
            {
              name: 'ASSISTANTDB_JDBCCONNECTIONSTRING'
              value: 'jdbc:postgresql://${postgres_outputs_hostname}/assistantdb?sslmode=require&authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin'
            }
            {
              name: 'ASSISTANTDB_DATABASENAME'
              value: 'assistantdb'
            }
            {
              name: 'CALENDARMCP_HTTP'
              value: 'https://calendarmcp.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__calendarmcp__http__0'
              value: 'https://calendarmcp.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'CALENDARMCP_HTTPS'
              value: 'https://calendarmcp.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__calendarmcp__https__0'
              value: 'https://calendarmcp.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'Assistant__Provider'
              value: 'AzureOpenAI'
            }
            {
              name: 'Assistant__AzureOpenAI__Endpoint'
              value: 'https://docchat-foundry.cognitiveservices.azure.com/'
            }
            {
              name: 'Assistant__Search__Endpoint'
              value: 'https://docchat-search-burak.search.windows.net'
            }
            {
              name: 'Assistant__ContentSafety__Endpoint'
              value: 'https://carrierhub-contentsafety.cognitiveservices.azure.com/'
            }
            {
              name: 'Assistant__ContentSafety__Mode'
              value: 'Observe'
            }
            {
              name: 'Assistant__Sweep__Audience'
              value: 'api://${entrapublicapiclientid_value}'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: insights_outputs_appinsightsconnectionstring
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: sweep_identity_outputs_clientid
            }
            {
              name: 'AZURE_TOKEN_CREDENTIALS'
              value: 'ManagedIdentityCredential'
            }
          ]
        }
      ]
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${sweep_identity_outputs_id}': { }
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}