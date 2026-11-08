using System.Text;

namespace Codex.Core.Book;

/// <summary>
/// Sample pages in each language a book prints (docs/BOOK_EXPORT.md 41.21, 41.22), written the way the generator writes a draft: a cover, the
/// contents, a chapter with an entry, a heading of each level the themes style, the boxed blocks, a monster block, a table, a list, and text
/// enough for two columns. The theme wizard previews a theme on them, and the tests draw every theme on them.
/// </summary>
public static class BookThemeSample
{
    /// <summary>The sample's words in one language.</summary>
    public sealed record Words(string Title, string Tagline, string Contents, string Chapter, string Name, string Paragraph, string NoteTitle, string Note,
        string Descriptive, string Secret, string Ship, string Lost, string A, string B, string Item1, string Item2);

    public static IReadOnlyDictionary<string, Words> Text { get; } = new Dictionary<string, Words>(StringComparer.Ordinal)
    {
        ["ru"] = new("Хроники Солтмира", "Кампания для четырёх игроков у холодного моря", "Содержание", "Действующие лица", "Вера Холлоуэй",
            "Вера держит маяк Грейуотер и ведёт **книгу всех кораблей**, что проходят риф ночью. Она *помнит каждый шторм* с тех пор, как колокольня упала в море — «и никто не спрашивает дважды», № 5, 1311 год.",
            "Заметка смотрителя", "Смотритель знает больше, чем говорит.", "В фонарной пахнет маслом и солью.", "Она спрятала вырванную страницу в фонарной.",
            "Корабль", "Пропал", "Олуша", "Крачка", "Ёж и ёлка", "Щедрый Жук"),
        ["he"] = new("דברי הימים של סולטמיר", "מערכה לארבעה שחקנים לחוף ים קר", "תוכן העניינים", "הדמויות", "ורה הולוויי",
            "ורה שומרת על המגדלור של גרייווטר ומנהלת **פנקס של כל ספינה** שעוברת את השונית בלילה. היא *זוכרת כל סערה* מאז שמגדל הפעמון נפל לים — \"ואיש אינו שואל פעמיים\", 5 ₪, שנת 1311. בית־הספר ז׳ורז׳, צה״ל.",
            "הערת השומר", "השומר יודע יותר ממה שהוא אומר.", "חדר הפנס מריח שמן ומלח.", "היא החביאה את הדף הקרוע בחדר הפנס.", "ספינה", "אבדה", "סולה", "שחפית",
            "ךםןףץ", "אבגדהוזחטיכלמנסעפצקרשת"),
        ["en"] = new("Saltmere Chronicle", "A campaign for four players by a cold sea", "Contents", "Cast", "Vera Holloway",
            "Vera keeps the Greywater lamp and a **ledger of every ship** that passes the reef at night. She *remembers every storm* since the bell tower fell into the sea — “and nobody asks twice”, No. 5, the year 1311.",
            "A keeper's note", "The keeper knows more than she says.", "The lamp room smells of oil and salt.", "She hid the torn page in the lamp room.",
            "Ship", "Lost", "Gannet", "Tern", "Quick fox", "Jumping")
    };

    public static Words For(string language) => Text.TryGetValue(BookThemes.Primary(language), out var w) ? w : Text["en"];

    public static string Markdown(string language)
    {
        var w = For(language);
        var md = new StringBuilder();
        md.Append($"{{{{frontCover}}}}\n\n# {w.Title}\n\n{w.Tagline}\n\n\\page\n\n{{{{toc,wide\n# {w.Contents}\n\n- [{w.Chapter}](#ch-cast)\n}}}}\n\n\\page\n\n");
        md.Append($"{{{{chapter,#ch-cast\n# {w.Chapter}\n}}}}\n\n{w.Paragraph}\n\n{{{{entry,#e-s1}}}}\n## {w.Name}\n\n");
        for (var i = 0; i < 9; i++) md.Append(w.Paragraph).Append("\n\n");
        md.Append($"### {w.NoteTitle}\n\n{{{{note\n##### {w.NoteTitle}\n{w.Note}\n}}}}\n\n{{{{descriptive\n{w.Descriptive}\n}}}}\n\n{{{{secret\n{w.Secret}\n}}}}\n\n");
        md.Append($"{{{{monster,frame\n## {w.Name}\n*{w.Descriptive}*\n___\n**{w.Ship}** 12\n\n{w.Note}\n}}}}\n\n");
        md.Append($"| {w.Ship} | {w.Lost} |\n|---|---|\n| {w.A} | 1311 |\n| {w.B} | 1314 |\n\n- {w.Item1}\n- {w.Item2}\n\n");
        for (var i = 0; i < 10; i++) md.Append(w.Paragraph).Append("\n\n");
        return md.ToString();
    }
}
