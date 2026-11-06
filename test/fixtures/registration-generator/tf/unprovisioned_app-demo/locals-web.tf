locals {
  web_app = {
    dotnet_version = "8.0"
    api_versions   = ["1"]

    app_settings = {
      ConnectionStrings__Demo    = format("@Microsoft.KeyVault(SecretUri=%ssecrets/%s)", data.terraform_remote_state.shared_misc.outputs.keyvault["shar"].uri, "demo-db") # trailing comment
      ServiceBusConnectionString = format("@Microsoft.KeyVault(SecretUri=%ssecrets/%s)", data.a.outputs.uri, data.b.outputs.name)
      ServiceBusDemoQueue        = "${module.naming.servicebus_queue.name}-shared-demo"
      ServiceBusDemoIngressQueue = "${module.naming.servicebus_queue.name}-shared-demo-ingress"
      ServiceBusDemoTopic        = "${module.naming.servicebus_topic.name}-shared-demo"

      // C-style comment
      ServiceBaseUrl = format("%s/demo/", data.terraform_remote_state.shared_misc.outputs.api_management_base_url)
    }
  }
}
