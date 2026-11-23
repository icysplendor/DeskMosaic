import Foundation
import XCTest
@testable import MACAssistant

final class NotebookCredentialStoreTests: XCTestCase {
    func testCredentialsPersistEncryptedAndStayBoundToTheirAccount() throws {
        let files = FileManager.default
        let directory = files.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? files.removeItem(at: directory) }
        let url = "https://cloud.example/notes/"
        let password = "test-password-笔记"
        let store = NotebookCredentialStore(directory: directory)
        XCTAssertNil(try store.load(url: url, user: "alice"))
        try store.save(password, url: url, user: "alice")

        let reopened = NotebookCredentialStore(directory: directory)
        XCTAssertEqual(try reopened.load(url: url, user: "alice"), password)
        XCTAssertNil(try reopened.load(url: url, user: "bob"))
        let entries = try files.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)
        XCTAssertEqual(entries.count, 2)
        let sealed = try XCTUnwrap(entries.first { $0.pathExtension == "aesgcm" })
        XCTAssertNil(try Data(contentsOf: sealed).range(of: Data(password.utf8)))
        XCTAssertEqual(try files.attributesOfItem(atPath: directory.path)[.posixPermissions] as? Int, 0o700)
        for entry in entries {
            XCTAssertEqual(try files.attributesOfItem(atPath: entry.path)[.posixPermissions] as? Int, 0o600)
        }

        try reopened.save("updated", url: url, user: "alice")
        XCTAssertEqual(try store.load(url: url, user: "alice"), "updated")
        var tampered = try Data(contentsOf: sealed)
        tampered[tampered.startIndex] ^= 1
        try tampered.write(to: sealed)
        XCTAssertThrowsError(try store.load(url: url, user: "alice"))
    }
}
