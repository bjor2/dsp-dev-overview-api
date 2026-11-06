using Demo.Messaging;

namespace Demo.Api.Controllers;

public class DemoController : ControllerBase
{
    private readonly IDemoQueueClient _queue;

    public DemoController(IDemoQueueClient queue, IDemoIngressQueueClient ingress) => _queue = queue;

    [HttpPost]
    public async Task<IActionResult> Post(string id)
    {
        await _queue.SendAsync(new DemoCommand { Id = id });
        return Accepted();
    }
}
