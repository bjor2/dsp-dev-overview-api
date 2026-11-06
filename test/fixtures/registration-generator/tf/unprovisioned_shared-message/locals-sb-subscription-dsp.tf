locals {
  subscriptions_dsp = [
    {
      version          = 2
      from             = "demo"
      to               = "demo"
      forward_to_queue = "demo"
      sql_filter       = "(sys.label = 'DemoReferenceChanged' OR sys.Label = 'DemoMoved')"
    },
    {
      from = "other-service"
      to   = "demo"
    },
    {
      version          = 2
      from             = "other-service"
      to               = "demo-ingr"
      forward_to_queue = "demo-ingress"
      sql_filter       = "DataSource = '${local.some_filter}'"
    },
    {
      version          = 3
      from             = "demo"
      to               = "sink"
      forward_to_queue = "sink"
    },
    {
      from       = "unrelated"
      to         = "other-service"
      sql_filter = "1=1"
    }
  ]
}
