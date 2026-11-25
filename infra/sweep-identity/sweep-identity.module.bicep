@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource sweep_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('sweep_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = sweep_identity.id

output clientId string = sweep_identity.properties.clientId

output principalId string = sweep_identity.properties.principalId

output principalName string = sweep_identity.name

output name string = sweep_identity.name