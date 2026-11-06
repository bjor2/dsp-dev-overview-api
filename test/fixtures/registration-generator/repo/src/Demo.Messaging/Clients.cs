namespace Demo.Messaging;

public interface IDemoQueueClient { Task SendAsync(object message); }

public class DemoQueueClient : QueueClientBase, IDemoQueueClient
{
    public DemoQueueClient(DemoSettings settings, IAzureClientFactory<ServiceBusClient> factory)
        : base(factory, settings.ClientInstanceName, settings.DemoQueue) { }
}

public interface IDemoTopicClient { Task SendAsync(object message); }

internal class DemoTopicClient(DemoSettings settings, IAzureClientFactory<ServiceBusClient> factory)
    : TopicClientBase(factory, settings.ClientInstanceName, settings.DemoTopic), IDemoTopicClient
{
}

// Registered by the Api but never used to send (ADDENDA-008 OQ-2 analogue: no edge).
public interface IDemoIngressQueueClient { Task SendAsync(object message); }

public class DemoIngressQueueClient : QueueClientBase, IDemoIngressQueueClient
{
    public DemoIngressQueueClient(DemoSettings settings, IAzureClientFactory<ServiceBusClient> factory)
        : base(factory, settings.ClientInstanceName, settings.IngressQueue) { }
}
