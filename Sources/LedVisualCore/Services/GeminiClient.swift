import Foundation

public enum GeminiClientError: Error, LocalizedError, Sendable {
    case missingAPIKey
    case emptyCapture
    case invalidURL
    case httpError(statusCode: Int, message: String)
    case emptyResponse
    case unparseableAnswer(rawText: String)
    case networkError(String)

    public var errorDescription: String? {
        switch self {
        case .missingAPIKey:
            return "Google AI Studio API key is not configured."
        case .emptyCapture:
            return "No screenshot or video frames were captured."
        case .invalidURL:
            return "Failed to construct Google AI Studio API URL."
        case .httpError(let statusCode, let message):
            return "API error (HTTP \(statusCode)): \(message)"
        case .emptyResponse:
            return "The model returned an empty response."
        case .unparseableAnswer(let rawText):
            return "Could not parse an MCQ answer number from model output: \"\(rawText)\""
        case .networkError(let detail):
            return "Network error: \(detail)"
        }
    }

    var isTransient: Bool {
        switch self {
        case .httpError(let code, _):
            return code == 429 || (500...599).contains(code)
        case .networkError:
            return true
        default:
            return false
        }
    }
}

public struct MCQResolution: Equatable, Sendable {
    public let answers: [Int]
    public let rawText: String
    public let modelUsed: GemmaModel

    public init(answers: [Int], rawText: String, modelUsed: GemmaModel) {
        self.answers = answers
        self.rawText = rawText
        self.modelUsed = modelUsed
    }
}

public final class GeminiClient: Sendable {
    public typealias Sleeper = @Sendable (Int) async throws -> Void

    private let session: URLSession
    private let baseURL: String
    private let maxRetriesPerModel: Int
    private let sleepMs: Sleeper

    public init(
        session: URLSession = .shared,
        baseURL: String = "https://generativelanguage.googleapis.com/v1beta",
        maxRetriesPerModel: Int = 2,
        sleepMs: @escaping Sleeper = { ms in
            guard ms > 0 else { return }
            try await Task.sleep(for: .milliseconds(ms))
        }
    ) {
        self.session = session
        self.baseURL = baseURL
        self.maxRetriesPerModel = max(0, maxRetriesPerModel)
        self.sleepMs = sleepMs
    }

    /// Solves an MCQ shown across one or more chronological JPEG frames and returns 1-based answer number(s).
    public func solveMCQ(
        jpegFrames: [Data],
        apiKey: String,
        model: GemmaModel,
        autoFallback: Bool = true
    ) async throws -> MCQResolution {
        let trimmedKey = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedKey.isEmpty else {
            throw GeminiClientError.missingAPIKey
        }
        guard !jpegFrames.isEmpty else {
            throw GeminiClientError.emptyCapture
        }

        var modelsToTry = [model]
        if autoFallback && !modelsToTry.contains(model.fallbackModel) {
            modelsToTry.append(model.fallbackModel)
        }

        var lastError: Error = GeminiClientError.emptyResponse

        for candidateModel in modelsToTry {
            do {
                if candidateModel.usesDecisionsAPI {
                    let (answers, summary) = try await sendDecisionWithRetries(
                        jpegFrames: jpegFrames,
                        apiKey: trimmedKey,
                        model: candidateModel
                    )
                    return MCQResolution(
                        answers: answers,
                        rawText: summary,
                        modelUsed: candidateModel
                    )
                } else {
                    let rawText = try await sendWithRetries(
                        jpegFrames: jpegFrames,
                        apiKey: trimmedKey,
                        model: candidateModel
                    )
                    guard let parsed = AnswerParser.parse(rawText) else {
                        throw GeminiClientError.unparseableAnswer(rawText: rawText)
                    }
                    return MCQResolution(
                        answers: parsed,
                        rawText: rawText,
                        modelUsed: candidateModel
                    )
                }
            } catch let error as GeminiClientError {
                lastError = error
                // Only fall back to another model on transient server/rate-limit errors
                if !error.isTransient {
                    throw error
                }
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                lastError = error
            }
        }

        throw lastError
    }

    /// Validates the API key by listing models and returning supported Gemini / Gemma model IDs.
    public func listAvailableGemmaModels(apiKey: String) async throws -> [String] {
        let trimmedKey = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedKey.isEmpty else {
            throw GeminiClientError.missingAPIKey
        }
        guard let url = URL(string: "\(baseURL)/models?pageSize=200") else {
            throw GeminiClientError.invalidURL
        }

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.timeoutInterval = 15
        request.setValue(trimmedKey, forHTTPHeaderField: "x-goog-api-key")

        let (data, response) = try await performRequest(request)
        guard let httpResponse = response as? HTTPURLResponse else {
            throw GeminiClientError.networkError("Invalid HTTP response.")
        }
        guard (200...299).contains(httpResponse.statusCode) else {
            let msg = Self.extractErrorMessage(from: data) ?? HTTPURLResponse.localizedString(forStatusCode: httpResponse.statusCode)
            throw GeminiClientError.httpError(statusCode: httpResponse.statusCode, message: msg)
        }

        struct ModelsResponse: Decodable {
            struct ModelInfo: Decodable {
                let name: String
            }
            let models: [ModelInfo]?
        }

        let supported = Set(AIModel.allCases.map(\.rawValue))
        let decoded = try JSONDecoder().decode(ModelsResponse.self, from: data)
        return (decoded.models ?? [])
            .map { $0.name.replacingOccurrences(of: "models/", with: "") }
            .filter { supported.contains($0) }
    }

    // MARK: - Request Construction & Response Extraction

    public static func buildPrompt(frameCount: Int) -> String {
        let captureContext: String
        if frameCount > 1 {
            captureContext = "You are given \(frameCount) chronological screenshots recorded while scrolling through a multiple-choice question on screen."
        } else {
            captureContext = "You are given a screenshot of a multiple-choice question on screen."
        }

        return """
        \(captureContext)
        Identify the multiple-choice question and determine the correct answer option number(s) using 1-based numbering (1 for the first option / A, 2 for the second / B, 3 for the third / C, 4 for the fourth / D, and so on in visual order from top to bottom).
        Rules:
        - Output ONLY the number (for example: 3).
        - If the question clearly allows and requires multiple correct choices (multi-select / checkboxes), output ONLY the numbers separated by commas in ascending order (for example: 1,3).
        - If no multiple-choice question is visible or you cannot determine an answer, output ONLY: NONE
        - Do not include any explanation, letters, punctuation, or extra words.
        """
    }

    public static func buildRequestBody(jpegFrames: [Data], model: AIModel = .gemini35FlashLite) throws -> Data {
        var parts: [[String: Any]] = [
            ["text": buildPrompt(frameCount: jpegFrames.count)]
        ]
        for frame in jpegFrames {
            parts.append([
                "inline_data": [
                    "mime_type": "image/jpeg",
                    "data": frame.base64EncodedString()
                ]
            ])
        }

        var generationConfig: [String: Any] = [
            "temperature": 0.0,
            "maxOutputTokens": 1024
        ]
        // Enable fast reasoning on Gemini 3.5 models so MCQ accuracy stays high while keeping ~1s latency.
        if model == .gemini35FlashLite || model == .gemini35Flash {
            generationConfig["thinkingConfig"] = [
                "thinkingLevel": "LOW"
            ]
        }

        let payload: [String: Any] = [
            "contents": [
                [
                    "role": "user",
                    "parts": parts
                ]
            ],
            "generationConfig": generationConfig
        ]
        return try JSONSerialization.data(withJSONObject: payload, options: [])
    }

    // MARK: - Gemini Decision 1 (/v1beta/decisions)

    public static let decisionOptionCount = 10

    public static func buildDecisionRequestBody(jpegFrames: [Data], model: AIModel = .geminiDecision1) throws -> Data {
        let contextText: String
        if jpegFrames.count > 1 {
            contextText = "These \(jpegFrames.count) chronological screenshots were captured while scrolling through a single multiple-choice question. Combine all screenshots to read the full question and all options numbered 1, 2, 3, ... from top to bottom."
        } else {
            contextText = "Screenshot of a multiple-choice question on screen. Options are numbered 1, 2, 3, ... from top to bottom (1=first/A, 2=second/B, etc.)."
        }

        var inputBlocks: [[String: Any]] = [
            ["type": "text", "text": contextText]
        ]
        for frame in jpegFrames {
            inputBlocks.append([
                "type": "image",
                "mime_type": "image/jpeg",
                "data": frame.base64EncodedString()
            ])
        }

        let ordinals = ["first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth"]
        let letters = ["A", "B", "C", "D", "E", "F", "G", "H", "I", "J"]

        var singleCriteria: [String: String] = [
            "none": "No multiple-choice question is visible or none of options 1-\(decisionOptionCount) apply."
        ]
        var questions: [String: Any] = [
            "has_mcq": [
                "type": "noul",
                "instructions": "Does the input show a multiple-choice question with selectable answer options?"
            ],
            "is_multi_select": [
                "type": "noul",
                "instructions": "Does this question allow or require selecting multiple correct options (e.g. checkboxes or 'select all that apply')?"
            ]
        ]

        for i in 0..<decisionOptionCount {
            let num = i + 1
            singleCriteria["\(num)"] = "Option \(num) (\(ordinals[i]) choice / \(letters[i])) is the single correct answer."
            questions["option_\(num)"] = [
                "type": "noul",
                "instructions": "Is option \(num) (choice \(letters[i]), the \(ordinals[i]) option overall) a correct answer? Answer No if option \(num) does not exist or is incorrect."
            ]
        }

        questions["single_choice"] = [
            "type": "choice",
            "instructions": "If only one answer is correct, which 1-based option number (1=first/A, 2=second/B, etc.) is the single best correct answer?",
            "criteria": singleCriteria
        ]

        let payload: [String: Any] = [
            "model": model.rawValue,
            "input": inputBlocks,
            "questions": questions
        ]
        return try JSONSerialization.data(withJSONObject: payload, options: [])
    }

    public static func parseDecisionResponse(_ responseData: Data) throws -> (answers: [Int], summary: String) {
        struct DecisionResponse: Decodable {
            struct AnswerValue: Decodable {
                let type: String?
                let noul: Double?
                let choice: String?
                let confidence: Double?
            }
            let answers: [String: AnswerValue]?
        }

        let decoded = try JSONDecoder().decode(DecisionResponse.self, from: responseData)
        guard let answersMap = decoded.answers, !answersMap.isEmpty else {
            throw GeminiClientError.emptyResponse
        }

        let hasMCQProb = answersMap["has_mcq"]?.noul ?? 1.0
        guard hasMCQProb >= 0.5 else {
            throw GeminiClientError.unparseableAnswer(rawText: "NONE (no MCQ detected)")
        }

        let isMultiSelect = (answersMap["is_multi_select"]?.noul ?? 0.0) >= 0.5

        // Gather per-option probabilities (1...decisionOptionCount)
        var optionProbs: [(index: Int, prob: Double)] = []
        for num in 1...decisionOptionCount {
            if let prob = answersMap["option_\(num)"]?.noul {
                optionProbs.append((num, prob))
            }
        }

        if isMultiSelect {
            let selected = optionProbs
                .filter { $0.prob >= 0.5 }
                .map(\.index)
                .sorted()
            if !selected.isEmpty {
                let text = selected.map(String.init).joined(separator: ",")
                return (selected, "\(text) (multi)")
            }
            // Fallback to highest-scoring single choice if multi-select probabilities were borderline
            if let choiceStr = answersMap["single_choice"]?.choice,
               let num = Int(choiceStr),
               (1...decisionOptionCount).contains(num) {
                return ([num], "\(num)")
            }
            throw GeminiClientError.unparseableAnswer(rawText: "NONE (no confident multi-select options)")
        } else {
            if let single = answersMap["single_choice"],
               let choiceStr = single.choice,
               let num = Int(choiceStr),
               (1...decisionOptionCount).contains(num) {
                let pct = Int(((single.confidence ?? 0) * 100).rounded())
                return ([num], "\(num) (\(pct)%)")
            }

            // Fallback to strongest option_k if single_choice returned "none" despite high per-option confidence
            if let best = optionProbs.max(by: { $0.prob < $1.prob }), best.prob >= 0.6 {
                let pct = Int((best.prob * 100).rounded())
                return ([best.index], "\(best.index) (\(pct)%)")
            }

            throw GeminiClientError.unparseableAnswer(rawText: "NONE")
        }
    }

    /// Extracts the final non-thought text from a Gemini/Gemma `generateContent` JSON response.
    public static func extractAnswerText(from responseData: Data) throws -> String {
        struct GenerateContentResponse: Decodable {
            struct Candidate: Decodable {
                struct Content: Decodable {
                    struct Part: Decodable {
                        let text: String?
                        let thought: Bool?
                    }
                    let parts: [Part]?
                }
                let content: Content?
            }
            let candidates: [Candidate]?
        }

        let decoded = try JSONDecoder().decode(GenerateContentResponse.self, from: responseData)
        guard let parts = decoded.candidates?.first?.content?.parts, !parts.isEmpty else {
            throw GeminiClientError.emptyResponse
        }

        // Gemma 4 models may emit reasoning parts with `thought: true` before the final answer part.
        let answerParts = parts
            .filter { $0.thought != true }
            .compactMap(\.text)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }

        if !answerParts.isEmpty {
            return answerParts.joined(separator: "\n")
        }

        // Fallback if every part was flagged as thought
        if let lastText = parts.compactMap(\.text).last?.trimmingCharacters(in: .whitespacesAndNewlines),
           !lastText.isEmpty {
            return lastText
        }

        throw GeminiClientError.emptyResponse
    }

    // MARK: - Internals

    private func sendDecisionWithRetries(
        jpegFrames: [Data],
        apiKey: String,
        model: AIModel
    ) async throws -> (answers: [Int], summary: String) {
        guard let url = URL(string: "\(baseURL)/decisions") else {
            throw GeminiClientError.invalidURL
        }

        let body = try Self.buildDecisionRequestBody(jpegFrames: jpegFrames, model: model)
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 25
        request.httpBody = body
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue(apiKey, forHTTPHeaderField: "x-goog-api-key")

        let data = try await executeWithRetries(request)
        return try Self.parseDecisionResponse(data)
    }

    private func sendWithRetries(
        jpegFrames: [Data],
        apiKey: String,
        model: AIModel
    ) async throws -> String {
        guard let url = URL(string: "\(baseURL)/models/\(model.rawValue):generateContent") else {
            throw GeminiClientError.invalidURL
        }

        let body = try Self.buildRequestBody(jpegFrames: jpegFrames, model: model)
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 35
        request.httpBody = body
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue(apiKey, forHTTPHeaderField: "x-goog-api-key")

        let data = try await executeWithRetries(request)
        return try Self.extractAnswerText(from: data)
    }

    private func executeWithRetries(_ request: URLRequest) async throws -> Data {
        let totalAttempts = maxRetriesPerModel + 1
        var lastError: Error = GeminiClientError.emptyResponse

        for attempt in 0..<totalAttempts {
            try Task.checkCancellation()
            do {
                let (data, response) = try await performRequest(request)
                guard let httpResponse = response as? HTTPURLResponse else {
                    throw GeminiClientError.networkError("Invalid HTTP response.")
                }

                if (200...299).contains(httpResponse.statusCode) {
                    return data
                }

                let message = Self.extractErrorMessage(from: data)
                    ?? HTTPURLResponse.localizedString(forStatusCode: httpResponse.statusCode)
                let error = GeminiClientError.httpError(statusCode: httpResponse.statusCode, message: message)
                lastError = error

                if !error.isTransient || attempt == totalAttempts - 1 {
                    throw error
                }
            } catch let error as GeminiClientError {
                lastError = error
                if !error.isTransient || attempt == totalAttempts - 1 {
                    throw error
                }
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                let wrapped = GeminiClientError.networkError(error.localizedDescription)
                lastError = wrapped
                if attempt == totalAttempts - 1 {
                    throw wrapped
                }
            }

            let delayMs = 700 * (1 << attempt)
            try await sleepMs(delayMs)
        }

        throw lastError
    }

    private func performRequest(_ request: URLRequest) async throws -> (Data, URLResponse) {
        do {
            return try await session.data(for: request)
        } catch let error as CancellationError {
            throw error
        } catch {
            throw GeminiClientError.networkError(error.localizedDescription)
        }
    }

    private static func extractErrorMessage(from data: Data) -> String? {
        struct APIErrorEnvelope: Decodable {
            struct APIErrorDetail: Decodable {
                let message: String?
            }
            let error: APIErrorDetail?
        }
        return try? JSONDecoder().decode(APIErrorEnvelope.self, from: data).error?.message
    }
}
