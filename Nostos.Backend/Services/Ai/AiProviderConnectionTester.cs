using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Nostos.Product.Services.Ai;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Executes live connection probes for LLM, STT, Embedding, TTS (read-aloud),
/// and Alignment Transcription provider configurations.
/// </summary>
internal sealed class AiProviderConnectionTester(
    AiProviderSettingsStore store,
    AiProviderProbeHttpClient httpClient,
    OpenAiCompatibleSttProvider multipartStt,
    JsonBase64SttProvider jsonStt)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Always succeeds at the HTTP layer: a refused provider is represented
    /// by {ok:false,error} so the UI can display the failure.
    /// </summary>
    public async Task<AiProviderTestResult> TestAsync(
        AiProviderTestRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var kind = AiProviderProbeHelpers.ParseKind(request.Kind);

            if (kind == AiProviderKind.AlignmentTranscription)
            {
                return await TestAlignmentAsync(request, ct);
            }

            var effective = await AiProviderProbeHelpers.GetEffectiveAsync(store, kind, ct);
            var baseUrl = AiProviderProbeHelpers.ResolveBaseUrl(request.BaseUrl, effective);
            AiProviderProbeHelpers.ValidateResolvedBaseUrl(baseUrl, kind);

            var model = string.IsNullOrWhiteSpace(request.Model)
                ? effective.Model
                : request.Model.Trim();
            var apiKey = AiProviderProbeHelpers.ResolveApiKey(request.ApiKey, effective);

            if (string.IsNullOrWhiteSpace(model))
            {
                return new AiProviderTestResult(
                    false,
                    null,
                    $"{AiProviderProbeHelpers.KindLabel(kind)} model is not configured.");
            }

            return kind switch
            {
                AiProviderKind.Llm =>
                    await TestLlmAsync(baseUrl, model, apiKey, ct),
                AiProviderKind.Embedding =>
                    await TestEmbeddingAsync(baseUrl, model, apiKey, ct),
                AiProviderKind.Tts =>
                    await TestTtsAsync(baseUrl, model, apiKey, ct),
                _ =>
                    await TestSttAsync(baseUrl, model, apiKey, ct),
            };
        }
        catch (AiProviderValidationException ex)
        {
            return new AiProviderTestResult(false, null, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AiProviderTestResult(false, null, ex.Message);
        }
    }

    private async Task<AiProviderTestResult> TestAlignmentAsync(
        AiProviderTestRequest request,
        CancellationToken ct)
    {
        // Resolve once. The form may test a selection it has not saved yet:
        // an explicit voice-reuse or API-style choice in the request wins over
        // the saved one, so "test" probes what "save" would then use.
        var resolved = await store.ResolveAlignmentProbeConfigAsync(
            request.UseVoiceTranscription,
            request.ApiStyle,
            ct);
        var effective = resolved.Provider;

        var baseUrl = AiProviderProbeHelpers.ResolveBaseUrl(request.BaseUrl, effective);
        AiProviderProbeHelpers.ValidateResolvedBaseUrl(
            baseUrl, AiProviderKind.AlignmentTranscription);

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? effective.Model
            : request.Model.Trim();
        var apiKey = AiProviderProbeHelpers.ResolveApiKey(request.ApiKey, effective);

        if (string.IsNullOrWhiteSpace(model))
        {
            return new AiProviderTestResult(
                false, null, "Alignment transcription model is not configured.");
        }

        // Explicit probe overrides are used only for this call. The selected
        // mode still controls the transport, client and validation.
        var probeConfig = effective with
        {
            // A dedicated setup that is not saved (or not enabled) yet can
            // still be probed; the test answers "would this connect".
            Enabled = resolved.UseVoiceTranscription ? effective.Enabled : true,
            BaseUrl = baseUrl,
            Model = model,
            ApiKey = apiKey,
        };

        using var audio = new MemoryStream(SilentWav(), writable: false);

        try
        {
            switch (resolved.ApiStyle)
            {
                case AlignmentTranscriptionApiStyles.OpenAiMultipart:
                    await multipartStt.TranscribeWithConfigAsync(
                        audio,
                        "silence.wav",
                        "audio/wav",
                        null,
                        probeConfig,
                        ct);
                    break;

                case AlignmentTranscriptionApiStyles.JsonBase64:
                    await jsonStt.TranscribeWithConfigAsync(
                        audio,
                        "silence.wav",
                        "audio/wav",
                        null,
                        probeConfig,
                        ct);
                    break;

                default:
                    return new AiProviderTestResult(
                        false, null, "Unsupported alignment transcription API style.");
            }

            return new AiProviderTestResult(
                true, $"Reached {model}.", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SttException ex)
        {
            return new AiProviderTestResult(false, null, ex.Message);
        }
        catch (HttpRequestException)
        {
            return new AiProviderTestResult(
                false, null, "Alignment transcription provider could not be reached.");
        }
        catch (OperationCanceledException)
        {
            return new AiProviderTestResult(
                false, null, "Alignment transcription timed out.");
        }
    }

    private async Task<AiProviderTestResult> TestLlmAsync(
        string baseUrl,
        string model,
        string? apiKey,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = false,
            ["max_tokens"] = 8,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = "ping",
                },
            },
        }, JsonOptions);

        using var content = new StringContent(
            payload, Encoding.UTF8, "application/json");

        var probe = await httpClient.SendAsync(
            HttpMethod.Post,
            AiProviderProbeHelpers.Combine(baseUrl, "chat/completions"),
            apiKey,
            content,
            ct);

        return probe.Ok
            ? new AiProviderTestResult(true, $"Reached {model}.", null)
            : new AiProviderTestResult(false, null, probe.Error);
    }

    private async Task<AiProviderTestResult> TestSttAsync(
        string baseUrl,
        string model,
        string? apiKey,
        CancellationToken ct)
    {
        // Preserve the existing voice STT test contract unchanged.
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(SilentWav());
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "silence.wav");
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("json"), "response_format");

        var probe = await httpClient.SendAsync(
            HttpMethod.Post,
            OpenAiCompatibleSttProvider.TranscriptionUrl(baseUrl),
            apiKey,
            form,
            ct);

        return probe.Ok
            ? new AiProviderTestResult(true, $"Reached {model}.", null)
            : new AiProviderTestResult(false, null, probe.Error);
    }

    /// <summary>
    /// Asks the speech server to say one word, as read aloud would. Answering
    /// with audio is the test: a server that answers JSON is not a speech
    /// server, whatever its status code.
    /// </summary>
    private async Task<AiProviderTestResult> TestTtsAsync(
        string baseUrl,
        string model,
        string? apiKey,
        CancellationToken ct)
    {
        var uri = OpenAiCompatibleReadAloudProvider.SpeechUri(baseUrl);
        if (uri is null)
        {
            return new AiProviderTestResult(
                false, null, "Read aloud baseUrl must be an absolute http:// or https:// URL.");
        }

        using var content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model,
                input = "Test.",
                voice = KokoroVoices.All[0].Id,
                response_format = "mp3",
                speed = 1.0,
            }),
            Encoding.UTF8,
            "application/json");

        var probe = await httpClient.SendAsync(
            HttpMethod.Post,
            uri.ToString(),
            apiKey,
            content,
            ct,
            OpenAiCompatibleReadAloudProvider.HttpClientName,
            HttpCompletionOption.ResponseHeadersRead);

        if (probe.StatusCode is null)
        {
            return new AiProviderTestResult(
                false, null, "The speech server could not be reached.");
        }

        if (!probe.Ok)
        {
            return new AiProviderTestResult(
                false, null, $"The speech server answered {(int)probe.StatusCode}.");
        }

        var mediaType = probe.MediaType ?? string.Empty;
        return mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? new AiProviderTestResult(true, $"{model} answered with audio.", null)
            : new AiProviderTestResult(
                false, null, "The server answered, but not with audio. Is this a speech server?");
    }

    private async Task<AiProviderTestResult> TestEmbeddingAsync(
        string baseUrl,
        string model,
        string? apiKey,
        CancellationToken ct)
    {
        var inputs = new[] { "ping" };
        using var content = new StringContent(
            OpenAiCompatibleEmbeddings.BuildRequestBody(model, inputs),
            Encoding.UTF8,
            "application/json");

        var probe = await httpClient.SendAsync(
            HttpMethod.Post,
            OpenAiCompatibleEmbeddings.BuildUri(baseUrl).ToString(),
            apiKey,
            content,
            ct);

        if (!probe.Ok)
        {
            return new AiProviderTestResult(false, null, probe.Error);
        }

        try
        {
            var batch = OpenAiCompatibleEmbeddings.ParseResponse(
                probe.Body, model, inputs.Length);

            return new AiProviderTestResult(
                true,
                $"Reached {model} ({batch.Vectors[0].Length} dimensions).",
                null);
        }
        catch (EmbeddingException ex)
        {
            return new AiProviderTestResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Generated one-second silent 16-bit mono WAV.
    /// </summary>
    internal static byte[] SilentWav()
    {
        const int sampleRate = 8000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int seconds = 1;

        var dataLength =
            sampleRate * channels * (bitsPerSample / 8) * seconds;

        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(
            stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        writer.Flush();

        return stream.ToArray();
    }
}
