@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource foundry 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: 'docchat-foundry'
}

output connectionString string = 'Endpoint=${foundry.properties.endpoint}'

output endpoint string = foundry.properties.endpoint

output name string = foundry.name

output id string = foundry.id