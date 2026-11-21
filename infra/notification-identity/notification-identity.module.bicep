@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource notification_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('notification_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = notification_identity.id

output clientId string = notification_identity.properties.clientId

output principalId string = notification_identity.properties.principalId

output principalName string = notification_identity.name

output name string = notification_identity.name