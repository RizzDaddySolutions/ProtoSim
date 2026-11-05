using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.Mqtt;

public sealed class MqttDataModel : IDeviceDataModel
{
    private readonly ConcurrentDictionary<string, MqttTopicBinding> _bindings = new(StringComparer.Ordinal);

    public IReadOnlyList<IDataArea> Areas { get; } = [];

    public event Action<string, object>? ValueChanged;

    public ITagBinding Bind(string address, TagDataType type, WordOrder order)
    {
        var topic = NormalizeTopic(address);
        var binding = _bindings.GetOrAdd(topic, _ => new MqttTopicBinding(this, topic, type));
        if (binding.DataType != type)
            throw new FormatException($"Topic '{topic}' is already bound as {binding.DataType}; use one data type per topic.");
        return binding;
    }

    internal void Set(string topic, object value)
    {
        ValueChanged?.Invoke(topic, value);
    }

    internal static string NormalizeTopic(string address)
    {
        var topic = address.Trim().Trim('/');
        if (topic.Length == 0 || topic.Contains('#') || topic.Contains('+') || topic.Contains('\0'))
            throw new FormatException("MQTT tag topics must be non-empty and cannot contain wildcard characters.");
        return topic;
    }

    internal bool TryWrite(string topic, string payload)
    {
        if (!_bindings.TryGetValue(topic, out var binding)) return false;
        binding.Write(payload);
        return true;
    }

    private sealed class MqttTopicBinding(MqttDataModel model, string topic, TagDataType type) : ITagBinding
    {
        private object _value = type == TagDataType.String ? string.Empty : ValueConverter.FromDouble(0, type);
        public TagDataType DataType => type;
        public object Read() => _value;
        public void Write(object value)
        {
            _value = ValueConverter.Coerce(value, type);
            model.Set(topic, _value);
        }
    }
}
