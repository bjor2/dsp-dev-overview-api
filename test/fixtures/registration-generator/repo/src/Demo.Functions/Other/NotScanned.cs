public class NotScanned
{
    [Function("NotScanned")]
    public void Run([ServiceBusTrigger("%ServiceBusNotificationQueue%")] string body) { }
}
