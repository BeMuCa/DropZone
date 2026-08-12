using System.Text.Json;
using Dropzone.LocalSend;

namespace Dropzone.LocalSend.Tests;

public class DeviceInfoJsonTests
{
    [Fact]
    public void Deserializes_the_spec_announcement_example()
    {
        const string json = """
        {
          "alias": "Nice Orange",
          "version": "2.0",
          "deviceModel": "Samsung",
          "deviceType": "mobile",
          "fingerprint": "random string",
          "port": 53317,
          "protocol": "https",
          "download": true,
          "announce": true
        }
        """;

        var info = JsonSerializer.Deserialize<DeviceInfo>(json, LocalSendJson.Options)!;

        Assert.Equal("Nice Orange", info.Alias);
        Assert.Equal("2.0", info.Version);
        Assert.Equal("Samsung", info.DeviceModel);
        Assert.Equal("mobile", info.DeviceType);
        Assert.Equal(53317, info.Port);
        Assert.Equal("https", info.Protocol);
        Assert.True(info.Download);
        Assert.True(info.Announce);
    }

    [Fact]
    public void Serializes_using_protocol_field_names()
    {
        var info = new DeviceInfo
        {
            Alias = "Secret Banana",
            Version = "2.0",
            DeviceModel = "Windows",
            DeviceType = "desktop",
            Fingerprint = "abc",
            Port = 53317,
            Protocol = "http",
            Download = false,
            Announce = true
        };

        var json = JsonSerializer.Serialize(info, LocalSendJson.Options);

        Assert.Contains("\"deviceModel\":\"Windows\"", json);
        Assert.Contains("\"deviceType\":\"desktop\"", json);
        Assert.Contains("\"fingerprint\":\"abc\"", json);
        Assert.Contains("\"announce\":true", json);
        Assert.DoesNotContain("DeviceModel", json);
    }

    [Fact]
    public void Omits_announce_when_not_set()
    {
        var info = new DeviceInfo { Alias = "x", Fingerprint = "f", Port = 1, Protocol = "http" };

        var json = JsonSerializer.Serialize(info, LocalSendJson.Options);

        Assert.DoesNotContain("announce", json);
    }

    [Fact]
    public void Round_trips_a_prepare_upload_request()
    {
        var req = new PrepareUploadRequest
        {
            Info = new DeviceInfo { Alias = "A", Fingerprint = "f", Port = 53317, Protocol = "http" },
            Files = new Dictionary<string, FileDto>
            {
                ["id1"] = new() { Id = "id1", FileName = "my image.png", Size = 324242, FileType = "image/png" }
            }
        };

        var json = JsonSerializer.Serialize(req, LocalSendJson.Options);
        var back = JsonSerializer.Deserialize<PrepareUploadRequest>(json, LocalSendJson.Options)!;

        Assert.Equal("my image.png", back.Files["id1"].FileName);
        Assert.Equal(324242, back.Files["id1"].Size);
        Assert.Contains("\"fileName\":\"my image.png\"", json);
    }
}

public class SessionStoreTests
{
    static Dictionary<string, FileDto> TwoFiles() => new()
    {
        ["a"] = new FileDto { Id = "a", FileName = "a.jpg", Size = 10, FileType = "image/jpeg" },
        ["b"] = new FileDto { Id = "b", FileName = "b.jpg", Size = 20, FileType = "image/jpeg" }
    };

    [Fact]
    public void Issues_a_token_per_file()
    {
        var store = new SessionStore();

        var session = store.Create(TwoFiles(), "127.0.0.1");

        Assert.Equal(2, session.FileTokens.Count);
        Assert.NotEqual(session.FileTokens["a"], session.FileTokens["b"]);
        Assert.NotEmpty(session.SessionId);
    }

    [Fact]
    public void Accepts_a_matching_token()
    {
        var store = new SessionStore();
        var session = store.Create(TwoFiles(), "127.0.0.1");

        Assert.True(store.TryValidate(session.SessionId, "a", session.FileTokens["a"], "127.0.0.1", out var file));
        Assert.Equal("a.jpg", file!.FileName);
    }

    [Fact]
    public void Rejects_a_wrong_token()
    {
        var store = new SessionStore();
        var session = store.Create(TwoFiles(), "127.0.0.1");

        Assert.False(store.TryValidate(session.SessionId, "a", "not-the-token", "127.0.0.1", out _));
    }

    [Fact]
    public void Rejects_a_token_from_a_different_address()
    {
        var store = new SessionStore();
        var session = store.Create(TwoFiles(), "127.0.0.1");

        Assert.False(store.TryValidate(session.SessionId, "a", session.FileTokens["a"], "10.0.0.9", out _));
    }

    [Fact]
    public void Rejects_an_unknown_session()
    {
        var store = new SessionStore();

        Assert.False(store.TryValidate("nope", "a", "t", "127.0.0.1", out _));
    }

    [Fact]
    public void Cancel_invalidates_the_session()
    {
        var store = new SessionStore();
        var session = store.Create(TwoFiles(), "127.0.0.1");

        store.Cancel(session.SessionId);

        Assert.False(store.TryValidate(session.SessionId, "a", session.FileTokens["a"], "127.0.0.1", out _));
    }

    [Fact]
    public void Only_one_session_at_a_time()
    {
        var store = new SessionStore();
        store.Create(TwoFiles(), "127.0.0.1");

        Assert.Throws<SessionBusyException>(() => store.Create(TwoFiles(), "10.0.0.5"));
    }

    [Fact]
    public void A_new_session_is_allowed_after_cancel()
    {
        var store = new SessionStore();
        var first = store.Create(TwoFiles(), "127.0.0.1");
        store.Cancel(first.SessionId);

        var second = store.Create(TwoFiles(), "10.0.0.5");

        Assert.NotEqual(first.SessionId, second.SessionId);
    }
}

public class SafeFileNameTests
{
    [Theory]
    [InlineData("../../evil.txt", "evil.txt")]
    [InlineData(@"..\..\evil.txt", "evil.txt")]
    [InlineData(@"C:\Windows\System32\evil.dll", "evil.dll")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("normal.jpg", "normal.jpg")]
    [InlineData("with space.png", "with space.png")]
    public void Strips_path_components(string input, string expected)
    {
        Assert.Equal(expected, SafeFileName.Of(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("/")]
    public void Falls_back_when_nothing_usable_remains(string input)
    {
        var result = SafeFileName.Of(input);

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain('\\', result);
    }

    [Fact]
    public void Removes_characters_windows_rejects()
    {
        var result = SafeFileName.Of("a:b*c?d.jpg");

        foreach (var c in Path.GetInvalidFileNameChars())
            Assert.DoesNotContain(c, result);
    }
}
