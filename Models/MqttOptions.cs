namespace MqttAgent.Models
{
    public class MqttOptions
    {
        public string Ip { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 1883;
        public string? User { get; set; }
        public string? Password { get; set; }
        public string? EntityId { get; set; }
    }
}
