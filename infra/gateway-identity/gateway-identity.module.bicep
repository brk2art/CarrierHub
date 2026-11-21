@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource gateway_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('gateway_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = gateway_identity.id

output clientId string = gateway_identity.properties.clientId

output principalId string = gateway_identity.properties.principalId

output principalName string = gateway_identity.name

output name string = gateway_identity.name