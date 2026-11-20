using System.Net;
using System.Security.Cryptography;
using System.Text;
using Codex.Core.Systems;
using Codex.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

/// <summary>The list of released systems and their downloads, against a fake server: no test reaches the internet.</summary>
public sealed class SystemCatalogTests
{
    private const string Base = "https://github.com/myarichuk/TTRPG.Codex/releases/download/v1.4.0/";

    private sealed class FakeServer : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SystemCatalogEntry Entry(byte[] archive, string file = "system-street-1.0.0.zip") =>
        new("street", "Street rules", "1.0.0", "Fast and dirty.", "CC-BY-4.0", null, false, file, archive.Length,
            Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant());

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static SystemCatalog Catalog(FakeServer server, string url)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(server, disposeHandler: false));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [SystemCatalog.CatalogUrlKey] = url }).Build();
        return new SystemCatalog(factory, configuration, NullLogger<SystemCatalog>.Instance, "1.4.0", isRelease: true);
    }

    private static SystemCatalog Default(FakeServer server, string version = "1.4.0", Clock? clock = null, bool release = true)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(server, disposeHandler: false));
        return new SystemCatalog(factory, new ConfigurationBuilder().Build(), NullLogger<SystemCatalog>.Instance, version, release, clock);
    }

    // ---- Where the list is read from ----

    [Fact]
    public void TheDefaultUrl_IsTheReleaseOfTheAppsOwnVersion_WithoutBuildMetadata()
    {
        var catalog = Default(new FakeServer(), "1.4.0+abc123");

        Assert.Equal(Base + "systems.json", catalog.CatalogUri!.ToString());
        Assert.False(catalog.FromLatest);
        Assert.Equal("TTRPG.Codex/1.4.0", catalog.UserAgent);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.0+abc")]
    [InlineData("1.4.0")]
    [InlineData("not-a-version")]
    public void ALocalBuild_ReadsTheLatestRelease_WhateverItsVersionSays(string version)
    {
        var catalog = Default(new FakeServer(), version, release: false);

        Assert.Equal(SystemCatalog.LatestUrl, catalog.CatalogUri!.ToString());
        Assert.True(catalog.FromLatest);
    }

    [Fact]
    public void AReleaseNumbered1_0_0_ReadsItsOwnRelease()
    {
        var catalog = Default(new FakeServer(), "1.0.0", release: true);

        Assert.EndsWith("/releases/download/v1.0.0/systems.json", catalog.CatalogUri!.ToString());
        Assert.False(catalog.FromLatest);
    }

    [Fact]
    public async Task AConfiguredUrlOverPlainHttp_TurnsTheListOff()
    {
        var server = new FakeServer();
        var catalog = Catalog(server, "http://mirror.example.org/codex/systems.json");

        Assert.False(catalog.Enabled);
        Assert.Null(await catalog.ListAsync());
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task AnEmptySetting_TurnsTheListOff_AndNothingIsRequested()
    {
        var server = new FakeServer();
        var catalog = Catalog(server, "");

        Assert.False(catalog.Enabled);
        Assert.Null(await catalog.ListAsync());
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void AConfiguredUrl_IsUsed_WithTheVersionFilledIn()
    {
        var catalog = Catalog(new FakeServer(), "https://mirror.example.org/codex/{version}/systems.json");

        Assert.Equal("https://mirror.example.org/codex/1.4.0/systems.json", catalog.CatalogUri!.ToString());
    }

    // ---- The list ----

    [Fact]
    public async Task TheList_IsReadWithAGenericUserAgent_AndKeptForAFewMinutes()
    {
        var archive = new byte[] { 1, 2, 3 };
        var server = new FakeServer { Respond = _ => Json(SystemCatalogFile.Write(new[] { Entry(archive) })) };
        var clock = new Clock();
        var catalog = Default(server, clock: clock);

        var first = await catalog.ListAsync();
        var second = await catalog.ListAsync();

        Assert.Equal("street", Assert.Single(first!.Systems).Id);
        Assert.Same(first, second);
        Assert.Single(server.Requests);
        Assert.Equal("TTRPG.Codex/1.4.0", server.Requests[0].Headers.UserAgent.ToString());
        Assert.Equal(Base + "systems.json", server.Requests[0].RequestUri!.ToString());

        clock.Now += TimeSpan.FromMinutes(6);
        await catalog.ListAsync();
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task AFailedList_IsKeptForHalfAMinute_ThenAskedAgain_AndTryAgainAsksAtOnce()
    {
        var server = new FakeServer();
        var clock = new Clock();
        var catalog = Default(server, clock: clock);

        Assert.Null(await catalog.ListAsync());
        server.Respond = _ => Json(SystemCatalogFile.Write(Array.Empty<SystemCatalogEntry>()));
        Assert.Null(await catalog.ListAsync());
        Assert.Single(server.Requests);

        clock.Now += TimeSpan.FromSeconds(31);
        Assert.Empty((await catalog.ListAsync())!.Systems);
        Assert.Equal(2, server.Requests.Count);

        server.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Null(await catalog.ListAsync(refresh: true));
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task ABadUriOrArgument_IsAFailure_NotAnException()
    {
        var server = new FakeServer { Respond = _ => throw new FormatException("bad header") };

        Assert.Null(await Default(server).ListAsync());
        var archive = new byte[] { 1 };
        Assert.Equal(SystemDownloadFailure.Unreachable, (await Default(server).DownloadAsync(Entry(archive))).Failure);
    }

    [Fact]
    public async Task AnIdWithAnUnderscore_IsListedAndDownloadable()
    {
        var archive = new byte[] { 1, 2 };
        var entry = Entry(archive, "system-my_rules-1.0.0.zip") with { Id = "my_rules" };
        var server = new FakeServer { Respond = r => r.RequestUri!.AbsolutePath.EndsWith("systems.json", StringComparison.Ordinal) ? Json(SystemCatalogFile.Write(new[] { entry })) : Bytes(archive) };
        var catalog = Default(server);

        Assert.Equal("my_rules", Assert.Single((await catalog.ListAsync())!.Systems).Id);
        Assert.Equal(archive, (await catalog.DownloadAsync(entry)).Bytes);
    }

    [Fact]
    public async Task ARelativeRedirect_AndAChainOfRedirects_AreFollowed()
    {
        var archive = new byte[] { 7 };
        var server = new FakeServer
        {
            Respond = r => r.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("system-street-1.0.0.zip", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/other/file.zip", UriKind.Relative) } },
                "/other/file.zip" when r.RequestUri.Host == "github.com" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://objects.githubusercontent.com/x") } },
                _ => Bytes(archive)
            }
        };

        Assert.Equal(archive, (await Default(server).DownloadAsync(Entry(archive))).Bytes);
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task EndlessRedirects_StopAfterAFewHops()
    {
        var hops = 0;
        var server = new FakeServer { Respond = _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://github.com/loop/" + hops++) } } };

        var download = await Default(server).DownloadAsync(Entry(new byte[] { 1 }));

        Assert.Equal(SystemDownloadFailure.Unreachable, download.Failure);
        Assert.Equal(6, server.Requests.Count);
    }

    [Fact]
    public async Task ARedirectToTheConfiguredHost_IsFollowedOverHttpsOnly()
    {
        var archive = new byte[] { 5 };
        var server = new FakeServer
        {
            Respond = r => r.RequestUri!.AbsolutePath == "/x/systems.json"
                ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://mirror.example.org/y/list.json") } }
                : Json(SystemCatalogFile.Write(new[] { Entry(archive) }))
        };

        Assert.Single((await Catalog(server, "https://mirror.example.org/x/systems.json").ListAsync())!.Systems);
        Assert.Equal(2, server.Requests.Count);

        var other = new FakeServer { Respond = _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://cdn.mirror.example.org/list.json") } } };
        Assert.Null(await Catalog(other, "https://mirror.example.org/x/systems.json").ListAsync());
    }

    [Fact]
    public async Task ARedirectFromAConfiguredHostToPlainHttp_IsRefused()
    {
        var server = new FakeServer { Respond = _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://mirror.example.org/systems.json") } } };

        Assert.Null(await Catalog(server, "https://mirror.example.org/x/systems.json").ListAsync());
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task ADownloadWhoseContentLengthIsNotTheListedSize_IsRefused()
    {
        var archive = new byte[] { 1, 2, 3 };
        var server = new FakeServer { Respond = _ => Bytes(new byte[] { 1, 2, 3, 4 }) };

        Assert.Equal(SystemDownloadFailure.Refused, (await Default(server).DownloadAsync(Entry(archive))).Failure);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"format\":2,\"systems\":[]}")]
    public async Task ABadOrUnknownFormatList_IsAFailure_NotAnException(string text)
    {
        var catalog = Default(new FakeServer { Respond = _ => Json(text) });

        Assert.Null(await catalog.ListAsync());
    }

    [Fact]
    public async Task AServerThatIsDown_IsAFailure()
    {
        var server = new FakeServer { Respond = _ => throw new HttpRequestException("no route") };

        Assert.Null(await Default(server).ListAsync());
    }

    [Fact]
    public async Task EntriesWithAFileNameThatBreaksTheRule_OrABadChecksum_AreLeftOut()
    {
        var good = Entry(new byte[] { 1 });
        var json = SystemCatalogFile.Write(new[]
        {
            good,
            good with { Id = "a", File = "../system-a-1.0.0.zip" },
            good with { Id = "b", File = "https://evil.example/system-b-1.0.0.zip" },
            good with { Id = "c", File = "notes.txt" },
            good with { Id = "d", Sha256 = "abc" },
            good with { Id = "e", Size = SystemArchive.MaxBytes + 1 }
        });

        var listing = await Default(new FakeServer { Respond = _ => Json(json) }).ListAsync();

        Assert.Equal("street", Assert.Single(listing!.Systems).Id);
    }

    // ---- Redirects ----

    [Fact]
    public async Task ARedirectToGithubsFileHost_IsFollowed()
    {
        var archive = new byte[] { 9, 9, 9 };
        var server = new FakeServer
        {
            Respond = r => r.RequestUri!.Host == "github.com"
                ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/x/file") } }
                : Bytes(archive)
        };

        var download = await Default(server).DownloadAsync(Entry(archive));

        Assert.Equal(archive, download.Bytes);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(Base + "system-street-1.0.0.zip", server.Requests[0].RequestUri!.ToString());
    }

    [Theory]
    [InlineData("https://evil.example/file.zip")]
    [InlineData("http://github.com/file.zip")]
    [InlineData("https://githubusercontent.com.evil.example/file.zip")]
    public async Task ARedirectToAnyOtherHost_IsRefused_AndNeverRequested(string target)
    {
        var archive = new byte[] { 9 };
        var server = new FakeServer { Respond = _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(target) } } };

        var download = await Default(server).DownloadAsync(Entry(archive));

        Assert.Null(download.Bytes);
        Assert.Single(server.Requests);
    }

    // ---- Downloads ----

    [Fact]
    public async Task ADownloadWithAnotherChecksum_IsRefused()
    {
        var archive = new byte[] { 1, 2, 3 };
        var server = new FakeServer { Respond = _ => Bytes(new byte[] { 1, 2, 4 }) };

        var download = await Default(server).DownloadAsync(Entry(archive));

        Assert.Equal((null, SystemDownloadFailure.Refused), (download.Bytes, download.Failure));
    }

    private sealed class EndlessContent : HttpContent
    {
        public long Served;

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Endless(this));

        private sealed class Endless(EndlessContent owner) : Stream
        {
            public override int Read(byte[] buffer, int offset, int count)
            {
                owner.Served += count;
                return count;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task ADownloadThatIsBiggerThanTheListSaid_IsStoppedWithoutReadingItAll()
    {
        var content = new EndlessContent();
        var server = new FakeServer { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content } };
        var archive = new byte[1000];

        var download = await Default(server).DownloadAsync(Entry(archive));

        Assert.Equal(SystemDownloadFailure.Refused, download.Failure);
        Assert.True(content.Served < 1_000_000, $"{content.Served} bytes were read");
    }

    [Fact]
    public async Task ADownloadThatIsSmallerThanTheListSaid_IsRefused()
    {
        var archive = new byte[] { 1, 2, 3 };
        var server = new FakeServer { Respond = _ => Bytes(new byte[] { 1, 2 }) };

        Assert.Equal(SystemDownloadFailure.Refused, (await Default(server).DownloadAsync(Entry(archive))).Failure);
    }

    [Fact]
    public async Task AnEntryTheRuleDoesNotAllow_IsNeverRequested()
    {
        var archive = new byte[] { 1 };
        var server = new FakeServer { Respond = _ => Bytes(archive) };

        var download = await Default(server).DownloadAsync(Entry(archive, file: "../../other/system-x-1.zip"));

        Assert.Equal(SystemDownloadFailure.Refused, download.Failure);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ADownloadThatIsNotFound_IsUnreachable()
    {
        var archive = new byte[] { 1 };

        Assert.Equal(SystemDownloadFailure.Unreachable, (await Default(new FakeServer()).DownloadAsync(Entry(archive))).Failure);
    }

    [Fact]
    public async Task ADownloadWithTheRightSizeAndChecksum_IsHandedOver()
    {
        var archive = Encoding.UTF8.GetBytes("a zip, pretend");
        var server = new FakeServer { Respond = _ => Bytes(archive) };

        var download = await Default(server).DownloadAsync(Entry(archive));

        Assert.Equal(archive, download.Bytes);
        Assert.Equal(SystemDownloadFailure.None, download.Failure);
    }
}
