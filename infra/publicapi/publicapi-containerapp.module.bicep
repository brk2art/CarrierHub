@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param publicapi_containerimage string

param publicapi_containerport string

param entratenantid_value string

param entrapublicapiclientid_value string

param entramerchantuserobjectid_value string

param insights_outputs_appinsightsconnectionstring string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

resource publicapi 'Microsoft.App/containerApps@2025-10-02-preview' = {
  name: 'publicapi'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: int(publicapi_containerport)
        transport: 'http'
      }
      registries: [
        {
          server: env_outputs_azure_container_registry_endpoint
          identity: env_outputs_azure_container_registry_managed_identity_id
        }
      ]
      runtime: {
        dotnet: {
          autoConfigureDataProtection: true
        }
      }
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          probes: [
            {
              failureThreshold: 30
              httpGet: {
                path: '/alive'
                port: 8080
              }
              periodSeconds: 5
              timeoutSeconds: 3
              type: 'Startup'
            }
            {
              failureThreshold: 3
              httpGet: {
                path: '/health'
                port: 8080
              }
              periodSeconds: 10
              timeoutSeconds: 5
              type: 'Readiness'
            }
            {
              failureThreshold: 3
              httpGet: {
                path: '/alive'
                port: 8080
              }
              periodSeconds: 10
              timeoutSeconds: 3
              type: 'Liveness'
            }
          ]
          image: publicapi_containerimage
          name: 'publicapi'
          env: [
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
              value: 'in_memory'
            }
            {
              name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
              value: 'true'
            }
            {
              name: 'HTTP_PORTS'
              value: publicapi_containerport
            }
            {
              name: 'TRACKING_HTTP'
              value: 'https://tracking.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__tracking__http__0'
              value: 'https://tracking.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'TRACKING_HTTPS'
              value: 'https://tracking.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__tracking__https__0'
              value: 'https://tracking.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'NOTIFICATION_HTTP'
              value: 'https://notification.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__notification__http__0'
              value: 'https://notification.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'NOTIFICATION_HTTPS'
              value: 'https://notification.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__notification__https__0'
              value: 'https://notification.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'Entra__TenantId'
              value: entratenantid_value
            }
            {
              name: 'Entra__PublicApi__ClientId'
              value: entrapublicapiclientid_value
            }
            {
              name: 'Entra__MerchantUsers__0__ObjectId'
              value: entramerchantuserobjectid_value
            }
            {
              name: 'Entra__MerchantUsers__0__MerchantId'
              value: 'merchant-1'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: insights_outputs_appinsightsconnectionstring
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
      }
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}