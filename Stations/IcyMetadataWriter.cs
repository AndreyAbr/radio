using System.Text;

namespace RadioServer.Stations;

/// <summary>
/// Обеспечивает вставку ICY-метаданных (StreamTitle) в аудиопоток в соответствии
/// со спецификацией протокола Shoutcast / Icecast при запросе заголовка Icy-MetaData: 1.
/// </summary>
public class IcyMetadataWriter
{
    private readonly Stream _destination;
    private readonly int _metaInterval;
    private readonly Func<string> _getTrackTitle;
    private int _bytesUntilMeta;
    private string? _lastTitle;

    public IcyMetadataWriter(Stream destination, int metaInterval, Func<string> getTrackTitle)
    {
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
        _metaInterval = metaInterval > 0 ? metaInterval : 16384;
        _getTrackTitle = getTrackTitle ?? throw new ArgumentNullException(nameof(getTrackTitle));
        _bytesUntilMeta = _metaInterval;
    }

    /// <summary>
    /// Интервал байт между блоками метаданных.
    /// </summary>
    public int MetaInterval => _metaInterval;

    /// <summary>
    /// Записывает порцию аудиоданных, автоматически инъецируя блоки ICY-метаданных
    /// через каждые MetaInterval байт.
    /// </summary>
    public async ValueTask WriteAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct = default)
    {
        while (audio.Length > 0)
        {
            int toWrite = Math.Min(audio.Length, _bytesUntilMeta);
            await _destination.WriteAsync(audio.Slice(0, toWrite), ct);
            _bytesUntilMeta -= toWrite;
            audio = audio.Slice(toWrite);

            if (_bytesUntilMeta == 0)
            {
                await WriteMetadataBlockAsync(ct);
                _bytesUntilMeta = _metaInterval;
            }
        }
    }

    /// <summary>
    /// Записывает блок метаданных (1 байт длины N + N*16 байт строки StreamTitle='...';).
    /// Если трек не изменился с момента прошлой отправки, отправляется 1 байт 0x00.
    /// </summary>
    private async ValueTask WriteMetadataBlockAsync(CancellationToken ct)
    {
        string currentTitle = _getTrackTitle() ?? "Unknown";

        // Если метаданные изменились (или это первая отправка)
        if (_lastTitle == null || !string.Equals(_lastTitle, currentTitle, StringComparison.Ordinal))
        {
            _lastTitle = currentTitle;
            var safeTitle = currentTitle.Replace("'", "");
            var payload = $"StreamTitle='{safeTitle}';";
            var payloadBytes = Encoding.UTF8.GetBytes(payload);

            // Длина должна быть кратна 16 байтам
            int paddedLen = ((payloadBytes.Length + 15) / 16) * 16;
            if (paddedLen > 255 * 16)
            {
                paddedLen = 255 * 16; // Максимум для 1-байтового заголовка длины
            }

            byte lengthByte = (byte)(paddedLen / 16);
            byte[] block = new byte[1 + paddedLen];
            block[0] = lengthByte;
            Buffer.BlockCopy(payloadBytes, 0, block, 1, Math.Min(payloadBytes.Length, paddedLen));

            await _destination.WriteAsync(block.AsMemory(), ct);
        }
        else
        {
            // Название трека не менялось: пишем 1 байт 0x00 (длина = 0)
            await _destination.WriteAsync(new byte[] { 0x00 }.AsMemory(), ct);
        }
    }
}
