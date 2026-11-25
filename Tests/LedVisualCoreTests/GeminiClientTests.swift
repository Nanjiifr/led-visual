import Foundation
import XCTest
@testable import LedVisualCore

private final class StubURLProtocol: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var handler: ((URLRequest) throws -> (HTTPURLResponse, Data))?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let handler = Self.handler else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }
        do {
            let (response, data) = try handler(request)
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            client?.urlProtocolDidFinishLoading(self)
        } catch {
            client?.urlProtocol(self, didFailWithError: error)
        }
    }

    override func stopLoading() {}
}

final class GeminiClientTests: XCTestCase {
    private func makeStubbedSession() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [StubURLProtocol.self]
        return URLSession(configuration: config)
    }

    func testExtractAnswerTextSkipsThoughtParts() throws {
        let json = """
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
        """
        let extracted = try GeminiClient.extractAnswerText(from: Data(json.utf8))
        XCTAssertEqual(extracted, "3")
    }

    func testSolveMCQFallsBackToAlternateModelOn503() async throws {
        var requestedURLs: [String] = []
        var sawAPIKeyHeader = false

        StubURLProtocol.handler = { request in
            let urlStr = request.url?.absoluteString ?? ""
            requestedURLs.append(urlStr)
            if request.value(forHTTPHeaderField: "x-goog-api-key") == "test-key" {
                sawAPIKeyHeader = true
            }
            // Key must never appear in query string
            XCTAssertFalse(urlStr.contains("test-key"))

            if urlStr.contains("gemini-3.5-flash-lite") {
                let resp = HTTPURLResponse(url: request.url!, statusCode: 503, httpVersion: nil, headerFields: nil)!
                let body = Data(#"{"error":{"code":503,"message":"High demand"}}"#.utf8)
                return (resp, body)
            } else {
                let resp = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
                let body = Data(#"{"candidates":[{"content":{"parts":[{"text":"Thinking...","thought":true},{"text":"1,3"}]}}]}"#.utf8)
                return (resp, body)
            }
        }

        let client = GeminiClient(
            session: makeStubbedSession(),
            maxRetriesPerModel: 1,
            sleepMs: { _ in }
        )

        let resolution = try await client.solveMCQ(
            jpegFrames: [Data([0xFF, 0xD8, 0xFF])],
            apiKey: "test-key",
            model: .gemini35FlashLite,
            autoFallback: true
        )

        XCTAssertTrue(sawAPIKeyHeader)
        XCTAssertEqual(resolution.answers, [1, 3])
        XCTAssertEqual(resolution.modelUsed, .gemini35Flash)
        // 2 attempts on flash-lite (initial + 1 retry), then 1 success on flash
        XCTAssertEqual(requestedURLs.count, 3)
    }

    func testSolveMCQThrowsUnparseableOnNoneWithoutFallback() async {
        var callCount = 0
        StubURLProtocol.handler = { request in
            callCount += 1
            let resp = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            let body = Data(#"{"candidates":[{"content":{"parts":[{"text":"NONE"}]}}]}"#.utf8)
            return (resp, body)
        }

        let client = GeminiClient(
            session: makeStubbedSession(),
            maxRetriesPerModel: 1,
            sleepMs: { _ in }
        )

        do {
            _ = try await client.solveMCQ(
                jpegFrames: [Data([0xFF, 0xD8])],
                apiKey: "test-key",
                model: .gemma4_26b_a4b,
                autoFallback: true
            )
            XCTFail("Expected unparseableAnswer error")
        } catch let error as GeminiClientError {
            if case .unparseableAnswer(let raw) = error {
                XCTAssertEqual(raw, "NONE")
            } else {
                XCTFail("Unexpected error: \(error)")
            }
        } catch {
            XCTFail("Unexpected error: \(error)")
        }

        XCTAssertEqual(callCount, 1)
    }

    func testDecisionAPISingleAndMultiSelectParsing() throws {
        let singleJSON = """
        {
          "answers": {
            "has_mcq": { "type": "noul", "noul": 0.91 },
            "is_multi_select": { "type": "noul", "noul": 0.08 },
            "single_choice": { "type": "choice", "choice": "3", "confidence": 0.98 },
            "option_1": { "type": "noul", "noul": 0.04 },
            "option_3": { "type": "noul", "noul": 0.96 }
          }
        }
        """
        let single = try GeminiClient.parseDecisionResponse(Data(singleJSON.utf8))
        XCTAssertEqual(single.answers, [3])

        let multiJSON = """
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
        """
        let multi = try GeminiClient.parseDecisionResponse(Data(multiJSON.utf8))
        XCTAssertEqual(multi.answers, [1, 3])

        let nonMCQJSON = """
        {
          "answers": {
            "has_mcq": { "type": "noul", "noul": 0.12 },
            "is_multi_select": { "type": "noul", "noul": 0.2 },
            "single_choice": { "type": "choice", "choice": "none", "confidence": 0.95 }
          }
        }
        """
        XCTAssertThrowsError(try GeminiClient.parseDecisionResponse(Data(nonMCQJSON.utf8)))
    }

    func testDecisionAPIFallsBackToFlashLiteOn503() async throws {
        var sawDecisionsEndpoint = false
        StubURLProtocol.handler = { request in
            let urlStr = request.url?.absoluteString ?? ""
            if urlStr.hasSuffix("/decisions") {
                sawDecisionsEndpoint = true
                let resp = HTTPURLResponse(url: request.url!, statusCode: 503, httpVersion: nil, headerFields: nil)!
                return (resp, Data(#"{"error":{"code":503,"message":"Overloaded"}}"#.utf8))
            } else {
                let resp = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
                return (resp, Data(#"{"candidates":[{"content":{"parts":[{"text":"2"}]}}]}"#.utf8))
            }
        }

        let client = GeminiClient(
            session: makeStubbedSession(),
            maxRetriesPerModel: 0,
            sleepMs: { _ in }
        )

        let res = try await client.solveMCQ(
            jpegFrames: [Data([0xFF, 0xD8])],
            apiKey: "test-key",
            model: .geminiDecision1,
            autoFallback: true
        )

        XCTAssertTrue(sawDecisionsEndpoint)
        XCTAssertEqual(res.answers, [2])
        XCTAssertEqual(res.modelUsed, .gemini35FlashLite)
    }
}
