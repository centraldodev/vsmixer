using System.Text.Json.Serialization;

namespace VSMixer.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MasterBus
{
    A,
    B
}
