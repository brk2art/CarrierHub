@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param notification_containerimage string

param notification_identity_outputs_id string

param notification_containerport string

param postgres_outputs_connectionstring string

param postgres_outputs_hostname string

param messaging_outputs_servicebusendpoint string

param messaging_outputs_servicebushostname string

param insights_outputs_appinsightsconnectionstring string

param notification_identity_outputs_clientid string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

resource notification 'Microsoft.App/containerApps@2025-10-02-preview' = {
  name: 'notification'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: int(notification_containerport)
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
          image: notification_containerimage
          name: 'notification'
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
              value: notification_containerport
            }
            {
              name: 'ConnectionStrings__notificationdb'
              value: '${postgres_outputs_connectionstring};Database=notificationdb'
            }
            {
              name: 'NOTIFICATIONDB_HOST'
              value: postgres_outputs_hostname
            }
            {
              name: 'NOTIFICATIONDB_PORT'
              value: '5432'
            }
            {
              name: 'NOTIFICATIONDB_URI'
              value: 'postgresql://${postgres_outputs_hostname}/notificationdb'
            }
            {
              name: 'NOTIFICATIONDB_JDBCCONNECTIONSTRING'
              value: 'jdbc:postgresql://${postgres_outputs_hostname}/notificationdb?sslmode=require&authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin'
            }
            {
              name: 'NOTIFICATIONDB_DATABASENAME'
              value: 'notificationdb'
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
              name: 'Database__MigrateOnStartup'
              value: 'true'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: insights_outputs_appinsightsconnectionstring
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: notification_identity_outputs_clientid
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
      '${notification_identity_outputs_id}': { }
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}