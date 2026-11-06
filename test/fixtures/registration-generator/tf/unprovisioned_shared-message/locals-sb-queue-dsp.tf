locals {
  queues_dsp = [
    {
      name          = "demo"
      lock_duration = "PT5M"
      size          = 10240
    },
    {
      # comment inside a list
      name               = "demo-ingress"
      max_delivery_count = 5
    },
    {
      name = "demo-snapshot"
    },
    {
      name = "notification"
    },
    {
      name = "sink",
    }
  ]
}
