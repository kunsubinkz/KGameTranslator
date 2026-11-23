import XCTest
@testable import TranslatorCore

final class TextNormalizerTests: XCTestCase {
    func testJoinsLinesAndCollapsesWhitespace() {
        XCTAssertEqual(TextNormalizer.normalize("Hello   there,\n  traveler. \n"), "Hello there, traveler.")
    }

    func testFixesPipeAsI() {
        XCTAssertEqual(TextNormalizer.normalize("| think we should go"), "I think we should go")
        XCTAssertEqual(TextNormalizer.normalize("Wel|, that was close"), "Well, that was close")
    }

    func testStripsTrailingContinueArrow() {
        XCTAssertEqual(TextNormalizer.normalize("Take this sword. ▼"), "Take this sword.")
    }

    func testRejectsShortOrLetterlessText() {
        XCTAssertNil(TextNormalizer.normalize("Hi"))
        XCTAssertNil(TextNormalizer.normalize("1234 !!"))
        XCTAssertNil(TextNormalizer.normalize("  \n "))
    }

    func testSimilarity() {
        XCTAssertTrue(TextNormalizer.isSameSentence("Welcome to Hyrule, traveler!", "Welcome to Hyrule, traveler!"))
        XCTAssertTrue(TextNormalizer.isSameSentence("Welcome to Hyrule, traveler!", "welcome to hyrule, traveler!"))
        XCTAssertFalse(TextNormalizer.isSameSentence("Welcome to Hyrule", "Goodbye and good luck"))
        XCTAssertEqual(TextNormalizer.levenshtein("kitten", "sitting"), 3)
    }

    func testCharacterErrorRate() {
        XCTAssertEqual(TextNormalizer.characterErrorRate(expected: "abcd", actual: "abcd"), 0)
        XCTAssertEqual(TextNormalizer.characterErrorRate(expected: "abcd", actual: "abxd"), 0.25, accuracy: 0.0001)
    }
}
