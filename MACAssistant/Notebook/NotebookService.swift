import CryptoKit
import Foundation

struct NotebookPreferences: Codable {
    var root: String?
    var url = ""
    var user = ""
    var enabled = false
    var theme: String?

    init() {}
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        root = try c.decodeIfPresent(String.self, forKey: .root)
        url = try c.decodeIfPresent(String.self, forKey: .url) ?? ""
        user = try c.decodeIfPresent(String.self, forKey: .user) ?? ""
        enabled = try c.decodeIfPresent(Bool.self, forKey: .enabled) ?? false
        theme = try c.decodeIfPresent(String.self, forKey: .theme)
    }
}

struct NotebookCredentialStore {
    let directory: URL
    private let files = FileManager.default

    init(directory: URL) {
        self.directory = directory
    }

    func load(url: String, user: String) throws -> String? {
        let identity = accountIdentity(url: url, user: user)
        let file = credentialFile(identity)
        guard files.fileExists(atPath: file.path) else { return nil }
        let box = try AES.GCM.SealedBox(combined: Data(contentsOf: file))
        let data = try AES.GCM.open(box, using: readKey(), authenticating: identity)
        return String(decoding: data, as: UTF8.self)
    }

    func save(_ password: String, url: String, user: String) throws {
        try files.createDirectory(at: directory, withIntermediateDirectories: true,
                                  attributes: [.posixPermissions: 0o700])
        try files.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
        let identity = accountIdentity(url: url, user: user)
        let key = try loadOrCreateKey()
        let box = try AES.GCM.seal(Data(password.utf8), using: key, authenticating: identity)
        guard let sealed = box.combined else { throw CocoaError(.fileWriteUnknown) }
        try writePrivate(sealed, to: credentialFile(identity))
    }

    private var keyFile: URL { directory.appendingPathComponent("credential.key") }

    private func accountIdentity(url: String, user: String) -> Data {
        Data("\(url.utf8.count):\(url)\(user)".utf8)
    }

    private func credentialFile(_ identity: Data) -> URL {
        let name = SHA256.hash(data: identity).map { String(format: "%02x", $0) }.joined()
        return directory.appendingPathComponent(name + ".aesgcm")
    }

    private func readKey() throws -> SymmetricKey {
        let data = try Data(contentsOf: keyFile)
        guard data.count == 32 else { throw CocoaError(.fileReadCorruptFile) }
        return SymmetricKey(data: data)
    }

    private func loadOrCreateKey() throws -> SymmetricKey {
        if files.fileExists(atPath: keyFile.path) { return try readKey() }
        let temporary = directory.appendingPathComponent("." + UUID().uuidString)
        let key = SymmetricKey(size: .bits256)
        defer { try? files.removeItem(at: temporary) }
        try writePrivate(key.withUnsafeBytes { Data($0) }, to: temporary)
        // Publish the complete key without replacing one created by another instance.
        do { try files.linkItem(at: temporary, to: keyFile) }
        catch {
            guard files.fileExists(atPath: keyFile.path) else { throw error }
            return try readKey()
        }
        return key
    }

    private func writePrivate(_ data: Data, to file: URL) throws {
        try data.write(to: file, options: .atomic)
        try files.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    }
}

actor NotebookService {
    private let fm = FileManager.default
    private let config: URL
    private let credentials: NotebookCredentialStore
    private var preferences: NotebookPreferences
    private let initialTheme: String

    init(theme: String) {
        initialTheme = theme
        config = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("MACAssistant/Notebook/preferences.json")
        credentials = NotebookCredentialStore(directory: config.deletingLastPathComponent()
            .appendingPathComponent("Credentials", isDirectory: true))
        let candidates = [config, URL(fileURLWithPath: config.path + ".bak")]
        preferences = candidates.compactMap { url -> NotebookPreferences? in
            guard let data = try? Data(contentsOf: url) else { return nil }
            return try? JSONDecoder().decode(NotebookPreferences.self, from: data)
        }.first ?? UserDefaults.standard.data(forKey: "NotebookPreferencesMirror").flatMap { try? JSONDecoder().decode(NotebookPreferences.self, from: $0) } ?? NotebookPreferences()
    }
    var root: URL {
        if let path = preferences.root { return URL(fileURLWithPath: path, isDirectory: true) }
        return fm.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("AssistantNotebook", isDirectory: true)
    }
    func settings() -> [String: Any] {
        ["platform": "mac", "nativeTitlebar": true, "theme": preferences.theme ?? initialTheme, "url": preferences.url, "user": preferences.user, "syncEnabled": preferences.enabled]
    }
    private func savePreferences() throws {
        if let primary = try? Data(contentsOf: config), (try? JSONDecoder().decode(NotebookPreferences.self, from: primary)) == nil {
            if let backup = try? Data(contentsOf: URL(fileURLWithPath: config.path + ".bak")), (try? JSONDecoder().decode(NotebookPreferences.self, from: backup)) != nil { try atomicWrite(config, data: backup, createBackup: false) }
        }
        let data = try JSONEncoder().encode(preferences)
        try atomicWrite(config, data: data)
        UserDefaults.standard.set(data, forKey: "NotebookPreferencesMirror")
    }
    func setTheme(_ value: String) throws { preferences.theme = value == "Dark" ? "Dark" : "Light"; try savePreferences() }
    func resolve(_ relative: String) throws -> URL {
        guard !relative.hasPrefix("/"), !relative.contains("\\"), !relative.contains(":"), !relative.split(separator: "/").contains(where: { $0 == ".." || $0 == "." }) else { throw failure("笔记路径无效") }
        let base = root.standardizedFileURL
        let url = base.appendingPathComponent(relative).standardizedFileURL
        guard url.path == base.path || url.path.hasPrefix(base.path + "/") else { throw failure("路径超出笔记库") }
        var cursor = url
        while cursor.path.count >= base.path.count {
            if (try? cursor.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true { throw failure("笔记库不支持符号链接") }
            if cursor.path == base.path { break }; cursor.deleteLastPathComponent()
        }
        return url
    }
    private func failure(_ message: String) -> NSError { NSError(domain: "Notebook", code: 1, userInfo: [NSLocalizedDescriptionKey: message]) }
    private func atomicWrite(_ url: URL, data: Data, createBackup: Bool = true) throws {
        try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        if createBackup && fm.fileExists(atPath: url.path) {
            let backup = URL(fileURLWithPath: url.path + ".bak")
            try Data(contentsOf: url).write(to: backup, options: .atomic)
        }
        try data.write(to: url, options: .atomic)
    }
    func exportAttachment(_ relative: String, to destination: URL) throws {
        let source = try resolve(relative)
        if source.standardizedFileURL == destination.standardizedFileURL { return }
        let temporary = destination.deletingLastPathComponent().appendingPathComponent(".attachment-" + UUID().uuidString)
        defer { try? fm.removeItem(at: temporary) }
        try fm.copyItem(at: source, to: temporary)
        if fm.fileExists(atPath: destination.path) {
            _ = try fm.replaceItemAt(destination, withItemAt: temporary)
        } else { try fm.moveItem(at: temporary, to: destination) }
    }
    func execute(_ op: String, args: [String: Any]) async throws -> Any {
        let path = args["path"] as? String ?? ""
        switch op {
        case "read", "readBinary":
            let url = try resolve(path)
            _ = try resolve(path + ".bak")
            guard fm.fileExists(atPath: url.path) else { return NSNull() }
            var data = try Data(contentsOf: url)
            if op == "readBinary" { return data.base64EncodedString() }
            if path.hasSuffix(".json"), (try? JSONSerialization.jsonObject(with: data)) == nil {
                data = try Data(contentsOf: URL(fileURLWithPath: url.path + ".bak"))
                _ = try JSONSerialization.jsonObject(with: data)
                try atomicWrite(url, data: data, createBackup: false)
            }
            guard let text = String(data: data, encoding: .utf8) else { throw failure("文件不是有效 UTF-8 文本") }; return text
        case "write":
            guard let text = args["data"] as? String, let data = Data(base64Encoded: text) else { throw failure("文件数据无效") }
            _ = try resolve(path + ".bak")
            try atomicWrite(resolve(path), data: data); return NSNull()
        case "mkdir": try fm.createDirectory(at: resolve(path), withIntermediateDirectories: true); return NSNull()
        case "list":
            let url = try resolve(path)
            guard fm.fileExists(atPath: url.path) else { return [] as [[String: Any]] }
            return try fm.contentsOfDirectory(at: url, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey, .contentModificationDateKey]).compactMap { entry -> [String: Any]? in
                let values = try entry.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey, .contentModificationDateKey])
                guard values.isSymbolicLink != true else { return nil }
                return ["name": entry.lastPathComponent, "dir": values.isDirectory == true, "modified": (values.contentModificationDate?.timeIntervalSince1970 ?? 0) * 1000]
            }
        case "move":
            guard let to = args["to"] as? String else { throw failure("目标路径无效") }
            let target = try resolve(to)
            try fm.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
            try fm.moveItem(at: resolve(path), to: target); return NSNull()
        case "remove":
            let url = try resolve(path)
            guard (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) != true else { throw failure("不允许递归永久删除笔记目录") }
            if fm.fileExists(atPath: url.path) { try fm.removeItem(at: url) }
            let backup = URL(fileURLWithPath: url.path + ".bak")
            if fm.fileExists(atPath: backup.path) { try fm.removeItem(at: backup) }; return NSNull()
        case "purgeDeleted":
            guard path.range(of: #"^\.(deleted|deleted-books)/[A-Za-z0-9_-]+$"#, options: .regularExpression) != nil else { throw failure("只能永久删除回收站中的笔记或笔记本") }
            let target = try resolve(path)
            guard fm.fileExists(atPath: target.path) else { return NSNull() }
            guard let enumerator = fm.enumerator(at: target, includingPropertiesForKeys: [.isSymbolicLinkKey]) else { throw failure("无法检查回收目录") }
            for case let child as URL in enumerator {
                guard try child.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink != true else { throw failure("回收目录包含符号链接，已停止删除") }
            }
            try fm.removeItem(at: target); return NSNull()
        case "http": return try await request(preferences, method: args["method"] as? String ?? "", path: path, body: args["data"] as? String, headers: args["headers"] as? [String: String] ?? [:])
        default: throw failure("未知笔记服务操作")
        }
    }
    private func missingPassword() -> NSError { failure("请在笔记本同步设置中填写 Nextcloud 应用密码并保存") }

    func configure(_ args: [String: Any]) async throws -> [String: Any] {
        var next = preferences
        next.url = (args["url"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        next.user = (args["user"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        next.enabled = args["enabled"] as? Bool ?? false
        if next.enabled {
            guard let url = URL(string: next.url), url.scheme == "https", url.host != nil, url.user == nil, url.query == nil, url.fragment == nil else { throw failure("请输入 HTTPS WebDAV 文件夹地址") }
            next.url = next.url.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "/"
            guard !next.user.isEmpty else { throw failure("请输入用户名") }
        }
        let newPassword = (args["password"] as? String).flatMap { $0.isEmpty ? nil : $0 }
        if next.enabled {
            let response = try await request(next, method: "PROPFIND", path: "", body: nil,
                                             headers: ["Depth": "0"], password: newPassword)
            guard response["status"] as? Int == 207 else { throw failure("WebDAV 连接校验失败 (\(response["status"] ?? "未知"))") }
        }
        if let newPassword { try credentials.save(newPassword, url: next.url, user: next.user) }
        if next.url != preferences.url || next.user != preferences.user {
            let baseline = try resolve(".sync.json")
            if fm.fileExists(atPath: baseline.path) { try fm.moveItem(at: baseline, to: URL(fileURLWithPath: baseline.path + ".previous-" + UUID().uuidString)) }
        }
        preferences = next; try savePreferences(); return settings()
    }
    private func request(_ config: NotebookPreferences, method: String, path: String, body: String?,
                         headers: [String: String], password: String? = nil) async throws -> [String: Any] {
        guard ["GET", "PUT", "DELETE", "PROPFIND", "MKCOL"].contains(method), !path.hasPrefix("/"), !path.contains("\\"), !path.split(separator: "/").contains(where: { $0 == ".." || $0 == "." }), let base = URL(string: config.url) else { throw failure("同步请求无效") }
        let url = try NotebookDAVURL.resolve(base: base, path: path)
        var request = URLRequest(url: url, timeoutInterval: 60); request.httpMethod = method
        guard let password = try password ?? credentials.load(url: config.url, user: config.user) else {
            throw missingPassword()
        }
        let auth = Data("\(config.user):\(password)".utf8).base64EncodedString()
        request.setValue("Basic \(auth)", forHTTPHeaderField: "Authorization")
        // Keep GET ETags consistent with conditional WebDAV writes and lock release.
        request.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
        for (key, value) in headers where ["Depth", "If-Match", "If-None-Match"].contains(key) { request.setValue(value, forHTTPHeaderField: key) }
        if let body { request.httpBody = Data(base64Encoded: body) }
        let session = URLSession(configuration: .ephemeral, delegate: NotebookNoRedirect(), delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw failure("WebDAV 响应无效") }
        return ["status": http.statusCode, "data": data.base64EncodedString(), "etag": http.value(forHTTPHeaderField: "ETag") ?? "", "retryAfter": http.value(forHTTPHeaderField: "Retry-After") ?? ""]
    }
    func migrate(to destination: URL) throws {
        let target = destination.standardizedFileURL
        _ = try resolve("")
        guard (try? target.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) != true else { throw failure("迁移目标不能是符号链接") }
        guard !target.path.hasPrefix(root.path + "/"), !root.path.hasPrefix(target.path + "/"), target.path != root.path else { throw failure("新目录不能与原笔记库相互包含") }
        guard try fm.contentsOfDirectory(atPath: target.path).isEmpty else { throw failure("请选择空文件夹，避免覆盖文件") }
        try fm.createDirectory(at: root, withIntermediateDirectories: true)
        guard let enumerator = fm.enumerator(at: root, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey]) else { throw failure("无法读取笔记库") }
        for case let source as URL in enumerator {
            let values = try source.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            guard values.isSymbolicLink != true else { throw failure("迁移不支持符号链接") }
            let relative = String(source.path.dropFirst(root.path.count + 1))
            let dest = target.appendingPathComponent(relative)
            if values.isDirectory == true { try fm.createDirectory(at: dest, withIntermediateDirectories: true) }
            else { let original = try Data(contentsOf: source); try original.write(to: dest, options: .atomic); guard try Data(contentsOf: dest) == original else { throw failure("迁移校验失败，原目录已保留") } }
        }
        preferences.root = target.path; try savePreferences()
    }
}
private final class NotebookNoRedirect: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}
