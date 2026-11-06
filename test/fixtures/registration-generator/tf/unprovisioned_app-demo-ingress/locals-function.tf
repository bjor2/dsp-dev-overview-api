locals {
  function_app = {
    runtime_version = 4
    app_settings = {
      WEBSITE_TIME_ZONE = "Europe/Oslo"
      ConnectionStrings__SqlIngress = "Server=${data.x.outputs.sql["pool"].fqdn}; Database=${local.project_name}-${var.environment}-demo-ingress-sql-db"

      ServiceBusDemoIngressQueue  = "${module.naming.servicebus_queue.name}-shared-demo-ingress"
      ServiceBusDemoSnapshotQueue = "${module.naming.servicebus_queue.name}-shared-demo-snapshot"
    }
  }
}
