import Foundation
import XCTest
#if canImport(MACAssistant)
@testable import MACAssistant
#endif

final class NotebookDAVURLTests: XCTestCase {
    private let base = URL(string: "https://cloud.example:30443/remote.php/dav/files/user/MyNotes/")!

    func testRootProbeUsesConfiguredCollection() throws {
        let result = try NotebookDAVURL.resolve(base: base, path: "")
        XCTAssertEqual(result.absoluteString, base.absoluteString)
        XCTAssertEqual(result.port, 30443)
    }

    func testChildFilesAndCollectionStayUnderRoot() throws {
        for path in [".sync-lock.json", "books/note/main.md", "books/note/reference/", ".manifest.json"] {
            XCTAssertEqual(try NotebookDAVURL.resolve(base: base, path: path).absoluteString, base.absoluteString + path)
        }
    }

    func testAttachmentNamesAreEncodedOnce() throws {
        let result = try NotebookDAVURL.resolve(base: base, path: "books/reference/附件 #?%: 1.pdf")
        XCTAssertTrue(result.absoluteString.hasSuffix("/%E9%99%84%E4%BB%B6%20%23%3F%25%3A%201.pdf"))
        XCTAssertNil(result.query)
        XCTAssertNil(result.fragment)
    }

    func testTraversalAndOtherOriginsAreRejected() {
        for path in ["../other", "books/../../other", "/other", "//other.example/file", "a\\b", "./file"] {
            XCTAssertThrowsError(try NotebookDAVURL.resolve(base: base, path: path), path)
        }
    }

    func testURLLikeFileNamesCannotChangeOrigin() throws {
        for path in ["https://other.example/file", "https://cloud.example:443/file", "%2e%2e/file"] {
            let result = try NotebookDAVURL.resolve(base: base, path: path)
            XCTAssertEqual(result.host, base.host)
            XCTAssertEqual(result.port, base.port)
            XCTAssertTrue(result.absoluteString.hasPrefix(base.absoluteString))
        }
    }

    func testEncodedCollectionAndHostRoot() throws {
        let encoded = URL(string: "https://cloud.example/remote.php/dav/files/user/%E7%AC%94%E8%AE%B0/")!
        XCTAssertEqual(try NotebookDAVURL.resolve(base: encoded, path: "").absoluteString, encoded.absoluteString)
        XCTAssertEqual(try NotebookDAVURL.resolve(base: encoded, path: "note.md").absoluteString, encoded.absoluteString + "note.md")
        let host = URL(string: "https://cloud.example/")!
        XCTAssertEqual(try NotebookDAVURL.resolve(base: host, path: "").absoluteString, host.absoluteString)
        XCTAssertEqual(try NotebookDAVURL.resolve(base: host, path: "note.md").absoluteString, host.absoluteString + "note.md")
    }
}
