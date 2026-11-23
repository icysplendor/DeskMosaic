import Foundation

enum NotebookDAVURL {
    static func resolve(base: URL, path: String) throws -> URL {
        func invalid(_ message: String) -> NSError {
            NSError(domain: "Notebook", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
        }
        guard !path.hasPrefix("/"), !path.contains("\\"),
              !path.split(separator: "/").contains(where: { $0 == "." || $0 == ".." }) else {
            throw invalid("同步请求无效")
        }
        var allowed = CharacterSet.urlPathAllowed
        allowed.remove(charactersIn: "/?#%:")
        let escaped = path.components(separatedBy: "/").map {
            $0.addingPercentEncoding(withAllowedCharacters: allowed) ?? ""
        }.joined(separator: "/")
        // Foundation can return nil for URL(string: "", relativeTo: base).
        // An empty path is the collection itself (the initial PROPFIND).
        guard let url = path.isEmpty ? base : URL(string: escaped, relativeTo: base)?.absoluteURL,
              url.scheme == "https", url.host == base.host, url.port == base.port,
              url.user == nil, url.password == nil, url.query == nil, url.fragment == nil else {
            throw invalid("同步请求超出已配置目录")
        }
        let root = base.path.hasSuffix("/") ? String(base.path.dropLast()) : base.path
        guard url.path == root || url.path.hasPrefix(root + "/") else {
            throw invalid("同步请求超出已配置目录")
        }
        return url
    }
}
