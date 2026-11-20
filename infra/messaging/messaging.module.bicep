@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sku string = 'Standard'

resource messaging 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: take('messaging-${uniqueString(resourceGroup().id)}', 50)
  location: location
  properties: {
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
  sku: {
    name: sku
  }
  tags: {
    'aspire-resource-name': 'messaging'
  }
}

resource carrier_events 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  name: 'carrier-events'
  properties: {
    deadLetteringOnMessageExpiration: true
    duplicateDetectionHistoryTimeWindow: 'PT5M'
    maxDeliveryCount: 10
    requiresDuplicateDetection: true
    requiresSession: true
  }
  parent: messaging
}

resource shipment_events 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  name: 'shipment-events'
  properties: {
    duplicateDetectionHistoryTimeWindow: 'PT5M'
    requiresDuplicateDetection: true
  }
  parent: messaging
}

resource notifications 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  name: 'notifications'
  properties: {
    deadLetteringOnMessageExpiration: true
    maxDeliveryCount: 5
    requiresSession: true
  }
  parent: shipment_events
}

output serviceBusEndpoint string = messaging.properties.serviceBusEndpoint

output serviceBusHostName string = split(replace(messaging.properties.serviceBusEndpoint, 'https://', ''), ':')[0]

output name string = messaging.name

output id string = messaging.id