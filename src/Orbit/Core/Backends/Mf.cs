using System.Runtime.InteropServices;

namespace Orbit.Core.Backends;

/// <summary>
/// Минимум Media Foundation для видеоредактора: Source Reader отдаёт кадры (RGB32) и звук (PCM) по одному,
/// Sink Writer кодирует их в H.264 + AAC. Интерфейсы COM вызываются по номерам в таблице методов — описывать их целиком не нужно.
/// Все вызовы — из фонового потока (MTA).
/// </summary>
internal static unsafe class Mf
{
    public const uint FirstVideoStream = 0xFFFFFFFC;
    public const uint FirstAudioStream = 0xFFFFFFFD;
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint EndOfStream = 0x2;
    private const uint MediaTypeChanged = 0x20;

    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71");
    public static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    public static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid Aac = new("00001610-0000-0010-8000-00aa00389b71");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid AudioSampleRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid AudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid AudioBlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid AudioAvgBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    public static readonly Guid EnableAdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    public static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid DisableThrottling = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");

    private static int _started;

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateAttributes(out nint attributes, uint size);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out nint type);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateSample(out nint sample);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMemoryBuffer(uint length, out nint buffer);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(string url, nint byteStream, nint attributes, out nint writer);

    public static void Startup()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            Check(MFStartup(0x20070, 0));
    }

    public static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    private static void** V(nint p) => *(void***)p;

    public static void Release(nint p)
    {
        if (p != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)V(p)[2])(p);
    }

    // ---------------------------------------------------------------- IMFAttributes (и IMFMediaType, IMFSample)

    public static void SetUInt32(nint p, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)V(p)[21])(p, &key, value));

    public static void SetUInt64(nint p, Guid key, ulong value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)V(p)[22])(p, &key, value));

    public static void SetGuid(nint p, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)V(p)[24])(p, &key, &value));

    public static bool TryGetUInt32(nint p, Guid key, out uint value)
    {
        uint result;
        var ok = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)V(p)[7])(p, &key, &result) >= 0;
        value = result;
        return ok;
    }

    public static ulong GetUInt64(nint p, Guid key)
    {
        ulong value;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)V(p)[8])(p, &key, &value));
        return value;
    }

    public static nint CreateAttributes()
    {
        Check(MFCreateAttributes(out var attributes, 4));
        return attributes;
    }

    public static nint CreateType(Guid major, Guid subtype)
    {
        Check(MFCreateMediaType(out var type));
        SetGuid(type, MajorType, major);
        SetGuid(type, Subtype, subtype);
        return type;
    }

    public static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    // ---------------------------------------------------------------- IMFSourceReader

    /// <summary>Чтение файла: кадры в RGB32 (сверху вниз), звук в 16-битном PCM.</summary>
    public sealed class Reader : IDisposable
    {
        private nint _reader;

        private Reader(nint reader) => _reader = reader;

        public bool HasVideo { get; private set; }

        public bool HasAudio { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        /// <summary>Байт на строку; отрицательный — строки снизу вверх.</summary>
        public int Stride { get; private set; }

        /// <summary>Кадры уменьшены до размера, заданного при открытии.</summary>
        public bool Scaled { get; private set; }

        public int SampleRate { get; private set; }

        public int Channels { get; private set; }

        /// <param name="audioRate">Частота, к которой привести звук; 0 — без звука.</param>
        /// <param name="size">Уменьшить кадры до этого размера (сразу при декодировании — быстрее и чище, чем потом).</param>
        public static Reader Open(string path, bool video, int audioRate, (int Width, int Height)? size = null)
        {
            Startup();
            var attributes = CreateAttributes();
            try
            {
                SetUInt32(attributes, size is null ? EnableVideoProcessing : EnableAdvancedVideoProcessing, 1);
                Check(MFCreateSourceReaderFromURL(path, attributes, out var handle));
                var reader = new Reader(handle);
                try
                {
                    reader.Configure(video, audioRate, size);
                    return reader;
                }
                catch
                {
                    reader.Dispose();
                    throw;
                }
            }
            finally
            {
                Release(attributes);
            }
        }

        private void Configure(bool video, int audioRate, (int Width, int Height)? size)
        {
            Check(Call(4, AllStreams, 0));
            if (video)
            {
                if (size is { } wanted)
                {
                    var scaled = CreateType(Video, Rgb32);
                    try
                    {
                        SetUInt64(scaled, FrameSize, Pack((uint)wanted.Width, (uint)wanted.Height));
                        SetUInt64(scaled, PixelAspectRatio, Pack(1, 1));
                        Scaled = SetType(FirstVideoStream, scaled);
                    }
                    finally
                    {
                        Release(scaled);
                    }
                }
                var type = CreateType(Video, Rgb32);
                try
                {
                    HasVideo = Scaled || SetType(FirstVideoStream, type);
                }
                finally
                {
                    Release(type);
                }
                if (HasVideo)
                {
                    Check(Call(4, FirstVideoStream, 1));
                    ReadVideoFormat();
                }
            }
            if (audioRate > 0)
            {
                // Сначала просим сразу нужную частоту и стерео; не вышло — исходный формат, приведём сами
                HasAudio = SetAudioType(audioRate, 2) || SetAudioType(0, 0);
                if (HasAudio)
                {
                    Check(Call(4, FirstAudioStream, 1));
                    var current = GetCurrentType(FirstAudioStream);
                    try
                    {
                        SampleRate = TryGetUInt32(current, AudioSampleRate, out var rate) ? (int)rate : audioRate;
                        Channels = TryGetUInt32(current, AudioChannels, out var channels) ? (int)channels : 2;
                    }
                    finally
                    {
                        Release(current);
                    }
                }
            }
        }

        private bool SetAudioType(int rate, int channels)
        {
            var type = CreateType(Audio, Pcm);
            try
            {
                SetUInt32(type, AudioBitsPerSample, 16);
                if (rate > 0)
                {
                    SetUInt32(type, AudioSampleRate, (uint)rate);
                    SetUInt32(type, AudioChannels, (uint)channels);
                    SetUInt32(type, AudioBlockAlignment, (uint)(channels * 2));
                    SetUInt32(type, AudioAvgBytesPerSecond, (uint)(rate * channels * 2));
                }
                return SetType(FirstAudioStream, type);
            }
            finally
            {
                Release(type);
            }
        }

        private bool SetType(uint stream, nint type) =>
            ((delegate* unmanaged[Stdcall]<nint, uint, uint*, nint, int>)V(_reader)[7])(_reader, stream, null, type) >= 0;

        private int Call(int slot, uint stream, int selected) =>
            ((delegate* unmanaged[Stdcall]<nint, uint, int, int>)V(_reader)[slot])(_reader, stream, selected);

        private nint GetCurrentType(uint stream)
        {
            nint type;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)V(_reader)[6])(_reader, stream, &type));
            return type;
        }

        private void ReadVideoFormat()
        {
            var type = GetCurrentType(FirstVideoStream);
            try
            {
                var size = GetUInt64(type, FrameSize);
                Width = (int)(size >> 32);
                Height = (int)(size & 0xFFFFFFFF);
                Stride = TryGetUInt32(type, DefaultStride, out var stride) ? (int)stride : Width * 4;
            }
            finally
            {
                Release(type);
            }
        }

        public void Deselect(uint stream) => Call(4, stream, 0);

        public void Seek(TimeSpan time)
        {
            // PROPVARIANT с VT_I8: время в сотнях наносекунд
            var variant = stackalloc long[3];
            variant[0] = 20;
            variant[1] = time.Ticks;
            variant[2] = 0;
            var format = Guid.Empty;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, long*, int>)V(_reader)[8])(_reader, &format, variant));
        }

        /// <summary>Следующий кадр или кусок звука; false — поток кончился.</summary>
        public bool Read(uint stream, out Sample sample)
        {
            while (true)
            {
                uint actual, flags;
                long time;
                nint handle;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)V(_reader)[9])(
                    _reader, stream, 0, &actual, &flags, &time, &handle));
                if ((flags & MediaTypeChanged) != 0 && stream == FirstVideoStream)
                    ReadVideoFormat();
                if (handle != 0)
                {
                    sample = new Sample(handle);
                    return true;
                }
                if ((flags & EndOfStream) != 0)
                {
                    sample = default;
                    return false;
                }
                // пропуск в потоке — читаем дальше
            }
        }

        public void Dispose()
        {
            Release(_reader);
            _reader = 0;
        }
    }

    /// <summary>Кадр или кусок звука, данные открыты для чтения до Dispose.</summary>
    public readonly struct Sample : IDisposable
    {
        private readonly nint _sample;
        private readonly nint _buffer;

        public Sample(nint sample)
        {
            _sample = sample;
            long time = 0, duration = 0;
            ((delegate* unmanaged[Stdcall]<nint, long*, int>)V(sample)[35])(sample, &time);
            ((delegate* unmanaged[Stdcall]<nint, long*, int>)V(sample)[37])(sample, &duration);
            Time = time;
            Duration = duration;
            nint buffer;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)V(sample)[41])(sample, &buffer));
            _buffer = buffer;
            byte* data;
            uint max, length;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)V(buffer)[3])(buffer, &data, &max, &length));
            Data = data;
            Length = (int)length;
        }

        public long Time { get; }

        public long Duration { get; }

        public byte* Data { get; }

        public int Length { get; }

        public void Dispose()
        {
            if (_buffer != 0)
            {
                ((delegate* unmanaged[Stdcall]<nint, int>)V(_buffer)[4])(_buffer);
                Release(_buffer);
            }
            Release(_sample);
        }
    }

    // ---------------------------------------------------------------- IMFSinkWriter

    /// <summary>Запись MP4: H.264 из кадров BGRA, AAC из 16-битного стерео PCM.</summary>
    public sealed class Writer : IDisposable
    {
        public const int AudioRate = 48000;

        private nint _writer;
        private readonly uint _video;
        private readonly uint _audio = uint.MaxValue;
        private readonly int _width;
        private readonly int _height;

        public Writer(string path, int width, int height, uint fpsNumerator, uint fpsDenominator, uint bitrate, bool audio)
        {
            Startup();
            _width = width;
            _height = height;
            var attributes = CreateAttributes();
            try
            {
                SetUInt32(attributes, EnableHardwareTransforms, 1);
                SetUInt32(attributes, DisableThrottling, 1);
                Check(MFCreateSinkWriterFromURL(path, 0, attributes, out _writer));
            }
            finally
            {
                Release(attributes);
            }

            var output = CreateType(Video, H264);
            var input = CreateType(Video, Rgb32);
            try
            {
                foreach (var type in new[] { output, input })
                {
                    SetUInt64(type, FrameSize, Pack((uint)width, (uint)height));
                    SetUInt64(type, FrameRate, Pack(fpsNumerator, fpsDenominator));
                    SetUInt64(type, PixelAspectRatio, Pack(1, 1));
                    SetUInt32(type, InterlaceMode, 2);
                }
                SetUInt32(output, AvgBitrate, bitrate);
                SetUInt32(input, DefaultStride, (uint)(width * 4));
                _video = AddStream(output);
                SetInput(_video, input);
            }
            finally
            {
                Release(output);
                Release(input);
            }

            if (audio)
            {
                output = CreateType(Audio, Aac);
                input = CreateType(Audio, Pcm);
                try
                {
                    foreach (var type in new[] { output, input })
                    {
                        SetUInt32(type, AudioBitsPerSample, 16);
                        SetUInt32(type, AudioSampleRate, AudioRate);
                        SetUInt32(type, AudioChannels, 2);
                    }
                    SetUInt32(output, AudioAvgBytesPerSecond, 24000); // 192 кбит/с
                    SetUInt32(input, AudioBlockAlignment, 4);
                    SetUInt32(input, AudioAvgBytesPerSecond, AudioRate * 4);
                    _audio = AddStream(output);
                    SetInput(_audio, input);
                }
                finally
                {
                    Release(output);
                    Release(input);
                }
            }
            Check(((delegate* unmanaged[Stdcall]<nint, int>)V(_writer)[5])(_writer));
        }

        private uint AddStream(nint type)
        {
            uint index;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)V(_writer)[3])(_writer, type, &index));
            return index;
        }

        private void SetInput(uint stream, nint type) =>
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)V(_writer)[4])(_writer, stream, type, 0));

        /// <summary>Кадр BGRA без отступов между строками.</summary>
        public void WriteVideo(uint* frame, long time, long duration) =>
            Write(_video, (byte*)frame, _width * _height * 4, time, duration);

        /// <summary>Стерео 16 бит, 48 кГц.</summary>
        public void WriteAudio(short* pcm, int frames, long time) =>
            Write(_audio, (byte*)pcm, frames * 4, time, frames * 10_000_000L / AudioRate);

        private void Write(uint stream, byte* data, int length, long time, long duration)
        {
            nint buffer = 0, sample = 0;
            try
            {
                Check(MFCreateMemoryBuffer((uint)length, out buffer));
                byte* target;
                uint max, current;
                Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)V(buffer)[3])(buffer, &target, &max, &current));
                Buffer.MemoryCopy(data, target, max, length);
                ((delegate* unmanaged[Stdcall]<nint, int>)V(buffer)[4])(buffer);
                Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)V(buffer)[6])(buffer, (uint)length));
                Check(MFCreateSample(out sample));
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)V(sample)[42])(sample, buffer));
                Check(((delegate* unmanaged[Stdcall]<nint, long, int>)V(sample)[36])(sample, time));
                Check(((delegate* unmanaged[Stdcall]<nint, long, int>)V(sample)[38])(sample, duration));
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)V(_writer)[6])(_writer, stream, sample));
            }
            finally
            {
                Release(sample);
                Release(buffer);
            }
        }

        public void Finish() => Check(((delegate* unmanaged[Stdcall]<nint, int>)V(_writer)[11])(_writer));

        public void Dispose()
        {
            Release(_writer);
            _writer = 0;
        }
    }
}
