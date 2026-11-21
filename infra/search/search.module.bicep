@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource search 'Microsoft.Search/searchServices@2023-11-01' existing = {
  name: 'docchat-search-burak'
}

output connectionString string = 'Endpoint=https://docchat-search-burak.search.windows.net'

output endpoint string = 'https://docchat-search-burak.search.windows.net'

output name string = search.name

output id string = search.id