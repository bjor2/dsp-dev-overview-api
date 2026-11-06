$settings = [ordered]@{
    'ServiceBusConnectionString'   = $sb
    'ServiceBusDemoQueue'          = 'demo'
    'ServiceBusDemoIngressQueue'   = 'demo-ingress'
    'ServiceBusDemoSnapshotQueue'  = 'demo-snapshot'
    'ServiceBusNotificationQueue'  = 'notification'
    # topic and queue names share one namespace locally
    'ServiceBusDemoTopic'          = 'demo-topic'
    'AzureWebJobsStorage'          = 'UseDevelopmentStorage=true'
}
