namespace Demo.Ingress.Functions.Functions;

public class IngressHandlers
{
    [Function("IngressQueueHandler")]
    public async Task Ingress(
        [ServiceBusTrigger("%ServiceBusDemoIngressQueue%", Connection = "ServiceBusNamespace")]
        ServiceBusReceivedMessage message)
    {
        await Task.CompletedTask;
    }

    [Function(nameof(SnapshotQueueHandler))]
    public async Task SnapshotQueueHandler(
        [ServiceBusTrigger(
            "%ServiceBusDemoSnapshotQueue%",
            Connection = "ServiceBusNamespace",
            IsBatched = false)]
        ServiceBusReceivedMessage message)
    {
        await Task.CompletedTask;
    }
}
