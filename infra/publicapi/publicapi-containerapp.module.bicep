@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param publicapi_containerimage string

param publicapi_containerport string

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