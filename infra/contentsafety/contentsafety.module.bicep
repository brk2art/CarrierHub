@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource contentsafety 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: 'carrierhub-contentsafety'
}

output connectionString string = 'Endpoint=${contentsafety.properties.endpoint}'

output endpoint string = contentsafety.properties.endpoint

output name string = contentsafety.name

output id string = contentsafety.id