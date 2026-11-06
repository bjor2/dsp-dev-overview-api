namespace Demo.Messaging;

public class DemoSettings
{
    public string ClientInstanceName => "Demo";
    public string DemoQueue { get; set; } = "";
    public string DemoTopic { get; set; } = "";
    public string IngressQueue { get; set; } = "";
}

public static class ServiceCollectionExtensions
{
    public static void AddDemoMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new DemoSettings
        {
            DemoQueue = configuration.GetValue<string>("ServiceBusDemoQueue"),
            DemoTopic = configuration.GetValue<string>("ServiceBusDemoTopic"),
            IngressQueue = configuration.GetValue<string>("ServiceBusDemoIngressQueue"),
        });
    }
}
