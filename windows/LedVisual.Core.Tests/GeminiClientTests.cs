using System.Net;
using System.Net.Http;
using System.Text;
using LedVisual.Core.Models;
using LedVisual.Core.Services;
using Xunit;

namespace LedVisual.Core.Tests;

internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(handler(request));
    }
}

public sealed class GeminiClientTests
{
    [Fact]
    public void ExtractAnswerTextSkipsThoughtParts()
    {
        const string json = """
        {
          "candidates": [
            {
              "content": {
                "parts": [
                  { "text": "Let's check options 1, 2, 3, 4 carefully. Option 3 is Paris.", "thought": true },
                  { "text": "3" }
                ]
              }
            }
          ]
        }
        """;

        var extracted = GeminiClient.ExtractAnswerText(json);
        Assert.Equal("3", extracted);
    }

    [Fact]
    public async Task SolveMcqFallsBackToAlternateModelOn503()
    {
        var requestedUrls = new List<string>();
        var sawApiKeyHeader = false;

        var handler = new StubHttpMessageHandler(request =>
        {
            var urlStr = request.RequestUri?.AbsoluteUri ?? string.Empty;
            requestedUrls.Add(urlStr);

            if (request.Headers.TryGetValues("x-goog-api-key", out var values) &&
                values.Contains("test-key"))
            {
                sawApiKeyHeader = true;
            }

            // Key must never appear in query string
            Assert.DoesNotContain("test-key", urlStr);

            if (urlStr.Contains("gemini-3.5-flash-lite"))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        """{"error":{"code":503,"message":"High demand"}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"candidates":[{"content":{"parts":[{"text":"Thinking...","thought":true},{"text":"1,3"}]}}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var client = new GeminiClient(
            httpClient: httpClient,
            maxRetriesPerModel: 1,
            sleepMs: (_, _) => Task.CompletedTask);

        var resolution = await client.SolveMcqAsync(
            jpegFrames: new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
            apiKey: "test-key",
            model: AIModel.Gemini35FlashLite,
            autoFallback: true);

        Assert.True(sawApiKeyHeader);
        Assert.Equal(new[] { 1, 3 }, resolution.Answers);
        Assert.Equal(AIModel.Gemini35Flash, resolution.ModelUsed);
        // 2 attempts on flash-lite (initial + 1 retry), then 1 success on flash
        Assert.Equal(3, requestedUrls.Count);
    }

    [Fact]
    public async Task SolveMcqThrowsUnparseableOnNoneWithoutFallback()
    {
        var callCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"candidates":[{"content":{"parts":[{"text":"NONE"}]}}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var client = new GeminiClient(
            httpClient: httpClient,
            maxRetriesPerModel: 1,
            sleepMs: (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<GeminiClientException>(() =>
            client.SolveMcqAsync(
                jpegFrames: new[] { new byte[] { 0xFF, 0xD8 } },
                apiKey: "test-key",
                model: AIModel.Gemma4_26B_A4B,
                autoFallback: true));

        Assert.Equal(GeminiErrorKind.UnparseableAnswer, ex.Kind);
        Assert.Equal("NONE", ex.RawText);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void DecisionApiSingleAndMultiSelectParsing()
    {
        const string singleJson = """
        {
          "answers": {
            "has_mcq": { "type": "noul", "noul": 0.91 },
            "is_multi_select": { "type": "noul", "noul": 0.08 },
            "single_choice": { "type": "choice", "choice": "3", "confidence": 0.98 },
            "option_1": { "type": "noul", "noul": 0.04 },
            "option_3": { "type": "noul", "noul": 0.96 }
          }
        }
        """;
        var (singleAnswers, _) = GeminiClient.ParseDecisionResponse(singleJson);
        Assert.Equal(new[] { 3 }, singleAnswers);

        const string multiJson = """
        {
          "answers": {
            "has_mcq": { "type": "noul", "noul": 0.92 },
            "is_multi_select": { "type": "noul", "noul": 0.93 },
            "single_choice": { "type": "choice", "choice": "none", "confidence": 0.5 },
            "option_1": { "type": "noul", "noul": 0.90 },
            "option_2": { "type": "noul", "noul": 0.06 },
            "option_3": { "type": "noul", "noul": 0.89 },
            "option_4": { "type": "noul", "noul": 0.07 }
          }
        }
        """;
        var (multiAnswers, _) = GeminiClient.ParseDecisionResponse(multiJson);
        Assert.Equal(new[] { 1, 3 }, multiAnswers);

        const string nonMcqJson = """
        {
          "answers": {
            "has_mcq": { "type": "noul", "noul": 0.12 },
            "is_multi_select": { "type": "noul", "noul": 0.2 },
            "single_choice": { "type": "choice", "choice": "none", "confidence": 0.95 }
          }
        }
        """;
        var ex = Assert.Throws<GeminiClientException>(() => GeminiClient.ParseDecisionResponse(nonMcqJson));
        Assert.Equal(GeminiErrorKind.UnparseableAnswer, ex.Kind);
    }

    [Fact]
    public async Task DecisionApiFallsBackToFlashLiteOn503()
    {
        var sawDecisionsEndpoint = false;
        var handler = new StubHttpMessageHandler(request =>
        {
            var urlStr = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (urlStr.EndsWith("/decisions", StringComparison.Ordinal))
            {
                sawDecisionsEndpoint = true;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        """{"error":{"code":503,"message":"Overloaded"}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"candidates":[{"content":{"parts":[{"text":"2"}]}}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var client = new GeminiClient(
            httpClient: httpClient,
            maxRetriesPerModel: 0,
            sleepMs: (_, _) => Task.CompletedTask);

        var resolution = await client.SolveMcqAsync(
            jpegFrames: new[] { new byte[] { 0xFF, 0xD8 } },
            apiKey: "test-key",
            model: AIModel.GeminiDecision1,
            autoFallback: true);

        Assert.True(sawDecisionsEndpoint);
        Assert.Equal(new[] { 2 }, resolution.Answers);
        Assert.Equal(AIModel.Gemini35FlashLite, resolution.ModelUsed);
    }
}
