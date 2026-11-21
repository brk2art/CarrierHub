@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param mcphttp_containerimage string

param mcphttp_containerport string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

resource mcphttp 'Microsoft.App/containerApps@2025-10-02-preview' = {
  name: 'mcphttp'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: int(mcphttp_containerport)
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
          image: mcphttp_containerimage
          name: 'mcphttp'
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
              value: mcphttp_containerport
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