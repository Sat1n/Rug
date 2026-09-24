#nullable enable
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Writes a captured BGRA frame as PNG alongside its incident metadata.</summary>
public sealed class AnomalyLogger : IAnomalyLogger
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ILogger<AnomalyLogger> _logger;
    private readonly string _directory;

    public AnomalyLogger(ILogger<AnomalyLogger> logger, string? directory = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _directory = directory ?? Path.Combine(Environment.CurrentDirectory, "logs", "anomalies");
    }

    public async Task<string> LogAnomalyAsync(TaskExecutionContext context, string reason, string detail)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(detail);

        CapturedFrame frame = await context.Capture.GrabFrameAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("No capture frame is available for the anomaly record.");
        byte[] png = await Task.Run(() => EncodePng(frame)).ConfigureAwait(false);

        DateTime timestamp = DateTime.UtcNow;
        string safePluginId = string.Concat(context.PluginId.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        string name = $"{timestamp:yyyyMMdd_HHmmss_fffffff}_{safePluginId}_{context.InstanceId:N}_{Guid.NewGuid():N}";
        string screenshotName = name + ".png";
        string pngPath = Path.Combine(_directory, screenshotName);
        string jsonPath = Path.Combine(_directory, name + ".json");
        var record = new AnomalyLogModel
        {
            Timestamp = timestamp,
            PluginId = context.PluginId,
            InstanceId = context.InstanceId,
            Reason = reason,
            ScriptContext = detail,
            ScreenshotPath = screenshotName,
            AgentResolution = null
        };

        Directory.CreateDirectory(_directory);
        try
        {
            await File.WriteAllBytesAsync(pngPath, png).ConfigureAwait(false);
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(record, JsonOptions)).ConfigureAwait(false);
        }
        catch
        {
            File.Delete(pngPath);
            File.Delete(jsonPath);
            throw;
        }
        _logger.LogWarning("[Anomaly Detected] Plugin {PluginId}, instance {InstanceId}: {Reason}. Record: {Path}",
            context.PluginId, context.InstanceId, reason, jsonPath);
        return jsonPath;
    }

    private static byte[] EncodePng(CapturedFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride < checked(frame.Width * 4) ||
            frame.Pixels.Length < checked(frame.Stride * frame.Height))
            throw new ArgumentException("Capture frame has invalid BGRA dimensions or buffer length.", nameof(frame));

        using var raw = new MemoryStream();
        for (int y = 0; y < frame.Height; y++)
        {
            raw.WriteByte(0); // PNG filter: none
            int offset = y * frame.Stride;
            for (int x = 0; x < frame.Width; x++)
            {
                int pixel = offset + x * 4;
                raw.WriteByte(frame.Pixels[pixel + 2]);
                raw.WriteByte(frame.Pixels[pixel + 1]);
                raw.WriteByte(frame.Pixels[pixel]);
                raw.WriteByte(frame.Pixels[pixel + 3]);
            }
        }

        using var compressed = new MemoryStream();
        raw.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            raw.WriteTo(zlib);

        using var output = new MemoryStream();
        output.Write(PngSignature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], frame.Height);
        header[8] = 8; // bits per channel
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        stream.Write(type);
        stream.Write(data);
        uint crc = 0xffffffff;
        foreach (byte value in type) crc = UpdateCrc(crc, value);
        foreach (byte value in data) crc = UpdateCrc(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        stream.Write(number);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u);
        return crc;
    }
}
