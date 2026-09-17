using System.Text.Json.Serialization;

namespace Potok.Backend.Core.Models.SearchEngine.Tracks;

public sealed class FfStream
{
    [JsonPropertyName("index")] public int Index { get; set; }

    [JsonPropertyName("codec_name")] public string? CodecName { get; set; }

    [JsonPropertyName("codec_long_name")] public string? CodecLongName { get; set; }

    [JsonPropertyName("codec_type")] public string? CodecType { get; set; }

    [JsonPropertyName("width")] public int? Width { get; set; }

    [JsonPropertyName("height")] public int? Height { get; set; }

    [JsonPropertyName("coded_width")] public int? CodedWidth { get; set; }

    [JsonPropertyName("coded_height")] public int? CodedHeight { get; set; }

    [JsonPropertyName("sample_fmt")] public string? SampleFmt { get; set; }

    [JsonPropertyName("sample_rate")] public string? SampleRate { get; set; }

    [JsonPropertyName("channels")] public int? Channels { get; set; }

    [JsonPropertyName("channel_layout")] public string? ChannelLayout { get; set; }

    [JsonPropertyName("bit_rate")] public string? BitRate { get; set; }

    [JsonPropertyName("tags")] public FfTags? Tags { get; set; }
}