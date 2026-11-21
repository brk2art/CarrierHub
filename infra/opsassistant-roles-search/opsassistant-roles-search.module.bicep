@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param principalId string

resource search 'Microsoft.Search/searchServices@2023-11-01' existing = {
  name: 'docchat-search-burak'
}

resource search_SearchIndexDataReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '1407120a-92aa-4202-b7e9-c0e197c71c8f'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '1407120a-92aa-4202-b7e9-c0e197c71c8f')
    principalType: 'ServicePrincipal'
  }
  scope: search
}