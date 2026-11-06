using Demo.Messaging;

namespace Demo.Application;

public interface IDemoService
{
    Task Handle(DemoCommand command);
}

public class DemoService : IDemoService
{
    private readonly IDemoTopicClient _topic;
    private readonly IDemoQueueClient _queue;

    public DemoService(IDemoTopicClient topic, IDemoQueueClient queue)
    {
        _topic = topic;
        _queue = queue;
    }

    public async Task Handle(DemoCommand command)
    {
        await _topic.SendAsync(new DemoChanged { Id = command.Id });
        // await _queue.SendAsync(new CommentedOut()); — comments never count
        if (command.Retry)
            await _queue.SendAsync(new DemoFollowUp { Id = command.Id }); // self-requeue: DemoQueueHandler consumes q:demo
    }
}
