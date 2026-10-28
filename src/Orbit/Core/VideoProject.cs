using System.Windows;
using System.Windows.Media;

namespace Orbit.Core;

/// <summary>Как значение идёт от ключа к следующему: ровно, с плавным разгоном и торможением или скачком в следующем ключе.</summary>
public enum KeyEase
{
    Linear,
    Smooth,
    Hold,
}

/// <summary>Ключевой кадр. Время — в исходном файле, поэтому ключи не сдвигаются при обрезке клипа.</summary>
public sealed class Keyframe
{
    public TimeSpan Time { get; set; }

    public double A { get; set; }

    /// <summary>Второе число — только у позиции (Y).</summary>
    public double B { get; set; }

    public KeyEase Ease { get; set; } = KeyEase.Linear;

    public Keyframe Clone() => (Keyframe)MemberwiseClone();
}

/// <summary>Свойство клипа: одно значение на весь клип или ключи, между которыми оно меняется (как в After Effects и CapCut).</summary>
public sealed class AnimatedValue
{
    /// <summary>Ключи ближе этого считаются одним — щелчок по ромбу на бегунке попадает в ключ.</summary>
    public static readonly TimeSpan Snap = TimeSpan.FromMilliseconds(20);

    public AnimatedValue(double a, double b = 0) => (Default, Static) = ((a, b), (a, b));

    public (double A, double B) Default { get; }

    public (double A, double B) Static { get; set; }

    public List<Keyframe> Keys { get; } = [];

    public bool IsAnimated => Keys.Count > 0;

    public bool IsDefault => !IsAnimated && Static == Default;

    public (double A, double B) At(TimeSpan time)
    {
        if (Keys.Count == 0)
            return Static;
        if (time <= Keys[0].Time)
            return (Keys[0].A, Keys[0].B);
        var last = Keys[^1];
        if (time >= last.Time)
            return (last.A, last.B);
        var i = 0;
        while (Keys[i + 1].Time <= time)
            i++;
        var (from, to) = (Keys[i], Keys[i + 1]);
        var f = (time - from.Time) / (to.Time - from.Time);
        f = from.Ease switch
        {
            KeyEase.Hold => 0,
            KeyEase.Smooth => f * f * (3 - 2 * f),
            _ => f,
        };
        return (from.A + (to.A - from.A) * f, from.B + (to.B - from.B) * f);
    }

    public Keyframe? KeyAt(TimeSpan time) => Keys.FirstOrDefault(k => (k.Time - time).Duration() <= Snap);

    /// <summary>Как в CapCut: если у свойства есть ключи, правка ставит или меняет ключ на бегунке, иначе меняет значение на весь клип.</summary>
    public void Set(TimeSpan time, double a, double b = 0)
    {
        if (!IsAnimated)
        {
            Static = (a, b);
            return;
        }
        var key = KeyAt(time) ?? AddKey(time);
        key.A = a;
        key.B = b;
    }

    /// <summary>Ключ с текущим значением в этом месте.</summary>
    public Keyframe AddKey(TimeSpan time)
    {
        if (KeyAt(time) is { } existing)
            return existing;
        var (a, b) = At(time);
        // Новый ключ продолжает переход, в который попал
        var ease = Keys.LastOrDefault(k => k.Time < time)?.Ease ?? KeyEase.Linear;
        var key = new Keyframe { Time = time, A = a, B = b, Ease = ease };
        Keys.Add(key);
        Sort();
        return key;
    }

    public void Remove(Keyframe key)
    {
        if (!Keys.Remove(key))
            return;
        // Последний ключ убрали — значение остаётся таким, каким было в нём
        if (Keys.Count == 0)
            Static = (key.A, key.B);
    }

    public void Sort() => Keys.Sort((x, y) => x.Time.CompareTo(y.Time));

    public void Reset()
    {
        Keys.Clear();
        Static = Default;
    }

    public AnimatedValue Clone()
    {
        var copy = new AnimatedValue(Default.A, Default.B) { Static = Static };
        copy.Keys.AddRange(Keys.Select(k => k.Clone()));
        return copy;
    }
}

/// <summary>Свойства клипа, которые можно анимировать ключами.</summary>
public sealed class ClipAnimation
{
    /// <summary>1 — 100 %.</summary>
    public AnimatedValue Scale { get; private init; } = new(1);

    /// <summary>Сдвиг от центра в пикселях кадра (X, Y).</summary>
    public AnimatedValue Position { get; private init; } = new(0, 0);

    /// <summary>Градусы по часовой стрелке.</summary>
    public AnimatedValue Rotation { get; private init; } = new(0);

    /// <summary>0…1, прозрачное — чёрное.</summary>
    public AnimatedValue Opacity { get; private init; } = new(1);

    /// <summary>0…2, 1 — как в исходнике.</summary>
    public AnimatedValue Volume { get; private init; } = new(1);

    public IEnumerable<AnimatedValue> All => [Scale, Position, Rotation, Opacity, Volume];

    public bool IsDefault => All.All(v => v.IsDefault);

    public ClipAnimation Clone() => new()
    {
        Scale = Scale.Clone(),
        Position = Position.Clone(),
        Rotation = Rotation.Clone(),
        Opacity = Opacity.Clone(),
        Volume = Volume.Clone(),
    };
}

/// <summary>Клип на дорожке: часть файла от In до Out, приближение и ключи.</summary>
public sealed class VideoClip
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    public required TimeSpan Duration { get; init; }

    public required uint Width { get; init; }

    public required uint Height { get; init; }

    public bool HasAudio { get; init; }

    public TimeSpan In { get; set; }

    public TimeSpan Out { get; set; }

    /// <summary>Приближенная часть в координатах кадра проекта, тех же пропорций, что и кадр.</summary>
    public Rect? Crop { get; set; }

    public ClipAnimation Animation { get; private set; } = new();

    public TimeSpan Length => Out - In;

    public bool IsEdited => In > TimeSpan.Zero || Out < Duration || Crop is not null || !Animation.IsDefault;

    public VideoClip Clone()
    {
        var copy = (VideoClip)MemberwiseClone();
        copy.Animation = Animation.Clone();
        return copy;
    }

    /// <summary>
    /// Пиксели файла → пиксели кадра проекта в момент time (время файла): клип вписывается в кадр, приближение растягивает
    /// свою часть на весь кадр, затем масштаб и поворот вокруг центра и сдвиг. Этой же матрицей рисуют и просмотр, и сохранение.
    /// </summary>
    public Matrix FrameMatrix(TimeSpan time, double canvasWidth, double canvasHeight, bool crop = true)
    {
        var m = Matrix.Identity;
        var fit = Math.Min(canvasWidth / Width, canvasHeight / Height);
        m.Scale(fit, fit);
        m.Translate((canvasWidth - Width * fit) / 2, (canvasHeight - Height * fit) / 2);
        if (crop && Crop is { Width: > 0 } area)
        {
            m.Translate(-area.X, -area.Y);
            var zoom = canvasWidth / area.Width;
            m.Scale(zoom, zoom);
        }
        var scale = Animation.Scale.At(time).A;
        var rotation = Animation.Rotation.At(time).A;
        var (x, y) = Animation.Position.At(time);
        m.Translate(-canvasWidth / 2, -canvasHeight / 2);
        m.Scale(scale, scale);
        m.Rotate(rotation);
        m.Translate(canvasWidth / 2 + x, canvasHeight / 2 + y);
        return m;
    }
}

/// <summary>Всё, что нужно для сохранения: клипы по порядку и параметры результата.</summary>
public sealed record VideoProject(IReadOnlyList<VideoClip> Clips, int CanvasWidth, int CanvasHeight, uint FrameRateNumerator, uint FrameRateDenominator,
    bool Mute, uint MaxHeight, double BitsPerPixel)
{
    /// <summary>Размер результата: кадр проекта, уменьшенный до MaxHeight и не больше 4K, стороны чётные.</summary>
    public (int Width, int Height) OutputSize
    {
        get
        {
            double width = CanvasWidth, height = CanvasHeight;
            if (MaxHeight > 0 && height > MaxHeight)
                (width, height) = (width * MaxHeight / height, MaxHeight);
            var (maxW, maxH) = width >= height ? (3840.0, 2160.0) : (2160.0, 3840.0);
            var limit = Math.Min(1, Math.Min(maxW / width, maxH / height));
            return (Math.Max(16, (int)Math.Round(width * limit) & ~1), Math.Max(16, (int)Math.Round(height * limit) & ~1));
        }
    }

    public TimeSpan Length => Clips.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Length);
}
