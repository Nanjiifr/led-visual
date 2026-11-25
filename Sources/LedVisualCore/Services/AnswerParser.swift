import Foundation

public enum AnswerParser {
    /// Maximum reasonable MCQ option index (prevents accidental 4-digit years or stray numbers).
    public static let maxOptionNumber = 20

    /// Parses raw model output into sorted, deduplicated 1-based answer indices (e.g. `[3]` or `[1, 3]`).
    /// Returns `nil` if the output indicates failure/no question or cannot be reliably parsed.
    public static func parse(_ rawText: String) -> [Int]? {
        let cleaned = rawText
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: "`", with: "")
            .replacingOccurrences(of: "*", with: "")

        guard !cleaned.isEmpty else { return nil }

        let upper = cleaned.uppercased()
        if upper == "NONE" || upper == "ERROR" || upper == "UNKNOWN" || upper == "N/A" || upper == "0" {
            return nil
        }

        // 1. Strict full-string match of numbers separated by commas, semicolons, "and", or whitespace
        if let strict = parseStrictNumberList(cleaned) {
            return strict
        }

        // 2. Check the last non-empty line (models occasionally echo reasoning then put the final answer last)
        let lines = cleaned
            .components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }

        if let lastLine = lines.last, lastLine != cleaned {
            let lastUpper = lastLine.uppercased()
            if lastUpper == "NONE" || lastUpper == "ERROR" || lastUpper == "UNKNOWN" {
                return nil
            }
            if let strictLast = parseStrictNumberList(lastLine) {
                return strictLast
            }
            if let lettersLast = parseStrictLetterList(lastLine) {
                return lettersLast
            }
        }

        // 3. Strict letter list fallback (e.g. "B" -> [2], "A, C" -> [1, 3])
        if let letters = parseStrictLetterList(cleaned) {
            return letters
        }

        // 4. Explicit answer phrase extraction (e.g. "Answer: 1, 3" or "Correct options are 2 and 4")
        if let extracted = parseLabeledAnswer(cleaned) {
            return extracted
        }

        return nil
    }

    private static func parseStrictNumberList(_ text: String) -> [Int]? {
        let normalized = text
            .replacingOccurrences(of: #"\band\b"#, with: ",", options: [.regularExpression, .caseInsensitive])
            .replacingOccurrences(of: "&", with: ",")

        let pattern = #"^\s*\d+(?:\s*[,;/\s]\s*\d+)*\s*\.?\s*$"#
        guard normalized.range(of: pattern, options: .regularExpression) != nil else {
            return nil
        }

        let numbers = extractValidNumbers(from: normalized)
        return numbers.isEmpty ? nil : numbers
    }

    private static func parseStrictLetterList(_ text: String) -> [Int]? {
        let normalized = text
            .replacingOccurrences(of: #"\band\b"#, with: ",", options: [.regularExpression, .caseInsensitive])
            .replacingOccurrences(of: "&", with: ",")

        let pattern = #"^\s*[A-Ja-j](?:\s*[,;/\s]\s*[A-Ja-j])*\s*\.?\s*$"#
        guard normalized.range(of: pattern, options: .regularExpression) != nil else {
            return nil
        }

        let letters = normalized.uppercased().unicodeScalars.compactMap { scalar -> Int? in
            guard scalar.value >= 65 && scalar.value <= 74 else { return nil } // A..J -> 1..10
            return Int(scalar.value - 64)
        }
        let sorted = Array(Set(letters)).sorted()
        return sorted.isEmpty ? nil : sorted
    }

    private static func parseLabeledAnswer(_ text: String) -> [Int]? {
        let regexPattern = #"(?i)(?:final\s+answer|correct\s+answers?|correct\s+options?|answers?|options?)\s*(?:is|are|:|=)?\s*([0-9][0-9,\s&and;/]*)"#
        guard let regex = try? NSRegularExpression(pattern: regexPattern) else { return nil }
        let range = NSRange(text.startIndex..<text.endIndex, in: text)
        let matches = regex.matches(in: text, range: range)
        guard let lastMatch = matches.last,
              lastMatch.numberOfRanges >= 2,
              let captureRange = Range(lastMatch.range(at: 1), in: text) else {
            return nil
        }
        let captured = String(text[captureRange])
        let numbers = extractValidNumbers(from: captured)
        return numbers.isEmpty ? nil : numbers
    }

    private static func extractValidNumbers(from text: String) -> [Int] {
        guard let regex = try? NSRegularExpression(pattern: #"\d+"#) else { return [] }
        let range = NSRange(text.startIndex..<text.endIndex, in: text)
        let rawNumbers: [Int] = regex.matches(in: text, range: range).compactMap { match in
            guard let r = Range(match.range, in: text),
                  let val = Int(text[r]),
                  val >= 1 && val <= maxOptionNumber else {
                return nil
            }
            return val
        }
        return Array(Set(rawNumbers)).sorted()
    }
}
