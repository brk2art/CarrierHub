@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param mockcarriers_containerimage string

param mockcarriers_containerport string

@secure()
param parcelgo_webhook_secret_value string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

resource mockcarriers 'Microsoft.App/containerApps@2025-10-02-preview' = {
  name: 'mockcarriers'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'parcelgo--webhooksecret'
          value: parcelgo_webhook_secret_value
        }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: int(mockcarriers_containerport)
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
          image: mockcarriers_containerimage
          name: 'mockcarriers'
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
              value: mockcarriers_containerport
            }
            {
              name: 'ParcelGo__WebhookSecret'
              secretRef: 'parcelgo--webhooksecret'
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