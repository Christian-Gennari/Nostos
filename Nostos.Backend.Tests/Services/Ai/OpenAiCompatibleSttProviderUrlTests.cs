using FluentAssertions;
using Nostos.Backend.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

public class OpenAiCompatibleSttProviderUrlTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/audio/transcriptions")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/audio/transcriptions")]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1/audio/transcriptions")]
    [InlineData("http://whisper:8000/", "http://whisper:8000/v1/audio/transcriptions")]
    [InlineData("https://gateway.example/openai/v1", "https://gateway.example/openai/v1/audio/transcriptions")]
    [InlineData("https://gateway.example/openai", "https://gateway.example/openai/v1/audio/transcriptions")]
    [InlineData("https://gateway.example/vision", "https://gateway.example/vision/v1/audio/transcriptions")]
    public void Transcription_url_accepts_the_endpoint_with_or_without_its_version(
        string baseUrl,
        string expected)
    {
        OpenAiCompatibleSttProvider.TranscriptionUrl(baseUrl).Should().Be(expected);
    }
}
