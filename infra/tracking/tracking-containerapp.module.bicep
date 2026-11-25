@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param tracking_containerimage string

param tracking_identity_outputs_id string

param tracking_containerport string

param postgres_outputs_connectionstring string

param postgres_outputs_hostname string

param messaging_outputs_servicebusendpoint string

param messaging_outputs_servicebushostname string

param tracking_identity_outputs_clientid string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

resource tracking 'Microsoft.App/containerApps@2025-10-02-preview' = {
  name: 'tracking'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: int(tracking_containerport)
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
          image: tracking_containerimage
          name: 'tracking'
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
              value: tracking_containerport
            }
            {
              name: 'ConnectionStrings__trackingdb'
              value: '${postgres_outputs_connectionstring};Database=trackingdb'
            }
            {
              name: 'TRACKINGDB_HOST'
              value: postgres_outputs_hostname
            }
            {
              name: 'TRACKINGDB_PORT'
              value: '5432'
            }
            {
              name: 'TRACKINGDB_URI'
              value: 'postgresql://${postgres_outputs_hostname}/trackingdb'
            }
            {
              name: 'TRACKINGDB_JDBCCONNECTIONSTRING'
              value: 'jdbc:postgresql://${postgres_outputs_hostname}/trackingdb?sslmode=require&authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin'
            }
            {
              name: 'TRACKINGDB_DATABASENAME'
              value: 'trackingdb'
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
              name: 'AZURE_CLIENT_ID'
              value: tracking_identity_outputs_clientid
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
      '${tracking_identity_outputs_id}': { }
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}