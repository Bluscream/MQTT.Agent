using System;
using System.Threading;
using System.Threading.Tasks;
using MqttAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;

namespace MqttAgent.Services
{
    public interface IMqttManager : Microsoft.Extensions.Hosting.IHostedService
    {
        Task EnqueueAsync(string topic, string payload, bool retain = true);
        Task SubscribeAsync(string topic, Func<MqttApplicationMessageReceivedEventArgs, Task> handler);
        bool IsConnected { get; }
        string UniqueId { get; }
        string EntityId { get; }
    }

    public class MqttManager : IMqttManager
    {
        private readonly ILogger<MqttManager> _logger;
        private readonly MqttOptions _options;
        private IManagedMqttClient? _mqttClient;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<MqttApplicationMessageReceivedEventArgs, Task>> _topicHandlers = new();
        public string UniqueId { get; }
        public string EntityId { get; }

        public MqttManager(ILogger<MqttManager> logger, IOptions<MqttOptions> options)
        {
            _logger = logger;
            _options = options.Value;

            UniqueId = $"{Environment.MachineName.ToLowerInvariant()}_status";
            
            var rawName = _options.EntityId ?? Environment.MachineName.ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
            EntityId = rawName.EndsWith("_action") ? rawName : $"{rawName}_action";
        }

        public bool IsConnected => _mqttClient?.IsConnected ?? false;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var mqttFactory = new MqttFactory();
            _mqttClient = mqttFactory.CreateManagedMqttClient();

            var stateTopic = $"homeassistant/select/{UniqueId}/state";

            var clientOptionsBuilder = new MqttClientOptionsBuilder()
                .WithClientId($"{UniqueId}_{Guid.NewGuid():N}")
                .WithTcpServer(_options.Ip, _options.Port)
                .WithWillTopic(stateTopic)
                .WithWillPayload("unavailable")
                .WithWillQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce)
                .WithWillRetain();

            if (!string.IsNullOrEmpty(_options.User) && !string.IsNullOrEmpty(_options.Password))
            {
                clientOptionsBuilder.WithCredentials(_options.User, _options.Password);
            }

            var managedOptions = new ManagedMqttClientOptionsBuilder()
                .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
                .WithClientOptions(clientOptionsBuilder.Build())
                .Build();

            _mqttClient.ConnectedAsync += async e =>
            {
                _logger.LogInformation("Connected to MQTT broker at {Ip}:{Port}", _options.Ip, _options.Port);
                if (!_topicHandlers.IsEmpty)
                {
                    var filters = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(_topicHandlers.Keys, t => new MqttTopicFilterBuilder().WithTopic(t).Build()));
                    await _mqttClient.SubscribeAsync(filters);
                }
            };

            _mqttClient.ConnectingFailedAsync += e =>
            {
                _logger.LogWarning("Failed to connect to MQTT broker: {Message}", e.Exception.Message);
                return Task.CompletedTask;
            };

            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                var topic = e.ApplicationMessage.Topic;
                if (_topicHandlers.TryGetValue(topic, out var handler))
                {
                    try { await handler(e); }
                    catch (Exception ex) { _logger.LogError(ex, "Error handling message for topic {Topic}", topic); }
                }
            };

            await _mqttClient.StartAsync(managedOptions);

            // Wait for initial connection
            int timeout = 50; 
            while (!_mqttClient.IsConnected && timeout-- > 0 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_mqttClient != null)
            {
                await _mqttClient.StopAsync();
                _mqttClient.Dispose();
            }
        }

        public async Task SubscribeAsync(string topic, Func<MqttApplicationMessageReceivedEventArgs, Task> handler)
        {
            _topicHandlers[topic] = handler;
            if (_mqttClient != null && _mqttClient.IsStarted)
            {
                await _mqttClient.SubscribeAsync(new[] { new MqttTopicFilterBuilder().WithTopic(topic).Build() });
            }
        }

        public async Task EnqueueAsync(string topic, string payload, bool retain = true)
        {
            if (_mqttClient == null) return;

            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(retain)
                .Build();

            await _mqttClient.EnqueueAsync(msg);
        }
    }
}
