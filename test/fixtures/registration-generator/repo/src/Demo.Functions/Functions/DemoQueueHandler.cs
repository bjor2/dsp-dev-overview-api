using Demo.Application;
using Demo.Messaging;

namespace Demo.Functions.Functions;

public class DemoQueueHandler : MessageHandler
{
    private readonly IDemoService _service;

    // The queue client goes to the Dahl.Core MessageHandler base (HandleWithRetry): retry infrastructure, not drawn.
    public DemoQueueHandler(IDemoService service, IDemoQueueClient demoQueueClient, ILogger<DemoQueueHandler> logger)
        : base(demoQueueClient, logger)
    {
        _service = service;
    }

    [Function("DemoQueueHandler")]
    public async Task Run(
       [ServiceBusTrigger("%ServiceBusDemoQueue%", Connection = "ServiceBusConnectionString")] ServiceBusReceivedMessage message, CancellationToken ct)
    {
        await HandleWithRetry<DemoCommand>(message, m => _service.Handle(m), cancellationToken: ct);
    }
}
