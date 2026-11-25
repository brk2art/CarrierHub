@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param principalId string

resource foundry 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: 'docchat-foundry'
}

resource foundry_CognitiveServicesOpenAIUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalType: 'ServicePrincipal'
  }
  scope: foundry
}