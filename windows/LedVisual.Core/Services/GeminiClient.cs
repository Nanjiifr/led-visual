using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LedVisual.Core.Models;

namespace LedVisual.Core.Services;

public enum GeminiErrorKind
{
    MissingApiKey,
    EmptyCapture,
    InvalidUrl,
    HttpError,
    EmptyResponse,
    UnparseableAnswer,
    NetworkError
}

public sealed class GeminiClientException : Exception
{
    public GeminiErrorKind Kind { get; }
    public int? StatusCode { get; }
    public string? RawText { get; }

    public bool IsTransient => Kind switch
    {
        GeminiErrorKind.HttpError => StatusCode is 429 or (>= 500 and <= 599),
        GeminiErrorKind.NetworkError => true,
        _ => false
    };

    public GeminiClientException(
        GeminiErrorKind kind,
        string message,
        int? statusCode = null,
        string? rawText = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        RawText = rawText;
    }

    public static GeminiClientException MissingApiKey() =>
        new(GeminiErrorKind.MissingApiKey, "Google AI Studio API key is not configured.");

    public static GeminiClientException EmptyCapture() =>
        new(GeminiErrorKind.EmptyCapture, "No screenshot or video frames were captured.");

    public static GeminiClientException InvalidUrl() =>
        new(GeminiErrorKind.InvalidUrl, "Failed to construct Google AI Studio API URL.");

    public static GeminiClientException HttpError(int statusCode, string message) =>
        new(GeminiErrorKind.HttpError, $"API error (HTTP {statusCode}): {message}", statusCode: statusCode);

    public static GeminiClientException EmptyResponse() =>
        new(GeminiErrorKind.EmptyResponse, "The model returned an empty response.");

    public static GeminiClientException UnparseableAnswer(string rawText) =>
        new(GeminiErrorKind.UnparseableAnswer, $"Could not parse an MCQ answer number from model output: \"{rawText}\"", rawText: rawText);

    public static GeminiClientException NetworkError(string detail, Exception? inner = null) =>
        new(GeminiErrorKind.NetworkError, $"Network error: {detail}", innerException: inner);
}

public sealed record McqResolution(IReadOnlyList<int> Answers, string RawText, AIModel ModelUsed);

public sealed class GeminiClient
{
    public const int DecisionOptionCount = 10;
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    private static readonly string[] Ordinals =
    [
        "first", "second", "third", "fourth", "fifth",
        "sixth", "seventh", "eighth", "ninth", "tenth"
    ];

    private static readonly string[] Letters =
    [
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J"
    ];

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly int _maxRetriesPerModel;
    private readonly Func<int, CancellationToken, Task> _sleepMs;

    public GeminiClient(
        HttpClient? httpClient = null,
        string baseUrl = DefaultBaseUrl,
        int maxRetriesPerModel = 2,
        Func<int, CancellationToken, Task>? sleepMs = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _maxRetriesPerModel = Math.Max(0, maxRetriesPerModel);
        _sleepMs = sleepMs ?? DefaultSleepAsync;
    }

    /// <summary>
    /// Solves an MCQ shown across one or more chronological JPEG frames and returns 1-based answer number(s).
    /// </summary>
    public async Task<McqResolution> SolveMcqAsync(
        IReadOnlyList<byte[]> jpegFrames,
        string apiKey,
        AIModel model,
        bool autoFallback = true,
        CancellationToken cancellationToken = default)
    {
        var trimmedKey = apiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmedKey))
        {
            throw GeminiClientException.MissingApiKey();
        }

        if (jpegFrames is null || jpegFrames.Count == 0)
        {
            throw GeminiClientException.EmptyCapture();
        }

        var modelsToTry = new List<AIModel> { model };
        var fallback = model.GetFallbackModel();
        if (autoFallback && !modelsToTry.Contains(fallback))
        {
            modelsToTry.Add(fallback);
        }

        Exception lastError = GeminiClientException.EmptyResponse();

        foreach (var candidateModel in modelsToTry)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (candidateModel.UsesDecisionsApi())
                {
                    var (answers, summary) = await SendDecisionWithRetriesAsync(
                        jpegFrames,
                        trimmedKey,
                        candidateModel,
                        cancellationToken).ConfigureAwait(false);

                    return new McqResolution(answers, summary, candidateModel);
                }
                else
                {
                    var rawText = await SendGenerateContentWithRetriesAsync(
                        jpegFrames,
                        trimmedKey,
                        candidateModel,
                        cancellationToken).ConfigureAwait(false);

                    var parsed = AnswerParser.Parse(rawText);
                    if (parsed is null)
                    {
                        throw GeminiClientException.UnparseableAnswer(rawText);
                    }

                    return new McqResolution(parsed, rawText, candidateModel);
                }
            }
            catch (GeminiClientException ex)
            {
                lastError = ex;
                if (!ex.IsTransient)
                {
                    throw;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError;
    }

    /// <summary>
    /// Validates the API key by listing models and returning supported Gemini / Gemma model IDs.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAvailableModelsAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var trimmedKey = apiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmedKey))
        {
            throw GeminiClientException.MissingApiKey();
        }

        var url = $"{_baseUrl}/models?pageSize=200";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw GeminiClientException.InvalidUrl();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("x-goog-api-key", trimmedKey);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw GeminiClientException.NetworkError(ex.Message, ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var message = ExtractErrorMessage(body) ?? response.ReasonPhrase ?? "Request failed";
                throw GeminiClientException.HttpError((int)response.StatusCode, message);
            }

            var supported = new HashSet<string>(
                AIModelExtensions.AllModels.Select(m => m.GetApiId()),
                StringComparer.OrdinalIgnoreCase);

            var result = new List<string>();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("models", out var modelsElement) &&
                modelsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in modelsElement.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var nameElement) &&
                        nameElement.ValueKind == JsonValueKind.String)
                    {
                        var name = nameElement.GetString()?.Replace("models/", string.Empty) ?? string.Empty;
                        if (supported.Contains(name))
                        {
                            result.Add(name);
                        }
                    }
                }
            }

            return result;
        }
    }

    // MARK: - Request Construction & Response Extraction

    public static string BuildPrompt(int frameCount)
    {
        var captureContext = frameCount > 1
            ? $"You are given {frameCount} chronological screenshots recorded while scrolling through a multiple-choice question on screen."
            : "You are given a screenshot of a multiple-choice question on screen.";

        return
            $"{captureContext}\n" +
            "Identify the multiple-choice question and determine the correct answer option number(s) using 1-based numbering (1 for the first option / A, 2 for the second / B, 3 for the third / C, 4 for the fourth / D, and so on in visual order from top to bottom).\n" +
            "Rules:\n" +
            "- Output ONLY the number (for example: 3).\n" +
            "- If the question clearly allows and requires multiple correct choices (multi-select / checkboxes), output ONLY the numbers separated by commas in ascending order (for example: 1,3).\n" +
            "- If no multiple-choice question is visible or you cannot determine an answer, output ONLY: NONE\n" +
            "- Do not include any explanation, letters, punctuation, or extra words.";
    }

    public static string BuildRequestBody(
        IReadOnlyList<byte[]> jpegFrames,
        AIModel model = AIModel.Gemini35FlashLite)
    {
        var parts = new JsonArray
        {
            new JsonObject { ["text"] = BuildPrompt(jpegFrames.Count) }
        };

        foreach (var frame in jpegFrames)
        {
            parts.Add(new JsonObject
            {
                ["inline_data"] = new JsonObject
                {
                    ["mime_type"] = "image/jpeg",
                    ["data"] = Convert.ToBase64String(frame)
                }
            });
        }

        var generationConfig = new JsonObject
        {
            ["temperature"] = 0.0,
            ["maxOutputTokens"] = 1024
        };

        if (model is AIModel.Gemini35FlashLite or AIModel.Gemini35Flash)
        {
            generationConfig["thinkingConfig"] = new JsonObject
            {
                ["thinkingLevel"] = "LOW"
            };
        }

        var payload = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = parts
                }
            },
            ["generationConfig"] = generationConfig
        };

        return payload.ToJsonString();
    }

    public static string BuildDecisionRequestBody(
        IReadOnlyList<byte[]> jpegFrames,
        AIModel model = AIModel.GeminiDecision1)
    {
        var contextText = jpegFrames.Count > 1
            ? $"These {jpegFrames.Count} chronological screenshots were captured while scrolling through a single multiple-choice question. Combine all screenshots to read the full question and all options numbered 1, 2, 3, ... from top to bottom."
            : "Screenshot of a multiple-choice question on screen. Options are numbered 1, 2, 3, ... from top to bottom (1=first/A, 2=second/B, etc.).";

        var inputBlocks = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = contextText
            }
        };

        foreach (var frame in jpegFrames)
        {
            inputBlocks.Add(new JsonObject
            {
                ["type"] = "image",
                ["mime_type"] = "image/jpeg",
                ["data"] = Convert.ToBase64String(frame)
            });
        }

        var singleCriteria = new JsonObject
        {
            ["none"] = $"No multiple-choice question is visible or none of options 1-{DecisionOptionCount} apply."
        };

        var questions = new JsonObject
        {
            ["has_mcq"] = new JsonObject
            {
                ["type"] = "noul",
                ["instructions"] = "Does the input show a multiple-choice question with selectable answer options?"
            },
            ["is_multi_select"] = new JsonObject
            {
                ["type"] = "noul",
                ["instructions"] = "Does this question allow or require selecting multiple correct options (e.g. checkboxes or 'select all that apply')?"
            }
        };

        for (var i = 0; i < DecisionOptionCount; i++)
        {
            var num = i + 1;
            singleCriteria[num.ToString()] = $"Option {num} ({Ordinals[i]} choice / {Letters[i]}) is the single correct answer.";
            questions[$"option_{num}"] = new JsonObject
            {
                ["type"] = "noul",
                ["instructions"] = $"Is option {num} (choice {Letters[i]}, the {Ordinals[i]} option overall) a correct answer? Answer No if option {num} does not exist or is incorrect."
            };
        }

        questions["single_choice"] = new JsonObject
        {
            ["type"] = "choice",
            ["instructions"] = "If only one answer is correct, which 1-based option number (1=first/A, 2=second/B, etc.) is the single best correct answer?",
            ["criteria"] = singleCriteria
        };

        var payload = new JsonObject
        {
            ["model"] = model.GetApiId(),
            ["input"] = inputBlocks,
            ["questions"] = questions
        };

        return payload.ToJsonString();
    }

    public static (IReadOnlyList<int> Answers, string Summary) ParseDecisionResponse(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("answers", out var answersElement) ||
            answersElement.ValueKind != JsonValueKind.Object)
        {
            throw GeminiClientException.EmptyResponse();
        }

        var hasAnyProperty = false;
        foreach (var _ in answersElement.EnumerateObject())
        {
            hasAnyProperty = true;
            break;
        }

        if (!hasAnyProperty)
        {
            throw GeminiClientException.EmptyResponse();
        }

        var hasMcqProb = TryGetNoul(answersElement, "has_mcq") ?? 1.0;
        if (hasMcqProb < 0.5)
        {
            throw GeminiClientException.UnparseableAnswer("NONE (no MCQ detected)");
        }

        var isMultiSelect = (TryGetNoul(answersElement, "is_multi_select") ?? 0.0) >= 0.5;

        var optionProbs = new List<(int Index, double Prob)>(DecisionOptionCount);
        for (var num = 1; num <= DecisionOptionCount; num++)
        {
            var prob = TryGetNoul(answersElement, $"option_{num}");
            if (prob.HasValue)
            {
                optionProbs.Add((num, prob.Value));
            }
        }

        if (isMultiSelect)
        {
            var selected = optionProbs
                .Where(p => p.Prob >= 0.5)
                .Select(p => p.Index)
                .OrderBy(i => i)
                .ToArray();

            if (selected.Length > 0)
            {
                var text = string.Join(",", selected);
                return (selected, $"{text} (multi)");
            }

            if (TryGetSingleChoice(answersElement, out var fallbackChoice, out _))
            {
                return ([fallbackChoice], fallbackChoice.ToString());
            }

            throw GeminiClientException.UnparseableAnswer("NONE (no confident multi-select options)");
        }
        else
        {
            if (TryGetSingleChoice(answersElement, out var singleNum, out var confidence))
            {
                var pct = (int)Math.Round(confidence * 100.0, MidpointRounding.AwayFromZero);
                return ([singleNum], $"{singleNum} ({pct}%)");
            }

            if (optionProbs.Count > 0)
            {
                var best = optionProbs.MaxBy(p => p.Prob);
                if (best.Prob >= 0.6)
                {
                    var pct = (int)Math.Round(best.Prob * 100.0, MidpointRounding.AwayFromZero);
                    return ([best.Index], $"{best.Index} ({pct}%)");
                }
            }

            throw GeminiClientException.UnparseableAnswer("NONE");
        }
    }

    /// <summary>
    /// Extracts the final non-thought text from a Gemini/Gemma generateContent JSON response.
    /// </summary>
    public static string ExtractAnswerText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            throw GeminiClientException.EmptyResponse();
        }

        var firstCandidate = candidates[0];
        if (!firstCandidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array ||
            parts.GetArrayLength() == 0)
        {
            throw GeminiClientException.EmptyResponse();
        }

        var answerParts = new List<string>();
        string? lastAnyText = null;

        foreach (var part in parts.EnumerateArray())
        {
            if (!part.TryGetProperty("text", out var textElement) ||
                textElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var trimmed = textElement.GetString()?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            lastAnyText = trimmed;

            var isThought = part.TryGetProperty("thought", out var thoughtElement) &&
                            thoughtElement.ValueKind == JsonValueKind.True;
            if (!isThought)
            {
                answerParts.Add(trimmed);
            }
        }

        if (answerParts.Count > 0)
        {
            return string.Join("\n", answerParts);
        }

        if (!string.IsNullOrEmpty(lastAnyText))
        {
            return lastAnyText;
        }

        throw GeminiClientException.EmptyResponse();
    }

    // MARK: - Internals

    private async Task<(IReadOnlyList<int> Answers, string Summary)> SendDecisionWithRetriesAsync(
        IReadOnlyList<byte[]> jpegFrames,
        string apiKey,
        AIModel model,
        CancellationToken cancellationToken)
    {
        var url = $"{_baseUrl}/decisions";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw GeminiClientException.InvalidUrl();
        }

        var bodyJson = BuildDecisionRequestBody(jpegFrames, model);
        var responseJson = await ExecutePostWithRetriesAsync(
            uri,
            bodyJson,
            apiKey,
            TimeSpan.FromSeconds(25),
            cancellationToken).ConfigureAwait(false);

        return ParseDecisionResponse(responseJson);
    }

    private async Task<string> SendGenerateContentWithRetriesAsync(
        IReadOnlyList<byte[]> jpegFrames,
        string apiKey,
        AIModel model,
        CancellationToken cancellationToken)
    {
        var url = $"{_baseUrl}/models/{model.GetApiId()}:generateContent";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw GeminiClientException.InvalidUrl();
        }

        var bodyJson = BuildRequestBody(jpegFrames, model);
        var responseJson = await ExecutePostWithRetriesAsync(
            uri,
            bodyJson,
            apiKey,
            TimeSpan.FromSeconds(35),
            cancellationToken).ConfigureAwait(false);

        return ExtractAnswerText(responseJson);
    }

    private async Task<string> ExecutePostWithRetriesAsync(
        Uri uri,
        string bodyJson,
        string apiKey,
        TimeSpan perAttemptTimeout,
        CancellationToken cancellationToken)
    {
        var totalAttempts = _maxRetriesPerModel + 1;
        Exception lastError = GeminiClientException.EmptyResponse();

        for (var attempt = 0; attempt < totalAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, uri);
                request.Headers.Add("x-goog-api-key", apiKey);
                request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(perAttemptTimeout);

                using var response = await _httpClient.SendAsync(request, attemptCts.Token).ConfigureAwait(false);
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return responseText;
                }

                var message = ExtractErrorMessage(responseText) ?? response.ReasonPhrase ?? "Request failed";
                var error = GeminiClientException.HttpError((int)response.StatusCode, message);
                lastError = error;

                if (!error.IsTransient || attempt == totalAttempts - 1)
                {
                    throw error;
                }
            }
            catch (GeminiClientException ex)
            {
                lastError = ex;
                if (!ex.IsTransient || attempt == totalAttempts - 1)
                {
                    throw;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var wrapped = GeminiClientException.NetworkError(ex.Message, ex);
                lastError = wrapped;
                if (attempt == totalAttempts - 1)
                {
                    throw wrapped;
                }
            }

            var delayMs = 700 * (1 << attempt);
            await _sleepMs(delayMs, cancellationToken).ConfigureAwait(false);
        }

        throw lastError;
    }

    private static double? TryGetNoul(JsonElement answersElement, string key)
    {
        if (answersElement.TryGetProperty(key, out var entry) &&
            entry.ValueKind == JsonValueKind.Object &&
            entry.TryGetProperty("noul", out var noulElement) &&
            noulElement.ValueKind == JsonValueKind.Number &&
            noulElement.TryGetDouble(out var value))
        {
            return value;
        }

        return null;
    }

    private static bool TryGetSingleChoice(JsonElement answersElement, out int choiceNumber, out double confidence)
    {
        choiceNumber = 0;
        confidence = 0.0;

        if (!answersElement.TryGetProperty("single_choice", out var single) ||
            single.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!single.TryGetProperty("choice", out var choiceElement) ||
            choiceElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var choiceStr = choiceElement.GetString();
        if (!int.TryParse(choiceStr, out var num) || num < 1 || num > DecisionOptionCount)
        {
            return false;
        }

        if (single.TryGetProperty("confidence", out var confElement) &&
            confElement.ValueKind == JsonValueKind.Number &&
            confElement.TryGetDouble(out var confVal))
        {
            confidence = confVal;
        }

        choiceNumber = num;
        return true;
    }

    private static string? ExtractErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.Object &&
                errorElement.TryGetProperty("message", out var msgElement) &&
                msgElement.ValueKind == JsonValueKind.String)
            {
                return msgElement.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private static Task DefaultSleepAsync(int milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds <= 0)
        {
            return Task.CompletedTask;
        }

        return Task.Delay(milliseconds, cancellationToken);
    }
}
