using Codex.Core.Book;
using Codex.Web.Book;
using Microsoft.Extensions.AI;

namespace Codex.Tests;

internal sealed class ScriptedChat(Func<IList<ChatMessage>, int, string> reply, Func<int, UsageDetails?>? usage = null, TimeSpan? delay = null) : IChatClient
{
    private int _calls;
    private int _running;
    public int Calls => _calls;
    public int Peak { get; private set; }
    public List<string> Requests { get; } = [];

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var call = Interlocked.Increment(ref _calls);
        lock (Requests) Requests.Add(string.Join("\n---\n", list.Select(m => m.Role + ": " + m.Text)));
        var now = Interlocked.Increment(ref _running);
        lock (Requests) Peak = Math.Max(Peak, now);
        try
        {
            if (delay is { } wait) await Task.Delay(wait, cancellationToken);
            var text = reply(list, call);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { Usage = usage?.Invoke(call) };
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

public class BookChecksTests
{
    private const string Source = "## Vera\n\nVera keeps the lighthouse of Saltmere. She is 41 years old and has kept it for 12 winters.\n\n1. Light the lamp.\n2. Ring the bell.";
    private static IReadOnlyList<string> Check(string output, int blocks = 0, string language = "en") => BookChecks.Validate(Source, output, blocks, language);

    private const string Good = "## Vera\n\nVera, 41 years old, has kept the lighthouse of Saltmere for 12 winters.\n\n1. Light the lamp.\n2. Ring the bell.";

    [Fact]
    public void AFaithfulRewrite_Passes() => Assert.Empty(Check(Good));

    [Fact]
    public void ANumberTheSourceNeverHad_IsRefused_ButAListMarkerAndAThousandsSeparatorAreNot()
    {
        Assert.Contains(Check(Good + "\n\nShe is level 17."), p => p.Contains("17"));
        Assert.Contains(Check(Good.Replace("12 winters", "13 winters")), p => p.Contains("13"));
        Assert.Empty(Check(Good + "\n\n3. Sleep.")); // a list marker is not a figure
        Assert.Empty(BookChecks.Validate("Gold: 1,000 pieces.", "Gold: 1000 pieces.", 0, "en"));
        Assert.Empty(BookChecks.Validate("Aged 41.", "Aged ٤١.", 0, "en")); // the same number in Arabic-Indic digits
    }

    [Theory]
    [InlineData("She has kept it for thirty-one winters.", true)]
    [InlineData("There were two hundred and twelve steps.", true)]
    [InlineData("A thousand lanterns burn.", true)]
    [InlineData("She kept it for twelve winters.", false)] // the source says 12
    [InlineData("One of them left, and two stayed.", false)] // idiom, not a figure
    [InlineData("It is a two-handed sword and no one carries it.", false)]
    public void ANumberSpelledOut_IsReadLikeADigit(string reply, bool refused)
    {
        var problems = BookChecks.Validate("## Vera\n\nVera keeps the lighthouse of Saltmere for 12 winters.", "## Vera\n\nVera keeps the lighthouse of Saltmere. " + reply, 0, "en");
        Assert.Equal(refused, problems.Any(p => p.Contains("numbers")));
    }

    [Fact]
    public void ANumberSpelledInTheSource_MayBeSpelledOrWrittenInTheReply()
    {
        var problems = BookChecks.Validate("## Vera\n\nShe kept it for twelve winters.", "## Vera\n\nShe kept it for 12 winters.", 0, "en");
        Assert.DoesNotContain(problems, p => p.Contains("numbers"));
    }

    [Fact]
    public void ANameTheSourceNeverUses_IsRefused_ButInflectionAndSentenceStartsAreNot()
    {
        Assert.Contains(Check(Good + " Her brother Marcus visits."), p => p.Contains("Marcus"));
        Assert.Empty(Check(Good + " The lamp is old. Vera trims it."));
        var ru = BookChecks.Validate("## Вера\n\nВера хранит маяк Солтмира.", "## Вера\n\nУ Веры есть маяк в Солтмире, и она хранит его.", 0, "ru");
        Assert.Empty(ru);
        Assert.NotEmpty(BookChecks.Validate("## Вера\n\nВера хранит маяк Солтмира.", "## Вера\n\nВера хранит маяк, а её брат Марк приходит.", 0, "ru"));
    }

    [Theory]
    [InlineData("See https://evil.example/x for more.")]
    [InlineData("See www.evil.example.")]
    [InlineData("Click javascript:alert(1).")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("![portrait](http://evil/x.png)")]
    [InlineData("![portrait](slot:p1)")]
    [InlineData("\n:::dm\nsecret\n:::\n")]
    [InlineData("[[actor:a-hidden]]")]
    [InlineData("{{danger,color:red text}}")]
    public void ForbiddenSyntax_IsRefused(string injected) => Assert.NotEmpty(Check(Good + "\n\n" + injected));

    [Fact]
    public void Blocks_MustComeBackOnceEach_OnALineOfTheirOwn_AndOnlyTheOnesThatExist()
    {
        Assert.Empty(Check(Good + "\n\n{{block:1}}\n\n{{block:2}}", blocks: 2));
        Assert.Contains(Check(Good + "\n\n{{block:1}}", blocks: 2), p => p.Contains("block:2") && p.Contains("missing"));
        Assert.Contains(Check(Good + "\n\n{{block:1}}\n\n{{block:1}}", blocks: 1), p => p.Contains("2 times"));
        Assert.Contains(Check(Good + "\n\n{{block:1}}\n\n{{block:5}}", blocks: 1), p => p.Contains("does not exist"));
        Assert.Contains(Check(Good + " and {{block:1}} inline.", blocks: 1), p => p.Contains("line of its own"));
    }

    [Fact]
    public void ARewrittenHeading_IsRefused()
    {
        Assert.Contains(Check(Good.Replace("## Vera", "## Lady Vera")), p => p.Contains("\"Vera\""));
        Assert.Empty(BookChecks.Validate("## Bob \\(the\\) Brave\n\nBob is brave.", "## Bob (the) Brave\n\nBob is very brave.", 0, "en")); // the compiler's escapes are not part of the name
    }

    [Fact]
    public void TheWrongLanguage_IsRefused_ForEveryBookLanguage()
    {
        var english = "The lighthouse keeper watches the grey coast every night and rings the bell when ships come near the rocks.";
        Assert.NotEmpty(BookChecks.Validate("Смотритель маяка", english, 0, "ru"));
        Assert.NotEmpty(BookChecks.Validate("שומר המגדלור", english, 0, "he"));
        Assert.Empty(BookChecks.Validate("Lighthouse keeper", english, 0, "en"));
        Assert.NotEmpty(BookChecks.Validate("Lighthouse keeper", "Смотритель маяка каждую ночь следит за серым берегом и звонит в колокол, когда корабли подходят к скалам.", 0, "en"));
    }

    [Fact]
    public void EmptyOrWildlyResizedReplies_AreRefused()
    {
        Assert.NotEmpty(Check(""));
        Assert.NotEmpty(Check("   \n"));
        var long_ = string.Join(" ", Enumerable.Repeat("Vera keeps the lighthouse of Saltmere.", 80));
        Assert.Contains(Check(long_), p => p.Contains("longer"));
        var bigSource = string.Join(" ", Enumerable.Repeat("Vera keeps the lighthouse of Saltmere.", 40));
        Assert.Contains(BookChecks.Validate(bigSource, "Vera.", 0, "en"), p => p.Contains("shorter"));
    }
}

/// <summary>A chapter prepared the way every chapter a model or the art director sees is prepared (<see cref="BookWork.Prepare"/>, the art
/// director's outline, docs/BOOK_EXPORT.md 36): secrets out, mentions read as the renderer reads them.</summary>
public class BookWorkTests
{
    private const string Chapter = "# Cast\n\n## Vera\n\nVera keeps the lighthouse of Saltmere. She is 41 years old.\n\n:::dm\nVera sells the lamp oil to smugglers.\n:::\n\nShe rings the bell at dusk.";

    private static BookWorkText Prepare(string markdown = Chapter, Func<string, string, string?>? mention = null) =>
        BookWork.Prepare(markdown, mention, new BookMarkdownOptions { BoxSecrets = true, Mention = mention });

    [Fact]
    public void ASecretIsCutOutBeforeTheModelSeesAnything_AndComesBackAsTheSameBox()
    {
        var work = Prepare();
        Assert.DoesNotContain("smugglers", work.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(":::", work.Text, StringComparison.Ordinal);
        Assert.Contains("{{block:1}}", work.Text, StringComparison.Ordinal);
        Assert.Contains("secret-dm", work.Blocks.Single().Html, StringComparison.Ordinal);
        Assert.Contains("smugglers", work.Blocks.Single().Html, StringComparison.Ordinal);
    }

    [Fact]
    public void AMention_IsGivenAsAName_AndAHiddenOneAsNothing_NeverAsAnId()
    {
        var work = Prepare("# Cast\n\n[[actor:a-known|Whatever]] met [[actor:a-hidden]] at the docks.", (kind, id) => id == "a-known" ? "Vera" : null);
        Assert.Contains("Vera met  at the docks.", work.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("a-known", work.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("a-hidden", work.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Whatever", work.Text, StringComparison.Ordinal);
    }

    // How many calls a book's runs make at once (BookModelConcurrency).
    [Theory]
    [InlineData("http://localhost:11434/v1", 1)]
    [InlineData("http://127.0.0.1:1234/v1", 1)]
    [InlineData("http://192.168.1.20:8080", 1)]
    [InlineData("http://10.0.0.5", 1)]
    [InlineData("http://172.20.1.1", 1)]
    [InlineData("http://myserver:8000", 1)]
    [InlineData("http://[::1]:8080", 1)]
    [InlineData("https://api.openai.com/v1", 2)]
    [InlineData("https://openrouter.ai/api/v1", 2)]
    [InlineData("http://172.32.0.1", 2)]
    [InlineData("not a url", 2)]
    [InlineData(null, 2)]
    public void Concurrency_IsOneForLocalModelsAndTwoForHostedOnes(string? endpoint, int expected) =>
        Assert.Equal(expected, BookModelConcurrency.For(endpoint));
}
