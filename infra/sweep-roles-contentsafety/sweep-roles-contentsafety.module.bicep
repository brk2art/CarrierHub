@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param principalId string

resource contentsafety 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: 'carrierhub-contentsafety'
}

resource contentsafety_CognitiveServicesUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(contentsafety.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalType: 'ServicePrincipal'
  }
  scope: contentsafety
}