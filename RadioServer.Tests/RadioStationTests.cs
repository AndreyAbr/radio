using RadioServer.Stations;
using Xunit;

namespace RadioServer.Tests;

public class RadioStationTests : IDisposable
{
    private readonly string _tempDir;

    public RadioStationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RadioStationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    private string CreateDummyTrack(string fileName)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00, 0x00, 0x00 });
        return path;
    }

    [Fact]
    public void Station_Initializes_With_Empty_Directory()
    {
        var station = new RadioStation("test_empty", "Empty Station", _tempDir);

        Assert.Equal("test_empty", station.Id);
        Assert.Equal("Empty Station", station.Name);
        Assert.Equal(0, station.TrackCount);
        Assert.Equal("Эфир пуст (нет треков)", station.GetCurrentTrackName());
        Assert.Equal(0, station.ListenersCount);
    }

    [Fact]
    public void Station_Discovers_Tracks_And_Sorts_Them()
    {
        CreateDummyTrack("track_b.mp3");
        CreateDummyTrack("track_a.mp3");
        CreateDummyTrack("track_c.ogg");

        var station = new RadioStation("test_tracks", "Test Station", _tempDir);

        Assert.Equal(3, station.TrackCount);
        var tracks = station.GetTracks();
        Assert.Equal(new[] { "track_a.mp3", "track_b.mp3", "track_c.ogg" }, tracks);
        Assert.Equal("track_a.mp3", station.GetCurrentTrackName());
    }

    [Fact]
    public void AddListener_And_RemoveListener_Work_Atomically()
    {
        var station = new RadioStation("test_listeners", "Test Station", _tempDir);

        Assert.Equal(0, station.ListenersCount);
        Assert.Equal(1, station.AddListener());
        Assert.Equal(2, station.AddListener());
        Assert.Equal(2, station.ListenersCount);

        Assert.Equal(1, station.RemoveListener());
        Assert.Equal(0, station.RemoveListener());
        Assert.Equal(0, station.RemoveListener()); // Защита от отрицательного значения
        Assert.Equal(0, station.ListenersCount);
    }

    [Fact]
    public void Concurrent_Listeners_Add_And_Remove_Maintains_Consistency()
    {
        var station = new RadioStation("test_concurrency", "Concurrency Station", _tempDir);

        Parallel.For(0, 100, _ =>
        {
            station.AddListener();
            station.AddListener();
            station.RemoveListener();
        });

        Assert.Equal(100, station.ListenersCount);

        Parallel.For(0, 100, _ =>
        {
            station.RemoveListener();
        });

        Assert.Equal(0, station.ListenersCount);
    }

    [Fact]
    public void SkipTrack_Advances_Track_And_Increments_Version()
    {
        CreateDummyTrack("track1.mp3");
        CreateDummyTrack("track2.mp3");
        CreateDummyTrack("track3.mp3");

        var station = new RadioStation("test_skip", "Skip Station", _tempDir);
        int initialVersion = station.TrackVersion;
        Assert.Equal("track1.mp3", station.GetCurrentTrackName());

        var next = station.SkipTrack();
        Assert.Equal("track2.mp3", next);
        Assert.Equal("track2.mp3", station.GetCurrentTrackName());
        Assert.True(station.TrackVersion > initialVersion);

        station.SkipTrack();
        Assert.Equal("track3.mp3", station.GetCurrentTrackName());

        station.SkipTrack(); // Wrap around
        Assert.Equal("track1.mp3", station.GetCurrentTrackName());
    }

    [Fact]
    public void OnTrackFinished_Advances_Station_Once_For_Same_Version()
    {
        CreateDummyTrack("track1.mp3");
        CreateDummyTrack("track2.mp3");

        var station = new RadioStation("test_rotation", "Rotation Station", _tempDir);
        var (path, trackIndex, version, _) = station.GetTrackForClient(-1, 0);

        Assert.Equal("track1.mp3", Path.GetFileName(path));
        Assert.Equal(0, trackIndex);

        // Первый клиент завершил трек
        station.OnTrackFinished(trackIndex, version);
        Assert.Equal("track2.mp3", station.GetCurrentTrackName());

        // Второй клиент завершил тот же трек с тем же version — станция не должна перескакивать снова
        station.OnTrackFinished(trackIndex, version);
        Assert.Equal("track2.mp3", station.GetCurrentTrackName());
    }

    [Fact]
    public void Missing_File_Is_Handled_Gracefully_By_ReFreshPlaylist()
    {
        var f1 = CreateDummyTrack("track1.mp3");
        var f2 = CreateDummyTrack("track2.mp3");

        var station = new RadioStation("test_missing", "Missing Station", _tempDir);
        Assert.Equal(2, station.TrackCount);

        // Удаляем первый файл с диска
        File.Delete(f1);

        // Клиент запрашивает трек
        var (path, _, _, _) = station.GetTrackForClient(0, station.TrackVersion);

        // Станция должна обнаружить отсутствие файла, обновить плейлист и выдать существующий track2.mp3
        Assert.NotNull(path);
        Assert.Equal("track2.mp3", Path.GetFileName(path));
        Assert.Equal(1, station.TrackCount);
    }

    [Fact]
    public void Rapid_Consecutive_Skips_Does_Not_Throw_Or_Deadlock()
    {
        CreateDummyTrack("track1.mp3");
        CreateDummyTrack("track2.mp3");
        CreateDummyTrack("track3.mp3");

        var station = new RadioStation("test_rapid_skips", "Rapid Skips", _tempDir);

        Parallel.For(0, 50, _ =>
        {
            var track = station.SkipTrack();
            Assert.NotNull(track);
            Assert.NotEmpty(track);
        });

        Assert.Equal(3, station.TrackCount);
        Assert.True(station.TrackVersion > 50);
    }

    [Fact]
    public void Empty_Station_GetTrackForClient_Returns_Null_Gracefully()
    {
        var station = new RadioStation("test_empty_stream", "Empty Stream", _tempDir);
        var (path, trackIndex, version, _) = station.GetTrackForClient(-1, 0);

        Assert.Null(path);
        Assert.Equal(0, trackIndex);
        Assert.Equal(1, version);
    }

    [Fact]
    public void Madeon_File_Detects_Accurate_Duration_And_Vbr_Bitrate()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var madeonPath = Path.Combine(projectRoot, "wwwroot", "audio", "pop", "Madeon - Lonely Space Age [Official Audio] [FPNr2JahAyo].mp3");

        if (!File.Exists(madeonPath))
        {
            // Если путь отличается при запуске тестов, пробуем текущую директорию
            madeonPath = Path.GetFullPath(Path.Combine("wwwroot", "audio", "pop", "Madeon - Lonely Space Age [Official Audio] [FPNr2JahAyo].mp3"));
        }

        Assert.True(File.Exists(madeonPath), $"Madeon file must exist at {madeonPath}");

        var info = RadioStation.ParseMp3TrackInfo(madeonPath);

        // Длительность должна быть ~230 секунд (3 мин 50 сек), а НЕ 497 секунд!
        int durationSec = (int)(info.DurationMs / 1000);
        Assert.InRange(durationSec, 228, 235);

        // Битрейт VBR должен быть ~138 kbps, а НЕ 64 kbps и НЕ 128 kbps!
        Assert.InRange(info.Bitrate, 130000, 145000);
        Assert.True(info.HasVbrHeader);
        Assert.Equal(48000, info.SampleRate);
    }

    [Fact]
    public void Standard_Cbr_Track_Detects_Accurate_Bitrate_And_Duration()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var trackPath = Path.Combine(projectRoot, "wwwroot", "audio", "rock", "track1.mp3");

        if (!File.Exists(trackPath))
        {
            trackPath = Path.GetFullPath(Path.Combine("wwwroot", "audio", "rock", "track1.mp3"));
        }

        Assert.True(File.Exists(trackPath), $"Track file must exist at {trackPath}");

        var info = RadioStation.ParseMp3TrackInfo(trackPath);

        Assert.Equal(128000, info.Bitrate);
        Assert.InRange((int)(info.DurationMs / 1000), 7, 9);
        Assert.Equal(44100, info.SampleRate);
    }

    [Fact]
    public void Mp3Parser_Extracts_Tlen_When_Present()
    {
        var filePath = CreateMp3WithId3v2("test_tlen.mp3", tlenMs: 248000, albumArtBytes: null, frameBitrateKbps: 128, frameCount: 20);

        var info = RadioStation.ParseMp3TrackInfo(filePath);

        Assert.Equal(248000, info.DurationMs);
        Assert.True(info.AudioOffset > 0);
    }

    [Fact]
    public void Mp3Parser_Skips_Large_Id3v2_Album_Art()
    {
        // Создаем файл с обложкой альбома размером 100 КБ и битрейтом 256 kbps
        var filePath = CreateMp3WithId3v2("test_album_art.mp3", tlenMs: null, albumArtBytes: 100 * 1024, frameBitrateKbps: 256, frameCount: 20);

        var info = RadioStation.ParseMp3TrackInfo(filePath);

        // Парсер должен перешагнуть 100 КБ тег и найти 256 kbps аудиофрейм (НЕ упасть в 128 kbps fallback!)
        Assert.True(info.AudioOffset >= 100 * 1024);
        Assert.Equal(256000, info.Bitrate);
    }

    [Fact]
    public void Mp3Parser_Handles_Dummy_And_Corrupted_Files_Gracefully()
    {
        // 1. Dummy 8-byte
        var dummyPath = CreateDummyTrack("dummy_8b.mp3");
        var infoDummy = RadioStation.ParseMp3TrackInfo(dummyPath);
        Assert.Equal(128000, infoDummy.Bitrate);

        // 2. Empty file
        var emptyPath = Path.Combine(_tempDir, "empty.mp3");
        File.WriteAllBytes(emptyPath, Array.Empty<byte>());
        var infoEmpty = RadioStation.ParseMp3TrackInfo(emptyPath);
        Assert.Equal(128000, infoEmpty.Bitrate);

        // 3. Random noise
        var noisePath = Path.Combine(_tempDir, "noise.mp3");
        File.WriteAllBytes(noisePath, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 });
        var infoNoise = RadioStation.ParseMp3TrackInfo(noisePath);
        Assert.Equal(128000, infoNoise.Bitrate);
    }

    private string CreateMp3WithId3v2(string fileName, long? tlenMs, int? albumArtBytes, int frameBitrateKbps = 128, int frameCount = 10)
    {
        var path = Path.Combine(_tempDir, fileName);
        using var ms = new MemoryStream();

        using (var tagBody = new MemoryStream())
        {
            if (tlenMs.HasValue)
            {
                byte[] textBytes = System.Text.Encoding.ASCII.GetBytes(tlenMs.Value.ToString());
                byte[] frameData = new byte[1 + textBytes.Length];
                frameData[0] = 0; // Latin1
                Buffer.BlockCopy(textBytes, 0, frameData, 1, textBytes.Length);

                tagBody.Write(System.Text.Encoding.ASCII.GetBytes("TLEN"));
                tagBody.Write(new byte[] { 0, 0, 0, (byte)frameData.Length });
                tagBody.Write(new byte[] { 0, 0 });
                tagBody.Write(frameData);
            }

            if (albumArtBytes.HasValue)
            {
                byte[] artData = new byte[albumArtBytes.Value];
                tagBody.Write(System.Text.Encoding.ASCII.GetBytes("APIC"));
                int artSize = artData.Length;
                tagBody.Write(new byte[] { (byte)((artSize >> 24) & 0xFF), (byte)((artSize >> 16) & 0xFF), (byte)((artSize >> 8) & 0xFF), (byte)(artSize & 0xFF) });
                tagBody.Write(new byte[] { 0, 0 });
                tagBody.Write(artData);
            }

            byte[] body = tagBody.ToArray();
            int tagSize = body.Length;

            ms.Write(System.Text.Encoding.ASCII.GetBytes("ID3"));
            ms.WriteByte(0x03);
            ms.WriteByte(0x00);
            ms.WriteByte(0x00);
            ms.WriteByte((byte)((tagSize >> 21) & 0x7F));
            ms.WriteByte((byte)((tagSize >> 14) & 0x7F));
            ms.WriteByte((byte)((tagSize >> 7) & 0x7F));
            ms.WriteByte((byte)(tagSize & 0x7F));
            ms.Write(body);
        }

        byte b2 = frameBitrateKbps switch
        {
            256 => 0xD0,
            320 => 0xE0,
            _ => 0x90
        };

        int frameLength = (144 * frameBitrateKbps * 1000) / 44100;
        byte[] frame = new byte[frameLength];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = b2;
        frame[3] = 0x64;

        for (int i = 0; i < frameCount; i++)
        {
            ms.Write(frame);
        }

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [Fact]
    public void Station_Pause_And_Resume_Controls()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "radio_test_pause_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        try
        {
            var station = new RadioStation("test_pause", "Test Pause", testDir);
            Assert.False(station.IsPaused);

            station.Pause();
            Assert.True(station.IsPaused);

            station.Resume();
            Assert.False(station.IsPaused);

            bool toggled = station.TogglePause();
            Assert.True(toggled);
            Assert.True(station.IsPaused);

            toggled = station.TogglePause();
            Assert.False(toggled);
            Assert.False(station.IsPaused);

            station.Dispose();
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void Station_Seek_And_LiveDj_Controls()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "radio_test_seek_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        try
        {
            var station = new RadioStation("test_seek", "Test Seek", testDir);

            // SeekTo
            Assert.True(station.SeekTo(45.0));
            Assert.True(station.SeekBy(15.0));
            Assert.True(station.SeekBy(-10.0));

            // Live DJ
            Assert.False(station.IsLiveDj);
            station.SetLiveDj(true);
            Assert.True(station.IsLiveDj);

            var chunk = new byte[] { 0xFF, 0xFB, 0x90, 0x64 };
            station.PushLiveAudio(chunk);
            Assert.True(station.IsLiveDj);

            station.SetLiveDj(false);
            Assert.False(station.IsLiveDj);

            station.Dispose();
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void Station_Subscribe_While_Paused_Returns_Silence_Burst()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "radio_test_silence_burst_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        try
        {
            var station = new RadioStation("test_silence", "Test Silence", testDir);
            station.Pause();

            var (subId, reader, initialBurst) = station.Subscribe();
            try
            {
                Assert.True(station.IsPaused);
                Assert.NotNull(initialBurst);
                // Burst тишины должен содержать валидные MP3 фреймы объемом ~62 КБ
                Assert.True(initialBurst.Length >= 60 * 1024, $"Burst was only {initialBurst.Length} bytes");

                // Проверяем первый фрейм тишины
                Assert.Equal(0xFF, initialBurst[0]);
                Assert.Equal(0xFB, initialBurst[1]); // MPEG-1 Layer 3
                Assert.Equal(0x90, initialBurst[2]); // 128 kbps, 44100 Hz
                Assert.Equal(0x64, initialBurst[3]); // Joint stereo
            }
            finally
            {
                station.Unsubscribe(subId);
                station.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void Vbr_Without_Xing_Header_Detects_Accurate_Bitrate_And_Duration()
    {
        // Создаем VBR файл без Xing-заголовка, где 1-й фрейм = 64 kbps, а остальные 30 фреймов = 128 kbps
        var path = Path.Combine(_tempDir, "vbr_no_xing.mp3");
        using (var ms = new MemoryStream())
        {
            // Первый фрейм (тихий интро) 64 kbps, 44100 Hz: длина 208 байт
            int len64 = (144 * 64 * 1000) / 44100;
            byte[] frame64 = new byte[len64];
            frame64[0] = 0xFF; frame64[1] = 0xFB; frame64[2] = 0x50; frame64[3] = 0x64;
            ms.Write(frame64);

            // Следующие 30 фреймов 128 kbps: длина 417 байт
            int len128 = (144 * 128 * 1000) / 44100;
            byte[] frame128 = new byte[len128];
            frame128[0] = 0xFF; frame128[1] = 0xFB; frame128[2] = 0x90; frame128[3] = 0x64;
            for (int i = 0; i < 30; i++)
            {
                ms.Write(frame128);
            }

            File.WriteAllBytes(path, ms.ToArray());
        }

        var info = RadioStation.ParseMp3TrackInfo(path);

        // Парсер должен определить VBR и средний битрейт ~126 kbps, а НЕ 64 kbps!
        Assert.True(info.HasVbrHeader);
        Assert.InRange(info.Bitrate, 120000, 130000);
        // Длительность должна быть рассчитана по среднему битрейту (~126 kbps), а НЕ удвоена из-за 64 kbps фрейма
        double expectedSec = (double)new FileInfo(path).Length * 8.0 / info.Bitrate;
        double actualSec = info.DurationMs / 1000.0;
        Assert.InRange(actualSec, expectedSec * 0.95, expectedSec * 1.05);
    }

    [Fact]
    public void Station_TrySubscribe_Respects_MaxListeners_Limit()
    {
        CreateDummyTrack("track_limit.mp3");
        var station = new RadioStation("test_limit", "Limit Station", _tempDir);
        station.MaxListeners = 2;

        var (success1, subId1, reader1, _) = station.TrySubscribe();
        Assert.True(success1);
        Assert.Equal(1, station.ListenersCount);

        var (success2, subId2, reader2, _) = station.TrySubscribe();
        Assert.True(success2);
        Assert.Equal(2, station.ListenersCount);

        // 3-й слушатель должен получить отказ (лимит 2)
        var (success3, subId3, _, _) = station.TrySubscribe();
        Assert.False(success3);
        Assert.Equal(Guid.Empty, subId3);
        Assert.Equal(2, station.ListenersCount);

        // После отключения одного слушателя новый должен подключиться успешно
        station.Unsubscribe(subId1);
        Assert.Equal(1, station.ListenersCount);

        var (success4, subId4, _, _) = station.TrySubscribe();
        Assert.True(success4);
        Assert.Equal(2, station.ListenersCount);

        station.Unsubscribe(subId2);
        station.Unsubscribe(subId4);
        station.Dispose();
    }

    [Fact]
    public void Station_DeleteTrack_Removes_File_And_Updates_Playlist()
    {
        CreateDummyTrack("track1.mp3");
        CreateDummyTrack("track2.mp3");
        var station = new RadioStation("test_del", "Del Station", _tempDir);

        Assert.Equal(2, station.TrackCount);

        bool deleted = station.DeleteTrack("track2.mp3");
        Assert.True(deleted);
        Assert.Equal(1, station.TrackCount);
        Assert.False(File.Exists(Path.Combine(_tempDir, "track2.mp3")));
        Assert.Contains("track1.mp3", station.GetTracks());

        // Попытка удалить несуществующий трек
        bool deletedNonexistent = station.DeleteTrack("nonexistent.mp3");
        Assert.False(deletedNonexistent);

        station.Dispose();
    }

    [Fact]
    public async Task IcyMetadataWriter_Injects_Metadata_At_Specified_Interval()
    {
        using var ms = new MemoryStream();
        string currentTitle = "Rock Artist - Super Song";
        var writer = new IcyMetadataWriter(ms, metaInterval: 100, () => currentTitle);

        // Пишем 150 байт аудио
        byte[] audio = new byte[150];
        Array.Fill(audio, (byte)0xAA);

        await writer.WriteAudioAsync(audio);

        var result = ms.ToArray();
        // Первые 100 байт - аудио
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal((byte)0xAA, result[i]);
        }

        // Байт 100 - длина метаданных N
        byte lenByte = result[100];
        Assert.True(lenByte > 0);
        int metaLength = lenByte * 16;

        // Метаданные содержат StreamTitle
        string metaStr = System.Text.Encoding.UTF8.GetString(result, 101, metaLength);
        Assert.Contains("StreamTitle='Rock Artist - Super Song';", metaStr);

        // После метаданных идут оставшиеся 50 байт аудио
        int audio2Start = 101 + metaLength;
        Assert.Equal(150 + 1 + metaLength, result.Length);
        for (int i = audio2Start; i < result.Length; i++)
        {
            Assert.Equal((byte)0xAA, result[i]);
        }
    }
}
