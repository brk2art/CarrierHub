@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource opsassistant_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('opsassistant_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = opsassistant_identity.id

output clientId string = opsassistant_identity.properties.clientId

output principalId string = opsassistant_identity.properties.principalId

output principalName string = opsassistant_identity.name

output name string = opsassistant_identity.name