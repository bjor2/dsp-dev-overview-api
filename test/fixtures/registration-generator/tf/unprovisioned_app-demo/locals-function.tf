locals {
  function_app = {
    runtime_version             = 4
    dotnet_version              = "8.0"
    use_dotnet_isolated_runtime = true

    app_settings = {
      ServiceBusConnectionString  = format("@Microsoft.KeyVault(SecretUri=%ssecrets/%s)", data.a.outputs.uri, data.b.outputs.name)
      ServiceBusDemoQueue         = "${module.naming.servicebus_queue.name}-shared-demo"
      ServiceBusDemoTopic         = "${module.naming.servicebus_topic.name}-shared-demo"
      ServiceBusDemoSubscription  = "demo-to-demo-subscription"
      ServiceBusNotificationQueue = "${module.naming.servicebus_queue.name}-shared-notification"
      /* block
         comment */
      DisableSomething = false
      Tenant__0__Name  = "Optimera"
    }
  }
}
