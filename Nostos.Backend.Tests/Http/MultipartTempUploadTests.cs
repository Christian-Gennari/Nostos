using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Product.Http;
using Xunit;

namespace Nostos.Backend.Tests.Http;

/// <summary>
/// The shared multipart spooling helper (#1629): the highlight import and the
/// reading-history import now write an upload to the same private temp
/// directory under a name the server chose, and delete it whatever happens.
/// </summary>
public sealed class MultipartTempUploadTests
{
    [Theory]
    [InlineData("kobo.sqlite", "kobo.sqlite")]
    [InlineData("folder/kobo.sqlite", "kobo.sqlite")]
    [InlineData("  spaced.csv  ", "spaced.csv")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(".", null)]
    [InlineData("..", null)]
    [InlineData(".hidden.sqlite", null)]
    [InlineData("folder/..", null)]
    public void Only_a_plain_file_name_is_kept(string fileName, string? expected)
    {
        MultipartTempUpload.SafeUploadName(fileName).Should().Be(expected);
    }

    [Fact]
    public void A_name_longer_than_a_file_name_can_be_is_refused()
    {
        MultipartTempUpload.SafeUploadName(new string('a', 200)).Should().HaveLength(200);
        MultipartTempUpload.SafeUploadName(new string('a', 201)).Should().BeNull();
    }

    [Fact]
    public async Task An_upload_is_spooled_under_a_safe_name_and_its_directory_is_removed()
    {
        var request = await MultipartRequestAsync(
            ("file", ".hidden.sqlite", Encoding.ASCII.GetBytes("not a database")));

        var upload = await MultipartTempUpload.ReceiveAsync(request, Options(), CancellationToken.None);
        var directory = Path.GetDirectoryName(upload.Path)!;
        try
        {
            upload.OriginalName.Should().Be(".hidden.sqlite");
            upload.Name.Should().Be("upload", "a dot-prefixed name is never written");
            Path.GetFileName(upload.Path).Should().Be("upload");
            (await File.ReadAllTextAsync(upload.Path)).Should().Be("not a database");
        }
        finally
        {
            upload.Dispose();
        }

        Directory.Exists(directory).Should().BeFalse("the scratch directory never outlives the request");
    }

    [Fact]
    public async Task Companion_files_keep_their_own_names_and_never_collide()
    {
        var request = await MultipartRequestAsync(
            ("file", "Reader.sqlite", Encoding.ASCII.GetBytes("main")),
            ("companion", "Reader.sqlite", Encoding.ASCII.GetBytes("clash")),
            ("companion", ".hidden.sqlite", Encoding.ASCII.GetBytes("unnamed")));

        using var upload = await MultipartTempUpload.ReceiveAsync(request, Options(), CancellationToken.None);
        var companions = upload.Form.Files.GetFiles("companion");

        var clash = await upload.SpoolAsync(companions[0], "companion-0", CancellationToken.None);
        var unnamed = await upload.SpoolAsync(companions[1], "companion-1", CancellationToken.None);

        Path.GetFileName(upload.Path).Should().Be("Reader.sqlite");
        Path.GetFileName(clash).Should().Be("companion-0-1", "the first file already owns that name");
        Path.GetFileName(unnamed).Should().Be("companion-1");
        (await File.ReadAllTextAsync(clash)).Should().Be("clash");
        (await File.ReadAllTextAsync(unnamed)).Should().Be("unnamed");
    }

    [Fact]
    public async Task A_request_that_is_not_a_multipart_form_is_refused_with_its_own_words()
    {
        var request = new DefaultHttpContext().Request;
        request.ContentType = "application/json";

        var receive = () => MultipartTempUpload.ReceiveAsync(request, Options(), CancellationToken.None);

        (await receive.Should().ThrowAsync<MultipartUploadRejectedException>())
            .Which.Message.Should().Be(Options().ExpectedFormMessage);
    }

    [Fact]
    public async Task An_empty_upload_is_refused_with_its_own_words()
    {
        var request = await MultipartRequestAsync(("file", "empty.csv", []));

        var receive = () => MultipartTempUpload.ReceiveAsync(request, Options(), CancellationToken.None);

        (await receive.Should().ThrowAsync<MultipartUploadRejectedException>())
            .Which.Message.Should().Be(Options().TooLargeMessage);
    }

    private static MultipartUploadOptions Options() => new(
        MaxBytes: 1024 * 1024,
        TempPrefix: "nostos-multipart-test-",
        ExpectedFormMessage: "Expected multipart/form-data.",
        MissingFileMessage: "Choose a file to import.",
        TooLargeMessage: "The file must be between 1 byte and 1 MB.");

    private static async Task<HttpRequest> MultipartRequestAsync(
        params (string Field, string Name, byte[] Bytes)[] files)
    {
        var content = new MultipartFormDataContent();
        foreach (var (field, name, bytes) in files)
            content.Add(new ByteArrayContent(bytes), field, name);

        var body = await content.ReadAsStreamAsync();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddOptions().BuildServiceProvider(),
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = content.Headers.ContentType!.ToString();
        context.Request.ContentLength = body.Length;
        context.Request.Body = body;
        return context.Request;
    }
}
