using System.Text.Json.Serialization;

namespace PiiMasker.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ColumnAction
{
    Shuffle,
    Replace,
    Calculate
}
