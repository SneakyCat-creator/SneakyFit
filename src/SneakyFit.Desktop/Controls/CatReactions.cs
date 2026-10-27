namespace SneakyFit.Desktop.Controls;

[Flags]
internal enum CatPersona
{
    Think = 1, Work = 2, Love = 4, Sleep = 8, Play = 16, Surprise = 32,
    Pride = 64, Cheer = 128, Water = 256, Food = 512, Sport = 1024, Groom = 2048, Wave = 4096, Peek = 8192, Heart = 16384
}

internal sealed record CatReaction(int Sprite, string Phrase);

internal static class CatReactions
{
    // Same row-major order as CatSprites: the actual drawing, not just its mood.
    private static readonly CatPersona[] Personas =
    [
        CatPersona.Think, CatPersona.Play | CatPersona.Cheer, CatPersona.Surprise,
        CatPersona.Love | CatPersona.Heart, CatPersona.Surprise | CatPersona.Cheer,
        CatPersona.Play | CatPersona.Wave, CatPersona.Love | CatPersona.Heart, CatPersona.Work | CatPersona.Think,
        CatPersona.Sleep, CatPersona.Groom,
        CatPersona.Surprise | CatPersona.Cheer, CatPersona.Water, CatPersona.Pride,
        CatPersona.Peek | CatPersona.Think, CatPersona.Cheer,
        CatPersona.Wave | CatPersona.Play, CatPersona.Food, CatPersona.Surprise,
        CatPersona.Love | CatPersona.Heart, CatPersona.Peek | CatPersona.Think,

        CatPersona.Think, CatPersona.Play | CatPersona.Cheer, CatPersona.Surprise,
        CatPersona.Wave | CatPersona.Pride, CatPersona.Love | CatPersona.Heart,
        CatPersona.Love | CatPersona.Pride, CatPersona.Cheer | CatPersona.Surprise,
        CatPersona.Peek | CatPersona.Play, CatPersona.Sleep, CatPersona.Food | CatPersona.Love | CatPersona.Heart,
        CatPersona.Play, CatPersona.Wave | CatPersona.Play, CatPersona.Sport | CatPersona.Pride,
        CatPersona.Pride, CatPersona.Peek | CatPersona.Think,
        CatPersona.Sleep | CatPersona.Love | CatPersona.Heart, CatPersona.Surprise, CatPersona.Water,
        CatPersona.Play | CatPersona.Wave, CatPersona.Play | CatPersona.Cheer
    ];

    private sealed record Line(string Text, CatPersona Personas);
    private static readonly IReadOnlyList<Line> Lines = CreateLines();
    private static string? _lastPhrase, _lastShownPhrase;

    public static void Committed(CatReaction reaction) => _lastShownPhrase = reaction.Phrase;

    public static CatReaction Choose(int current, int pending)
    {
        var sprites = Enumerable.Range(0, Personas.Length).Where(index => index != current && index != pending).ToArray();
        var sprite = sprites[Random.Shared.Next(sprites.Length)];
        var candidates = Lines.Where(line => (line.Personas & Personas[sprite]) != 0
            && line.Text != _lastPhrase && line.Text != _lastShownPhrase).ToArray();
        var line = candidates[Random.Shared.Next(candidates.Length)];
        _lastPhrase = line.Text;
        return new(sprite, line.Text);
    }

    private static IReadOnlyList<Line> CreateLines()
    {
        var tags = new Dictionary<string, CatPersona>(StringComparer.Ordinal);
        void Tag(CatPersona personas, params string[] phrases)
        {
            foreach (var phrase in phrases) tags[phrase] = personas;
        }
        Tag(CatPersona.Work | CatPersona.Sleep, "Эй, я тут работаю!", "Это мой рабочий мур!");
        Tag(CatPersona.Think | CatPersona.Work | CatPersona.Pride,
            "Я серьёзный кот. Почти.", "Тс-с, я делаю умный вид.", "Усатый специалист на связи.",
            "А я как раз собирался быть важным.", "Я смеюсь, но всё помню!");
        Tag(CatPersona.Peek | CatPersona.Think | CatPersona.Pride,
            "Так, кто тут хулиганит?", "Ты меня раскусил!", "Хитро, очень хитро!",
            "Я всё вижу. И хихикаю.", "Ты опять за своё?", "Так и знал: опять щекотка!");
        Tag(CatPersona.Love | CatPersona.Play, "Ну ты и шалун!", "Ох уж эти любопытные пальцы!", "Мур-мур. Секрет между нами.");
        Tag(CatPersona.Love | CatPersona.Sleep,
            "Мур! Ещё разок?", "Мне положен перерыв на мурчание.", "Мурчание включено!",
            "Ещё щелчок — и замурчу!", "Пушистый режим активирован.", "Один клик — один мур.",
            "Сейчас как замурчу!", "Так и запишем: кот доволен.", "Ты нашёл секретную мурчалку.", "Сейчас будет ответный мур.");
        Tag(CatPersona.Surprise, "Ха! Не ожидал!", "Вот это поворот!", "Эй! Я чуть усы не уронил!",
            "Неожиданно! Мне нравится.", "Вот так встреча с пальцем!");
        Tag(CatPersona.Wave | CatPersona.Cheer,
            "Лапу за такую смелость!", "Придётся ответить пятюней!", "Лапы вверх, сдаюсь!",
            "Я всё понял. Ты за пятюней!", "Кто щекочет главного по лапкам?", "Ха! Внезапная пятюня!");
        Tag(CatPersona.Pride | CatPersona.Groom, "Усы не трогать!", "Я вообще-то позировал!", "Пойду поправлю серьёзные усы.");
        Tag(CatPersona.Sleep | CatPersona.Play, "Ты умеешь растормошить кота.");

        var result = FitCatPhrases.TickleLines.Select(text => new Line(text,
            tags.GetValueOrDefault(text, CatPersona.Play | CatPersona.Cheer))).ToList();
        result.AddRange([
            new("Сейчас допью — и снова мурчать!", CatPersona.Water),
            new("Пауза на глоточек. Щекотку помню!", CatPersona.Water),
            new("Вода в лапке, смешинка в усах.", CatPersona.Water),
            new("Мур между глотками!", CatPersona.Water),
            new("Я и перекус сегодня неразлучны.", CatPersona.Food),
            new("М-м-м… щекотку заказывали к обеду?", CatPersona.Food),
            new("Сначала кусочек, потом хихиканье!", CatPersona.Food),
            new("Не смеши, у меня тут вкусное!", CatPersona.Food),
            new("Специалист по мурчанию занят ноутбуком.", CatPersona.Work),
            new("Печатаю лапками. Не отвле… ха-ха!", CatPersona.Work),
            new("Секундочку, сохраню важный мур.", CatPersona.Work),
            new("Кот думает. Усы помогают.", CatPersona.Think),
            new("Хм… а щекотка входит в мои планы?", CatPersona.Think),
            new("Вот теперь мне точно есть над чем подумать.", CatPersona.Think),
            new("Я не сплю. Я коплю мурчание.", CatPersona.Sleep),
            new("Ещё пять минуточек пушистого отдыха.", CatPersona.Sleep),
            new("Во сне я тоже щекотный.", CatPersona.Sleep),
            new("Полотенце есть, серьёзности уже нет!", CatPersona.Groom),
            new("Сейчас вытру усы и отвечу мурчанием.", CatPersona.Groom),
            new("Пушистость привожу в порядок.", CatPersona.Groom),
            new("Гантели держу, смех — уже нет!", CatPersona.Sport),
            new("Раз, два… ха! Сбился со счёта.", CatPersona.Sport),
            new("Упражнение для усов: улыбнуться!", CatPersona.Sport),
            new("Я всё вижу из своего укрытия.", CatPersona.Peek),
            new("Ку-ку! Ну вот, кот обнаружен.", CatPersona.Peek),
            new("Один глаз — на тебя, второй — на щекотку.", CatPersona.Peek),
            new("Вот за это — лапку!", CatPersona.Wave),
            new("С таким сердечком сердиться невозможно.", CatPersona.Heart),
            new("Принимаю клик и отправляю мур.", CatPersona.Love),
            new("Делаю важный вид. Получается?", CatPersona.Pride),
            new("Усы гордо вверх!", CatPersona.Pride)
        ]);
        return result;
    }
}
