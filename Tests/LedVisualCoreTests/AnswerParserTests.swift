import XCTest
@testable import LedVisualCore

final class AnswerParserTests: XCTestCase {
    func testSingleNumberAnswers() {
        XCTAssertEqual(AnswerParser.parse("1"), [1])
        XCTAssertEqual(AnswerParser.parse("  3\n"), [3])
        XCTAssertEqual(AnswerParser.parse("`4`"), [4])
        XCTAssertEqual(AnswerParser.parse("12"), [12])
    }

    func testMultiSelectAnswers() {
        XCTAssertEqual(AnswerParser.parse("1,3"), [1, 3])
        XCTAssertEqual(AnswerParser.parse("1, 3, 4"), [1, 3, 4])
        XCTAssertEqual(AnswerParser.parse("4, 2, 2"), [2, 4])
        XCTAssertEqual(AnswerParser.parse("1 and 3"), [1, 3])
        XCTAssertEqual(AnswerParser.parse("2 5"), [2, 5])
    }

    func testLetterAndLabeledFallbacks() {
        XCTAssertEqual(AnswerParser.parse("C"), [3])
        XCTAssertEqual(AnswerParser.parse("A, C"), [1, 3])
        XCTAssertEqual(AnswerParser.parse("The correct answer is 3."), [3])
        XCTAssertEqual(AnswerParser.parse("Correct options: 1, 4"), [1, 4])
        XCTAssertEqual(AnswerParser.parse("Reasoning...\n2, 3"), [2, 3])
    }

    func testFailureAndInvalidOutputs() {
        XCTAssertNil(AnswerParser.parse(""))
        XCTAssertNil(AnswerParser.parse("NONE"))
        XCTAssertNil(AnswerParser.parse("none"))
        XCTAssertNil(AnswerParser.parse("ERROR"))
        XCTAssertNil(AnswerParser.parse("0"))
        XCTAssertNil(AnswerParser.parse("2026"))
        XCTAssertNil(AnswerParser.parse("No multiple choice question visible."))
    }
}
