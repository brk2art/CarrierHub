@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource tracking_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('tracking_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = tracking_identity.id

output clientId string = tracking_identity.properties.clientId

output principalId string = tracking_identity.properties.principalId

output principalName string = tracking_identity.name

output name string = tracking_identity.name