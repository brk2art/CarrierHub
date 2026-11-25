@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param gateway_containerimage string

param gateway_identity_outputs_id string

param gateway_containerport string

@secure()
param redis_password_value string

param messaging_outputs_servicebusendpoint string

param messaging_outputs_servicebushostname string

@secure()
param parcelgo_webhook_secret_value string

param gateway_identity_outputs_clientid string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

resource gateway 'Microsoft.App/containerApps@2025-10-02-preview' = {
  name: 'gateway'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'connectionstrings--redis'
          value: 'redis:6379,password=${redis_password_value}'
        }
        {
          name: 'redis-password'
          value: redis_password_value
        }
        {
          name: 'redis-uri'
          value: 'redis://:${uriComponent(redis_password_value)}@redis:6379'
        }
        {
          name: 'parcelgo--webhooksecret'
          value: parcelgo_webhook_secret_value
        }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: int(gateway_containerport)
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
          image: gateway_containerimage
          name: 'gateway'
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
              value: gateway_containerport
            }
            {
              name: 'MOCKCARRIERS_HTTP'
              value: 'https://mockcarriers.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__mockcarriers__http__0'
              value: 'https://mockcarriers.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'MOCKCARRIERS_HTTPS'
              value: 'https://mockcarriers.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__mockcarriers__https__0'
              value: 'https://mockcarriers.internal.${env_outputs_azure_container_apps_environment_default_domain}'
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
              name: 'ConnectionStrings__redis'
              secretRef: 'connectionstrings--redis'
            }
            {
              name: 'REDIS_HOST'
              value: 'redis'
            }
            {
              name: 'REDIS_PORT'
              value: '6379'
            }
            {
              name: 'REDIS_PASSWORD'
              secretRef: 'redis-password'
            }
            {
              name: 'REDIS_URI'
              secretRef: 'redis-uri'
            }
            {
              name: 'ConnectionStrings__messaging'
              value: messaging_outputs_servicebusendpoint
            }
            {
              name: 'MESSAGING_HOST'
              value: messaging_outputs_servicebushostname
            }
            {
              name: 'MESSAGING_URI'
              value: messaging_outputs_servicebusendpoint
            }
            {
              name: 'ParcelGo__WebhookSecret'
              secretRef: 'parcelgo--webhooksecret'
            }
            {
              name: 'ParcelGo__CallbackBaseUrl'
              value: 'https://gateway.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: gateway_identity_outputs_clientid
            }
            {
              name: 'AZURE_TOKEN_CREDENTIALS'
              value: 'ManagedIdentityCredential'
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
      '${gateway_identity_outputs_id}': { }
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}